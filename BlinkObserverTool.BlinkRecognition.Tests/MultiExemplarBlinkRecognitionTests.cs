using System.IO.Compression;
using System.Text.Json;
using BlinkObserverTool.Models;
using BlinkObserverTool.Services;
using Recognition.Core;
using Xunit;

namespace BlinkObserverTool.BlinkRecognition.Tests;

public sealed class MultiExemplarBlinkRecognitionTests
{
    private static readonly Lazy<SwayFixture> Fixture = new(SwayFixture.Load);
    private static readonly Lazy<CalibratedFixture> Calibrated = new(() => CalibratedFixture.Create(Fixture.Value));

    [Fact]
    public void CandidateProposalFindsTrainingBlinksOnly()
    {
        var fixture = Fixture.Value;
        var set = fixture.CreateSampleSet(fixture.TrainStart, fixture.TrainEnd);

        var candidates = MultiExemplarBlinkCalibrator.ProposeClosedCandidates(set, cancellationToken: TestContext.Current.CancellationToken);

        var trainBlinks = fixture.Blinks.Where(blink => blink.End <= fixture.TrainEnd).ToList();
        Assert.Equal(trainBlinks.Count, candidates.Count);
        Assert.All(candidates, candidate => Assert.Contains(trainBlinks, blink => candidate.StartIndex <= blink.End + 1 && candidate.EndIndex >= blink.Start - 1));
    }

    [Fact]
    public void CalibrationPassesQualityGateWithThreeTrainingBlinks()
    {
        var result = Calibrated.Value.Result;

        Assert.True(result.Passed, result.Summary);
        Assert.NotNull(result.Model);
        Assert.Equal(3, result.ClosedEventCount);
        Assert.True(result.MarginGap > 0d, result.Summary);
        Assert.InRange(result.Model!.OpenExemplars.Count, 8, 16);
        Assert.True(result.Model.ClosedExemplars.Count >= 2);
    }

    [Fact]
    public void FullSequenceDetectsEveryBlinkIncludingHeldOutWithoutFalsePositives()
    {
        var fixture = Fixture.Value;
        var transitions = Calibrated.Value.RunTransitions();

        var unexpected = transitions.Where(frame => !fixture.Blinks.Any(blink => frame >= blink.Start && frame <= blink.End)).ToList();
        var missing = fixture.Blinks.Where(blink => !transitions.Any(frame => frame >= blink.Start && frame <= blink.End)).ToList();
        Assert.True(unexpected.Count == 0, $"False positives at [{string.Join(", ", unexpected)}]");
        Assert.True(missing.Count == 0, $"Missed blinks [{string.Join(", ", missing.Select(blink => blink.Start))}]");
        Assert.Equal(fixture.Blinks.Count, transitions.Count);
        Assert.Contains(1206, transitions);
    }

    [Fact]
    public void ClosedFrameLabelsMatchGroundTruthFrameByFrame()
    {
        var fixture = Fixture.Value;
        var calibrated = Calibrated.Value;
        using var recognizer = new MultiExemplarBlinkRecognitionFactory().Create(calibrated.Parameters);
        var falseClosed = new List<int>();
        for (var index = 0; index < fixture.FrameCount; index++)
        {
            var result = recognizer.Evaluate(fixture.Frame(index));
            if (result.IsDetected && !fixture.IsClosed(index))
            {
                falseClosed.Add(index);
            }
        }

        Assert.True(falseClosed.Count == 0, $"Detected outside closed frames at [{string.Join(", ", falseClosed)}]");
    }

    [Fact]
    public void ForwarderReceivesEveryBlinkOrAggregatesDoubleBlinks()
    {
        var transitions = Calibrated.Value.RunTransitions();

        Assert.Equal(10, Forward(transitions, requiredBlinkCount: 1));
        Assert.Equal(6, Forward(transitions, requiredBlinkCount: 2));
    }

