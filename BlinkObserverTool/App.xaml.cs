using System.IO;
using System.Windows;
using BlinkObserverTool.ProfileSync;
using BlinkObserverTool.Services;

namespace BlinkObserverTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var workspaceDirectory = InitializeWorkspaceDirectory();
            var host = BlinkRecognitionHost.CreateDefault();
            var targetWindowFinder = new TargetWindowFinder();
            var keyboardSendService = new KeyboardSendService(targetWindowFinder);
            var physicalKeyboardSendService = new PhysicalKeyboardSendService(targetWindowFinder);
            var configurationStore = new BlinkSendConfigurationStore(Path.Combine(workspaceDirectory, "BlinkConfigs"));

            var mainWindow = new MainWindow(host, configurationStore, targetWindowFinder, keyboardSendService, physicalKeyboardSendService);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            MessageBox.Show(exception.ToString(), "Blink Observer Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static string InitializeWorkspaceDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var workspaceDirectory = Path.Combine(localAppData, "pokenae", "BlinkObserverTool");
        Directory.CreateDirectory(workspaceDirectory);

        SynchronizeDefaultProfiles(workspaceDirectory);
        MigrateDirectoryIfNeeded("BlinkConfigs", workspaceDirectory);

        Environment.CurrentDirectory = workspaceDirectory;
        return workspaceDirectory;
    }

    private static void SynchronizeDefaultProfiles(string workspaceDirectory)
    {
        var packagedDefaultsDirectory = Path.Combine(AppContext.BaseDirectory, "DefaultProfiles");
        if (!Directory.Exists(packagedDefaultsDirectory))
        {
            return;
        }

        var profilesDirectory = Path.Combine(workspaceDirectory, "Profiles");
        var stateFilePath = Path.Combine(workspaceDirectory, "default-profiles-state.json");
        new DefaultProfileSynchronizer().Synchronize(packagedDefaultsDirectory, workspaceDirectory, stateFilePath);
    }

    private static void MigrateDirectoryIfNeeded(string directoryName, string workspaceDirectory)
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, directoryName);
        var destinationDirectory = Path.Combine(workspaceDirectory, directoryName);
        if (!Directory.Exists(sourceDirectory) || Directory.Exists(destinationDirectory))
        {
            return;
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationPathDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationPathDirectory))
            {
                Directory.CreateDirectory(destinationPathDirectory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }
}
