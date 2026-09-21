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
        var document = JsonSerializer.Deserialize<BlinkSendConfigurationDocument>(stream, SerializerOptions);
        return document ?? new BlinkSendConfigurationDocument();
    }

    public void Save(BlinkSendConfigurationDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var stream = File.Create(filePath);
        JsonSerializer.Serialize(stream, document, SerializerOptions);
    }
}
