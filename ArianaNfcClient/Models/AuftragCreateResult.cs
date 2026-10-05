namespace NfcTagReader.Models;

public sealed record AuftragCreateResult(
    bool Succeeded,
    string Message,
    string? TechnicalDetails,
    string? AuftragNummer = null,
    string? ProbenNummer = null,
    int? HttpStatus = null,
    bool AlreadyExists = false);
