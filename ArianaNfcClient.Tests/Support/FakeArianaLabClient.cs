using ArianaNfcClient.Models;
using ArianaNfcClient.Services.ArianaLab;
using ArianaNfcClient.Services.Connection;

namespace ArianaNfcClient.Tests.Support;

internal sealed class FakeArianaLabClient : IArianaLabClient
{
    public Uri? EffectiveBaseAddress { get; set; } = new("https://lab.example/");

    public AuftragCreateResult CreateResult { get; set; } =
        new(true, "created", null, AuftragNummer: "A-1");

    public AuftragCreateResult ActivateResult { get; set; } =
        new(true, "Probe P-1 wurde in ArianaLab angelegt.", null, "A-1", "P-1", IsActivated: true);

    public bool DeleteResult { get; set; } = true;

    public List<string> Calls { get; } = [];

    public int CreateCalls => Calls.Count(c => c == "Create");

    public int ActivateCalls => Calls.Count(c => c.StartsWith("Activate", StringComparison.Ordinal));

    public int DeleteCalls => Calls.Count(c => c.StartsWith("Delete", StringComparison.Ordinal));

    public void ApplyConnection() => Calls.Add("ApplyConnection");

    public Task<AuftragCreateResult> CreateAuftragAsync(
        NfcTagPayload payload,
        string jsonText,
        CancellationToken cancellationToken)
    {
        Calls.Add("Create");
        return Task.FromResult(CreateResult);
    }

    public Task<AuftragCreateResult> ActivateAuftragAsync(string auftragId, CancellationToken cancellationToken)
    {
        Calls.Add($"Activate:{auftragId}");
        return Task.FromResult(ActivateResult);
    }

    public Task<bool> DeleteAuftragAsync(string auftragId, CancellationToken cancellationToken)
    {
        Calls.Add($"Delete:{auftragId}");
        return Task.FromResult(DeleteResult);
    }
}

internal sealed class FakeConnectionSettingsStore : IConnectionSettingsStore
{
    public ConnectionSettings Load() => new("https://lab.example", "user", "secret");

    public void Save(ConnectionSettings settings)
    {
    }
}
