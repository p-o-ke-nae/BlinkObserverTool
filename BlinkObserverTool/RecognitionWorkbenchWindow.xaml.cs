using System.ComponentModel;
using System.Windows;
using Recognition.Wpf;

namespace BlinkObserverTool;

public partial class RecognitionWorkbenchWindow : Window
{
    public RecognitionWorkbenchWindow()
        : this((RecognitionWorkbenchViewModel?)null)
    {
    }

    internal RecognitionWorkbenchWindow(RecognitionWorkbenchViewModel? viewModel)
    {
        InitializeComponent();

        if (DesignerProperties.GetIsInDesignMode(this) || viewModel is null)
        {
            return;
        }

        WorkbenchControl.Initialize(viewModel);
    }
}
