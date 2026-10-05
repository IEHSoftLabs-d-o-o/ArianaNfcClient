using System.Windows;
using ArianaNfcClient.ViewModels;

namespace ArianaNfcClient.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
