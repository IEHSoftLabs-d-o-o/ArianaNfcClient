namespace NfcTagReader.Models;

public enum AppStatusKind
{
    Waiting,
    NoReader,
    Reading,
    Validating,
    Creating,
    Success,
    AlreadyExists,
    Error,
    RemoveTag
}
