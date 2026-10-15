using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.ArianaLab;

public interface IArianaLabClient
{
    void ApplyConnection();

    Task<AuftragCreateResult> CreateAuftragAsync(NfcTagPayload payload, string jsonText, CancellationToken cancellationToken);

    Task<AuftragCreateResult> ActivateAuftragAsync(string auftragId, CancellationToken cancellationToken);
}
