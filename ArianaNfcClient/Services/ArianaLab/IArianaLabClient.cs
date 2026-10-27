using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.ArianaLab;

public interface IArianaLabClient
{
    Uri? EffectiveBaseAddress { get; }

    void ApplyConnection();

    Task<AuftragCreateResult> CreateAuftragAsync(NfcTagPayload payload, string jsonText, CancellationToken cancellationToken);

    Task<AuftragCreateResult> ActivateAuftragAsync(string auftragId, CancellationToken cancellationToken);

    Task<bool> DeleteAuftragAsync(string auftragId, CancellationToken cancellationToken);
}
