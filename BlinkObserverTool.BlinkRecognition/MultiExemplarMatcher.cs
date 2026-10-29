using OpenCvSharp;
using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

/// <summary>Scores a BGR search window against open and closed exemplars (max over exemplars and positions).</summary>
internal sealed class MultiExemplarMatcher : IDisposable
{
    private readonly List<Mat> openTemplates;
    private readonly List<Mat> closedTemplates;
    private readonly Mat result = new();

    public MultiExemplarMatcher(IEnumerable<byte[]> open, IEnumerable<byte[]> closed, int width, int height)
    {
        openTemplates = open.Select(bytes => FromBgr(bytes, width, height)).ToList();
        closedTemplates = closed.Select(bytes => FromBgr(bytes, width, height)).ToList();
        TemplateWidth = width;
        TemplateHeight = height;
    }

    public int TemplateWidth { get; }

    public int TemplateHeight { get; }

    public MatchScores Score(Mat window)
    {
        var open = Best(window, openTemplates, out var openLocation);
        var closed = Best(window, closedTemplates, out var closedLocation);
        return new MatchScores(open, closed, openLocation, closedLocation);
    }

    public double BestOpen(Mat window) => Best(window, openTemplates, out _);

    public void Dispose()
    {
        foreach (var template in openTemplates.Concat(closedTemplates))
        {
            template.Dispose();
        }

        result.Dispose();
    }

    internal double Best(Mat window, IReadOnlyList<Mat> templates, out Point location)
    {
        location = new Point(-1, -1);
        if (window.Width < TemplateWidth || window.Height < TemplateHeight)
        {
            return -1d;
        }

        var best = -1d;
        foreach (var template in templates)
        {
            Cv2.MatchTemplate(window, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var max, out _, out var maxLocation);
            if (double.IsFinite(max) && max > best)
            {
                best = max;
                location = maxLocation;
            }
        }

        return Math.Clamp(best, -1d, 1d);
    }

    internal static Mat FromBgr(byte[] bytes, int width, int height)
    {
        using var wrapped = Mat.FromPixelData(height, width, MatType.CV_8UC3, bytes);
        return wrapped.Clone();
    }

    internal static double StdDev(Mat bgr)
    {
        Cv2.MeanStdDev(bgr, out _, out var stddev);
        return (stddev.Val0 + stddev.Val1 + stddev.Val2) / 3d;
    }

