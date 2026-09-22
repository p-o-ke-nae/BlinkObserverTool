using System.Text.Json;
using Recognition.Core;
using Xunit;

namespace BlinkObserverTool.BlinkRecognition.Tests;

public sealed class FixedBlinkRecognitionTests
{
    private const int Width = 64;
    private const int Height = 40;
    private static readonly RoiArea EyeRegion = new(16, 10, 32, 16);

    [Fact]
    public void ModelParametersSurviveProfileJsonRoundTrip()
    {
        var parameters = CreateTrainedParameters();
        var profile = new RecognitionProfile
        {
            Name = "fixed-blink",
            Recognizer = new ComponentConfiguration
            {
                ComponentId = FixedBlinkRecognitionFactory.ComponentId,
                Parameters = parameters
            }
        };

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<RecognitionProfile>(json)!;
        var model = FixedBlinkModel.Load(restored.Recognizer.Parameters);

        Assert.Equal(FixedBlinkModel.CurrentSchemaVersion, model.SchemaVersion);
        Assert.Equal(EyeRegion, model.EyeRegion);
        Assert.Equal(model.CalculateHash(), model.Hash);
        Assert.Equal(parameters["OpenTemplateBase64"], restored.Recognizer.Parameters["OpenTemplateBase64"]);
        Assert.Equal(parameters["ClosedTemplateBase64"], restored.Recognizer.Parameters["ClosedTemplateBase64"]);
    }

    [Fact]
    public void RuntimeThresholdChangesDoNotInvalidateTheTrainedModel()
    {
        var parameters = CreateTrainedParameters();
        var originalHash = parameters["ModelHash"];
        parameters["MinimumCorrelation"] = "0.5";
        parameters["MinimumCorrelationMargin"] = "0.08";
        parameters["RearmOpenFrames"] = "5";

        var model = FixedBlinkModel.Load(parameters);

        Assert.Equal(originalHash, model.Hash);
        Assert.Equal(0.5d, model.MinimumCorrelation);
        Assert.Equal(0.08d, model.MinimumCorrelationMargin);
        Assert.Equal(5, model.RearmOpenFrames);
    }

    [Fact]
    public void SameFrameSequenceProducesIdenticalResults()
    {
        var parameters = CreateTrainedParameters();
        var sequence = new[]
        {
            CreateFrame(EyeState.Open), CreateFrame(EyeState.Open), CreateFrame(EyeState.Open),
            CreateFrame(EyeState.Closed), CreateFrame(EyeState.Closed),
            CreateFrame(EyeState.Open), CreateFrame(EyeState.Open), CreateFrame(EyeState.Open)
        };

        var first = Evaluate(parameters, sequence);
        var second = Evaluate(parameters, sequence);

        Assert.Equal(first, second);
    }

    [Fact]
    public void OcclusionSuppressesDetectionAndRequiresConsecutiveOpenFramesToRearm()
    {
        var parameters = CreateTrainedParameters();
        var originalHash = parameters["ModelHash"];
        var originalOpenTemplate = parameters["OpenTemplateBase64"];
        var originalClosedTemplate = parameters["ClosedTemplateBase64"];
        using var recognizer = new FixedBlinkRecognitionFactory().Create(parameters);

        Assert.All(Enumerable.Range(0, 3), _ => Assert.False(recognizer.Evaluate(CreateFrame(EyeState.Open)).IsDetected));
        Assert.True(recognizer.Evaluate(CreateFrame(EyeState.Closed)).IsDetected);

        var unknown = recognizer.Evaluate(CreateFrame(EyeState.Occluded));
        Assert.False(unknown.IsDetected);
        Assert.Equal("blink-unknown", unknown.Label);

        var closedWhileDisarmed = recognizer.Evaluate(CreateFrame(EyeState.Closed));
        Assert.False(closedWhileDisarmed.IsDetected);
        Assert.Equal("blink-reacquiring", closedWhileDisarmed.Label);

        Assert.Equal("blink-reacquiring", recognizer.Evaluate(CreateFrame(EyeState.Open)).Label);
        Assert.Equal("blink-reacquiring", recognizer.Evaluate(CreateFrame(EyeState.Open)).Label);
        Assert.Equal("blink-open", recognizer.Evaluate(CreateFrame(EyeState.Open)).Label);
        Assert.True(recognizer.Evaluate(CreateFrame(EyeState.Closed)).IsDetected);
        Assert.Equal(originalHash, parameters["ModelHash"]);
        Assert.Equal(originalOpenTemplate, parameters["OpenTemplateBase64"]);
        Assert.Equal(originalClosedTemplate, parameters["ClosedTemplateBase64"]);
    }

