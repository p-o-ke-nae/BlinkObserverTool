using OpenCvSharp;
using Recognition.Core;
using System.Runtime.InteropServices;

namespace BlinkObserverTool.BlinkRecognition;

internal sealed class BlinkRecognizer : IRecognitionMethod
{
    private const int NormalizedEyeWidth = 96;

    private readonly RoiArea configuredEyeRegion;
    private readonly RoiArea configuredSearchRegion;
    private readonly int smoothingFrames;
    private readonly int minClosedFrames;
    private readonly int calibrationFrames;
    private readonly double closeThreshold;
    private readonly double openThreshold;
    private readonly Queue<FeatureSet> calibrationSamples = [];
    private readonly Queue<double> recentScores = [];

    private RoiArea currentEyeRegion;
    private Mat? trackingTemplate;
    private FeatureSet? baseline;
    private int consecutiveClosedFrames;
    private bool isClosed;

    public BlinkRecognizer(
        RoiArea configuredEyeRegion,
        RoiArea configuredSearchRegion,
        string sensitivity,
        int smoothingFrames,
        int minClosedFrames,
        int calibrationFrames,
        double detectionThreshold)
    {
        this.configuredEyeRegion = configuredEyeRegion;
        this.configuredSearchRegion = configuredSearchRegion;
        this.smoothingFrames = Math.Clamp(smoothingFrames, 1, 8);
        this.minClosedFrames = Math.Clamp(minClosedFrames, 1, 8);
        this.calibrationFrames = Math.Clamp(calibrationFrames, 6, 120);
        (closeThreshold, openThreshold) = sensitivity switch
        {
            "High" => (0.045d, 0.022d),
            "Low" => (0.08d, 0.045d),
            _ => (0.055d, 0.027d)
        };
        if (detectionThreshold > 0d)
        {
            closeThreshold = Math.Clamp(detectionThreshold, 0.001d, 1d);
            openThreshold = closeThreshold * 0.50d;
        }
        currentEyeRegion = configuredEyeRegion;
    }

    public RecognitionMatch Evaluate(RecognitionFrame frame)
    {
        using var source = ToMat(frame);
        using var gray = ToGray(source);
        var configuredEye = Clamp(configuredEyeRegion, gray.Width, gray.Height);
        if (configuredEye.IsEmpty)
        {
            return new RecognitionMatch(false, 0d, "blink-eye-roi-not-configured");
        }

        var search = ResolveSearchRegion(configuredEye, gray.Width, gray.Height);
        if (search.IsEmpty || search.Width < configuredEye.Width || search.Height < configuredEye.Height)
        {
            return new RecognitionMatch(false, 0d, "blink-search-roi-invalid", configuredEye);
        }

        var tracking = Track(gray, configuredEye, search);
        currentEyeRegion = tracking.Region;
        using var eye = Crop(gray, currentEyeRegion);
        if (eye.Empty())
        {
            return new RecognitionMatch(false, 0d, "blink-eye-roi-empty", currentEyeRegion);
        }

        using var normalized = NormalizeEye(eye);
        var features = ExtractFeatures(normalized);
        if (baseline is null)
        {
            calibrationSamples.Enqueue(features);
            if (calibrationSamples.Count < calibrationFrames)
            {
                return new RecognitionMatch(false, calibrationSamples.Count / (double)calibrationFrames, "blink-learning", currentEyeRegion);
            }

            baseline = FeatureSet.Median(calibrationSamples);
            calibrationSamples.Clear();
            UpdateTrackingTemplate(normalized);
            return new RecognitionMatch(false, 0d, "blink-ready", currentEyeRegion);
        }

        var score = CalculateClosedScore(features, baseline.Value);
        if (tracking.Confidence < 0.20d)
        {
            recentScores.Clear();
            consecutiveClosedFrames = 0;
            return new RecognitionMatch(false, 0d, "blink-tracking-unstable", currentEyeRegion);
        }

        recentScores.Enqueue(score);
        while (recentScores.Count > smoothingFrames)
        {
            recentScores.Dequeue();
        }

        var smoothedScore = recentScores.Average();
        UpdateState(smoothedScore);
        if (!isClosed && smoothedScore < openThreshold)
        {
            UpdateTrackingTemplate(normalized);
        }

        return new RecognitionMatch(
            isClosed,
            Math.Clamp(smoothedScore, 0d, 1d),
            isClosed ? "blink-closed" : "blink-open",
            currentEyeRegion);
    }

