using System.Globalization;
using System.IO.Compression;
using BlinkObserverTool.Models;
using BlinkObserverTool.Services;
using Recognition.Core;
using Xunit;

namespace BlinkObserverTool.BlinkRecognition.Tests;

public sealed class DarkVideoRecognitionTests
{
    private static readonly VideoCase CityCase = new(
        "blink-dark-city.gray8.gz",
        89,
        92,
        60.003550505947096d,
        new RoiArea(17, 17, 54, 54),
        0d,
        [(375, 395), (511, 532), (605, 615)]);

    public static TheoryData<VideoCase> Cases => new()
    {
        new VideoCase(
            "blink-dark-cave.gray8.gz",
            80,
            80,
            60.00375023438965d,
            new RoiArea(18, 21, 40, 34),
            0.010d,
            [(134, 145), (417, 427), (558, 581)])
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DarkReferenceSequenceProducesThreeAggregatedBlinks(VideoCase video)
    {
        var frames = LoadFrames(video);
        using var recognizer = new BlinkRecognitionFactory().Create(CreateParameters(video));
        var transitions = new List<int>();
        var scores = new List<(int Frame, double Score, string? Label)>();
        var wasDetected = false;

        for (var index = 0; index < frames.Count; index++)
        {
            var result = recognizer.Evaluate(new RecognitionFrame(
                frames[index],
                video.Width,
                video.Height,
                video.Width,
                FramePixelFormat.Gray8,
                DateTimeOffset.UnixEpoch.AddSeconds(index / video.FramesPerSecond)));
            scores.Add((index, result.Confidence, result.Label));
            if (!wasDetected && result.IsDetected)
            {
                transitions.Add(index);
            }

            wasDetected = result.IsDetected;
        }

        var diagnostics = string.Join(
            ", ",
            scores.Where(item => item.Label != "blink-learning")
                .OrderByDescending(item => item.Score)
                .Take(20)
                .Select(item => $"{item.Frame}:{item.Score:F3}/{item.Label}"));
        Assert.True(
            transitions.All(frame => video.BlinkWindows.Any(window => frame >= window.Start && frame <= window.End)),
            $"Unexpected transitions [{string.Join(", ", transitions)}]; top scores [{diagnostics}]");
        Assert.True(
            video.BlinkWindows.All(window => transitions.Any(frame => frame >= window.Start && frame <= window.End)),
            $"Missing expected blink; transitions [{string.Join(", ", transitions)}]; top scores [{diagnostics}]");

        var forwarded = new List<DateTimeOffset>();
        var forwarder = new RecognitionKeyForwarder(forwarded.Add)
        {
            CurrentConfiguration = new BlinkSendConfiguration
            {
                RequiredBlinkCount = 2,
                BlinkAggregationWindowMilliseconds = 350
            }
        };
        var emptyFrame = new RecognitionFrame([], 0, 0, 0, FramePixelFormat.Gray8, DateTimeOffset.UnixEpoch);
        foreach (var frame in transitions)
        {
            forwarder.HandleRecognitionEvent(
                null,
                new RecognitionCycleResult
                {
                    Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(frame / video.FramesPerSecond),
                    SourceFrame = emptyFrame,
                    PreviewFrame = emptyFrame,
                    IsDetected = true,
                    EventTriggered = true,
                    DetectionConfidence = 1d,
                    FramesPerSecond = video.FramesPerSecond
                });
        }

        Assert.Equal(3, forwarded.Count);
    }

    [Fact]
    public void DarkCitySequenceProducesThreeAggregatedTemplateMatches()
    {
        var video = CityCase;
        var frames = LoadFrames(video);
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "blink-dark-city-template.png");
        using var recognizer = new Recognition.Infrastructure.TemplateMatchingRecognitionFactory().Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TemplatePath"] = templatePath,
                ["Threshold"] = "0.95",
                ["Method"] = "CCoeffNormed",
                ["SearchX"] = "0",
                ["SearchY"] = "0",
                ["SearchWidth"] = video.Width.ToString(CultureInfo.InvariantCulture),
                ["SearchHeight"] = video.Height.ToString(CultureInfo.InvariantCulture)
            });
        var transitions = new List<int>();
        var wasDetected = false;
        for (var index = 0; index < frames.Count; index++)
        {
            var result = recognizer.Evaluate(new RecognitionFrame(
                frames[index],
                video.Width,
                video.Height,
                video.Width,
                FramePixelFormat.Gray8,
                DateTimeOffset.UnixEpoch.AddSeconds(index / video.FramesPerSecond)));
            if (!wasDetected && result.IsDetected)
            {
                transitions.Add(index);
            }

            wasDetected = result.IsDetected;
        }

        Assert.All(transitions, frame =>
            Assert.Contains(video.BlinkWindows, window => frame >= window.Start && frame <= window.End));
        Assert.All(video.BlinkWindows, window =>
            Assert.Contains(transitions, frame => frame >= window.Start && frame <= window.End));
        Assert.Equal(3, CountForwardedBlinks(transitions, video.FramesPerSecond));
    }

    private static Dictionary<string, string> CreateParameters(VideoCase video) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["EyeX"] = video.EyeRegion.X.ToString(CultureInfo.InvariantCulture),
        ["EyeY"] = video.EyeRegion.Y.ToString(CultureInfo.InvariantCulture),
        ["EyeWidth"] = video.EyeRegion.Width.ToString(CultureInfo.InvariantCulture),
        ["EyeHeight"] = video.EyeRegion.Height.ToString(CultureInfo.InvariantCulture),
        ["SearchX"] = video.EyeRegion.X.ToString(CultureInfo.InvariantCulture),
        ["SearchY"] = video.EyeRegion.Y.ToString(CultureInfo.InvariantCulture),
        ["SearchWidth"] = video.EyeRegion.Width.ToString(CultureInfo.InvariantCulture),
        ["SearchHeight"] = video.EyeRegion.Height.ToString(CultureInfo.InvariantCulture),
        ["Sensitivity"] = "Standard",
        ["SmoothingFrames"] = "2",
        ["MinClosedFrames"] = "1",
        ["CalibrationFrames"] = "24",
        ["DetectionThreshold"] = video.DetectionThreshold.ToString(CultureInfo.InvariantCulture)
    };

    private static List<byte[]> LoadFrames(VideoCase video)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", video.FileName);
        using var source = File.OpenRead(path);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var bytes = output.ToArray();
        var frameLength = video.Width * video.Height;
        Assert.Equal(0, bytes.Length % frameLength);
        return Enumerable.Range(0, bytes.Length / frameLength)
            .Select(index => bytes.AsSpan(index * frameLength, frameLength).ToArray())
            .ToList();
    }

    private static int CountForwardedBlinks(IEnumerable<int> transitions, double framesPerSecond)
    {
        var forwarded = new List<DateTimeOffset>();
        var forwarder = new RecognitionKeyForwarder(forwarded.Add)
        {
            CurrentConfiguration = new BlinkSendConfiguration
            {
                RequiredBlinkCount = 2,
                BlinkAggregationWindowMilliseconds = 350
            }
        };
        var emptyFrame = new RecognitionFrame([], 0, 0, 0, FramePixelFormat.Gray8, DateTimeOffset.UnixEpoch);
        foreach (var frame in transitions)
        {
            forwarder.HandleRecognitionEvent(
                null,
                new RecognitionCycleResult
                {
                    Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(frame / framesPerSecond),
                    SourceFrame = emptyFrame,
                    PreviewFrame = emptyFrame,
                    IsDetected = true,
                    EventTriggered = true,
                    DetectionConfidence = 1d,
                    FramesPerSecond = framesPerSecond
                });
        }

        return forwarded.Count;
    }

    public sealed record VideoCase(
        string FileName,
        int Width,
        int Height,
        double FramesPerSecond,
        RoiArea EyeRegion,
        double DetectionThreshold,
        IReadOnlyList<(int Start, int End)> BlinkWindows)
    {
        public override string ToString() => FileName;
    }
}
