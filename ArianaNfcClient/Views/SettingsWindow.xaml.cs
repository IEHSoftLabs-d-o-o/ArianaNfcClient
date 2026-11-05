using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace ArianaNfcClient.Views;

public partial class SettingsWindow
{
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _originalBaseUrl;
    private readonly string _originalUsername;
    private readonly string _originalPassword;
    private readonly bool _originalAutoActivateAuftrag;
    private readonly bool _originalDeleteAuftragOnActivationFailure;
    private readonly IReadOnlyList<string> _originalHealthAuthorities;

    private List<string> _healthAuthorities;
    private bool _suppressToggleSync;

    public SettingsWindow(
        string baseUrl,
        string username,
        string password,
        bool autoActivateAuftrag,
        bool deleteAuftragOnActivationFailure,
        IReadOnlyList<string> healthAuthorities)
    {
        InitializeComponent();
        _originalBaseUrl = baseUrl;
        _originalUsername = username;
        _originalPassword = password;
        _originalAutoActivateAuftrag = autoActivateAuftrag;
        _originalDeleteAuftragOnActivationFailure = deleteAuftragOnActivationFailure;
        _originalHealthAuthorities = healthAuthorities.ToList();
        _healthAuthorities = healthAuthorities.ToList();
        BaseUrlBox.Text = baseUrl;
        UsernameBox.Text = username;
        PasswordBox.Password = password;
        AutoActivateBox.IsChecked = autoActivateAuftrag;
        DeleteOnActivationFailureBox.IsChecked = deleteAuftragOnActivationFailure;
        RefreshHealthAuthorities();
        UpdateSaveEnabled();
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    public string Password { get; private set; } = string.Empty;

    public bool AutoActivateAuftrag { get; private set; } = true;

    public bool DeleteAuftragOnActivationFailure { get; private set; } = false;

    public IReadOnlyList<string> HealthAuthorities { get; private set; } = [];

    private void Setting_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateSaveEnabled();
    }

    private void UpdateSaveEnabled()
    {
        if (SaveButton is null)
        {
            return;
        }

        SaveButton.IsEnabled =
            BaseUrlBox.Text != _originalBaseUrl ||
            UsernameBox.Text != _originalUsername ||
            PasswordBox.Password != _originalPassword ||
            AutoActivateBox.IsChecked != _originalAutoActivateAuftrag ||
            DeleteOnActivationFailureBox.IsChecked != _originalDeleteAuftragOnActivationFailure ||
            !_healthAuthorities.SequenceEqual(_originalHealthAuthorities);
    }

    private void RefreshHealthAuthorities()
    {
        HealthAuthoritiesLabel.Text = $"Gesundheitsämter ({_healthAuthorities.Count})";
        HealthAuthoritiesList.ItemsSource = _healthAuthorities;
        HealthAuthoritiesToggle.IsEnabled = _healthAuthorities.Count > 0;
        if (_healthAuthorities.Count == 0)
        {
            HealthAuthoritiesPopup.IsOpen = false;
        }
    }

    private void LoadHealthAuthoritiesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Textdateien (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var lines = ReadHealthAuthorityLines(dialog.FileName);
            _healthAuthorities = lines;
            RefreshHealthAuthorities();
            UpdateSaveEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            MessageBox.Show(
                this,
                $"Die Datei konnte nicht geladen werden.{Environment.NewLine}{dialog.FileName}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Einstellungen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static List<string> ReadHealthAuthorityLines(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        string text;
        try
        {
            text = Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1252).GetString(bytes);
        }

        // Strip UTF-8 BOM if present after decoding.
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var entry = line.Trim();
            if (entry.Length == 0 || !seen.Add(entry))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    private void HealthAuthoritiesToggle_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync || _healthAuthorities.Count == 0)
        {
            return;
        }

        HealthAuthoritiesPopup.IsOpen = true;
    }

    private void HealthAuthoritiesToggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync)
        {
            return;
        }

        HealthAuthoritiesPopup.IsOpen = false;
    }

    private void HealthAuthoritiesPopup_OnClosed(object? sender, EventArgs e)
    {
        if (HealthAuthoritiesToggle.IsChecked != true)
        {
            return;
        }

        _suppressToggleSync = true;
        try
        {
            HealthAuthoritiesToggle.IsChecked = false;
        }
        finally
        {
            _suppressToggleSync = false;
        }
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
        AutoActivateAuftrag = AutoActivateBox.IsChecked == true;
        DeleteAuftragOnActivationFailure = DeleteOnActivationFailureBox.IsChecked == true;
        HealthAuthorities = _healthAuthorities.ToList();
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