    [Fact]
    public void EvaluationIsDeterministic()
    {
        Assert.Equal(Calibrated.Value.RunTransitions(), Calibrated.Value.RunTransitions());
    }

    [Fact]
    public void ModelRoundTripsThroughParameters()
    {
        var model = Calibrated.Value.Result.Model!;
        var loaded = MultiExemplarBlinkModel.Load(model.ToParameters());

        Assert.Equal(model.ToJson(), loaded.ToJson());
        Assert.StartsWith("学習済み", MultiExemplarBlinkModel.GetModelStatus(model.ToParameters()));
    }

    [Fact]
    public void CorruptedOrMissingModelIsRejected()
    {
        var parameters = Calibrated.Value.Parameters;
        var tampered = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            [MultiExemplarBlinkModel.ModelHashKey] = new string('0', 64)
        };
        var missing = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        missing.Remove(MultiExemplarBlinkModel.ModelJsonKey);

        Assert.Equal("blink-model-corrupt", Assert.Throws<FixedBlinkModelException>(() => MultiExemplarBlinkModel.Load(tampered)).ErrorCode);
        Assert.Equal("blink-model-missing", Assert.Throws<FixedBlinkModelException>(() => MultiExemplarBlinkModel.Load(missing)).ErrorCode);
        using var recognizer = new MultiExemplarBlinkRecognitionFactory().Create(tampered);
        var result = recognizer.Evaluate(Fixture.Value.Frame(0));
        Assert.False(result.IsDetected);
        Assert.Equal("blink-model-corrupt", result.Label);
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        var model = Calibrated.Value.Result.Model! with { SchemaVersion = 99 };
        var json = model.ToJson();
        var parameters = new Dictionary<string, string>(Calibrated.Value.Parameters, StringComparer.OrdinalIgnoreCase)
        {
            [MultiExemplarBlinkModel.ModelJsonKey] = json,
            [MultiExemplarBlinkModel.ModelHashKey] = MultiExemplarBlinkModel.CalculateHash(json)
        };

