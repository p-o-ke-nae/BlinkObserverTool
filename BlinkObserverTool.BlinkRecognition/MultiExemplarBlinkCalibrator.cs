using System.Globalization;
using OpenCvSharp;

namespace BlinkObserverTool.BlinkRecognition;

public sealed record BlinkCandidateEvent(int StartIndex, int EndIndex, int PeakIndex, double OpenSimilarity);

public sealed record BlinkCalibrationResult(
    bool Passed,
    MultiExemplarBlinkModel? Model,
    string Summary,
    IReadOnlyList<string> Warnings,
    double MarginGap,
    double OpenGap,
    int ClosedEventCount,
    int OpenFrameCount,
    IReadOnlyList<BlinkCandidateEvent> DetectedEvents);

public sealed record BlinkCalibrationOptions
{
    public int CandidateSearchRadius { get; init; } = 4;
    public int MinimumOpenExemplars { get; init; } = 8;
    public int MaximumOpenExemplars { get; init; } = 16;
    public double OpenCoverageTarget { get; init; } = 0.94d;
    public int TransitionGuardFrames { get; init; } = 2;
    public int EventGapFrames { get; init; } = 3;
    public int MinimumClosedEvents { get; init; } = 2;
    public int MinimumOpenFrames { get; init; } = 60;
    public double MinimumGap { get; init; } = 0.02d;
    public double RecommendedGap { get; init; } = 0.05d;
    public double ClosedFloorMargin { get; init; } = 0.15d;
    public int MaximumPoolSize { get; init; } = 240;
}

/// <summary>
/// Builds a <see cref="MultiExemplarBlinkModel"/> from labelled setup frames. Thresholds are derived from
/// held-out folds (open frames split into contiguous time blocks, closed frames split per blink event) so that the
/// operator never needs to tune numbers by hand.
/// </summary>
public static class MultiExemplarBlinkCalibrator
{
    private const int CandidateSampleTarget = 120;

    /// <summary>Proposes closed-eye candidates: frames that look unlike every typical (recurring) frame in the recording.</summary>
    public static IReadOnlyList<BlinkCandidateEvent> ProposeClosedCandidates(
        BlinkSampleSet set,
        BlinkCalibrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BlinkCalibrationOptions();
        var count = set.Count;
        if (count < 8)
        {
            return [];
        }

        var radius = Math.Clamp(options.CandidateSearchRadius, MultiExemplarBlinkModel.MinimumSearchRadius, MultiExemplarBlinkModel.MaximumSearchRadius);
        using var cache = new WindowCache(set, radius);
        var step = Math.Max(2, (int)Math.Ceiling(count / (double)CandidateSampleTarget));
        var sampleIndices = Enumerable.Range(0, count).Where(index => index % step == 0).ToList();
        var typicality = new double[sampleIndices.Count];
        using var result = new Mat();
        for (var a = 0; a < sampleIndices.Count; a++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scores = new List<double>();
            for (var b = 0; b < sampleIndices.Count; b++)
            {
                if (Math.Abs(sampleIndices[a] - sampleIndices[b]) > options.EventGapFrames)
                {
                    scores.Add(Match(cache.Window(sampleIndices[a]), cache.Exemplar(sampleIndices[b]), result));
                }
            }

            scores.Sort();
            typicality[a] = scores.Count >= 2 ? scores[^2] : scores.LastOrDefault(-1d);
        }

        var (median, mad) = MedianMad(typicality);
        var cleanThreshold = median - Math.Max(4d * mad, 0.06d);
        var reference = sampleIndices.Where((_, a) => typicality[a] > cleanThreshold).ToList();
        var candidateThreshold = median - Math.Max(6d * mad, 0.06d);

        var lowFrames = new List<(int Index, double Score)>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = -1d;
            foreach (var other in reference)
            {
                if (Math.Abs(index - other) <= options.EventGapFrames)
                {
                    continue;
                }

                best = Math.Max(best, Match(cache.Window(index), cache.Exemplar(other), result));
                if (best >= candidateThreshold)
                {
                    break;
                }
            }

            if (best < candidateThreshold)
            {
                lowFrames.Add((index, best));
            }
        }

