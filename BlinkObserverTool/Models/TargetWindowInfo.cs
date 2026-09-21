namespace BlinkObserverTool.Models;

internal sealed class TargetWindowInfo
{
    public required nint Handle { get; init; }

    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    public required string WindowTitle { get; init; }

    public string DisplayName => $"{ProcessName} ({ProcessId}) - {WindowTitle}";
}
