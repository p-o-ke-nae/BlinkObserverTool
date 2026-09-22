using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlinkObserverTool.BlinkRecognition;
using BlinkObserverTool.Models;
using BlinkObserverTool.Services;
using Recognition.Core;
using Recognition.Wpf;

namespace BlinkObserverTool;

public partial class MainWindow : Window
{
    private readonly BlinkRecognitionHost host;
    private readonly BlinkSendConfigurationStore configurationStore;
    private readonly TargetWindowFinder targetWindowFinder;
    private readonly KeyboardSendService keyboardSendService;
    private readonly PhysicalKeyboardSendService physicalKeyboardSendService;
    private readonly RecognitionKeyForwarder recognitionKeyForwarder;
    private readonly ObservableCollection<BlinkSendConfiguration> configurations = [];
    private readonly ObservableCollection<KeyOption> availableKeys = CreateKeyOptions();
    private readonly ObservableCollection<KeySendModeOption> availableSendModes = CreateSendModeOptions();
    private readonly ObservableCollection<KeySendTriggerOption> availableSendTriggers = CreateSendTriggerOptions();
    private readonly ObservableCollection<BlinkNotificationSoundOption> availableNotificationSounds = CreateNotificationSoundOptions();
    private readonly ObservableCollection<TargetWindowInfo> windowCandidates = [];
    private readonly ObservableCollection<string> toolLogEntries = [];
    private RecognitionWorkbenchWindow? recognitionWorkbenchWindow;
    private BlinkSetupWindow? blinkSetupWindow;
    private bool suppressConfigurationSelectionChanged;
    private bool suppressObservationModeChange;
    private bool uiReady;
    private bool isCapturingSendKey;
    private ObservationActionMode currentObservationMode = ObservationActionMode.KeySend;
    private string selectedRecognitionProfileName = "Default";

    internal MainWindow(
        BlinkRecognitionHost host,
        BlinkSendConfigurationStore configurationStore,
        TargetWindowFinder targetWindowFinder,
        KeyboardSendService keyboardSendService,
        PhysicalKeyboardSendService physicalKeyboardSendService)
    {
        InitializeComponent();
        this.host = host;
        this.configurationStore = configurationStore;
        this.targetWindowFinder = targetWindowFinder;
        this.keyboardSendService = keyboardSendService;
        this.physicalKeyboardSendService = physicalKeyboardSendService;
        recognitionKeyForwarder = new RecognitionKeyForwarder(HandleBlinkDetected);

        DataContext = host.ViewModel;
        ConfigurationComboBox.ItemsSource = configurations;
        SendKeyComboBox.ItemsSource = availableKeys;
        SendModeComboBox.ItemsSource = availableSendModes;
        SendTriggerComboBox.ItemsSource = availableSendTriggers;
        VerificationSoundComboBox.ItemsSource = availableNotificationSounds;
        WindowCandidatesComboBox.ItemsSource = windowCandidates;
        ToolLogListBox.ItemsSource = toolLogEntries;
        SendKeyComboBox.SelectedItem = availableKeys.First(option => option.Key == Key.LeftShift);
        SendModeComboBox.SelectedItem = availableSendModes.First(option => option.Mode == KeySendMode.Auto);
        SendTriggerComboBox.SelectedItem = availableSendTriggers.First(option => option.Trigger == KeySendTrigger.Press);
        VerificationSoundComboBox.SelectedItem = availableNotificationSounds.First(option => option.Sound == BlinkNotificationSound.Asterisk);
        VerificationSoundCheckBox.IsChecked = false;
        SendModifierAsCommonKeyCheckBox.IsChecked = false;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        uiReady = true;
        UpdateDriverStatus();
        UpdateObservationModeUi();

        host.ViewModel.RecognitionEventRaised += recognitionKeyForwarder.HandleRecognitionEvent;
        recognitionKeyForwarder.StatusUpdated += (_, message) => Dispatcher.Invoke(() => SetKeySendStatus(message));

        _ = LoadConfigurationsAsync();
        RefreshWindowCandidates();
        UpdateCurrentConfigurationSummary();
    }

    protected override async void OnClosed(EventArgs e)
    {
        host.ViewModel.RecognitionEventRaised -= recognitionKeyForwarder.HandleRecognitionEvent;
        AppendToolLog(physicalKeyboardSendService.ReleaseAllSimulatedKeys());
        physicalKeyboardSendService.Dispose();
        if (host.ViewModel.IsRunning)
        {
            await host.ViewModel.StopAsync();
        }

        if (recognitionWorkbenchWindow is not null)
        {
            recognitionWorkbenchWindow.Close();
            recognitionWorkbenchWindow = null;
        }

        if (blinkSetupWindow is not null)
        {
            blinkSetupWindow.Close();
            blinkSetupWindow = null;
        }

        base.OnClosed(e);
    }