    public void Dispose()
    {
        trackingTemplate?.Dispose();
    }

    private TrackingResult Track(Mat gray, RoiArea configuredEye, RoiArea search)
    {
        var candidate = Clamp(currentEyeRegion.IsEmpty ? configuredEye : currentEyeRegion, gray.Width, gray.Height);
        if (trackingTemplate is null)
        {
            using var initial = Crop(gray, candidate);
            using var normalized = NormalizeEye(initial);
            UpdateTrackingTemplate(normalized);
            return new TrackingResult(candidate, 1d);
        }

        using var searchMat = Crop(gray, search);
        if (searchMat.Width < candidate.Width || searchMat.Height < candidate.Height)
        {
            return new TrackingResult(candidate, 0d);
        }

        using var normalizedSearch = NormalizeSearch(searchMat, candidate.Width, candidate.Height);
        using var result = new Mat();
        Cv2.MatchTemplate(normalizedSearch, trackingTemplate, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var confidence, out _, out var location);

        var scale = NormalizedEyeWidth / (double)Math.Max(1, candidate.Width);
        if (confidence < 0.55d)
        {
            return new TrackingResult(candidate, Math.Clamp(confidence, 0d, 1d));
        }

        var matchedX = search.X + (int)Math.Round(location.X / scale);
        var matchedY = search.Y + (int)Math.Round(location.Y / scale);
        var x = Math.Clamp(matchedX, candidate.X - 2, candidate.X + 2);
        var y = Math.Clamp(matchedY, candidate.Y - 2, candidate.Y + 2);
        return new TrackingResult(
            Clamp(new RoiArea(x, y, candidate.Width, candidate.Height), gray.Width, gray.Height),
            Math.Clamp(confidence, 0d, 1d));
    }

    private static Mat NormalizeSearch(Mat search, int eyeWidth, int eyeHeight)
    {
        var scale = NormalizedEyeWidth / (double)Math.Max(1, eyeWidth);
        var targetSize = new Size(
            Math.Max(NormalizedEyeWidth, (int)Math.Round(search.Width * scale)),
            Math.Max(8, (int)Math.Round(search.Height * scale)));
        var resized = new Mat();
        Cv2.Resize(search, resized, targetSize, 0d, 0d, InterpolationFlags.Cubic);
        var normalized = Equalize(resized);
        resized.Dispose();
        return normalized;
    }

    private static Mat NormalizeEye(Mat eye)
    {
        var targetHeight = Math.Max(12, (int)Math.Round(eye.Height * (NormalizedEyeWidth / (double)Math.Max(1, eye.Width))));
        var resized = new Mat();
        Cv2.Resize(eye, resized, new Size(NormalizedEyeWidth, targetHeight), 0d, 0d, InterpolationFlags.Cubic);
        var normalized = Equalize(resized);
        resized.Dispose();
        return normalized;
    }

    private static Mat Equalize(Mat input)
    {
        using var denoised = new Mat();
        Cv2.GaussianBlur(input, denoised, new Size(3, 3), 0d);
        using var clahe = Cv2.CreateCLAHE(2.0, new Size(4, 4));
        var output = new Mat();
        clahe.Apply(denoised, output);
        return output;
    }

