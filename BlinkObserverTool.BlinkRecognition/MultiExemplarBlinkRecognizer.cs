using System.Globalization;
using OpenCvSharp;
using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

public sealed class MultiExemplarBlinkRecognitionFactory : IRecognitionMethodFactory
{
    public const string ComponentId = "builtin.recognition.blink-multi-exemplar";

    public ComponentDescriptor Descriptor { get; } = new(
        ComponentId,
        "瞬き複数パターン認識",
        "セットアップで採取した複数の開眼・閉眼パターンと自動校正した閾値で判定し、揺れのあるキャラクターでも瞬きを検出します。",
        [
            new ParameterDefinition("EyeX", "目 ROI X", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("EyeY", "目 ROI Y", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("EyeWidth", "目 ROI 幅", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("EyeHeight", "目 ROI 高さ", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("ClosedMarginThreshold", "閉眼相関差の閾値 (空=校正値)", ParameterValueKind.Decimal, "", Minimum: -2, Maximum: 2, Step: 0.005),
            new ParameterDefinition("OpenSimilarityThreshold", "開眼類似度の上限 (空=校正値)", ParameterValueKind.Decimal, "", Minimum: -1, Maximum: 1, Step: 0.005),
            new ParameterDefinition("ClosedMinimumCorrelation", "閉眼最低一致度 (空=校正値)", ParameterValueKind.Decimal, "", Minimum: -1, Maximum: 1, Step: 0.01),
            new ParameterDefinition("RearmOpenFrames", "再検出までの連続開眼数", ParameterValueKind.Integer, "2", Minimum: 1, Maximum: 30, Step: 1),
            new ParameterDefinition("UnknownGraceFrames", "判定不能を許容するフレーム数", ParameterValueKind.Integer, "6", Minimum: 0, Maximum: 60, Step: 1),
            new ParameterDefinition(MultiExemplarBlinkModel.ModelJsonKey, "学習モデル (JSON)", ParameterValueKind.Text, ""),
            new ParameterDefinition(MultiExemplarBlinkModel.ModelHashKey, "学習モデル SHA-256", ParameterValueKind.Text, "")
        ]);

    public IRecognitionMethod Create(IReadOnlyDictionary<string, string> parameters)
    {
        MultiExemplarBlinkModel? model = null;
        FixedBlinkModelException? modelError = null;
        try
        {
            model = MultiExemplarBlinkModel.Load(parameters);
        }
        catch (FixedBlinkModelException exception)
        {
            modelError = exception;
        }

        var thresholds = model is null
            ? null
            : new MultiExemplarBlinkThresholds(
                ReadOverride(parameters, "ClosedMarginThreshold", model.Calibrated.ClosedMarginThreshold, -2d, 2d),
                ReadOverride(parameters, "OpenSimilarityThreshold", model.Calibrated.OpenSimilarityThreshold, -1d, 1d),
                ReadOverride(parameters, "ClosedMinimumCorrelation", model.Calibrated.ClosedMinimumCorrelation, -1d, 1d));
        return new MultiExemplarBlinkRecognizer(
            parameters.GetRoi("Eye"),
            parameters.GetInt32("RearmOpenFrames", 2),
            parameters.GetInt32("UnknownGraceFrames", 6),
            model,
            thresholds,
            modelError);
    }

    private static double ReadOverride(IReadOnlyDictionary<string, string> parameters, string key, double fallback, double minimum, double maximum) =>
        parameters.TryGetValue(key, out var text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}

internal sealed class MultiExemplarBlinkRecognizer : IRecognitionMethod
{
    private readonly RoiArea configuredEyeRegion;
    private readonly int rearmOpenFrames;
    private readonly int unknownGraceFrames;
    private readonly MultiExemplarBlinkModel? model;
    private readonly MultiExemplarBlinkThresholds? thresholds;
    private readonly FixedBlinkModelException? modelError;
    private readonly MultiExemplarMatcher? matcher;
    private bool isArmed;
    private bool isClosed;
    private int consecutiveOpenFrames;
    private int consecutiveUnknownFrames;

    public MultiExemplarBlinkRecognizer(
        RoiArea configuredEyeRegion,
        int rearmOpenFrames,
        int unknownGraceFrames,
        MultiExemplarBlinkModel? model,
        MultiExemplarBlinkThresholds? thresholds,
        FixedBlinkModelException? modelError)
    {
        this.configuredEyeRegion = configuredEyeRegion;
        this.rearmOpenFrames = Math.Clamp(rearmOpenFrames, 1, 30);
        this.unknownGraceFrames = Math.Clamp(unknownGraceFrames, 0, 60);
        this.model = model;
        this.thresholds = thresholds;
        this.modelError = modelError;
        if (model is not null)
        {
            matcher = new MultiExemplarMatcher(model.OpenExemplars, model.ClosedExemplars, model.TemplateWidth, model.TemplateHeight);
        }
    }

    public RecognitionMatch Evaluate(RecognitionFrame frame)
    {
        if (model is null || matcher is null || thresholds is null)
        {
            return new RecognitionMatch(false, 0d, modelError?.ErrorCode ?? "blink-model-missing", null);
        }

        if (frame.Width != model.InputWidth || frame.Height != model.InputHeight)
        {
            Disarm();
            return new RecognitionMatch(false, 0d, "blink-model-input-mismatch", configuredEyeRegion);
        }

        if (configuredEyeRegion != model.EyeRegion)
        {
            Disarm();
            return new RecognitionMatch(false, 0d, "blink-model-roi-mismatch", configuredEyeRegion);
        }

        if (frame.PixelFormat is not (FramePixelFormat.Bgra32 or FramePixelFormat.Bgr24))
        {
            Disarm();
            return new RecognitionMatch(false, 0d, "blink-model-pixel-format-mismatch", configuredEyeRegion);
        }

        var searchRegion = MultiExemplarMatcher.Expand(model.EyeRegion, model.SearchRadius, frame.Width, frame.Height);
        MatchScores scores;
        using (var window = MultiExemplarMatcher.CropBgr(frame, searchRegion))
        {
            scores = MultiExemplarMatcher.StdDev(window) < 1d
                ? new MatchScores(-1d, -1d, new Point(-1, -1), new Point(-1, -1))
                : matcher.Score(window);
        }

        var location = scores.Closed > scores.Open ? scores.ClosedLocation : scores.OpenLocation;
        var region = location.X < 0
            ? model.EyeRegion
            : new RoiArea(searchRegion.X + location.X, searchRegion.Y + location.Y, model.EyeRegion.Width, model.EyeRegion.Height);

        if (MultiExemplarBlinkCalibrator.IsClosed(scores, thresholds))
        {
            return OnClosed(scores, region);
        }

        if (scores.Open >= thresholds.OpenSimilarityThreshold && scores.Margin <= thresholds.ClosedMarginThreshold)
        {
            return OnOpen(scores, region);
        }

        return OnUnknown(scores, region);
    }

    public void Dispose() => matcher?.Dispose();

    private RecognitionMatch OnOpen(MatchScores scores, RoiArea region)
    {
        isClosed = false;
        consecutiveUnknownFrames = 0;
        consecutiveOpenFrames++;
        if (consecutiveOpenFrames >= rearmOpenFrames)
        {
            isArmed = true;
        }

        return new RecognitionMatch(false, Math.Clamp(scores.Open, 0d, 1d), isArmed ? "blink-open" : "blink-reacquiring", region);
    }

    private RecognitionMatch OnClosed(MatchScores scores, RoiArea region)
    {
        consecutiveOpenFrames = 0;
        consecutiveUnknownFrames = 0;
        var confidence = Math.Clamp(scores.Closed, 0d, 1d);
        if (isClosed)
        {
            return new RecognitionMatch(true, confidence, "blink-closed", region);
        }

        if (!isArmed)
        {
            return new RecognitionMatch(false, confidence, "blink-reacquiring", region);
        }

        isArmed = false;
        isClosed = true;
        return new RecognitionMatch(true, confidence, "blink-closed", region);
    }

    private RecognitionMatch OnUnknown(MatchScores scores, RoiArea region)
    {
        consecutiveOpenFrames = 0;
        consecutiveUnknownFrames++;
        var confidence = Math.Clamp(Math.Max(scores.Open, scores.Closed), 0d, 1d);
        if (consecutiveUnknownFrames > unknownGraceFrames)
        {
            Disarm();
            return new RecognitionMatch(false, confidence, "blink-unknown", region);
        }

        // A transition frame inside an ongoing closure keeps the closure alive so that one blink fires once.
        return isClosed
            ? new RecognitionMatch(true, confidence, "blink-closed", region)
            : new RecognitionMatch(false, confidence, "blink-unknown", region);
    }

    private void Disarm()
    {
        isArmed = false;
        isClosed = false;
        consecutiveOpenFrames = 0;
    }
}
