namespace ArianaNfcClient.Services.Connection;

public interface IConnectionSettingsStore
{
    ConnectionSettings Load();

    void Save(ConnectionSettings settings);
}

public sealed record ConnectionSettings(string BaseUrl, string Username, string Password);
