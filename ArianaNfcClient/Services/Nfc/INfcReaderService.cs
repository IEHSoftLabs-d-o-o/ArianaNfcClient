using NfcTagReader.Models;

namespace NfcTagReader.Services.Nfc;

public interface INfcReaderService
{
    IAsyncEnumerable<NfcReaderEvent> WatchAsync(CancellationToken cancellationToken);
}
