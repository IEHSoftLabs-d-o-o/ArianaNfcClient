using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.ArianaLab;

public interface IArianaLabClient
{
    void ApplyConnection();

    Task<AuftragCreateResult> CreateAuftragAsync(NfcTagPayload payload, string jsonText, CancellationToken cancellationToken);
    Task<string?> DiscoverCreatePathAsync(CancellationToken cancellationToken);
}
