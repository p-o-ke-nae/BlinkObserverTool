using BlinkObserverTool.Models;
using Recognition.Core;

namespace BlinkObserverTool.Services;

internal sealed class RecognitionKeyForwarder
{
    private readonly Action<DateTimeOffset> blinkDetectedHandler;
    private int aggregatedBlinkCount;
    private DateTimeOffset? lastBlinkAt;

    public RecognitionKeyForwarder(Action<DateTimeOffset> blinkDetectedHandler)
    {
        this.blinkDetectedHandler = blinkDetectedHandler;
    }

    public BlinkSendConfiguration CurrentConfiguration { get; set; } = new();

    public event EventHandler<string>? StatusUpdated;

    public void ResetAggregationState()
    {
        aggregatedBlinkCount = 0;
        lastBlinkAt = null;
    }

    public void HandleRecognitionEvent(object? sender, RecognitionCycleResult result)
    {
        if (!result.EventTriggered)
        {
            return;
        }

        var requiredBlinkCount = Math.Max(1, CurrentConfiguration.RequiredBlinkCount);
        var aggregationWindow = Math.Max(50, CurrentConfiguration.BlinkAggregationWindowMilliseconds);
        if (requiredBlinkCount > 1)
        {
            var now = result.Timestamp;
            if (lastBlinkAt is not null && (now - lastBlinkAt.Value).TotalMilliseconds <= aggregationWindow)
            {
                aggregatedBlinkCount = Math.Min(requiredBlinkCount, Math.Max(1, aggregatedBlinkCount) + 1);
                lastBlinkAt = now;
                StatusUpdated?.Invoke(this, $"瞬き集約中: {aggregatedBlinkCount}/{requiredBlinkCount} を同一瞬きとして扱います");
                return;
            }

            aggregatedBlinkCount = 1;
            lastBlinkAt = now;
        }
        else
        {
            ResetAggregationState();
        }

        blinkDetectedHandler(result.Timestamp);
    }
}
