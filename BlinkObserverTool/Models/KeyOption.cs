using System.Windows.Input;

namespace BlinkObserverTool.Models;

internal sealed class KeyOption
{
    public required Key Key { get; init; }

    public required string Label { get; init; }
}
