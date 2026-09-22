using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recognition.Core;
using Recognition.Wpf;

namespace BlinkObserverTool.BlinkRecognition;

public partial class BlinkSetupWindow : Window
{
    private readonly RecognitionWorkbenchViewModel viewModel;
    private Point dragStart;
    private bool isDragging;
    private RoiArea selectedEyeRegion = RoiArea.Empty;
    private RecognitionFrame? openFrame;
    private RecognitionFrame? closedFrame;
    private FixedBlinkModel? validatedModel;

    public BlinkSetupWindow(RecognitionWorkbenchViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void CapturePreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await viewModel.RunTestAsync();
            if (!viewModel.IsRunning)
            {
                await viewModel.StartAsync();
            }
            ResetSamples();
            StatusText.Text = "プレビューを記録中です。対象の目をドラッグして囲んでください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"プレビュー取得に失敗しました: {exception.Message}";
        }
    }

    private void CaptureOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            openFrame = CaptureSelectedFrame();
            closedFrame = null;
            validatedModel = null;
            CaptureClosedButton.IsEnabled = true;
            ValidateButton.IsEnabled = false;
            ApplyModelButton.IsEnabled = false;
            StatusText.Text = "選択中の履歴から開眼テンプレートを採取しました。閉眼フレームを選び「3. 閉眼を採取」を押してください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"開眼採取に失敗しました: {exception.Message}";
        }
    }

    private void CaptureClosed_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            closedFrame = CaptureSelectedFrame();
            validatedModel = null;
            ValidateButton.IsEnabled = true;
            ApplyModelButton.IsEnabled = false;
            StatusText.Text = "選択中の履歴から閉眼テンプレートを採取しました。「4. 品質を検証」を押してください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"閉眼採取に失敗しました: {exception.Message}";
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (openFrame is null || closedFrame is null || selectedEyeRegion.IsEmpty)
            {
                throw new InvalidOperationException("ROI、開眼画像、閉眼画像を順に採取してください。");
            }

            validatedModel = FixedBlinkModel.Create(openFrame, closedFrame, selectedEyeRegion);
            var crossCorrelation = FixedBlinkModel.Correlation(validatedModel.OpenTemplate, validatedModel.ClosedTemplate);
            ApplyModelButton.IsEnabled = true;
            StatusText.Text = $"品質検証に合格しました (開閉テンプレート相関 {crossCorrelation:F3})。「5. モデルを反映」を押してください。";
        }
        catch (Exception exception)
        {
            validatedModel = null;
            ApplyModelButton.IsEnabled = false;
            StatusText.Text = $"品質検証に失敗しました: {exception.Message}";
        }
    }

    private void ApplyModel_Click(object sender, RoutedEventArgs e)
    {
        if (validatedModel is null)
        {
            StatusText.Text = "先に品質検証を完了してください。";
            return;
        }

        var option = viewModel.RecognitionMethod.Options.FirstOrDefault(
            candidate => string.Equals(candidate.Descriptor.Id, FixedBlinkRecognitionFactory.ComponentId, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            StatusText.Text = "固定テンプレート瞬き認識部品が読み込まれていません。";
            return;
        }

        viewModel.RecognitionMethod.SelectedOption = option;
        ApplyRoiParameters(validatedModel);
        foreach (var parameter in validatedModel.ToParameters())
        {
            SetParameter(parameter.Key, parameter.Value);
        }

        SetParameter("SmoothingFrames", 2);
        SetParameter("MinClosedFrames", 1);
        StatusText.Text = "固定学習モデルを反映しました。自動認識設定でプロファイルを保存してください。";
    }

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!viewModel.TryGetPreviewFrameSize(out _))
        {
            StatusText.Text = "先に最新プレビューを取得してください。";
            return;
        }

        dragStart = e.GetPosition(PreviewSurface);
        isDragging = true;
        PreviewSurface.CaptureMouse();
        UpdateSelection(dragStart, dragStart);
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void PreviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (isDragging)
        {
            UpdateSelection(dragStart, e.GetPosition(PreviewSurface));
        }
    }

    private void PreviewSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging)
        {
            return;
        }

        var dragEnd = e.GetPosition(PreviewSurface);
        isDragging = false;
        PreviewSurface.ReleaseMouseCapture();
        UpdateSelection(dragStart, dragEnd);
        ApplySelection(dragStart, dragEnd);
    }

    private void ApplySelection(Point start, Point end)
    {
        if (!viewModel.TryGetPreviewFrameSize(out var frameSize)
            || !TryMapToFrame(start, PreviewSurface.RenderSize, frameSize, out var frameStart)
            || !TryMapToFrame(end, PreviewSurface.RenderSize, frameSize, out var frameEnd))
        {
            StatusText.Text = "画像の表示範囲内で目を選択してください。";
            return;
        }

        var x = (int)Math.Floor(Math.Min(frameStart.X, frameEnd.X));
        var y = (int)Math.Floor(Math.Min(frameStart.Y, frameEnd.Y));
        var width = (int)Math.Ceiling(Math.Abs(frameEnd.X - frameStart.X));
        var height = (int)Math.Ceiling(Math.Abs(frameEnd.Y - frameStart.Y));
        if (width < 4 || height < 3)
        {
            StatusText.Text = "目の範囲が小さすぎます。少し広めに囲んでください。";
            return;
        }

        selectedEyeRegion = new RoiArea(x, y, width, height);
        ResetSamples();
        CaptureOpenButton.IsEnabled = true;
        StatusText.Text = $"目領域 {width}x{height} ({x}, {y}) を選択しました。目を開けて「2. 開眼を採取」を押してください。";
    }

    private void SetParameter(string key, object value)
    {
        var parameter = viewModel.RecognitionMethod.Parameters.FirstOrDefault(entry =>
            string.Equals(entry.Definition.Key, key, StringComparison.OrdinalIgnoreCase));
        if (parameter is null)
        {
            throw new InvalidOperationException($"認識パラメーター '{key}' が見つかりません。");
        }
        parameter.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private RecognitionFrame CaptureSelectedFrame()
    {
        if (selectedEyeRegion.IsEmpty)
        {
            throw new InvalidOperationException("先に目 ROI を選択してください。");
        }

        var index = viewModel.SelectedHistoryIndex;
        if (index < 0 || index >= viewModel.FrameHistoryEntries.Count)
        {
            throw new InvalidOperationException("採取する履歴フレームを選択してください。");
        }

        return viewModel.FrameHistoryEntries[index].ProcessedFrame.Clone();
    }

    private void ApplyRoiParameters(FixedBlinkModel model)
    {
        var frameWidth = model.InputWidth;
        var frameHeight = model.InputHeight;
        var eye = model.EyeRegion;
        var paddingX = Math.Max(8, eye.Width / 3);
        var paddingY = Math.Max(4, eye.Height / 3);
        var searchX = Math.Max(0, eye.X - paddingX);
        var searchY = Math.Max(0, eye.Y - paddingY);
        var searchRight = Math.Min(frameWidth, eye.X + eye.Width + paddingX);
        var searchBottom = Math.Min(frameHeight, eye.Y + eye.Height + paddingY);

        SetParameter("EyeX", eye.X);
        SetParameter("EyeY", eye.Y);
        SetParameter("EyeWidth", eye.Width);
        SetParameter("EyeHeight", eye.Height);
        SetParameter("SearchX", searchX);
        SetParameter("SearchY", searchY);
        SetParameter("SearchWidth", searchRight - searchX);
        SetParameter("SearchHeight", searchBottom - searchY);
    }

    private void ResetSamples()
    {
        openFrame = null;
        closedFrame = null;
        validatedModel = null;
        CaptureClosedButton.IsEnabled = false;
        ValidateButton.IsEnabled = false;
        ApplyModelButton.IsEnabled = false;
    }

    private void UpdateSelection(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        SelectionRectangle.Width = Math.Abs(end.X - start.X);
        SelectionRectangle.Height = Math.Abs(end.Y - start.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
    }

    private static bool TryMapToFrame(Point pointer, Size renderSize, Size frameSize, out Point framePoint)
    {
        framePoint = default;
        if (renderSize.Width <= 0 || renderSize.Height <= 0 || frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            return false;
        }

        var scale = Math.Min(renderSize.Width / frameSize.Width, renderSize.Height / frameSize.Height);
        var contentWidth = frameSize.Width * scale;
        var contentHeight = frameSize.Height * scale;
        var offsetX = (renderSize.Width - contentWidth) / 2d;
        var offsetY = (renderSize.Height - contentHeight) / 2d;
        if (pointer.X < offsetX || pointer.Y < offsetY || pointer.X > offsetX + contentWidth || pointer.Y > offsetY + contentHeight)
        {
            return false;
        }

        framePoint = new Point((pointer.X - offsetX) / scale, (pointer.Y - offsetY) / scale);
        return true;
    }
}
