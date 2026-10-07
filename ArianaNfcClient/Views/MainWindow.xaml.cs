using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ArianaNfcClient.Models;
using ArianaNfcClient.ViewModels;

namespace ArianaNfcClient.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan SweepDuration = TimeSpan.FromSeconds(1.8);

    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StatusKind) or "")
        {
            UpdateSweep();
        }
    }

    private void CopyableTextBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3 || sender is not TextBox textBox)
        {
            return;
        }

        textBox.Focus();
        textBox.SelectAll();
        e.Handled = true;
    }

    private void CopyTextBoxMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: TextBox textBox } })
        {
            return;
        }

        var text = textBox.SelectionLength > 0 ? textBox.SelectedText : textBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // The clipboard is in use by another process.
        }
    }

    private void StatusBanner_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        StatusBanner.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            8,
            8);
    }

    private void UpdateSweep()
    {
        if (_viewModel.StatusKind == AppStatusKind.Creating)
        {
            StatusSweep.Visibility = Visibility.Visible;
            StatusSweepTransform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, SweepDuration)
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
            return;
        }

        StatusSweepTransform.BeginAnimation(TranslateTransform.XProperty, null);
        StatusSweep.Visibility = Visibility.Collapsed;
    }
}
