using OpenCvSharp;
using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

internal sealed class FixedBlinkRecognizer : IRecognitionMethod
{
    private readonly RoiArea configuredEyeRegion;
    private readonly RoiArea configuredSearchRegion;
    private readonly int smoothingFrames;
    private readonly int minClosedFrames;
    private readonly FixedBlinkModel? model;
    private readonly FixedBlinkModelException? modelError;
    private readonly Queue<(double Open, double Closed)> recentScores = [];
    private RoiArea currentEyeRegion;
    private Mat? openTrackingTemplate;
    private Mat? closedTrackingTemplate;
    private bool isClosed;
    private bool isArmed;
    private int consecutiveClosedFrames;
    private int consecutiveOpenFrames;

    public FixedBlinkRecognizer(
        RoiArea configuredEyeRegion,
        RoiArea configuredSearchRegion,
        int smoothingFrames,
        int minClosedFrames,
        FixedBlinkModel? model,
        FixedBlinkModelException? modelError)
    {
        this.configuredEyeRegion = configuredEyeRegion;
        this.configuredSearchRegion = configuredSearchRegion;
        this.smoothingFrames = Math.Clamp(smoothingFrames, 1, 8);
        this.minClosedFrames = Math.Clamp(minClosedFrames, 1, 8);
        this.model = model;
        this.modelError = modelError;
        currentEyeRegion = configuredEyeRegion;
        if (model is not null)
        {
            openTrackingTemplate = Mat.FromPixelData(
                model.TemplateHeight,
                model.TemplateWidth,
                MatType.CV_8UC1,
                model.OpenTemplate).Clone();
            closedTrackingTemplate = Mat.FromPixelData(
                model.TemplateHeight,
                model.TemplateWidth,
                MatType.CV_8UC1,
                model.ClosedTemplate).Clone();
        }
    }

    public RecognitionMatch Evaluate(RecognitionFrame frame)
    {
        if (model is null)
        {
            return new RecognitionMatch(false, 0d, modelError?.ErrorCode ?? "blink-model-missing");
        }

        if (frame.Width != model.InputWidth || frame.Height != model.InputHeight)
        {
            ResetForUnknown();
            return new RecognitionMatch(false, 0d, "blink-model-input-mismatch", configuredEyeRegion);
        }

        if (configuredEyeRegion != model.EyeRegion)
        {
            ResetForUnknown();
            return new RecognitionMatch(false, 0d, "blink-model-roi-mismatch", configuredEyeRegion);
        }

        using var source = FixedBlinkModel.ToMat(frame);
        using var gray = FixedBlinkModel.ToGray(source);
        var search = ResolveSearchRegion(gray.Width, gray.Height);
        if (search.IsEmpty || search.Width < configuredEyeRegion.Width || search.Height < configuredEyeRegion.Height)
        {
            ResetForUnknown();
            return new RecognitionMatch(false, 0d, "blink-search-roi-invalid", configuredEyeRegion);
        }

        var tracking = Track(gray, search);
        currentEyeRegion = tracking.Region;
        if (tracking.Confidence < 0.20d)
        {
            ResetForUnknown();
            return new RecognitionMatch(false, tracking.Confidence, "blink-unknown", currentEyeRegion);
        }

        using var eye = new Mat(gray, new Rect(currentEyeRegion.X, currentEyeRegion.Y, currentEyeRegion.Width, currentEyeRegion.Height)).Clone();
        using var normalized = FixedBlinkModel.NormalizeEye(eye);
        if (normalized.Width != model.TemplateWidth || normalized.Height != model.TemplateHeight)
        {
            ResetForUnknown();
            return new RecognitionMatch(false, 0d, "blink-model-roi-mismatch", currentEyeRegion);
        }

        var pixels = new byte[normalized.Width * normalized.Height];
        System.Runtime.InteropServices.Marshal.Copy(normalized.Data, pixels, 0, pixels.Length);
        var openCorrelation = FixedBlinkModel.Correlation(pixels, model.OpenTemplate);
        var closedCorrelation = FixedBlinkModel.Correlation(pixels, model.ClosedTemplate);
        recentScores.Enqueue((openCorrelation, closedCorrelation));
        while (recentScores.Count > smoothingFrames)
        {
            recentScores.Dequeue();
        }

        var smoothedOpen = recentScores.Average(value => value.Open);
        var smoothedClosed = recentScores.Average(value => value.Closed);
        var best = Math.Max(smoothedOpen, smoothedClosed);
        var difference = Math.Abs(smoothedOpen - smoothedClosed);
        if (best < model.MinimumCorrelation || difference < model.MinimumCorrelationMargin)
        {
            ResetForUnknown(clearScores: false);
            return new RecognitionMatch(false, Math.Clamp(best, 0d, 1d), "blink-unknown", currentEyeRegion);
        }

        if (smoothedOpen > smoothedClosed)
        {
            isClosed = false;
            consecutiveClosedFrames = 0;
            consecutiveOpenFrames++;
            if (consecutiveOpenFrames >= model.RearmOpenFrames)
            {
                isArmed = true;
                return new RecognitionMatch(false, Math.Clamp(smoothedOpen, 0d, 1d), "blink-open", currentEyeRegion);
            }

            return new RecognitionMatch(false, Math.Clamp(smoothedOpen, 0d, 1d), "blink-reacquiring", currentEyeRegion);
        }

        consecutiveOpenFrames = 0;
        if (!isArmed)
        {
            consecutiveClosedFrames = 0;
            return new RecognitionMatch(false, Math.Clamp(smoothedClosed, 0d, 1d), "blink-reacquiring", currentEyeRegion);
        }

        if (!isClosed && ++consecutiveClosedFrames >= minClosedFrames)
        {
            isClosed = true;
        }

        return new RecognitionMatch(isClosed, Math.Clamp(smoothedClosed, 0d, 1d), "blink-closed", currentEyeRegion);
    }

