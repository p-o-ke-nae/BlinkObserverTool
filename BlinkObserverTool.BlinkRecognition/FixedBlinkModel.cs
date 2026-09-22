using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;
using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

public sealed record FixedBlinkModel(
    int SchemaVersion,
    int InputWidth,
    int InputHeight,
    RoiArea EyeRegion,
    int TemplateWidth,
    int TemplateHeight,
    string Preprocessing,
    double MinimumCorrelation,
    double MinimumCorrelationMargin,
    int RearmOpenFrames,
    byte[] OpenTemplate,
    byte[] ClosedTemplate,
    string Hash)
{
    public const int CurrentSchemaVersion = 1;
    public const int NormalizedWidth = 96;
    public const string CurrentPreprocessing = "gray-resize96-proportional-gaussian3-clahe2-grid4";

    public static byte[] CaptureTemplate(RecognitionFrame frame, RoiArea eyeRegion, out int width, out int height)
    {
        ValidateFrame(frame);
        var bounded = Clamp(eyeRegion, frame.Width, frame.Height);
        if (bounded != eyeRegion)
        {
            throw new InvalidOperationException("目 ROI が入力画像の範囲外です。ROI を選択し直してください。");
        }

        using var source = ToMat(frame);
        using var gray = ToGray(source);
        using var eye = new Mat(gray, new Rect(eyeRegion.X, eyeRegion.Y, eyeRegion.Width, eyeRegion.Height)).Clone();
        using var normalized = NormalizeEye(eye);
        width = normalized.Width;
        height = normalized.Height;
        var bytes = new byte[width * height];
        System.Runtime.InteropServices.Marshal.Copy(normalized.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    public static FixedBlinkModel Create(
        RecognitionFrame openFrame,
        RecognitionFrame closedFrame,
        RoiArea eyeRegion,
        double minimumCorrelation = 0.45d,
        double minimumCorrelationMargin = 0.05d,
        int rearmOpenFrames = 3)
    {
        if (openFrame.Width != closedFrame.Width || openFrame.Height != closedFrame.Height)
        {
            throw new InvalidOperationException("開眼画像と閉眼画像の入力寸法が一致しません。");
        }

        var open = CaptureTemplate(openFrame, eyeRegion, out var width, out var height);
        var closed = CaptureTemplate(closedFrame, eyeRegion, out var closedWidth, out var closedHeight);
        if (width != closedWidth || height != closedHeight)
        {
            throw new InvalidOperationException("開眼テンプレートと閉眼テンプレートの寸法が一致しません。");
        }

        var separation = 1d - Correlation(open, closed);
        if (separation < Math.Max(0.02d, minimumCorrelationMargin))
        {
            throw new InvalidOperationException(
                $"開眼と閉眼の差が不足しています (相関差 {separation:F3})。目の状態を明確に変えて採取し直してください。");
        }

        var model = new FixedBlinkModel(
            CurrentSchemaVersion,
            openFrame.Width,
            openFrame.Height,
            eyeRegion,
            width,
            height,
            CurrentPreprocessing,
            Math.Clamp(minimumCorrelation, -1d, 1d),
            Math.Clamp(minimumCorrelationMargin, 0d, 2d),
            Math.Clamp(rearmOpenFrames, 1, 30),
            open,
            closed,
            string.Empty);
        return model with { Hash = model.CalculateHash() };
    }

    public Dictionary<string, string> ToParameters() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ModelSchemaVersion"] = SchemaVersion.ToString(CultureInfo.InvariantCulture),
        ["ModelInputWidth"] = InputWidth.ToString(CultureInfo.InvariantCulture),
        ["ModelInputHeight"] = InputHeight.ToString(CultureInfo.InvariantCulture),
        ["ModelEyeX"] = EyeRegion.X.ToString(CultureInfo.InvariantCulture),
        ["ModelEyeY"] = EyeRegion.Y.ToString(CultureInfo.InvariantCulture),
        ["ModelEyeWidth"] = EyeRegion.Width.ToString(CultureInfo.InvariantCulture),
        ["ModelEyeHeight"] = EyeRegion.Height.ToString(CultureInfo.InvariantCulture),
        ["ModelTemplateWidth"] = TemplateWidth.ToString(CultureInfo.InvariantCulture),
        ["ModelTemplateHeight"] = TemplateHeight.ToString(CultureInfo.InvariantCulture),
        ["ModelPreprocessing"] = Preprocessing,
        ["MinimumCorrelation"] = MinimumCorrelation.ToString("R", CultureInfo.InvariantCulture),
        ["MinimumCorrelationMargin"] = MinimumCorrelationMargin.ToString("R", CultureInfo.InvariantCulture),
        ["RearmOpenFrames"] = RearmOpenFrames.ToString(CultureInfo.InvariantCulture),
        ["OpenTemplateBase64"] = Convert.ToBase64String(OpenTemplate),
        ["ClosedTemplateBase64"] = Convert.ToBase64String(ClosedTemplate),
        ["ModelHash"] = Hash
    };

    public static FixedBlinkModel Load(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("ModelSchemaVersion", out var schemaText) || string.IsNullOrWhiteSpace(schemaText))
        {
            throw new FixedBlinkModelException("blink-model-missing", "固定学習モデルがありません。瞬き観測セットアップを実行してください。");
        }

        if (!int.TryParse(schemaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schema))
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "固定学習モデルのスキーマ版が不正です。");
        }

        if (schema != CurrentSchemaVersion)
        {
            throw new FixedBlinkModelException("blink-model-version-unsupported", $"固定学習モデルの版 {schema} はサポートされていません。再セットアップしてください。");
        }

        try
        {
            var model = new FixedBlinkModel(
                schema,
                ReadInt(parameters, "ModelInputWidth"),
                ReadInt(parameters, "ModelInputHeight"),
                new RoiArea(
                    ReadInt(parameters, "ModelEyeX"),
                    ReadInt(parameters, "ModelEyeY"),
                    ReadInt(parameters, "ModelEyeWidth"),
                    ReadInt(parameters, "ModelEyeHeight")),
                ReadInt(parameters, "ModelTemplateWidth"),
                ReadInt(parameters, "ModelTemplateHeight"),
                ReadRequired(parameters, "ModelPreprocessing"),
                ReadDouble(parameters, "MinimumCorrelation"),
                ReadDouble(parameters, "MinimumCorrelationMargin"),
                ReadInt(parameters, "RearmOpenFrames"),
                Convert.FromBase64String(ReadRequired(parameters, "OpenTemplateBase64")),
                Convert.FromBase64String(ReadRequired(parameters, "ClosedTemplateBase64")),
                ReadRequired(parameters, "ModelHash"));
            model.Validate();
            return model;
        }
        catch (FixedBlinkModelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "固定学習モデルのデータが破損しています。", exception);
        }
    }

    public void Validate()
    {
        if (InputWidth <= 0 || InputHeight <= 0 || EyeRegion.IsEmpty
            || TemplateWidth <= 0 || TemplateHeight <= 0
            || TemplateWidth * TemplateHeight != OpenTemplate.Length
            || OpenTemplate.Length != ClosedTemplate.Length
            || Preprocessing != CurrentPreprocessing
            || MinimumCorrelation is < -1d or > 1d
            || MinimumCorrelationMargin is < 0d or > 2d
            || RearmOpenFrames is < 1 or > 30)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "固定学習モデルの内容が不正です。再セットアップしてください。");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash.ToUpperInvariant()),
                Encoding.ASCII.GetBytes(CalculateHash())))
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "固定学習モデルのハッシュが一致しません。再セットアップしてください。");
        }
    }

    public string CalculateHash()
    {
        var canonical = string.Join(
            "\n",
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            InputWidth.ToString(CultureInfo.InvariantCulture),
            InputHeight.ToString(CultureInfo.InvariantCulture),
            EyeRegion.X.ToString(CultureInfo.InvariantCulture),
            EyeRegion.Y.ToString(CultureInfo.InvariantCulture),
            EyeRegion.Width.ToString(CultureInfo.InvariantCulture),
            EyeRegion.Height.ToString(CultureInfo.InvariantCulture),
            TemplateWidth.ToString(CultureInfo.InvariantCulture),
            TemplateHeight.ToString(CultureInfo.InvariantCulture),
            Preprocessing,
            Convert.ToBase64String(OpenTemplate),
            Convert.ToBase64String(ClosedTemplate));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static double Correlation(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length || left.IsEmpty)
        {
            return -1d;
        }

        var leftMean = 0d;
        var rightMean = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            leftMean += left[index];
            rightMean += right[index];
        }
        leftMean /= left.Length;
        rightMean /= right.Length;

        var numerator = 0d;
        var leftEnergy = 0d;
        var rightEnergy = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            var leftDelta = left[index] - leftMean;
            var rightDelta = right[index] - rightMean;
            numerator += leftDelta * rightDelta;
            leftEnergy += leftDelta * leftDelta;
            rightEnergy += rightDelta * rightDelta;
        }

        var denominator = Math.Sqrt(leftEnergy * rightEnergy);
        return denominator <= 0.000001d ? -1d : Math.Clamp(numerator / denominator, -1d, 1d);
    }

    internal static Mat NormalizeEye(Mat eye)
    {
        var targetHeight = Math.Max(12, (int)Math.Round(eye.Height * (NormalizedWidth / (double)Math.Max(1, eye.Width))));
        using var resized = new Mat();
        Cv2.Resize(eye, resized, new Size(NormalizedWidth, targetHeight), 0d, 0d, InterpolationFlags.Cubic);
        using var denoised = new Mat();
        Cv2.GaussianBlur(resized, denoised, new Size(3, 3), 0d);
        using var clahe = Cv2.CreateCLAHE(2.0, new Size(4, 4));
        var output = new Mat();
        clahe.Apply(denoised, output);
        return output;
    }

    internal static Mat ToMat(RecognitionFrame frame)
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

    internal static Mat ToGray(Mat source)
    {
        if (source.Channels() == 1)
        {
            return source.Clone();
        }

        var gray = new Mat();
        Cv2.CvtColor(source, gray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    internal static RoiArea Clamp(RoiArea region, int width, int height)
    {
        if (region.IsEmpty || width <= 0 || height <= 0)
        {
            return RoiArea.Empty;
        }

        var x = Math.Clamp(region.X, 0, width - 1);
        var y = Math.Clamp(region.Y, 0, height - 1);
        return new RoiArea(x, y, Math.Clamp(region.Width, 1, width - x), Math.Clamp(region.Height, 1, height - y));
    }

    private static void ValidateFrame(RecognitionFrame frame)
    {
        var bytesPerPixel = frame.PixelFormat switch
        {
            FramePixelFormat.Gray8 => 1,
            FramePixelFormat.Bgr24 => 3,
            FramePixelFormat.Bgra32 => 4,
            _ => throw new NotSupportedException($"Unsupported pixel format: {frame.PixelFormat}.")
        };
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * bytesPerPixel
            || frame.PixelData.Length < frame.Stride * frame.Height)
        {
            throw new InvalidOperationException("採取画像の寸法または画素データが不正です。");
        }
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FixedBlinkModelException("blink-model-corrupt", $"固定学習モデルの {key} がありません。");

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.Parse(ReadRequired(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key) =>
        double.Parse(ReadRequired(values, key), NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed class FixedBlinkModelException : InvalidOperationException
{
    public FixedBlinkModelException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
