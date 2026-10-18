using System.IO;
using BlinkObserverTool.Models;
using BlinkObserverTool.Services;
using Xunit;

namespace BlinkObserverTool.BlinkRecognition.Tests;

public sealed class BlinkSendConfigurationStoreTests
{
    [Fact]
    public void Load_MigratesRemovedInterceptionSendModes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"blink-store-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "blink-send-configurations.json"),
                """
                {
                  "SelectedConfigurationName": "a",
                  "Configurations": [
                    { "Name": "tap", "SendMode": 4 },
                    { "Name": "hold", "SendMode": 5 },
                    { "Name": "vk", "SendMode": 1 },
                    { "Name": "unknown", "SendMode": 99 }
                  ]
                }
                """);

            var document = new BlinkSendConfigurationStore(directory).Load();

            Assert.Equal(
                [KeySendMode.Auto, KeySendMode.ScanCodeHold, KeySendMode.VirtualKeyTap, KeySendMode.Auto],
                document.Configurations.Select(configuration => configuration.SendMode));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
