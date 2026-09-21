using System.Windows.Input;

namespace BlinkObserverTool.Models;

internal sealed class BlinkSendConfiguration
{
    public string Name { get; set; } = "Default";

    public string RecognitionProfileName { get; set; } = "Default";

    public string TargetProcessName { get; set; } = string.Empty;

    public string TargetWindowTitleContains { get; set; } = string.Empty;

    public Key SendKey { get; set; } = Key.LeftShift;

    public bool SendModifierAsCommonKey { get; set; }

    public KeySendMode SendMode { get; set; } = KeySendMode.Auto;

    public KeySendTrigger SendTrigger { get; set; } = KeySendTrigger.Press;

    public int KeyHoldMilliseconds { get; set; } = 50;

    public int RequiredBlinkCount { get; set; } = 1;

    public int BlinkAggregationWindowMilliseconds { get; set; } = 350;

    public bool BringTargetToForeground { get; set; } = true;
}