    private static FeatureSet ExtractFeatures(Mat eye)
    {
        Cv2.MeanStdDev(eye, out var mean, out var deviation);
        using var sobelY = new Mat();
        Cv2.Sobel(eye, sobelY, MatType.CV_32F, 0, 1, 3);
        using var absoluteSobel = new Mat();
        Cv2.ConvertScaleAbs(sobelY, absoluteSobel);
        var verticalEdgeEnergy = Cv2.Mean(absoluteSobel).Val0 / 255d;

        using var binary = new Mat();
        Cv2.Threshold(eye, binary, 0d, 255d, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        var rows = binary.Rows;
        var columns = binary.Cols;
        var pixels = new byte[rows * columns];
        Marshal.Copy(binary.Data, pixels, 0, pixels.Length);
        var rowProjection = new double[rows];
        for (var row = 0; row < rows; row++)
        {
            var rowOffset = row * columns;
            var count = 0;
            for (var column = 0; column < columns; column++)
            {
                if (pixels[rowOffset + column] != 0)
                {
                    count++;
                }
            }

            rowProjection[row] = count;
        }

        var darkPixels = rowProjection.Sum();
        var darkSpread = 0d;
        var lineConcentration = 0d;
        if (darkPixels > 0)
        {
            var center = rowProjection.Select((value, index) => value * index).Sum() / darkPixels;
            var variance = rowProjection.Select((value, index) => value * Math.Pow(index - center, 2d)).Sum() / darkPixels;
            darkSpread = Math.Clamp(Math.Sqrt(variance) / Math.Max(1d, binary.Rows / 2d), 0d, 1d);
            lineConcentration = Math.Clamp(rowProjection.Max() / darkPixels, 0d, 1d);
        }

        return new FeatureSet(
            Math.Clamp(darkPixels / Math.Max(1d, binary.Rows * binary.Cols), 0d, 1d),
            verticalEdgeEnergy,
            darkSpread,
            lineConcentration,
            Math.Clamp(deviation.Val0 / 64d, 0d, 1d),
            Math.Clamp(mean.Val0 / 255d, 0d, 1d));
    }

    private static double CalculateClosedScore(FeatureSet current, FeatureSet open)
    {
        var darkPixelDrop = RelativeDrop(current.DarkPixelRatio, open.DarkPixelRatio);
        var edgeDrop = RelativeDrop(current.VerticalEdgeEnergy, open.VerticalEdgeEnergy);
        var spreadIncrease = RelativeIncreaseFromBaseline(current.DarkSpread, open.DarkSpread);
        var contrastDrop = RelativeDrop(current.Contrast, open.Contrast);
        var lineIncrease = RelativeIncreaseFromBaseline(current.LineConcentration, open.LineConcentration);
        var score = Math.Clamp(
            (darkPixelDrop * 0.35d)
            + (edgeDrop * 0.25d)
            + (spreadIncrease * 0.20d)
            + (lineIncrease * 0.15d)
            + (contrastDrop * 0.05d),
            0d,
            1d);
        return spreadIncrease >= 0.04d && darkPixelDrop >= 0.08d
            ? score
            : score * 0.20d;
    }

    private void UpdateState(double score)
    {
        if (isClosed)
        {
            if (score <= openThreshold)
            {
                isClosed = false;
                consecutiveClosedFrames = 0;
            }
            return;
        }

        if (score >= closeThreshold)
        {
            consecutiveClosedFrames++;
            if (consecutiveClosedFrames >= minClosedFrames)
            {
                isClosed = true;
            }
        }
        else
        {
            consecutiveClosedFrames = 0;
        }
    }

    private void UpdateTrackingTemplate(Mat normalizedEye)
    {
        if (trackingTemplate is null || trackingTemplate.Size() != normalizedEye.Size())
        {
            trackingTemplate?.Dispose();
            trackingTemplate = normalizedEye.Clone();
            return;
        }

        Cv2.AddWeighted(trackingTemplate, 0.98d, normalizedEye, 0.02d, 0d, trackingTemplate);
    }

    private RoiArea ResolveSearchRegion(RoiArea eye, int frameWidth, int frameHeight)
    {
        if (!configuredSearchRegion.IsEmpty)
        {
            return Clamp(configuredSearchRegion, frameWidth, frameHeight);
        }

        var paddingX = Math.Max(8, eye.Width / 3);
        var paddingY = Math.Max(4, eye.Height / 3);
        return Clamp(
            new RoiArea(eye.X - paddingX, eye.Y - paddingY, eye.Width + paddingX * 2, eye.Height + paddingY * 2),
            frameWidth,
            frameHeight);
    }

    private static double RelativeDrop(double current, double baseline) =>
        baseline <= 0.0001d ? 0d : Math.Clamp((baseline - current) / baseline, 0d, 1d);

    private static double RelativeIncreaseFromBaseline(double current, double baseline) =>
        Math.Clamp((current - baseline) / Math.Max(0.03d, baseline), 0d, 1d);

    private static Mat ToMat(RecognitionFrame frame)
    {
        var type = frame.PixelFormat switch
        {
            FramePixelFormat.Bgra32 => MatType.CV_8UC4,
            FramePixelFormat.Bgr24 => MatType.CV_8UC3,
            FramePixelFormat.Gray8 => MatType.CV_8UC1,
            _ => throw new NotSupportedException($"Unsupported pixel format: {frame.PixelFormat}.")
        };
        using var wrapped = Mat.FromPixelData(frame.Height, frame.Width, type, frame.PixelData, frame.Stride);
        return wrapped.Clone();
    }

    private static Mat ToGray(Mat source)
    {
        if (source.Channels() == 1)
        {
            return source.Clone();
        }

        var gray = new Mat();
        Cv2.CvtColor(source, gray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat Crop(Mat source, RoiArea region)
    {
        var bounded = Clamp(region, source.Width, source.Height);
        return bounded.IsEmpty
            ? new Mat()
            : new Mat(source, new Rect(bounded.X, bounded.Y, bounded.Width, bounded.Height)).Clone();
    }

    private static RoiArea Clamp(RoiArea region, int width, int height)
    {
        if (region.IsEmpty || width <= 0 || height <= 0)
        {
            return RoiArea.Empty;
        }

        var x = Math.Clamp(region.X, 0, width - 1);
        var y = Math.Clamp(region.Y, 0, height - 1);
        return new RoiArea(x, y, Math.Clamp(region.Width, 1, width - x), Math.Clamp(region.Height, 1, height - y));
    }

    private readonly record struct TrackingResult(RoiArea Region, double Confidence);

    private readonly record struct FeatureSet(
        double DarkPixelRatio,
        double VerticalEdgeEnergy,
        double DarkSpread,
        double LineConcentration,
        double Contrast,
        double MeanBrightness)
    {
        public static FeatureSet Lerp(FeatureSet left, FeatureSet right, double amount) => new(
            Lerp(left.DarkPixelRatio, right.DarkPixelRatio, amount),
            Lerp(left.VerticalEdgeEnergy, right.VerticalEdgeEnergy, amount),
            Lerp(left.DarkSpread, right.DarkSpread, amount),
            Lerp(left.LineConcentration, right.LineConcentration, amount),
            Lerp(left.Contrast, right.Contrast, amount),
            Lerp(left.MeanBrightness, right.MeanBrightness, amount));

        public static FeatureSet Median(IEnumerable<FeatureSet> samples)
        {
            var values = samples.ToArray();
            return new FeatureSet(
                Median(values.Select(value => value.DarkPixelRatio)),
                Median(values.Select(value => value.VerticalEdgeEnergy)),
                Median(values.Select(value => value.DarkSpread)),
                Median(values.Select(value => value.LineConcentration)),
                Median(values.Select(value => value.Contrast)),
                Median(values.Select(value => value.MeanBrightness)));
        }

        private static double Lerp(double left, double right, double amount) => left + ((right - left) * amount);

        private static double Median(IEnumerable<double> values)
        {
            var ordered = values.Order().ToArray();
            var middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2d
                : ordered[middle];
        }
    }
}
