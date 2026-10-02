using NfcTagReader.Models;

namespace NfcTagReader.Services.ArianaLab;

public interface IArianaLabClient
{
    Task<AuftragCreateResult> CreateAuftragAsync(NfcTagPayload payload, string jsonText, CancellationToken cancellationToken);
    Task<string?> DiscoverCreatePathAsync(CancellationToken cancellationToken);
}
