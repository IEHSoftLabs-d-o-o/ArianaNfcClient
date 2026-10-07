using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ArianaNfcClient.Services.Connection;

public sealed class ConnectionSettingsStore : IConnectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<ConnectionSettingsStore> _logger;

    public ConnectionSettingsStore(ILogger<ConnectionSettingsStore> logger)
    {
        _logger = logger;
    }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArianaNfcClient",
        "connection.json");

    public ConnectionSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new ConnectionSettings(string.Empty, string.Empty, string.Empty);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<ConnectionFile>(File.ReadAllText(FilePath)) ?? new ConnectionFile();
            return new ConnectionSettings(
                stored.BaseUrl ?? string.Empty,
                stored.Username ?? string.Empty,
                Unprotect(stored.PasswordProtected));
        }
        catch (Exception ex) when (ex is JsonException or IOException or CryptographicException)
        {
            _logger.LogWarning(ex, "Connection settings could not be read from {Path}", FilePath);
            return new ConnectionSettings(string.Empty, string.Empty, string.Empty);
        }
    }

    public void Save(ConnectionSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stored = new ConnectionFile
        {
            BaseUrl = settings.BaseUrl ?? string.Empty,
            Username = settings.Username ?? string.Empty,
            PasswordProtected = Protect(settings.Password ?? string.Empty)
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, JsonOptions));
        _logger.LogInformation("Connection settings saved");
    }

    private string Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedPassword);
            var passwordBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(passwordBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            _logger.LogWarning("Stored password could not be decrypted");
            return string.Empty;
        }
    }

    private static string Protect(string password)
    {
        if (password.Length == 0)
        {
            return string.Empty;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private sealed class ConnectionFile
    {
        public string? BaseUrl { get; set; }

        public string? Username { get; set; }

        public string? PasswordProtected { get; set; }
    }
}
