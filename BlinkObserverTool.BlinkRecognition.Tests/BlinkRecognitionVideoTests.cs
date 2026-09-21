using System.Globalization;
using System.IO.Compression;
using BlinkObserverTool.Models;
using BlinkObserverTool.Services;
using Recognition.Core;
using Recognition.Infrastructure;
using Xunit;

namespace BlinkObserverTool.BlinkRecognition.Tests;

public sealed class BlinkRecognitionVideoTests
{
    private const int Width = 75;
    private const int Height = 55;
    private const double FramesPerSecond = 60.00350385423966d;
    private static readonly (int Start, int End)[] BlinkWindows = [(42, 56), (168, 182), (625, 649)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceSequenceDetectsThreeBlinkGroupsWithoutFalsePositives(bool degraded)
    {
        var frames = LoadFrames();
        using var recognizer = new BlinkRecognitionFactory().Create(CreateParameters());
        var transitions = new List<int>();
        var scores = new List<(int Frame, double Score, string? Label)>();
        var wasDetected = false;

        for (var index = 0; index < frames.Count; index++)
        {
            var pixels = degraded ? Degrade(frames[index]) : frames[index];
            var result = recognizer.Evaluate(new RecognitionFrame(
                pixels,
                Width,
                Height,
                Width,
                FramePixelFormat.Gray8,
                DateTimeOffset.UnixEpoch.AddSeconds(index / FramesPerSecond)));
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
            transitions.All(frame => BlinkWindows.Any(window => frame >= window.Start && frame <= window.End)),
            $"Unexpected transitions [{string.Join(", ", transitions)}]; top scores [{diagnostics}]");
        Assert.True(
            BlinkWindows.All(window => transitions.Any(frame => frame >= window.Start && frame <= window.End)),
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
                    Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(frame / FramesPerSecond),
                    SourceFrame = emptyFrame,
                    PreviewFrame = emptyFrame,
                    IsDetected = true,
                    EventTriggered = true,
                    DetectionConfidence = 1d,
                    FramesPerSecond = FramesPerSecond
                });
        }

        Assert.Equal(3, forwarded.Count);
    }

    [Fact]
    public void InvalidEyeRegionReturnsExplicitFailureLabel()
    {
        var parameters = CreateParameters();
        parameters["EyeWidth"] = "0";
        using var recognizer = new BlinkRecognitionFactory().Create(parameters);

        var result = recognizer.Evaluate(new RecognitionFrame(
            new byte[Width * Height],
            Width,
            Height,
            Width,
            FramePixelFormat.Gray8,
            DateTimeOffset.UnixEpoch));

        Assert.False(result.IsDetected);
        Assert.Equal("blink-eye-roi-not-configured", result.Label);
    }

    [Fact]
    public void GenericCatalogIncludesInjectedBlinkFactoryAlongsideTemplateMatching()
    {
        var catalog = new RecognitionPluginCatalog(
            Path.Combine(Path.GetTempPath(), $"blink-plugins-{Guid.NewGuid():N}"),
            [new BlinkRecognitionFactory()]);

        Assert.Contains(catalog.RecognitionMethodFactories, factory =>
            factory.Descriptor.Id == BlinkRecognitionFactory.ComponentId);
        Assert.Contains(catalog.RecognitionMethodFactories, factory =>
            factory.Descriptor.Id == "builtin.recognition.template-match");
    }

    private static Dictionary<string, string> CreateParameters() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["EyeX"] = "10",
        ["EyeY"] = "10",
        ["EyeWidth"] = "52",
        ["EyeHeight"] = "32",
        ["SearchX"] = "0",
        ["SearchY"] = "0",
        ["SearchWidth"] = Width.ToString(CultureInfo.InvariantCulture),
        ["SearchHeight"] = Height.ToString(CultureInfo.InvariantCulture),
        ["Sensitivity"] = "Standard",
        ["SmoothingFrames"] = "2",
        ["MinClosedFrames"] = "1",
        ["CalibrationFrames"] = "24"
    };

    private static List<byte[]> LoadFrames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "blink-eye-frames.gray8.gz");
        using var source = File.OpenRead(path);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var bytes = output.ToArray();
        var frameLength = Width * Height;
        Assert.Equal(0, bytes.Length % frameLength);

        return Enumerable.Range(0, bytes.Length / frameLength)
            .Select(index => bytes.AsSpan(index * frameLength, frameLength).ToArray())
            .ToList();
    }

    private static byte[] Degrade(byte[] source)
    {
        const int scale = 2;
        var reducedWidth = Width / scale;
        var reducedHeight = Height / scale;
        var reduced = new byte[reducedWidth * reducedHeight];
        for (var y = 0; y < reducedHeight; y++)
        {
            for (var x = 0; x < reducedWidth; x++)
            {
                var sum = 0;
                for (var offsetY = 0; offsetY < scale; offsetY++)
                {
                    for (var offsetX = 0; offsetX < scale; offsetX++)
                    {
                        sum += source[((y * scale + offsetY) * Width) + (x * scale + offsetX)];
                    }
                }

                reduced[(y * reducedWidth) + x] = (byte)(sum / (scale * scale));
            }
        }

        var result = new byte[source.Length];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var value = reduced[(Math.Min(reducedHeight - 1, y / scale) * reducedWidth) + Math.Min(reducedWidth - 1, x / scale)];
                var darkened = Math.Pow(value / 255d, 1.35d) * 255d * 0.48d;
                var noise = (((x * 17) + (y * 31)) % 7) - 3;
                result[(y * Width) + x] = (byte)Math.Clamp((int)Math.Round(darkened) + noise, 0, 255);
            }
        }

        return result;
    }

}
