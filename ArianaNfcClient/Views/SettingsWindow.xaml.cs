using System.Windows;

namespace ArianaNfcClient.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(string baseUrl, string username, string password)
    {
        InitializeComponent();
        BaseUrlBox.Text = baseUrl;
        UsernameBox.Text = username;
        PasswordBox.Password = password;
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    public string Password { get; private set; } = string.Empty;

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
}
