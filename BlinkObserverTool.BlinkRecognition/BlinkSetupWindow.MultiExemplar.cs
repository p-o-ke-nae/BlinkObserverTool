using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

public partial class BlinkSetupWindow
{
    private BlinkSampleSet? multiSamples;
    private MultiExemplarBlinkModel? multiModel;
    private bool isMultiBusy;

    public ObservableCollection<BlinkCandidateItem> Candidates { get; } = [];

    private void MultiCapture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (selectedEyeRegion.IsEmpty)
            {
                throw new InvalidOperationException("先に目 ROI を選択してください。");
            }

            var frames = viewModel.FrameHistoryEntries.Select(entry => entry.ProcessedFrame).ToList();
            if (frames.Count == 0)
            {
                throw new InvalidOperationException("履歴にフレームがありません。プレビューを開始して数秒録画してください。");
            }

            var first = frames[0];
            var set = new BlinkSampleSet(first.Width, first.Height, selectedEyeRegion);
            foreach (var frame in frames)
            {
                set.Add(frame);
            }

            ResetMultiState();
            multiSamples = set;
            var duration = (set.Samples[^1].CapturedAt - set.Samples[0].CapturedAt).TotalSeconds;
            var fps = duration > 0 ? (set.Count - 1) / duration : 0d;
            MultiSummaryText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "取り込み {0} フレーム / {1:F1} 秒 (実効 {2:F1} fps)。{3}",
                set.Count,
                duration,
                fps,
                fps is > 0 and < 45 ? "実効fpsが低いため1フレームの瞬きを取りこぼす可能性があります。" : string.Empty);
            UpdateMultiButtons();
            StatusText.Text = "履歴を取り込みました。「B. 閉眼候補を検出」を押してください。";
        }
        catch (NotSupportedException)
        {
            StatusText.Text = "複数パターン方式はカラー (BGR/BGRA) 入力が必要です。グレースケール前処理を無効にしてください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"履歴の取り込みに失敗しました: {exception.Message}";
        }
    }

    private async void MultiPropose_Click(object sender, RoutedEventArgs e)
    {
        if (multiSamples is null)
        {
            return;
        }

        var set = multiSamples;
        try
        {
            SetMultiBusy(true, "閉眼候補を検出しています…");
            var events = await Task.Run(() => MultiExemplarBlinkCalibrator.ProposeClosedCandidates(set));
            Candidates.Clear();
            multiModel = null;
            foreach (var candidate in events)
            {
                Candidates.Add(CreateItem(
                    set,
                    candidate.StartIndex,
                    candidate.EndIndex,
                    candidate.PeakIndex,
                    BlinkCandidateItem.ClosedLabel,
                    $"自動 類似度 {candidate.OpenSimilarity:F2}"));
            }

            StatusText.Text = events.Count == 0
                ? "閉眼候補が見つかりませんでした。瞬きを含むよう録画し直すか、履歴スライダーで閉眼フレームを選び手動追加してください。"
                : $"閉眼候補 {events.Count} 件を検出しました。各候補を確認し、誤りは「開眼（誤候補）」または「除外」に変更してください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"閉眼候補の検出に失敗しました: {exception.Message}";
        }
        finally
        {
            SetMultiBusy(false, null);
        }
    }

    private void MultiAddClosed_Click(object sender, RoutedEventArgs e) => AddSelectedHistory(BlinkCandidateItem.ClosedLabel);

    private void MultiAddExcluded_Click(object sender, RoutedEventArgs e) => AddSelectedHistory(BlinkCandidateItem.ExcludedLabel);

    private void AddSelectedHistory(string label)
    {
        if (multiSamples is null)
        {
            return;
        }

        var index = viewModel.SelectedHistoryIndex;
        if (index < 0 || index >= viewModel.FrameHistoryEntries.Count)
        {
            StatusText.Text = "履歴スライダーで追加するフレームを選択してください。";
            return;
        }

        var sampleIndex = multiSamples.IndexOf(viewModel.FrameHistoryEntries[index].ProcessedFrame.CapturedAt);
        if (sampleIndex < 0)
        {
            StatusText.Text = "選択中のフレームは取り込み範囲外です。「A. 履歴を取り込む」をやり直してください。";
            return;
        }

        var existing = Candidates.FirstOrDefault(item => item.Contains(multiSamples.Samples[sampleIndex].CapturedAt));
        if (existing is not null)
        {
            existing.Label = label;
            CandidateList.SelectedItem = existing;
        }
        else
        {
            var item = CreateItem(multiSamples, sampleIndex, sampleIndex, sampleIndex, label, "手動");
            var position = Candidates.TakeWhile(candidate => candidate.PeakAt < item.PeakAt).Count();
            Candidates.Insert(position, item);
            CandidateList.SelectedItem = item;
        }

        multiModel = null;
        UpdateMultiButtons();
        StatusText.Text = $"選択フレームを「{label}」として登録しました。";
    }

    private async void MultiCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (multiSamples is null)
        {
            return;
        }

        var set = multiSamples;
        var closed = Candidates.Where(item => item.Label == BlinkCandidateItem.ClosedLabel)
            .SelectMany(item => item.Frames).Select(set.IndexOf).Where(index => index >= 0).ToList();
        var excluded = Candidates.Where(item => item.Label == BlinkCandidateItem.ExcludedLabel)
            .SelectMany(item => item.Frames).Select(set.IndexOf).Where(index => index >= 0).ToList();
        try
        {
            SetMultiBusy(true, "自動校正しています（数十秒かかる場合があります）…");
            var result = await Task.Run(() => MultiExemplarBlinkCalibrator.Calibrate(set, closed, excluded));
            multiModel = result.Passed ? result.Model : null;
            var warnings = result.Warnings.Count == 0 ? string.Empty : " / 注意: " + string.Join(" ", result.Warnings);
            MultiSummaryText.Text = $"{result.Summary}{warnings}";
            StatusText.Text = result.Passed
                ? "品質検証に合格しました。「D. モデルを反映」を押してください。"
                : "品質検証に不合格です。候補の判定を見直すか、瞬きを追加で録画して取り込み直してください。";
        }
        catch (Exception exception)
        {
            multiModel = null;
            StatusText.Text = $"自動校正に失敗しました: {exception.Message}";
        }
        finally
        {
            SetMultiBusy(false, null);
        }
    }

    private void MultiApply_Click(object sender, RoutedEventArgs e)
    {
        if (multiModel is null)
        {
            StatusText.Text = "先に自動校正・品質検証を完了してください。";
            return;
        }

        var option = viewModel.RecognitionMethod.Options.FirstOrDefault(
            candidate => string.Equals(candidate.Descriptor.Id, MultiExemplarBlinkRecognitionFactory.ComponentId, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            StatusText.Text = "瞬き複数パターン認識部品が読み込まれていません。";
            return;
        }

        try
        {
            viewModel.RecognitionMethod.SelectedOption = option;
            foreach (var parameter in multiModel.ToParameters())
            {
                SetParameter(parameter.Key, parameter.Value);
            }

            SetParameter("RearmOpenFrames", 2);
            SetParameter("UnknownGraceFrames", 6);
            StatusText.Text = "複数パターン学習モデルを反映しました。自動認識設定でプロファイルを保存してください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"モデルの反映に失敗しました: {exception.Message}";
        }
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is not BlinkCandidateItem item)
        {
            return;
        }

        for (var index = 0; index < viewModel.FrameHistoryEntries.Count; index++)
        {
            if (viewModel.FrameHistoryEntries[index].ProcessedFrame.CapturedAt == item.PeakAt)
            {
                viewModel.SelectedHistoryIndex = index;
                return;
            }
        }
    }

    private void Candidate_LabelChanged(object? sender, EventArgs e)
    {
        multiModel = null;
        UpdateMultiButtons();
    }

    private BlinkCandidateItem CreateItem(BlinkSampleSet set, int start, int end, int peak, string label, string source)
    {
        var frames = Enumerable.Range(start, end - start + 1).Select(index => set.Samples[index].CapturedAt).ToList();
        var item = new BlinkCandidateItem(
            frames,
            set.Samples[peak].CapturedAt,
            $"#{peak} ({end - start + 1}f) {source}",
            CreateThumbnail(set, Math.Max(0, start - 3)),
            CreateThumbnail(set, peak),
            label);
        item.LabelChanged += Candidate_LabelChanged;
        return item;
    }

    private static BitmapSource CreateThumbnail(BlinkSampleSet set, int index)
    {
        var region = set.CaptureRegion;
        var bitmap = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgr24, null, set.CapturePixels(index), region.Width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private void ResetMultiState()
    {
        multiSamples = null;
        multiModel = null;
        Candidates.Clear();
        MultiSummaryText.Text = "未取り込み";
        UpdateMultiButtons();
    }

    private void SetMultiBusy(bool busy, string? message)
    {
        isMultiBusy = busy;
        if (message is not null)
        {
            StatusText.Text = message;
        }

        UpdateMultiButtons();
    }

    private void UpdateMultiButtons()
    {
        var ready = !isMultiBusy;
        MultiCaptureButton.IsEnabled = ready && !selectedEyeRegion.IsEmpty;
        MultiProposeButton.IsEnabled = ready && multiSamples is not null;
        MultiAddClosedButton.IsEnabled = ready && multiSamples is not null;
        MultiAddExcludedButton.IsEnabled = ready && multiSamples is not null;
        MultiCalibrateButton.IsEnabled = ready && multiSamples is not null && Candidates.Any(item => item.Label == BlinkCandidateItem.ClosedLabel);
        MultiApplyButton.IsEnabled = ready && multiModel is not null;
        CandidateList.IsEnabled = ready;
    }
}

public sealed class BlinkCandidateItem : INotifyPropertyChanged
{
    public const string ClosedLabel = "閉眼";
    public const string OpenLabel = "開眼（誤候補）";
    public const string ExcludedLabel = "除外";

    private string label;

    public BlinkCandidateItem(
        IReadOnlyList<DateTimeOffset> frames,
        DateTimeOffset peakAt,
        string title,
        BitmapSource beforeImage,
        BitmapSource peakImage,
        string label)
    {
        Frames = frames;
        PeakAt = peakAt;
        Title = title;
        BeforeImage = beforeImage;
        PeakImage = peakImage;
        this.label = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LabelChanged;

    public IReadOnlyList<string> LabelOptions { get; } = [ClosedLabel, OpenLabel, ExcludedLabel];

    public IReadOnlyList<DateTimeOffset> Frames { get; }

    public DateTimeOffset PeakAt { get; }

    public string Title { get; }

    public BitmapSource BeforeImage { get; }

    public BitmapSource PeakImage { get; }

    public string Label
    {
        get => label;
        set
        {
            if (label == value)
            {
                return;
            }

            label = value;
            OnPropertyChanged();
            LabelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Contains(DateTimeOffset capturedAt) => Frames.Contains(capturedAt);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
