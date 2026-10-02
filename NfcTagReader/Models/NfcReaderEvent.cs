namespace NfcTagReader.Models;

public sealed record NfcReaderEvent(
    NfcReaderEventKind Kind,
    string? ReaderName = null,
    string? Uid = null,
    string? Payload = null,
    string? ErrorMessage = null,
    string? TechnicalDetails = null);
