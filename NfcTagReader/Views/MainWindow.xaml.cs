using System.Windows;
using NfcTagReader.ViewModels;

namespace NfcTagReader.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
