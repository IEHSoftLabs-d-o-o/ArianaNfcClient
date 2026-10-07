using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ArianaNfcClient.Views;

public partial class SettingsWindow : Window
{
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    private readonly string _originalBaseUrl;
    private readonly string _originalUsername;
    private readonly string _originalPassword;

    public SettingsWindow(string baseUrl, string username, string password)
    {
        InitializeComponent();
        _originalBaseUrl = baseUrl;
        _originalUsername = username;
        _originalPassword = password;
        BaseUrlBox.Text = baseUrl;
        UsernameBox.Text = username;
        PasswordBox.Password = password;
        UpdateSaveEnabled();
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    public string Password { get; private set; } = string.Empty;

    private void Setting_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateSaveEnabled();
    }

    private void UpdateSaveEnabled()
    {
        SaveButton.IsEnabled =
            BaseUrlBox.Text != _originalBaseUrl ||
            UsernameBox.Text != _originalUsername ||
            PasswordBox.Password != _originalPassword;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text.Trim();
        if (baseUrl.Length > 0 &&
            (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address) ||
             (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(
                this,
                "Die ArianaLab-Adresse muss mit http:// oder https:// beginnen.",
                "Einstellungen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        BaseUrl = baseUrl;
        Username = UsernameBox.Text.Trim();
        Password = PasswordBox.Password;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void LogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Der Logordner konnte nicht geöffnet werden.{Environment.NewLine}{LogDirectory}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Einstellungen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
