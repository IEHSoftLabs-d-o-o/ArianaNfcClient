namespace NfcTagReader.Models;

public sealed record TagValidationResult(
    bool IsValid,
    string? JsonText,
    NfcTagPayload? Payload,
    string? Reason,
    string? TechnicalDetails);
