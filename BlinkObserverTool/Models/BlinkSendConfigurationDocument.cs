namespace BlinkObserverTool.Models;

internal sealed class BlinkSendConfigurationDocument
{
    public string SelectedConfigurationName { get; set; } = string.Empty;

    public List<BlinkSendConfiguration> Configurations { get; set; } = [];
}
