using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recognition.Wpf;

namespace BlinkObserverTool.BlinkRecognition;

public partial class BlinkSetupWindow : Window
{
    private readonly RecognitionWorkbenchViewModel viewModel;
    private Point dragStart;
    private bool isDragging;

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
            StatusText.Text = "対象の目をドラッグして囲んでください。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"プレビュー取得に失敗しました: {exception.Message}";
        }
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

        var option = viewModel.RecognitionMethod.Options.FirstOrDefault(
            candidate => string.Equals(candidate.Descriptor.Id, BlinkRecognitionFactory.ComponentId, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            StatusText.Text = "瞬き認識部品が読み込まれていません。";
            return;
        }

        viewModel.RecognitionMethod.SelectedOption = option;
        var paddingX = Math.Max(8, width / 3);
        var paddingY = Math.Max(4, height / 3);
        var searchX = Math.Max(0, x - paddingX);
        var searchY = Math.Max(0, y - paddingY);
        var searchRight = Math.Min((int)frameSize.Width, x + width + paddingX);
        var searchBottom = Math.Min((int)frameSize.Height, y + height + paddingY);

        SetParameter("EyeX", x);
        SetParameter("EyeY", y);
        SetParameter("EyeWidth", width);
        SetParameter("EyeHeight", height);
        SetParameter("SearchX", searchX);
        SetParameter("SearchY", searchY);
        SetParameter("SearchWidth", searchRight - searchX);
        SetParameter("SearchHeight", searchBottom - searchY);
        SetParameter("SmoothingFrames", 3);
        SetParameter("MinClosedFrames", 1);
        SetParameter("CalibrationFrames", 12);
        SetParameter("DetectionThreshold", 0);
        SetParameter("Sensitivity", "Standard");

        StatusText.Text = $"目領域 {width}x{height} ({x}, {y}) を設定しました。詳細設定でプロファイルを保存してください。";
    }

    private void SetParameter(string key, object value)
    {
        var parameter = viewModel.RecognitionMethod.Parameters.First(entry =>
            string.Equals(entry.Definition.Key, key, StringComparison.OrdinalIgnoreCase));
        parameter.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
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
