using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace ArianaNfcClient.Views;

public partial class ActivationFailedWindow
{
    private bool _acknowledged;

    public ActivationFailedWindow(string message, string? auftragUrl)
    {
        InitializeComponent();
        MessageText.Text = string.IsNullOrWhiteSpace(message)
            ? "ArianaLab hat die Aktivierung abgelehnt."
            : message;

        if (string.IsNullOrWhiteSpace(auftragUrl) ||
            !Uri.TryCreate(auftragUrl, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        AuftragLink.NavigateUri = address;
        LinkPanel.Visibility = Visibility.Visible;
    }

    private void AuftragLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Der Link konnte nicht geöffnet werden.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Aktivierung fehlgeschlagen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        _acknowledged = true;
        DialogResult = true;
    }

    private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_acknowledged)
        {
            e.Cancel = true;
        }
    }
}