    internal static byte[] ToBytes(Mat bgr)
    {
        using var continuous = bgr.Clone();
        var bytes = new byte[continuous.Width * continuous.Height * 3];
        System.Runtime.InteropServices.Marshal.Copy(continuous.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Crops <paramref name="region"/> from the frame and converts it to a BGR24 Mat without copying the full frame.</summary>
    internal static Mat CropBgr(RecognitionFrame frame, RoiArea region)
    {
        var type = frame.PixelFormat switch
        {
            FramePixelFormat.Bgra32 => MatType.CV_8UC4,
            FramePixelFormat.Bgr24 => MatType.CV_8UC3,
            _ => throw new NotSupportedException($"複数パターン瞬き認識はカラー入力 (BGR/BGRA) が必要です: {frame.PixelFormat}")
        };
        var bytesPerPixel = frame.PixelFormat == FramePixelFormat.Bgra32 ? 4 : 3;
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * bytesPerPixel
            || frame.PixelData.Length < frame.Stride * frame.Height)
        {
            throw new InvalidOperationException("入力画像の寸法または画素データが不正です。");
        }

        using var wrapped = Mat.FromPixelData(frame.Height, frame.Width, type, frame.PixelData, frame.Stride);
        using var sub = new Mat(wrapped, new Rect(region.X, region.Y, region.Width, region.Height));
        var output = new Mat();
        if (bytesPerPixel == 4)
        {
            Cv2.CvtColor(sub, output, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            sub.CopyTo(output);
        }

        return output;
    }

    internal static RoiArea Expand(RoiArea eye, int radius, int width, int height)
    {
        var x = Math.Max(0, eye.X - radius);
        var y = Math.Max(0, eye.Y - radius);
        var right = Math.Min(width, eye.X + eye.Width + radius);
        var bottom = Math.Min(height, eye.Y + eye.Height + radius);
        return right <= x || bottom <= y ? RoiArea.Empty : new RoiArea(x, y, right - x, bottom - y);
    }
}

internal readonly record struct MatchScores(double Open, double Closed, Point OpenLocation, Point ClosedLocation)
{
    public double Margin => Closed - Open;
}

/// <summary>
/// Frames collected during setup. Only the eye region plus the maximum search margin is kept (packed BGR24),
/// so several seconds of 1080p history can be retained cheaply.
/// </summary>
public sealed class BlinkSampleSet
{
    private readonly List<BlinkSample> samples = [];

    public BlinkSampleSet(int inputWidth, int inputHeight, RoiArea eyeRegion)
    {
        if (eyeRegion.IsEmpty || eyeRegion.X < 0 || eyeRegion.Y < 0
            || eyeRegion.X + eyeRegion.Width > inputWidth || eyeRegion.Y + eyeRegion.Height > inputHeight)
        {
            throw new InvalidOperationException("目 ROI が入力画像の範囲外です。ROI を選択し直してください。");
        }

        if (eyeRegion.Width > MultiExemplarBlinkModel.MaximumTemplateSide || eyeRegion.Height > MultiExemplarBlinkModel.MaximumTemplateSide)
        {
            throw new InvalidOperationException("目 ROI が大きすぎます。目の周辺だけを囲んでください。");
        }

        InputWidth = inputWidth;
        InputHeight = inputHeight;
        EyeRegion = eyeRegion;
        CaptureRegion = MultiExemplarMatcher.Expand(eyeRegion, MultiExemplarBlinkModel.MaximumSearchRadius, inputWidth, inputHeight);
    }

    public int InputWidth { get; }

    public int InputHeight { get; }

    public RoiArea EyeRegion { get; }

    public RoiArea CaptureRegion { get; }

    public IReadOnlyList<BlinkSample> Samples => samples;

    public int Count => samples.Count;

    /// <summary>Adds a frame (kept sorted by capture time; duplicates by timestamp are ignored).</summary>
    public int Add(RecognitionFrame frame)
    {
        if (frame.Width != InputWidth || frame.Height != InputHeight)
        {
            throw new InvalidOperationException("入力画像の寸法が ROI 選択時と異なります。ROI を選択し直してください。");
        }

        var existing = samples.FindIndex(sample => sample.CapturedAt == frame.CapturedAt);
        if (existing >= 0)
        {
            return existing;
        }

        using var crop = MultiExemplarMatcher.CropBgr(frame, CaptureRegion);
        var sample = new BlinkSample(frame.CapturedAt, MultiExemplarMatcher.ToBytes(crop));
        var index = samples.FindIndex(candidate => candidate.CapturedAt > frame.CapturedAt);
        if (index < 0)
        {
            samples.Add(sample);
            return samples.Count - 1;
        }

        samples.Insert(index, sample);
        return index;
    }

    public int IndexOf(DateTimeOffset capturedAt) => samples.FindIndex(sample => sample.CapturedAt == capturedAt);

    /// <summary>Returns a BGR Mat of the eye ROI expanded by <paramref name="radius"/> (clamped to the input frame).</summary>
    internal Mat Window(int index, int radius)
    {
        var window = MultiExemplarMatcher.Expand(EyeRegion, radius, InputWidth, InputHeight);
        return Crop(index, window);
    }

    internal byte[] Exemplar(int index)
    {
        using var eye = Crop(index, EyeRegion);
        return MultiExemplarMatcher.ToBytes(eye);
    }

    /// <summary>Packed BGR24 pixels of the capture region for preview rendering.</summary>
    public byte[] CapturePixels(int index) => samples[index].Pixels;

    private Mat Crop(int index, RoiArea region)
    {
        using var capture = MultiExemplarMatcher.FromBgr(samples[index].Pixels, CaptureRegion.Width, CaptureRegion.Height);
        using var sub = new Mat(capture, new Rect(region.X - CaptureRegion.X, region.Y - CaptureRegion.Y, region.Width, region.Height));
        return sub.Clone();
    }
}

public sealed record BlinkSample(DateTimeOffset CapturedAt, byte[] Pixels);
