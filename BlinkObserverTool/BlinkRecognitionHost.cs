using System.IO;
using BlinkObserverTool.BlinkRecognition;
using Recognition.Core;
using Recognition.Infrastructure;
using Recognition.Wpf;

namespace BlinkObserverTool;

internal sealed class BlinkRecognitionHost
{
    public BlinkRecognitionHost(
        IRecognitionPluginCatalog pluginCatalog,
        IRecognitionRunner runner,
        IRecognitionProfileStore profileStore,
        IRecognitionProfileCalibrationService calibrationService,
        RecognitionWorkbenchViewModel viewModel)
    {
        PluginCatalog = pluginCatalog;
        Runner = runner;
        ProfileStore = profileStore;
        CalibrationService = calibrationService;
        ViewModel = viewModel;
    }

    public IRecognitionPluginCatalog PluginCatalog { get; }

    public IRecognitionRunner Runner { get; }

    public IRecognitionProfileStore ProfileStore { get; }

    public IRecognitionProfileCalibrationService CalibrationService { get; }

    public RecognitionWorkbenchViewModel ViewModel { get; }

    public static BlinkRecognitionHost CreateDefault()
    {
        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
        Directory.CreateDirectory(pluginDirectory);

        var catalog = new RecognitionPluginCatalog(
            pluginDirectory,
            [new BlinkRecognitionFactory()]);
        var runner = new RecognitionRunner(catalog);
        var profileStore = new JsonRecognitionProfileStore();
        var calibrationService = new TemplateMatchingProfileCalibrationService();
        var viewModel = new RecognitionWorkbenchViewModel(catalog, runner, profileStore, calibrationService);
        return new BlinkRecognitionHost(catalog, runner, profileStore, calibrationService, viewModel);
    }
}