        Assert.Equal("blink-model-version-unsupported", Assert.Throws<FixedBlinkModelException>(() => MultiExemplarBlinkModel.Load(parameters)).ErrorCode);
    }

    [Fact]
    public void MismatchedInputsAreReportedWithoutDetection()
    {
        var fixture = Fixture.Value;
        var parameters = new Dictionary<string, string>(Calibrated.Value.Parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["EyeX"] = (fixture.EyeRoi.X + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        using var shifted = new MultiExemplarBlinkRecognitionFactory().Create(parameters);
        using var recognizer = new MultiExemplarBlinkRecognitionFactory().Create(Calibrated.Value.Parameters);
        var frame = fixture.Frame(250);

        Assert.Equal("blink-model-roi-mismatch", shifted.Evaluate(frame).Label);
        Assert.Equal(
            "blink-model-input-mismatch",
            recognizer.Evaluate(new RecognitionFrame(new byte[10 * 10 * 3], 10, 10, 30, FramePixelFormat.Bgr24, frame.CapturedAt)).Label);
        Assert.Equal(
            "blink-model-pixel-format-mismatch",
            recognizer.Evaluate(new RecognitionFrame(new byte[fixture.Width * fixture.Height], fixture.Width, fixture.Height, fixture.Width, FramePixelFormat.Gray8, frame.CapturedAt)).Label);
    }

    [Fact]
    public void ConstantOrOccludedFrameIsUnknownAndDoesNotFire()
    {
        var fixture = Fixture.Value;
        using var recognizer = new MultiExemplarBlinkRecognitionFactory().Create(Calibrated.Value.Parameters);
        for (var index = 200; index < 240; index++)
        {
            recognizer.Evaluate(fixture.Frame(index));
        }

        var gray = new byte[fixture.Width * fixture.Height * 3];
        Array.Fill(gray, (byte)128);
        var result = recognizer.Evaluate(new RecognitionFrame(gray, fixture.Width, fixture.Height, fixture.Width * 3, FramePixelFormat.Bgr24, DateTimeOffset.UnixEpoch));

        Assert.False(result.IsDetected);
        Assert.Equal("blink-unknown", result.Label);
    }

    [Fact]
    public void BgraInputIsAcceptedEquivalentlyToBgr()
    {
        var fixture = Fixture.Value;
        using var bgr = new MultiExemplarBlinkRecognitionFactory().Create(Calibrated.Value.Parameters);
        using var bgra = new MultiExemplarBlinkRecognitionFactory().Create(Calibrated.Value.Parameters);
        for (var index = 230; index < 260; index++)
        {
            var frame = fixture.Frame(index);
            var expected = bgr.Evaluate(frame);
            var actual = bgra.Evaluate(ToBgra(frame));
            Assert.Equal(expected.IsDetected, actual.IsDetected);
            Assert.Equal(expected.Label, actual.Label);
        }
    }

    [Fact]
    public void CalibrationFromProposedCandidatesPassesLikeTheSetupFlow()
    {
        var fixture = Fixture.Value;
        var set = fixture.CreateSampleSet(fixture.TrainStart, fixture.TrainEnd);
        var candidates = MultiExemplarBlinkCalibrator.ProposeClosedCandidates(set, cancellationToken: TestContext.Current.CancellationToken);
        var closed = candidates.SelectMany(candidate => Enumerable.Range(candidate.StartIndex, candidate.EndIndex - candidate.StartIndex + 1)).ToList();

        var result = MultiExemplarBlinkCalibrator.Calibrate(set, closed, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Summary);
    }

    [Fact]
    public void CalibrationWithSingleBlinkAsksForMoreSamples()
    {
        var fixture = Fixture.Value;
        var set = fixture.CreateSampleSet(0, 300);
        var result = MultiExemplarBlinkCalibrator.Calibrate(set, [249, 250], [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Null(result.Model);
    }

    private static int Forward(IReadOnlyList<int> transitions, int requiredBlinkCount)
    {
        var forwarded = new List<DateTimeOffset>();
        var forwarder = new RecognitionKeyForwarder(forwarded.Add)
        {
            CurrentConfiguration = new BlinkSendConfiguration
            {
                RequiredBlinkCount = requiredBlinkCount,
                BlinkAggregationWindowMilliseconds = 350
            }
        };
        var emptyFrame = new RecognitionFrame([], 0, 0, 0, FramePixelFormat.Bgr24, DateTimeOffset.UnixEpoch);
        foreach (var frame in transitions)
        {
            forwarder.HandleRecognitionEvent(null, new RecognitionCycleResult
            {
                Timestamp = SwayFixture.Timestamp(frame),
                SourceFrame = emptyFrame,
                PreviewFrame = emptyFrame,
                IsDetected = true,
                EventTriggered = true,
                DetectionConfidence = 1d,
                FramesPerSecond = 60d
            });
        }

        return forwarded.Count;
    }

    private static RecognitionFrame ToBgra(RecognitionFrame frame)
    {
        var pixels = new byte[frame.Width * frame.Height * 4];
        for (var i = 0; i < frame.Width * frame.Height; i++)
        {
            pixels[i * 4] = frame.PixelData[i * 3];
            pixels[i * 4 + 1] = frame.PixelData[i * 3 + 1];
            pixels[i * 4 + 2] = frame.PixelData[i * 3 + 2];
            pixels[i * 4 + 3] = 255;
        }

        return new RecognitionFrame(pixels, frame.Width, frame.Height, frame.Width * 4, FramePixelFormat.Bgra32, frame.CapturedAt);
    }

    private sealed record CalibratedFixture(BlinkCalibrationResult Result, Dictionary<string, string> Parameters)
    {
        public static CalibratedFixture Create(SwayFixture fixture)
        {
            var set = fixture.CreateSampleSet(fixture.TrainStart, fixture.TrainEnd);
            var closed = Enumerable.Range(fixture.TrainStart, fixture.TrainEnd - fixture.TrainStart + 1).Where(fixture.IsClosed).ToList();
            var result = MultiExemplarBlinkCalibrator.Calibrate(set, closed, []);
            if (result.Model is null)
            {
                return new CalibratedFixture(result, []);
            }

            var parameters = new Dictionary<string, string>(result.Model.ToParameters(), StringComparer.OrdinalIgnoreCase);
            return new CalibratedFixture(result, parameters);
        }

        public List<int> RunTransitions()
        {
            var fixture = Fixture.Value;
            using var recognizer = new MultiExemplarBlinkRecognitionFactory().Create(Parameters);
            var transitions = new List<int>();
            var wasDetected = false;
            for (var index = 0; index < fixture.FrameCount; index++)
            {
                var result = recognizer.Evaluate(fixture.Frame(index));
                if (result.IsDetected && !wasDetected)
                {
                    transitions.Add(index);
                }

                wasDetected = result.IsDetected;
            }

            return transitions;
        }
    }

    private sealed class SwayFixture
    {
        private readonly byte[] data;
        private readonly HashSet<int> closedFrames;

        private SwayFixture(byte[] data, int width, int height, int frameCount, RoiArea eyeRoi, List<(int Start, int End)> blinks, int trainStart, int trainEnd)
        {
            this.data = data;
            Width = width;
            Height = height;
            FrameCount = frameCount;
            EyeRoi = eyeRoi;
            Blinks = blinks;
            TrainStart = trainStart;
            TrainEnd = trainEnd;
            closedFrames = blinks.SelectMany(blink => Enumerable.Range(blink.Start, blink.End - blink.Start + 1)).ToHashSet();
        }

        public int Width { get; }

        public int Height { get; }

        public int FrameCount { get; }

        public RoiArea EyeRoi { get; }

        public IReadOnlyList<(int Start, int End)> Blinks { get; }

        public int TrainStart { get; }

        public int TrainEnd { get; }

        public static DateTimeOffset Timestamp(int index) => DateTimeOffset.UnixEpoch.AddSeconds(index / 60d);

        public static SwayFixture Load()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "blink-sway-forest.json")));
            var root = json.RootElement;
            var crop = root.GetProperty("crop");
            var width = crop.GetProperty("width").GetInt32();
            var height = crop.GetProperty("height").GetInt32();
            var frameCount = root.GetProperty("frameCount").GetInt32();
            var eye = root.GetProperty("eyeRoi");
            var blinks = root.GetProperty("closedFrames").EnumerateArray()
                .Select(range => (range[0].GetInt32(), range[1].GetInt32()))
                .ToList();
            var train = root.GetProperty("trainFrames");

            using var input = File.OpenRead(Path.Combine(directory, "blink-sway-forest.bgr24.gz"));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            var data = output.ToArray();
            Assert.Equal(width * height * 3 * frameCount, data.Length);

            return new SwayFixture(
                data,
                width,
                height,
                frameCount,
                new RoiArea(eye.GetProperty("x").GetInt32(), eye.GetProperty("y").GetInt32(), eye.GetProperty("width").GetInt32(), eye.GetProperty("height").GetInt32()),
                blinks,
                train[0].GetInt32(),
                train[1].GetInt32());
        }

        public bool IsClosed(int index) => closedFrames.Contains(index);

        public RecognitionFrame Frame(int index)
        {
            var size = Width * Height * 3;
            return new RecognitionFrame(data.AsSpan(index * size, size).ToArray(), Width, Height, Width * 3, FramePixelFormat.Bgr24, Timestamp(index));
        }

        public BlinkSampleSet CreateSampleSet(int start, int end)
        {
            var set = new BlinkSampleSet(Width, Height, EyeRoi);
            for (var index = start; index <= end; index++)
            {
                set.Add(Frame(index));
            }

            return set;
        }
    }
}
