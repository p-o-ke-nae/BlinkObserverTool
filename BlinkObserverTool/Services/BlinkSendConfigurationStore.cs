using System.IO;
using System.Text.Json;
using BlinkObserverTool.Models;

namespace BlinkObserverTool.Services;

internal sealed class BlinkSendConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string filePath;

    public BlinkSendConfigurationStore(string workspaceDirectory)
    {
        Directory.CreateDirectory(workspaceDirectory);
        filePath = Path.Combine(workspaceDirectory, "blink-send-configurations.json");
    }

    public BlinkSendConfigurationDocument Load()
    {
        if (!File.Exists(filePath))
        {
            return new BlinkSendConfigurationDocument();
        }

        using var stream = File.OpenRead(filePath);
        var document = JsonSerializer.Deserialize<BlinkSendConfigurationDocument>(stream, SerializerOptions)
            ?? new BlinkSendConfigurationDocument();
        foreach (var configuration in document.Configurations)
        {
            configuration.SendMode = MigrateSendMode(configuration.SendMode);
        }

        return document;
    }

    // 4/5 were the removed Interception modes; map them to the closest SendInput mode.
    internal static KeySendMode MigrateSendMode(KeySendMode mode)
    {
        return (int)mode switch
        {
            4 => KeySendMode.Auto,
            5 => KeySendMode.ScanCodeHold,
            _ when Enum.IsDefined(mode) => mode,
            _ => KeySendMode.Auto
        };
    }

    public void Save(BlinkSendConfigurationDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var stream = File.Create(filePath);
        JsonSerializer.Serialize(stream, document, SerializerOptions);
    }
}