    [Fact]
    public void MissingCorruptAndInputMismatchModelsReturnExplicitErrors()
    {
        using var missing = new FixedBlinkRecognitionFactory().Create(new Dictionary<string, string>());
        var missingResult = missing.Evaluate(CreateFrame(EyeState.Open));
        Assert.Equal("blink-model-missing", missingResult.Label);

        var corruptParameters = CreateTrainedParameters();
        corruptParameters["OpenTemplateBase64"] = "not-base64";
        using var corrupt = new FixedBlinkRecognitionFactory().Create(corruptParameters);
        Assert.Equal("blink-model-corrupt", corrupt.Evaluate(CreateFrame(EyeState.Open)).Label);

        var validParameters = CreateTrainedParameters();
        using var valid = new FixedBlinkRecognitionFactory().Create(validParameters);
        var wrongSize = new RecognitionFrame(new byte[(Width + 1) * Height], Width + 1, Height, Width + 1, FramePixelFormat.Gray8, DateTimeOffset.UtcNow);
        Assert.Equal("blink-model-input-mismatch", valid.Evaluate(wrongSize).Label);

        var unsupportedParameters = CreateTrainedParameters();
        unsupportedParameters["ModelSchemaVersion"] = "999";
        using var unsupported = new FixedBlinkRecognitionFactory().Create(unsupportedParameters);
        Assert.Equal("blink-model-version-unsupported", unsupported.Evaluate(CreateFrame(EyeState.Open)).Label);

        var roiMismatchParameters = CreateTrainedParameters();
        roiMismatchParameters["EyeX"] = (EyeRegion.X + 1).ToString();
        using var roiMismatch = new FixedBlinkRecognitionFactory().Create(roiMismatchParameters);
        Assert.Equal("blink-model-roi-mismatch", roiMismatch.Evaluate(CreateFrame(EyeState.Open)).Label);

    }

    private static List<(bool IsDetected, double Confidence, string? Label)> Evaluate(
        IReadOnlyDictionary<string, string> parameters,
        IEnumerable<RecognitionFrame> frames)
    {
        using var recognizer = new FixedBlinkRecognitionFactory().Create(parameters);
        return frames.Select(frame =>
        {
            var result = recognizer.Evaluate(frame);
            return (result.IsDetected, result.Confidence, result.Label);
        }).ToList();
    }

    private static Dictionary<string, string> CreateTrainedParameters()
    {
        var model = FixedBlinkModel.Create(
            CreateFrame(EyeState.Open),
            CreateFrame(EyeState.Closed),
            EyeRegion,
            minimumCorrelation: 0.35d,
            minimumCorrelationMargin: 0.04d,
            rearmOpenFrames: 3);
        var parameters = model.ToParameters();
        parameters["EyeX"] = EyeRegion.X.ToString();
        parameters["EyeY"] = EyeRegion.Y.ToString();
        parameters["EyeWidth"] = EyeRegion.Width.ToString();
        parameters["EyeHeight"] = EyeRegion.Height.ToString();
        parameters["SearchX"] = EyeRegion.X.ToString();
        parameters["SearchY"] = EyeRegion.Y.ToString();
        parameters["SearchWidth"] = EyeRegion.Width.ToString();
        parameters["SearchHeight"] = EyeRegion.Height.ToString();
        parameters["SmoothingFrames"] = "1";
        parameters["MinClosedFrames"] = "1";
        return parameters;
    }

    private static RecognitionFrame CreateFrame(EyeState state)
    {
        var pixels = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                pixels[(y * Width) + x] = (byte)(145 + ((x * 3 + y * 5) % 24));
            }
        }

        for (var y = EyeRegion.Y; y < EyeRegion.Y + EyeRegion.Height; y++)
        {
            for (var x = EyeRegion.X; x < EyeRegion.X + EyeRegion.Width; x++)
            {
                var localX = x - EyeRegion.X;
                var localY = y - EyeRegion.Y;
                pixels[(y * Width) + x] = state switch
                {
                    EyeState.Open when IsOpenEyePixel(localX, localY) => 25,
                    EyeState.Closed when Math.Abs(localY - (EyeRegion.Height / 2)) <= 1 => 25,
                    EyeState.Occluded => 70,
                    _ => (byte)(170 + ((localX + localY) % 18))
                };
            }
        }

        return new RecognitionFrame(pixels, Width, Height, Width, FramePixelFormat.Gray8, DateTimeOffset.UtcNow);
    }

    private static bool IsOpenEyePixel(int x, int y)
    {
        var centerX = EyeRegion.Width / 2d;
        var centerY = EyeRegion.Height / 2d;
        var ellipse = Math.Pow((x - centerX) / 14d, 2d) + Math.Pow((y - centerY) / 6d, 2d);
        var pupil = Math.Pow((x - centerX) / 3d, 2d) + Math.Pow((y - centerY) / 5d, 2d);
        return Math.Abs(ellipse - 1d) < 0.35d || pupil <= 1d;
    }

    private enum EyeState
    {
        Open,
        Closed,
        Occluded
    }
}
