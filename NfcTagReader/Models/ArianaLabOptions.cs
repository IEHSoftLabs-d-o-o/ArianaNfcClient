namespace NfcTagReader.Models;

public sealed class ArianaLabOptions
{
    public const string SectionName = "ArianaLab";

    public string BaseUrl { get; set; } = "https://arianalab.drberns.de";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CreatePath { get; set; } = string.Empty;
    public string ProtocolAttributeName { get; set; } = DefaultProtocolAttributeName;

    public const string DefaultProtocolAttributeName = "$Protokoll-Id";
}