    public void Dispose()
    {
        openTrackingTemplate?.Dispose();
        closedTrackingTemplate?.Dispose();
    }

    private TrackingResult Track(Mat gray, RoiArea search)
    {
        var candidate = FixedBlinkModel.Clamp(currentEyeRegion, gray.Width, gray.Height);
        if (candidate.IsEmpty || openTrackingTemplate is null || closedTrackingTemplate is null)
        {
            return new TrackingResult(RoiArea.Empty, 0d);
        }

        using var searchMat = new Mat(gray, new Rect(search.X, search.Y, search.Width, search.Height)).Clone();
        var scale = FixedBlinkModel.NormalizedWidth / (double)candidate.Width;
        using var resized = new Mat();
        Cv2.Resize(
            searchMat,
            resized,
            new Size(
                Math.Max(FixedBlinkModel.NormalizedWidth, (int)Math.Round(searchMat.Width * scale)),
                Math.Max(openTrackingTemplate.Height, (int)Math.Round(searchMat.Height * scale))),
            0d,
            0d,
            InterpolationFlags.Cubic);
        using var denoised = new Mat();
        Cv2.GaussianBlur(resized, denoised, new Size(3, 3), 0d);
        using var normalizedSearch = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.0, new Size(4, 4)))
        {
            clahe.Apply(denoised, normalizedSearch);
        }

        using var openResult = new Mat();
        using var closedResult = new Mat();
        Cv2.MatchTemplate(normalizedSearch, openTrackingTemplate, openResult, TemplateMatchModes.CCoeffNormed);
        Cv2.MatchTemplate(normalizedSearch, closedTrackingTemplate, closedResult, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(openResult, out _, out var openConfidence, out _, out var openLocation);
        Cv2.MinMaxLoc(closedResult, out _, out var closedConfidence, out _, out var closedLocation);
        var confidence = Math.Max(openConfidence, closedConfidence);
        var location = openConfidence >= closedConfidence ? openLocation : closedLocation;
        if (confidence < 0.20d)
        {
            return new TrackingResult(candidate, Math.Clamp(confidence, 0d, 1d));
        }

        var matchedX = search.X + (int)Math.Round(location.X / scale);
        var matchedY = search.Y + (int)Math.Round(location.Y / scale);
        var x = Math.Clamp(matchedX, candidate.X - 2, candidate.X + 2);
        var y = Math.Clamp(matchedY, candidate.Y - 2, candidate.Y + 2);
        return new TrackingResult(
            FixedBlinkModel.Clamp(new RoiArea(x, y, candidate.Width, candidate.Height), gray.Width, gray.Height),
            Math.Clamp(confidence, 0d, 1d));
    }

    private RoiArea ResolveSearchRegion(int width, int height)
    {
        if (!configuredSearchRegion.IsEmpty)
        {
            return FixedBlinkModel.Clamp(configuredSearchRegion, width, height);
        }

        var paddingX = Math.Max(8, configuredEyeRegion.Width / 3);
        var paddingY = Math.Max(4, configuredEyeRegion.Height / 3);
        return FixedBlinkModel.Clamp(
            new RoiArea(
                configuredEyeRegion.X - paddingX,
                configuredEyeRegion.Y - paddingY,
                configuredEyeRegion.Width + (paddingX * 2),
                configuredEyeRegion.Height + (paddingY * 2)),
            width,
            height);
    }

    private void ResetForUnknown(bool clearScores = true)
    {
        isClosed = false;
        isArmed = false;
        consecutiveClosedFrames = 0;
        consecutiveOpenFrames = 0;
        if (clearScores)
        {
            recentScores.Clear();
        }
    }

    private readonly record struct TrackingResult(RoiArea Region, double Confidence);
}