        return GroupEvents(lowFrames, options.EventGapFrames);
    }

    public static BlinkCalibrationResult Calibrate(
        BlinkSampleSet set,
        IReadOnlyCollection<int> closedIndices,
        IReadOnlyCollection<int> excludedIndices,
        BlinkCalibrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BlinkCalibrationOptions();
        var warnings = new List<string>();
        var closed = closedIndices.Where(index => index >= 0 && index < set.Count).Distinct().Order().ToList();
        var excluded = excludedIndices.Where(index => index >= 0 && index < set.Count).ToHashSet();
        var guarded = closed.Concat(excluded).ToList();
        var open = Enumerable.Range(0, set.Count)
            .Where(index => !excluded.Contains(index) && guarded.All(other => Math.Abs(other - index) > options.TransitionGuardFrames))
            .ToList();
        var events = GroupEvents(closed.Select(index => (index, 0d)).ToList(), options.EventGapFrames);

        if (events.Count < options.MinimumClosedEvents)
        {
            return Fail($"閉眼が {events.Count} 回分しかありません。異なるタイミングの瞬きを {options.MinimumClosedEvents} 回以上含めてください (録画を延ばすか手動で追加)。", events.Count, open.Count);
        }

        if (open.Count < options.MinimumOpenFrames)
        {
            return Fail($"開眼フレームが {open.Count} 枚しかありません。揺れが数周期入るよう {options.MinimumOpenFrames} フレーム以上録画してください。", events.Count, open.Count);
        }

        foreach (var index in closed)
        {
            using var eye = MultiExemplarMatcher.FromBgr(set.Exemplar(index), set.EyeRegion.Width, set.EyeRegion.Height);
            if (MultiExemplarMatcher.StdDev(eye) < MultiExemplarBlinkModel.MinimumExemplarStdDev)
            {
                return Fail($"フレーム {index} の目領域がほぼ単色です。ROI を目の輪郭が入るよう広げてください。", events.Count, open.Count);
            }
        }

        var radius = EstimateSearchRadius(set, open, options, cancellationToken);
        using var cache = new WindowCache(set, radius);

        var foldCount = Math.Clamp(events.Count, 2, 4);
        var heldOpen = new List<MatchScores>();
        var heldClosed = new List<(int Event, MatchScores Scores)>();
        for (var fold = 0; fold < foldCount; fold++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockStart = open.Count * fold / foldCount;
            var blockEnd = open.Count * (fold + 1) / foldCount;
            var trainOpen = open.Take(blockStart).Concat(open.Skip(blockEnd)).ToList();
            var testOpen = open.Skip(blockStart).Take(blockEnd - blockStart).ToList();
            var trainClosed = events.Where((_, e) => e % foldCount != fold).SelectMany(EventFrames).ToList();
            var testEvents = events.Select((value, e) => (value, e)).Where(item => item.e % foldCount == fold).ToList();
            if (trainClosed.Count == 0 || testEvents.Count == 0)
            {
                continue;
            }

            var openExemplars = SelectOpenExemplars(cache, trainOpen, options, cancellationToken, out _);
            var closedExemplars = SelectClosedExemplars(cache, trainClosed);
            using var matcher = CreateMatcher(set, openExemplars, closedExemplars);
            heldOpen.AddRange(testOpen.Select(index => matcher.Score(cache.Window(index))));
            foreach (var (value, e) in testEvents)
            {
                heldClosed.AddRange(EventFrames(value).Select(index => (e, matcher.Score(cache.Window(index)))));
            }
        }

        if (heldOpen.Count == 0 || heldClosed.Count == 0)
        {
            return Fail("検証用のデータを分割できませんでした。録画を延ばしてください。", events.Count, open.Count);
        }

        // Per event only the most closed-looking frame has to be detected (blinks can be a single frame).
        var eventPeaks = heldClosed.GroupBy(item => item.Event)
            .Select(group => group.MaxBy(item => item.Scores.Margin).Scores)
            .ToList();
        var maxOpenMargin = heldOpen.Max(score => score.Margin);
        var minClosedMargin = eventPeaks.Min(score => score.Margin);
        var maxClosedOpenSimilarity = eventPeaks.Max(score => score.Open);
        var minOpenSimilarity = heldOpen.Min(score => score.Open);
        var minClosedCorrelation = eventPeaks.Min(score => score.Closed);
        var marginGap = minClosedMargin - maxOpenMargin;
        var openGap = minOpenSimilarity - maxClosedOpenSimilarity;

        var marginThreshold = marginGap >= 0d ? (maxOpenMargin + minClosedMargin) / 2d : minClosedMargin - 0.01d;
        var openThreshold = openGap >= 0d ? (maxClosedOpenSimilarity + minOpenSimilarity) / 2d : maxClosedOpenSimilarity + 0.01d;
        var closedFloor = Math.Clamp(minClosedCorrelation - options.ClosedFloorMargin, 0.2d, 0.95d);
        var thresholds = new MultiExemplarBlinkThresholds(
            Math.Clamp(marginThreshold, -2d, 2d),
            Math.Clamp(openThreshold, -1d, 1d),
            closedFloor);

        var falsePositives = heldOpen.Count(score => IsClosed(score, thresholds));
        var missedEvents = eventPeaks.Count(score => !IsClosed(score, thresholds));
        var bestGap = Math.Max(marginGap, openGap);
        var passed = falsePositives == 0 && missedEvents == 0 && bestGap >= options.MinimumGap;
        if (passed && Math.Min(marginGap, openGap) < options.RecommendedGap)
        {
            warnings.Add("開眼と閉眼の分離余裕が小さめです。可能なら別の姿勢の瞬きを追加してください。");
        }

        if (events.Count < 3)
        {
            warnings.Add("閉眼パターンが 2 回分です。3 回以上の瞬きを含めると揺れへの追従が安定します。");
        }

        var finalOpen = SelectOpenExemplars(cache, open, options, cancellationToken, out var coverage);
        var finalClosed = SelectClosedExemplars(cache, closed);
        var model = new MultiExemplarBlinkModel(
            MultiExemplarBlinkModel.CurrentSchemaVersion,
            set.InputWidth,
            set.InputHeight,
            set.EyeRegion,
            radius,
            MultiExemplarBlinkModel.CurrentPreprocessing,
            finalOpen.Select(set.Exemplar).ToList(),
            finalClosed.Select(set.Exemplar).ToList(),
            thresholds);
        model.Validate();

        var detected = new List<(int Index, double Score)>();
        using (var finalMatcher = CreateMatcher(set, finalOpen, finalClosed))
        {
            for (var index = 0; index < set.Count; index++)
            {
                var score = finalMatcher.Score(cache.Window(index));
                if (IsClosed(score, thresholds))
                {
                    detected.Add((index, score.Open));
                }
            }
        }

        var summary = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: 開眼 {1} パターン (被覆 {2:F3}) / 閉眼 {3} パターン, 探索 ±{4}px, 検証分割 {5}。"
            + "相関差の余裕 {6:+0.000;-0.000}, 開眼類似度の余裕 {7:+0.000;-0.000}, 検証の誤検出 {8} / 見逃し {9}。"
            + "閾値: 相関差 > {10:F3}, 開眼類似度 < {11:F3}, 閉眼相関 ≥ {12:F3}",
            passed ? "品質検証に合格" : "品質検証に不合格",
            finalOpen.Count,
            coverage,
            finalClosed.Count,
            radius,
            foldCount,
            marginGap,
            openGap,
            falsePositives,
            missedEvents,
            thresholds.ClosedMarginThreshold,
            thresholds.OpenSimilarityThreshold,
            thresholds.ClosedMinimumCorrelation);
        if (!passed)
        {
            warnings.Add("閉眼候補の判定 (閉眼/開眼/除外) を見直すか、録画を延ばして別の瞬きを追加してください。");
        }

        return new BlinkCalibrationResult(
            passed,
            passed ? model : null,
            summary,
            warnings,
            marginGap,
            openGap,
            events.Count,
            open.Count,
            GroupEvents(detected, options.EventGapFrames));

        IEnumerable<int> EventFrames(BlinkCandidateEvent value) =>
            closed.Where(index => index >= value.StartIndex && index <= value.EndIndex);

        BlinkCalibrationResult Fail(string message, int eventCount, int openCount) =>
            new(false, null, message, warnings, double.NaN, double.NaN, eventCount, openCount, []);
    }

    internal static bool IsClosed(MatchScores score, MultiExemplarBlinkThresholds thresholds) =>
        score.Closed >= thresholds.ClosedMinimumCorrelation
        && score.Margin > thresholds.ClosedMarginThreshold
        && score.Open < thresholds.OpenSimilarityThreshold;

    private static int EstimateSearchRadius(
        BlinkSampleSet set,
        IReadOnlyList<int> open,
        BlinkCalibrationOptions options,
        CancellationToken cancellationToken)
    {
        const int probeRadius = MultiExemplarBlinkModel.MaximumSearchRadius;
        using var cache = new WindowCache(set, probeRadius);
        var exemplars = SelectOpenExemplars(cache, open, options with { MaximumOpenExemplars = options.MinimumOpenExemplars }, cancellationToken, out _);
        using var matcher = CreateMatcher(set, exemplars, exemplars.Take(1).ToList());
        var expectedX = set.EyeRegion.X - Math.Max(0, set.EyeRegion.X - probeRadius);
        var expectedY = set.EyeRegion.Y - Math.Max(0, set.EyeRegion.Y - probeRadius);
        var displacements = new List<int>();
        foreach (var index in Subsample(open, options.MaximumPoolSize))
        {
            var score = matcher.Score(cache.Window(index));
            displacements.Add(Math.Max(Math.Abs(score.OpenLocation.X - expectedX), Math.Abs(score.OpenLocation.Y - expectedY)));
        }

        displacements.Sort();
        var p95 = displacements.Count == 0 ? 0 : displacements[(int)Math.Floor((displacements.Count - 1) * 0.95d)];
        return Math.Clamp(p95 + 2, 4, MultiExemplarBlinkModel.MaximumSearchRadius);
    }

    /// <summary>Farthest-point selection: repeatedly adds the open frame worst covered by the current exemplars.</summary>
    private static List<int> SelectOpenExemplars(
        WindowCache cache,
        IReadOnlyList<int> open,
        BlinkCalibrationOptions options,
        CancellationToken cancellationToken,
        out double coverage)
    {
        var pool = Subsample(open, options.MaximumPoolSize);
        var maximum = Math.Clamp(options.MaximumOpenExemplars, 1, MultiExemplarBlinkModel.MaximumOpenExemplars);
        var minimum = Math.Clamp(options.MinimumOpenExemplars, 1, maximum);
        var chosen = new List<int> { pool[0] };
        var cover = Enumerable.Repeat(-1d, pool.Count).ToArray();
        using var result = new Mat();
        UpdateCover(pool[0]);
        while (chosen.Count < Math.Min(maximum, pool.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chosen.Count >= minimum && cover.Min() >= options.OpenCoverageTarget)
            {
                break;
            }

            var worst = Array.IndexOf(cover, cover.Min());
            if (chosen.Contains(pool[worst]))
            {
                break;
            }

            chosen.Add(pool[worst]);
            UpdateCover(pool[worst]);
        }

        coverage = cover.Min();
        return chosen;

        void UpdateCover(int exemplarIndex)
        {
            var exemplar = cache.Exemplar(exemplarIndex);
            for (var i = 0; i < pool.Count; i++)
            {
                var score = pool[i] == exemplarIndex ? 1d : Match(cache.Window(pool[i]), exemplar, result);
                cover[i] = Math.Max(cover[i], score);
            }
        }
    }

    private static List<int> SelectClosedExemplars(WindowCache cache, IReadOnlyList<int> closed)
    {
        if (closed.Count <= MultiExemplarBlinkModel.MaximumClosedExemplars)
        {
            return closed.ToList();
        }

        var chosen = new List<int> { closed[0] };
        using var result = new Mat();
        while (chosen.Count < MultiExemplarBlinkModel.MaximumClosedExemplars)
        {
            var next = closed.Where(index => !chosen.Contains(index))
                .MinBy(index => chosen.Max(other => Match(cache.Window(index), cache.Exemplar(other), result)));
            chosen.Add(next);
        }

        return chosen;
    }

    private static MultiExemplarMatcher CreateMatcher(BlinkSampleSet set, IReadOnlyList<int> open, IReadOnlyList<int> closed) =>
        new(open.Select(set.Exemplar), closed.Select(set.Exemplar), set.EyeRegion.Width, set.EyeRegion.Height);

    private static List<int> Subsample(IReadOnlyList<int> values, int maximum)
    {
        if (values.Count <= maximum)
        {
            return values.ToList();
        }

        var step = (int)Math.Ceiling(values.Count / (double)maximum);
        return values.Where((_, i) => i % step == 0).ToList();
    }

    private static double Match(Mat window, Mat template, Mat result)
    {
        Cv2.MatchTemplate(window, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double max);
        return double.IsFinite(max) ? Math.Clamp(max, -1d, 1d) : -1d;
    }

    private static (double Median, double Mad) MedianMad(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var median = sorted.Length == 0 ? 0d : sorted[sorted.Length / 2];
        var deviations = sorted.Select(value => Math.Abs(value - median)).Order().ToArray();
        return (median, deviations.Length == 0 ? 0d : deviations[deviations.Length / 2]);
    }

    private static List<BlinkCandidateEvent> GroupEvents(IReadOnlyList<(int Index, double Score)> frames, int gap)
    {
        var events = new List<BlinkCandidateEvent>();
        var group = new List<(int Index, double Score)>();
        foreach (var frame in frames.OrderBy(item => item.Index))
        {
            if (group.Count > 0 && frame.Index - group[^1].Index > gap)
            {
                Flush();
            }

            group.Add(frame);
        }

        Flush();
        return events;

        void Flush()
        {
            if (group.Count == 0)
            {
                return;
            }

            var peak = group.MinBy(item => item.Score);
            events.Add(new BlinkCandidateEvent(group[0].Index, group[^1].Index, peak.Index, peak.Score));
            group.Clear();
        }
    }

    private sealed class WindowCache : IDisposable
    {
        private readonly BlinkSampleSet set;
        private readonly int radius;
        private readonly Dictionary<int, Mat> windows = [];
        private readonly Dictionary<int, Mat> exemplars = [];

        public WindowCache(BlinkSampleSet set, int radius)
        {
            this.set = set;
            this.radius = radius;
        }

        public Mat Window(int index)
        {
            if (!windows.TryGetValue(index, out var window))
            {
                window = set.Window(index, radius);
                windows[index] = window;
            }

            return window;
        }

        public Mat Exemplar(int index)
        {
            if (!exemplars.TryGetValue(index, out var exemplar))
            {
                exemplar = MultiExemplarMatcher.FromBgr(set.Exemplar(index), set.EyeRegion.Width, set.EyeRegion.Height);
                exemplars[index] = exemplar;
            }

            return exemplar;
        }

        public void Dispose()
        {
            foreach (var mat in windows.Values.Concat(exemplars.Values))
            {
                mat.Dispose();
            }
        }
    }
}