    private async void ReloadConfigurations_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(async () =>
        {
            await LoadConfigurationsAsync();
            SetKeySendStatus("保存済み設定を更新しました。");
            AppendToolLog("保存済み設定を更新しました。");
        });
    }

    private async void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(async () =>
        {
            var configuration = BuildCurrentConfiguration();
            var existing = configurations.FirstOrDefault(item => string.Equals(item.Name, configuration.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                configurations.Add(configuration);
            }
            else
            {
                CopyConfiguration(existing, configuration);
            }

            SaveConfigurations(configuration.Name);
            await SelectConfigurationAsync(configuration.Name);
            UpdateCurrentConfigurationSummary();
            SetKeySendStatus($"設定を保存しました: {configuration.Name}");
            AppendToolLog($"設定を保存しました: {configuration.Name}");
        });
    }

    private async void DeleteConfiguration_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(async () =>
        {
            if (ConfigurationComboBox.SelectedItem is not BlinkSendConfiguration selected)
            {
                throw new InvalidOperationException("削除する設定を選択してください。");
            }

            configurations.Remove(selected);
            SaveConfigurations(string.Empty);
            if (configurations.Count > 0)
            {
                await SelectConfigurationAsync(configurations[0].Name);
            }
            else
            {
                ClearConfigurationInputs();
            }

            UpdateCurrentConfigurationSummary();
            SetKeySendStatus($"設定を削除しました: {selected.Name}");
            AppendToolLog($"設定を削除しました: {selected.Name}");
        });
    }

    private async void ConfigurationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressConfigurationSelectionChanged || ConfigurationComboBox.SelectedItem is not BlinkSendConfiguration configuration)
        {
            return;
        }

        await ExecuteToolActionAsync(async () =>
        {
            await ApplyConfigurationAsync(configuration);
            AppendToolLog($"設定を読み込みました: {configuration.Name}");
        });
    }

    private async void ReloadProfileList_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRecognitionActionAsync(() =>
        {
            host.ViewModel.RefreshProfileList();
            return Task.CompletedTask;
        });
    }

    private async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRecognitionActionAsync(async () =>
        {
            await EnsureSelectedProfileLoadedAsync();
            UpdateBlinkModelStatus();
            selectedRecognitionProfileName = host.ViewModel.SelectedProfileEntry?.Name ?? selectedRecognitionProfileName;
            UpdateCurrentConfigurationSummary();
            AppendToolLog($"認識プロファイルを読み込みました: {selectedRecognitionProfileName}");
        });
    }

    private async void StartObservation_Click(object sender, RoutedEventArgs e)
    {
        BlinkSendConfiguration configuration;
        try
        {
            configuration = BuildCurrentConfiguration(allowIncompleteKeySendSettings: currentObservationMode == ObservationActionMode.Verification);
        }
        catch (Exception exception)
        {
            HandleToolException(exception);
            return;
        }

        await ExecuteRecognitionActionAsync(async () =>
        {
            await EnsureSelectedProfileLoadedAsync();
            ValidateFixedBlinkModelForObservation();
            UpdateBlinkModelStatus();
            recognitionKeyForwarder.CurrentConfiguration = configuration;
            recognitionKeyForwarder.ResetAggregationState();
            await host.ViewModel.StartAsync();
            selectedRecognitionProfileName = configuration.RecognitionProfileName;
            SetKeySendStatus(
                currentObservationMode == ObservationActionMode.Verification
                    ? "観測を開始しました。検証モードで瞬き検出をログ表示します。"
                    : "観測を開始しました。認識イベント発火時にキー送信します。");
            AppendToolLog(
                currentObservationMode == ObservationActionMode.Verification
                    ? $"観測開始: {configuration.RecognitionProfileName} / 検証のみ"
                    : $"観測開始: {configuration.RecognitionProfileName} / {configuration.SendKey}");
            UpdateCurrentConfigurationSummary();
        });
    }

    private async void StopObservation_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRecognitionActionAsync(async () =>
        {
            await host.ViewModel.StopAsync();
            recognitionKeyForwarder.ResetAggregationState();
            AppendToolLog(physicalKeyboardSendService.ReleaseAllSimulatedKeys());
            SetKeySendStatus("観測を停止しました。");
            AppendToolLog("観測を停止しました。");
        });
    }

    private void ObservationModeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!uiReady || suppressObservationModeChange)
        {
            return;
        }

        var requestedMode = VerificationModeRadioButton.IsChecked == true
            ? ObservationActionMode.Verification
            : ObservationActionMode.KeySend;
        if (requestedMode == currentObservationMode)
        {
            return;
        }

        if (requestedMode == ObservationActionMode.KeySend && host.ViewModel.IsRunning)
        {
            try
            {
                recognitionKeyForwarder.CurrentConfiguration = BuildCurrentConfiguration();
            }
            catch (Exception exception)
            {
                suppressObservationModeChange = true;
                VerificationModeRadioButton.IsChecked = true;
                suppressObservationModeChange = false;
                UpdateObservationModeUi();
                UpdateCurrentConfigurationSummary();
                SetKeySendStatus($"キー送信モードへ切り替えできません: {exception.Message}");
                AppendToolLog($"キー送信モード切替失敗: {exception.Message}");
                return;
            }
        }

        currentObservationMode = requestedMode;
        if (host.ViewModel.IsRunning)
        {
            recognitionKeyForwarder.CurrentConfiguration = BuildCurrentConfiguration(
                allowIncompleteKeySendSettings: currentObservationMode == ObservationActionMode.Verification);
        }

        recognitionKeyForwarder.ResetAggregationState();
        UpdateObservationModeUi();
        UpdateCurrentConfigurationSummary();

        var message = currentObservationMode == ObservationActionMode.Verification
            ? "観測モードを切り替えました: 検証のみ (ログ / 通知音)"
            : "観測モードを切り替えました: 瞬き計算ツールへ入力";
        SetKeySendStatus(message);
        AppendToolLog(message);
    }

    private void VerificationNotificationSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!uiReady)
        {
            return;
        }

        UpdateObservationModeUi();
        UpdateCurrentConfigurationSummary();
    }

    private async void RefreshWindowCandidates_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(() =>
        {
            RefreshWindowCandidates();
            SetKeySendStatus("候補ウィンドウを更新しました。");
            AppendToolLog("候補ウィンドウを更新しました。");
            return Task.CompletedTask;
        });
    }

    private async void ApplyWindowCandidate_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(() =>
        {
            if (WindowCandidatesComboBox.SelectedItem is not TargetWindowInfo windowInfo)
            {
                throw new InvalidOperationException("反映する候補ウィンドウを選択してください。");
            }

            TargetProcessNameTextBox.Text = windowInfo.ProcessName;
            TargetWindowTitleTextBox.Text = windowInfo.WindowTitle;
            UpdateCurrentConfigurationSummary();
            AppendToolLog($"候補を反映しました: {windowInfo.DisplayName}");
            return Task.CompletedTask;
        });
    }

    private async void SendTestKey_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(() =>
        {
            var configuration = BuildCurrentConfiguration();
            var target = SendConfiguredKey(configuration);
            SetKeySendStatus($"送信成功: {configuration.SendKey} -> {target}");
            AppendToolLog($"送信テスト成功: {configuration.SendKey} -> {target}");
            UpdateCurrentConfigurationSummary();
            return Task.CompletedTask;
        });
    }

    private void CaptureSendKey_Click(object sender, RoutedEventArgs e)
    {
        isCapturingSendKey = true;
        if (CaptureSendKeyStatusTextBlock is not null)
        {
            CaptureSendKeyStatusTextBlock.Text = "設定したいキーをこのウィンドウ上で1回押してください。Esc で取消。";
        }

        AppendToolLog("送信キー入力待ちを開始しました。");
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private async void InstallPhysicalDriver_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolActionAsync(() =>
        {
            var message = physicalKeyboardSendService.InstallDriver();
            UpdateDriverStatus();
            SetKeySendStatus(message);
            AppendToolLog(message);
            return Task.CompletedTask;
        });
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is ProfileListEntry entry)
        {
            selectedRecognitionProfileName = entry.Name;
        }

        UpdateCurrentConfigurationSummary();
        BlinkModelStatusTextBlock.Text = "モデル状態: プロファイルを読み込んでください。";
    }

    private void ConfigurationInput_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCurrentConfigurationSummary();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!isCapturingSendKey)
        {
            return;
        }

        e.Handled = true;
        var key = ResolveCapturedKey(e);
        if (key == Key.Escape)
        {
            isCapturingSendKey = false;
            if (CaptureSendKeyStatusTextBlock is not null)
            {
                CaptureSendKeyStatusTextBlock.Text = "送信キー設定を取り消しました。";
            }

            AppendToolLog("送信キー入力待ちを取り消しました。");
            return;
        }

        var selectedOption = availableKeys.FirstOrDefault(option => option.Key == key);
        if (selectedOption is null)
        {
            selectedOption = new KeyOption
            {
                Key = key,
                Label = key.ToString()
            };
            availableKeys.Add(selectedOption);
        }

        SendKeyComboBox.SelectedItem = selectedOption;
        isCapturingSendKey = false;
        if (CaptureSendKeyStatusTextBlock is not null)
        {
            CaptureSendKeyStatusTextBlock.Text = $"送信キーを {selectedOption.Label} に設定しました。";
        }

        AppendToolLog($"送信キーを押下から設定しました: {selectedOption.Label}");
        UpdateCurrentConfigurationSummary();
    }

    private void OpenDetailedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (recognitionWorkbenchWindow is null)
        {
            recognitionWorkbenchWindow = new RecognitionWorkbenchWindow(host.ViewModel)
            {
                Owner = this
            };
            recognitionWorkbenchWindow.Closed += (_, _) => recognitionWorkbenchWindow = null;
        }

        if (!recognitionWorkbenchWindow.IsVisible)
        {
            recognitionWorkbenchWindow.Show();
        }

        recognitionWorkbenchWindow.Activate();
    }

    private void OpenBlinkSetup_Click(object sender, RoutedEventArgs e)
    {
        if (blinkSetupWindow is null)
        {
            blinkSetupWindow = new BlinkSetupWindow(host.ViewModel)
            {
                Owner = this
            };
            blinkSetupWindow.Closed += (_, _) =>
            {
                blinkSetupWindow = null;
                UpdateBlinkModelStatus();
            };
        }

        if (!blinkSetupWindow.IsVisible)
        {
            blinkSetupWindow.Show();
        }

        blinkSetupWindow.Activate();
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (!host.ViewModel.TryGetPreviewFrameSize(out var frameSize))
        {
            return;
        }

        var pointer = e.GetPosition(element);
        if (!TryMapToPreviewFrame(pointer, element.RenderSize, frameSize, out var framePoint))
        {
            host.ViewModel.UpdatePreviewCursor(null);
            return;
        }

        host.ViewModel.UpdatePreviewCursor(framePoint);
    }

    private void PreviewImage_MouseLeave(object sender, MouseEventArgs e)
    {
        host.ViewModel.UpdatePreviewCursor(null);
    }

    private async Task LoadConfigurationsAsync()
    {
        var document = configurationStore.Load();
        configurations.Clear();
        foreach (var configuration in document.Configurations.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            configurations.Add(configuration);
        }

        if (configurations.Count == 0)
        {
            var defaultConfiguration = new BlinkSendConfiguration();
            configurations.Add(defaultConfiguration);
            SaveConfigurations(defaultConfiguration.Name);
        }

        var selectedName = string.IsNullOrWhiteSpace(document.SelectedConfigurationName)
            ? configurations[0].Name
            : document.SelectedConfigurationName;
        await SelectConfigurationAsync(selectedName);
    }

    private void SaveConfigurations(string selectedConfigurationName)
    {
        configurationStore.Save(new BlinkSendConfigurationDocument
        {
            SelectedConfigurationName = selectedConfigurationName,
            Configurations = [.. configurations.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)]
        });
    }

    private async Task SelectConfigurationAsync(string configurationName)
    {
        suppressConfigurationSelectionChanged = true;
        try
        {
            ConfigurationComboBox.SelectedItem = configurations.FirstOrDefault(item => string.Equals(item.Name, configurationName, StringComparison.OrdinalIgnoreCase))
                ?? configurations.FirstOrDefault();
        }
        finally
        {
            suppressConfigurationSelectionChanged = false;
        }

        if (ConfigurationComboBox.SelectedItem is BlinkSendConfiguration configuration)
        {
            await ApplyConfigurationAsync(configuration);
        }
    }

    private async Task ApplyConfigurationAsync(BlinkSendConfiguration configuration)
    {
        ConfigurationNameTextBox.Text = configuration.Name;
        TargetProcessNameTextBox.Text = configuration.TargetProcessName;
        TargetWindowTitleTextBox.Text = configuration.TargetWindowTitleContains;
        BringToFrontCheckBox.IsChecked = configuration.BringTargetToForeground;
        SendKeyComboBox.SelectedItem = availableKeys.FirstOrDefault(option => option.Key == configuration.SendKey)
            ?? availableKeys.First(option => option.Key == Key.LeftShift);
        SendModifierAsCommonKeyCheckBox.IsChecked = configuration.SendModifierAsCommonKey;
        SendModeComboBox.SelectedItem = availableSendModes.FirstOrDefault(option => option.Mode == configuration.SendMode)
            ?? availableSendModes.First(option => option.Mode == KeySendMode.Auto);
        SendTriggerComboBox.SelectedItem = availableSendTriggers.FirstOrDefault(option => option.Trigger == configuration.SendTrigger)
            ?? availableSendTriggers.First(option => option.Trigger == KeySendTrigger.Press);
        KeyHoldMillisecondsTextBox.Text = configuration.KeyHoldMilliseconds.ToString();
        RequiredBlinkCountTextBox.Text = configuration.RequiredBlinkCount.ToString();
        BlinkAggregationWindowTextBox.Text = configuration.BlinkAggregationWindowMilliseconds.ToString();
        selectedRecognitionProfileName = configuration.RecognitionProfileName;

        var profileEntry = host.ViewModel.AvailableProfiles.FirstOrDefault(entry => string.Equals(entry.Name, configuration.RecognitionProfileName, StringComparison.OrdinalIgnoreCase));
        if (profileEntry is not null)
        {
            host.ViewModel.SelectedProfileEntry = profileEntry;
            await host.ViewModel.LoadProfileAsync();
            UpdateBlinkModelStatus();
        }
        else
        {
            host.ViewModel.SelectedProfileEntry = null;
            SetKeySendStatus($"認識プロファイル '{configuration.RecognitionProfileName}' が見つかりません。必要なら自動認識設定を開いて作成してください。");
            AppendToolLog($"認識プロファイル未検出: {configuration.RecognitionProfileName}");
        }

        recognitionKeyForwarder.CurrentConfiguration = configuration;
        recognitionKeyForwarder.ResetAggregationState();
        UpdateCurrentConfigurationSummary();
    }

    private async Task EnsureSelectedProfileLoadedAsync()
    {
        if (host.ViewModel.SelectedProfileEntry is null)
        {
            throw new InvalidOperationException("使用する認識プロファイルを選択してください。");
        }

        selectedRecognitionProfileName = host.ViewModel.SelectedProfileEntry.Name;
        await host.ViewModel.LoadProfileAsync();
        UpdateBlinkModelStatus();
    }

    private void ValidateFixedBlinkModelForObservation()
    {
        var selected = host.ViewModel.RecognitionMethod.SelectedOption;
        if (!string.Equals(selected?.Descriptor.Id, FixedBlinkRecognitionFactory.ComponentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parameters = GetRecognitionParameters();
        var model = FixedBlinkModel.Load(parameters);
        if (parameters.GetRoi("Eye") != model.EyeRegion)
        {
            throw new FixedBlinkModelException(
                "blink-model-roi-mismatch",
                "目 ROI が固定学習モデルの採取時から変更されています。瞬き観測セットアップをやり直してください。");
        }

    }

    private void UpdateBlinkModelStatus()
    {
        if (!uiReady || BlinkModelStatusTextBlock is null)
        {
            return;
        }

        var componentId = host.ViewModel.RecognitionMethod.SelectedOption?.Descriptor.Id;
        BlinkModelStatusTextBlock.Text = componentId switch
        {
            FixedBlinkRecognitionFactory.ComponentId =>
                $"モデル状態: {FixedBlinkRecognitionFactory.GetModelStatus(GetRecognitionParameters())}",
            BlinkRecognitionFactory.ComponentId =>
                "モデル状態: 互換の瞬き特徴認識です。固定モデルを使うには専用セットアップが必要です。",
            _ => "モデル状態: 固定瞬きモデルを使用しない認識方式です。"
        };
    }

    private Dictionary<string, string> GetRecognitionParameters() =>
        host.ViewModel.RecognitionMethod.Parameters.ToDictionary(
            parameter => parameter.Definition.Key,
            parameter => parameter.Value,
            StringComparer.OrdinalIgnoreCase);

    private BlinkSendConfiguration BuildCurrentConfiguration(bool allowIncompleteKeySendSettings = false)
    {
        var name = allowIncompleteKeySendSettings
            ? NormalizeOptionalValue(ConfigurationNameTextBox.Text, "Default")
            : NormalizeRequiredValue(ConfigurationNameTextBox.Text, "設定名");
        var selectedProfile = ProfileComboBox.SelectedItem as ProfileListEntry
            ?? throw new InvalidOperationException("使用する認識プロファイルを選択してください。");
        var processName = allowIncompleteKeySendSettings
            ? (TargetProcessNameTextBox.Text ?? string.Empty).Trim()
            : NormalizeRequiredValue(TargetProcessNameTextBox.Text, "対象プロセス名");
        var selectedKey = (SendKeyComboBox.SelectedItem as KeyOption)?.Key ?? Key.LeftShift;
        var sendMode = (SendModeComboBox.SelectedItem as KeySendModeOption)?.Mode ?? KeySendMode.Auto;
        var sendTrigger = (SendTriggerComboBox.SelectedItem as KeySendTriggerOption)?.Trigger ?? KeySendTrigger.Press;
        var keyHoldMilliseconds = Math.Max(1, ParseInt32OrDefault(KeyHoldMillisecondsTextBox.Text, 50));
        var requiredBlinkCount = Math.Max(1, ParseInt32OrDefault(RequiredBlinkCountTextBox.Text, 1));
        var blinkAggregationWindowMilliseconds = Math.Max(50, ParseInt32OrDefault(BlinkAggregationWindowTextBox.Text, 350));

        return new BlinkSendConfiguration
        {
            Name = name,
            RecognitionProfileName = selectedProfile.Name,
            TargetProcessName = processName,
            TargetWindowTitleContains = (TargetWindowTitleTextBox.Text ?? string.Empty).Trim(),
            SendKey = selectedKey,
            SendModifierAsCommonKey = SendModifierAsCommonKeyCheckBox.IsChecked == true,
            SendMode = sendMode,
            SendTrigger = sendTrigger,
            KeyHoldMilliseconds = keyHoldMilliseconds,
            RequiredBlinkCount = requiredBlinkCount,
            BlinkAggregationWindowMilliseconds = blinkAggregationWindowMilliseconds,
            BringTargetToForeground = BringToFrontCheckBox.IsChecked == true
        };
    }

    private void RefreshWindowCandidates()
    {
        var selected = WindowCandidatesComboBox.SelectedItem as TargetWindowInfo;
        windowCandidates.Clear();
        foreach (var candidate in targetWindowFinder.GetCandidateWindows())
        {
            windowCandidates.Add(candidate);
        }

        if (selected is not null)
        {
            WindowCandidatesComboBox.SelectedItem = windowCandidates.FirstOrDefault(item => item.Handle == selected.Handle);
        }
    }

    private void ClearConfigurationInputs()
    {
        ConfigurationNameTextBox.Text = string.Empty;
        TargetProcessNameTextBox.Text = string.Empty;
        TargetWindowTitleTextBox.Text = string.Empty;
        BringToFrontCheckBox.IsChecked = true;
        SendKeyComboBox.SelectedItem = availableKeys.First(option => option.Key == Key.LeftShift);
        SendModifierAsCommonKeyCheckBox.IsChecked = false;
        SendModeComboBox.SelectedItem = availableSendModes.First(option => option.Mode == KeySendMode.Auto);
        SendTriggerComboBox.SelectedItem = availableSendTriggers.First(option => option.Trigger == KeySendTrigger.Press);
        KeyHoldMillisecondsTextBox.Text = "50";
        RequiredBlinkCountTextBox.Text = "1";
        BlinkAggregationWindowTextBox.Text = "350";
        selectedRecognitionProfileName = host.ViewModel.SelectedProfileEntry?.Name ?? "Default";
        UpdateCurrentConfigurationSummary();
    }

    private void UpdateCurrentConfigurationSummary()
    {
        if (!uiReady
            || CurrentConfigurationTextBlock is null
            || ConfigurationNameTextBox is null
            || TargetProcessNameTextBox is null
            || TargetWindowTitleTextBox is null
            || SendKeyComboBox is null
            || CaptureSendKeyStatusTextBlock is null
            || SendModifierAsCommonKeyCheckBox is null
            || SendModeComboBox is null
            || SendTriggerComboBox is null
            || DriverStatusTextBlock is null
            || KeyHoldMillisecondsTextBox is null
            || RequiredBlinkCountTextBox is null
            || BlinkAggregationWindowTextBox is null
            || ProfileComboBox is null
            || VerificationSoundCheckBox is null
            || VerificationSoundComboBox is null)
        {
            return;
        }

        var keyLabel = (SendKeyComboBox.SelectedItem as KeyOption)?.Label ?? "Shift";
        var profileName = (ProfileComboBox.SelectedItem as ProfileListEntry)?.Name ?? selectedRecognitionProfileName;
        var observationModeLabel = currentObservationMode == ObservationActionMode.Verification
            ? "検証のみ (非保存)"
            : "瞬き計算ツールへ入力 (非保存)";
        var notificationSoundLabel = VerificationSoundCheckBox.IsChecked == true
            ? (VerificationSoundComboBox.SelectedItem as BlinkNotificationSoundOption)?.Label ?? "通知音"
            : "オフ";
        CurrentConfigurationTextBlock.Text =
            $"観測モード: {observationModeLabel}\r\n" +
            $"通知音: {notificationSoundLabel}\r\n" +
            $"設定名: {ConfigurationNameTextBox.Text}\r\n" +
            $"認識プロファイル: {profileName}\r\n" +
            $"送信キー: {keyLabel}\r\n" +
            $"修飾キー送信: {(SendModifierAsCommonKeyCheckBox.IsChecked == true ? "共通キー" : "左右別キー")}\r\n" +
            $"送信方式: {(SendModeComboBox.SelectedItem as KeySendModeOption)?.Label ?? "Auto"}\r\n" +
            $"送信契機: {(SendTriggerComboBox.SelectedItem as KeySendTriggerOption)?.Label ?? "押下+離上"}\r\n" +
            $"押下時間: {KeyHoldMillisecondsTextBox.Text}ms\r\n" +
            $"同一瞬きとしてまとめる回数: {RequiredBlinkCountTextBox.Text}\r\n" +
            $"集約時間: {BlinkAggregationWindowTextBox.Text}ms\r\n" +
            $"対象プロセス: {TargetProcessNameTextBox.Text}\r\n" +
            $"対象ウィンドウ: {TargetWindowTitleTextBox.Text}";
    }

    private async Task ExecuteRecognitionActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await host.ViewModel.HandleUserVisibleErrorAsync("Blink Observer Recognition", exception);
            MessageBox.Show(
                host.ViewModel.GetUserFacingErrorMessage(),
                "Blink Observer Recognition",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ExecuteToolActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            HandleToolException(exception);
        }
    }

    private void HandleToolException(Exception exception)
    {
        AppendToolLog(physicalKeyboardSendService.ReleaseAllSimulatedKeys());
        Console.Error.WriteLine(exception);
        SetKeySendStatus($"本体エラー: {exception.Message}");
        AppendToolLog($"本体エラー: {exception}");
        MessageBox.Show(
            exception.Message,
            "Blink Observer Tool",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        UpdateDriverStatus();
    }

    private void HandleBlinkDetected(DateTimeOffset timestamp)
    {
        Dispatcher.Invoke(() =>
        {
            if (currentObservationMode == ObservationActionMode.Verification)
            {
                var verificationMessage = $"瞬き検出: {timestamp:HH:mm:ss.fff}";
                SetKeySendStatus($"検証検出: {timestamp:HH:mm:ss.fff}");
                AppendToolLog(verificationMessage);
                PlayVerificationSoundIfEnabled();
                return;
            }

            try
            {
                var targetLabel = SendConfiguredKey(recognitionKeyForwarder.CurrentConfiguration);
                SetKeySendStatus($"送信成功: {targetLabel}");
                AppendToolLog($"送信成功: {targetLabel}");
            }
            catch (Exception exception)
            {
                SetKeySendStatus($"送信失敗: {exception.Message}");
                AppendToolLog($"送信失敗: {exception.Message}");
            }
        });
    }

    private void PlayVerificationSoundIfEnabled()
    {
        if (VerificationSoundCheckBox.IsChecked != true)
        {
            return;
        }

        var selectedSound = (VerificationSoundComboBox.SelectedItem as BlinkNotificationSoundOption)?.Sound
            ?? BlinkNotificationSound.Asterisk;
        ResolveNotificationSound(selectedSound).Play();
    }

    private void UpdateObservationModeUi()
    {
        if (!uiReady)
        {
            return;
        }

        var isVerificationMode = currentObservationMode == ObservationActionMode.Verification;
        KeySendSettingsGroupBox.IsEnabled = !isVerificationMode;
        VerificationOptionsPanel.IsEnabled = isVerificationMode;
        VerificationSoundComboBox.IsEnabled = isVerificationMode && VerificationSoundCheckBox.IsChecked == true;
        ObservationModeHintTextBlock.Text = isVerificationMode
            ? "キー送信は行わず、瞬き検出の記録と任意の通知音だけを行います。"
            : "認識した瞬きを瞬き計算ツールへのキー入力として送信します。";
    }

    private void SetKeySendStatus(string message)
    {
        if (!uiReady || KeySendStatusTextBlock is null)
        {
            return;
        }

        KeySendStatusTextBlock.Text = message;
    }

    private void AppendToolLog(string message)
    {
        toolLogEntries.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
    }

    private static SystemSound ResolveNotificationSound(BlinkNotificationSound sound)
    {
        return sound switch
        {
            BlinkNotificationSound.Beep => SystemSounds.Beep,
            BlinkNotificationSound.Exclamation => SystemSounds.Exclamation,
            BlinkNotificationSound.Question => SystemSounds.Question,
            _ => SystemSounds.Asterisk
        };
    }

    private string SendConfiguredKey(BlinkSendConfiguration configuration)
    {
        var sendMode = ResolveEffectiveSendMode(configuration);
        var target = sendMode is KeySendMode.InterceptionTap or KeySendMode.InterceptionHold
            ? physicalKeyboardSendService.SendKey(new BlinkSendConfiguration
            {
                Name = configuration.Name,
                RecognitionProfileName = configuration.RecognitionProfileName,
                TargetProcessName = configuration.TargetProcessName,
                TargetWindowTitleContains = configuration.TargetWindowTitleContains,
                SendKey = configuration.SendKey,
                SendModifierAsCommonKey = configuration.SendModifierAsCommonKey,
                SendMode = sendMode,
                SendTrigger = configuration.SendTrigger,
                KeyHoldMilliseconds = configuration.KeyHoldMilliseconds,
                RequiredBlinkCount = configuration.RequiredBlinkCount,
                BlinkAggregationWindowMilliseconds = configuration.BlinkAggregationWindowMilliseconds,
                BringTargetToForeground = configuration.BringTargetToForeground
            })
            : keyboardSendService.SendKey(new BlinkSendConfiguration
            {
                Name = configuration.Name,
                RecognitionProfileName = configuration.RecognitionProfileName,
                TargetProcessName = configuration.TargetProcessName,
                TargetWindowTitleContains = configuration.TargetWindowTitleContains,
                SendKey = configuration.SendKey,
                SendModifierAsCommonKey = configuration.SendModifierAsCommonKey,
                SendMode = sendMode,
                SendTrigger = configuration.SendTrigger,
                KeyHoldMilliseconds = configuration.KeyHoldMilliseconds,
                RequiredBlinkCount = configuration.RequiredBlinkCount,
                BlinkAggregationWindowMilliseconds = configuration.BlinkAggregationWindowMilliseconds,
                BringTargetToForeground = configuration.BringTargetToForeground
            });
        UpdateDriverStatus();
        var triggerLabel = sendMode == KeySendMode.InterceptionHold && configuration.SendTrigger == KeySendTrigger.Press
            ? "押下維持+離上"
            : configuration.SendTrigger switch
            {
                KeySendTrigger.KeyDownOnly => "押下のみ",
                KeySendTrigger.KeyUpOnly => "離上のみ",
                _ => "押下+離上"
            };
        var modifierModeLabel = configuration.SendModifierAsCommonKey ? "修飾共通" : "修飾左右別";
        return $"{configuration.SendKey} / {sendMode} / {triggerLabel} / {modifierModeLabel} -> {target}";
    }

    private KeySendMode ResolveEffectiveSendMode(BlinkSendConfiguration configuration)
    {
        if (configuration.SendMode != KeySendMode.Auto)
        {
            return configuration.SendMode;
        }

        return configuration.SendKey is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            ? KeySendMode.ScanCodeTap
            : KeySendMode.VirtualKeyTap;
    }

    private void UpdateDriverStatus()
    {
        if (!uiReady || DriverStatusTextBlock is null)
        {
            return;
        }

        DriverStatusTextBlock.Text = $"ドライバ状態: {physicalKeyboardSendService.GetDriverStatus()}";
    }

    private static string NormalizeRequiredValue(string? value, string fieldName)
    {
        var normalized = (value ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(normalized)
            ? normalized
            : throw new InvalidOperationException($"{fieldName} を入力してください。");
    }

    private static string NormalizeOptionalValue(string? value, string defaultValue)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? defaultValue
            : normalized;
    }

    private static void CopyConfiguration(BlinkSendConfiguration destination, BlinkSendConfiguration source)
    {
        destination.Name = source.Name;
        destination.RecognitionProfileName = source.RecognitionProfileName;
        destination.TargetProcessName = source.TargetProcessName;
        destination.TargetWindowTitleContains = source.TargetWindowTitleContains;
        destination.SendKey = source.SendKey;
        destination.SendModifierAsCommonKey = source.SendModifierAsCommonKey;
        destination.SendMode = source.SendMode;
        destination.SendTrigger = source.SendTrigger;
        destination.KeyHoldMilliseconds = source.KeyHoldMilliseconds;
        destination.RequiredBlinkCount = source.RequiredBlinkCount;
        destination.BlinkAggregationWindowMilliseconds = source.BlinkAggregationWindowMilliseconds;
        destination.BringTargetToForeground = source.BringTargetToForeground;
    }

    private static ObservableCollection<KeyOption> CreateKeyOptions()
    {
        return
        [
            new KeyOption { Key = Key.LeftShift, Label = "Shift (左)" },
            new KeyOption { Key = Key.RightShift, Label = "Shift (右)" },
            new KeyOption { Key = Key.LeftCtrl, Label = "Ctrl (左)" },
            new KeyOption { Key = Key.RightCtrl, Label = "Ctrl (右)" },
            new KeyOption { Key = Key.LeftAlt, Label = "Alt (左)" },
            new KeyOption { Key = Key.RightAlt, Label = "Alt (右)" },
            new KeyOption { Key = Key.Space, Label = "Space" },
            new KeyOption { Key = Key.Enter, Label = "Enter" },
            new KeyOption { Key = Key.Tab, Label = "Tab" },
            new KeyOption { Key = Key.D0, Label = "0" },
            new KeyOption { Key = Key.D1, Label = "1" },
            new KeyOption { Key = Key.D2, Label = "2" },
            new KeyOption { Key = Key.D3, Label = "3" },
            new KeyOption { Key = Key.D4, Label = "4" },
            new KeyOption { Key = Key.D5, Label = "5" },
            new KeyOption { Key = Key.D6, Label = "6" },
            new KeyOption { Key = Key.D7, Label = "7" },
            new KeyOption { Key = Key.D8, Label = "8" },
            new KeyOption { Key = Key.D9, Label = "9" },
            new KeyOption { Key = Key.Up, Label = "Up" },
            new KeyOption { Key = Key.Down, Label = "Down" },
            new KeyOption { Key = Key.Left, Label = "Left" },
            new KeyOption { Key = Key.Right, Label = "Right" },
            new KeyOption { Key = Key.F1, Label = "F1" },
            new KeyOption { Key = Key.F2, Label = "F2" },
            new KeyOption { Key = Key.F3, Label = "F3" },
            new KeyOption { Key = Key.F4, Label = "F4" },
            new KeyOption { Key = Key.F5, Label = "F5" },
            new KeyOption { Key = Key.F6, Label = "F6" },
            new KeyOption { Key = Key.F7, Label = "F7" },
            new KeyOption { Key = Key.F8, Label = "F8" },
            new KeyOption { Key = Key.F9, Label = "F9" },
            new KeyOption { Key = Key.F10, Label = "F10" },
            new KeyOption { Key = Key.F11, Label = "F11" },
            new KeyOption { Key = Key.F12, Label = "F12" },
            new KeyOption { Key = Key.A, Label = "A" },
            new KeyOption { Key = Key.B, Label = "B" },
            new KeyOption { Key = Key.C, Label = "C" },
            new KeyOption { Key = Key.D, Label = "D" },
            new KeyOption { Key = Key.E, Label = "E" },
            new KeyOption { Key = Key.F, Label = "F" },
            new KeyOption { Key = Key.G, Label = "G" },
            new KeyOption { Key = Key.H, Label = "H" },
            new KeyOption { Key = Key.I, Label = "I" },
            new KeyOption { Key = Key.J, Label = "J" },
            new KeyOption { Key = Key.K, Label = "K" },
            new KeyOption { Key = Key.L, Label = "L" },
            new KeyOption { Key = Key.M, Label = "M" },
            new KeyOption { Key = Key.N, Label = "N" },
            new KeyOption { Key = Key.O, Label = "O" },
            new KeyOption { Key = Key.P, Label = "P" },
            new KeyOption { Key = Key.Q, Label = "Q" },
            new KeyOption { Key = Key.R, Label = "R" },
            new KeyOption { Key = Key.S, Label = "S" },
            new KeyOption { Key = Key.T, Label = "T" },
            new KeyOption { Key = Key.U, Label = "U" },
            new KeyOption { Key = Key.V, Label = "V" },
            new KeyOption { Key = Key.W, Label = "W" },
            new KeyOption { Key = Key.X, Label = "X" },
            new KeyOption { Key = Key.Y, Label = "Y" },
            new KeyOption { Key = Key.Z, Label = "Z" }
        ];
    }

    private static ObservableCollection<KeySendModeOption> CreateSendModeOptions()
    {
        return
        [
            new KeySendModeOption { Mode = KeySendMode.Auto, Label = "Auto (修飾キーはスキャンコード)" },
            new KeySendModeOption { Mode = KeySendMode.VirtualKeyTap, Label = "仮想キー送信" },
            new KeySendModeOption { Mode = KeySendMode.ScanCodeTap, Label = "スキャンコード送信" },
            new KeySendModeOption { Mode = KeySendMode.ScanCodeHold, Label = "スキャンコード長押し" },
            new KeySendModeOption { Mode = KeySendMode.InterceptionTap, Label = "物理入力(Interception)" },
            new KeySendModeOption { Mode = KeySendMode.InterceptionHold, Label = "物理入力(Interception長押し)" }
        ];
    }

    private static ObservableCollection<KeySendTriggerOption> CreateSendTriggerOptions()
    {
        return
        [
            new KeySendTriggerOption { Trigger = KeySendTrigger.Press, Label = "押下+離上" },
            new KeySendTriggerOption { Trigger = KeySendTrigger.KeyDownOnly, Label = "押下のみ" },
            new KeySendTriggerOption { Trigger = KeySendTrigger.KeyUpOnly, Label = "離上のみ" }
        ];
    }

    private static ObservableCollection<BlinkNotificationSoundOption> CreateNotificationSoundOptions()
    {
        return
        [
            new BlinkNotificationSoundOption { Sound = BlinkNotificationSound.Asterisk, Label = "通知 (Asterisk)" },
            new BlinkNotificationSoundOption { Sound = BlinkNotificationSound.Beep, Label = "Beep" },
            new BlinkNotificationSoundOption { Sound = BlinkNotificationSound.Exclamation, Label = "警告 (Exclamation)" },
            new BlinkNotificationSoundOption { Sound = BlinkNotificationSound.Question, Label = "確認 (Question)" }
        ];
    }

    private static int ParseInt32OrDefault(string? text, int defaultValue)
    {
        return int.TryParse((text ?? string.Empty).Trim(), out var parsed)
            ? parsed
            : defaultValue;
    }

    private static Key ResolveCapturedKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key switch
        {
            Key.LeftShift or Key.RightShift => key,
            Key.LeftCtrl or Key.RightCtrl => key,
            Key.LeftAlt or Key.RightAlt => key,
            _ when key == Key.ImeProcessed || key == Key.DeadCharProcessed => e.ImeProcessedKey,
            _ => key
        };
    }

    private static bool TryMapToPreviewFrame(Point pointer, Size renderSize, Size frameSize, out Point framePoint)
    {
        framePoint = default;
        if (renderSize.Width <= 0 || renderSize.Height <= 0 || frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            return false;
        }

        var scale = Math.Min(renderSize.Width / frameSize.Width, renderSize.Height / frameSize.Height);
        if (scale <= 0)
        {
            return false;
        }

        var contentWidth = frameSize.Width * scale;
        var contentHeight = frameSize.Height * scale;
        var offsetX = (renderSize.Width - contentWidth) / 2d;
        var offsetY = (renderSize.Height - contentHeight) / 2d;
        if (pointer.X < offsetX || pointer.Y < offsetY || pointer.X > offsetX + contentWidth || pointer.Y > offsetY + contentHeight)
        {
            return false;
        }

        framePoint = new Point(
            (pointer.X - offsetX) / scale,
            (pointer.Y - offsetY) / scale);
        return true;
    }
}
