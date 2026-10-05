namespace ArianaNfcClient.Models;

public enum NfcReaderEventKind
{
    ReaderAvailable,
    ReaderLost,
    TagPresent,
    TagRemoved,
    TagRead,
    TagReadFailed
}
