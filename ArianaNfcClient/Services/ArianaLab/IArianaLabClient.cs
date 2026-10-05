using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.ArianaLab;

public interface IArianaLabClient
{
    Task<AuftragCreateResult> CreateAuftragAsync(NfcTagPayload payload, string jsonText, CancellationToken cancellationToken);
    Task<string?> DiscoverCreatePathAsync(CancellationToken cancellationToken);
}
