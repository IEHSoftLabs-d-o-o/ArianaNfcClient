using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.Nfc;

public interface INfcReaderService
{
    IAsyncEnumerable<NfcReaderEvent> WatchAsync(CancellationToken cancellationToken);
}
