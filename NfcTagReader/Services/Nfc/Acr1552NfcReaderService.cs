using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NfcTagReader.Models;
using PCSC;
using PCSC.Exceptions;
using PCSC.Monitoring;

namespace NfcTagReader.Services.Nfc;

public sealed class Acr1552NfcReaderService : INfcReaderService
{
    private readonly ILogger<Acr1552NfcReaderService> _logger;

    public Acr1552NfcReaderService(ILogger<Acr1552NfcReaderService> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<NfcReaderEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<NfcReaderEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        ISCardMonitor? monitor = null;
        string? currentReader = null;
        string? presentUid = null;
        var announcedMissingReader = false;

        void Publish(NfcReaderEvent evt) => channel.Writer.TryWrite(evt);

        void OnInserted(object? _, CardStatusEventArgs args)
        {
            if (currentReader is null ||
                !string.Equals(args.ReaderName, currentReader, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TryReadAndPublish(currentReader, Publish, ref presentUid);
        }

        void OnRemoved(object? _, CardStatusEventArgs args)
        {
            if (currentReader is null ||
                !string.Equals(args.ReaderName, currentReader, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            presentUid = null;
            Publish(new NfcReaderEvent(NfcReaderEventKind.TagRemoved, currentReader));
        }

        void OnMonitorException(object? _, PCSCException args) =>
            _logger.LogWarning(args, "PC/SC monitor exception");

        void ReleaseMonitor()
        {
            if (monitor is null)
            {
                return;
            }

            Detach(monitor, OnInserted, OnRemoved, OnMonitorException);
            TryStop(monitor);
            DisposeMonitor(monitor);
            monitor = null;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var readerName = FindReaderName();
                if (readerName is null)
                {
                    if (currentReader is not null || !announcedMissingReader)
                    {
                        currentReader = null;
                        presentUid = null;
                        announcedMissingReader = true;
                        ReleaseMonitor();
                        Publish(new NfcReaderEvent(
                            NfcReaderEventKind.ReaderLost,
                            ErrorMessage: "Kein NFC-Leser gefunden.",
                            TechnicalDetails: "No PC/SC PICC reader is available."));
                    }
                }
                else if (!string.Equals(currentReader, readerName, StringComparison.OrdinalIgnoreCase))
                {
                    announcedMissingReader = false;
                    ReleaseMonitor();
                    if (!TryStartMonitor(readerName, OnInserted, OnRemoved, OnMonitorException, out monitor))
                    {
                        currentReader = null;
                        presentUid = null;
                    }
                    else
                    {
                        currentReader = readerName;
                        Publish(new NfcReaderEvent(NfcReaderEventKind.ReaderAvailable, readerName));
                        TryReadAndPublish(readerName, Publish, ref presentUid);
                    }
                }

                var wait = currentReader is null ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(400);
                foreach (var evt in await ReadEventsUntilDeviceChangeAsync(channel, wait, cancellationToken))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            ReleaseMonitor();
            channel.Writer.TryComplete();
        }
    }

    private bool TryStartMonitor(
        string readerName,
        CardInsertedEvent OnInserted,
        CardRemovedEvent OnRemoved,
        MonitorExceptionEvent OnMonitorException,
        out ISCardMonitor? monitor)
    {
        monitor = null;
        ISCardMonitor? started = null;
        try
        {
            started = MonitorFactory.Instance.Create(SCardScope.System);
            started.CardInserted += OnInserted;
            started.CardRemoved += OnRemoved;
            started.MonitorException += OnMonitorException;
            started.Start(readerName);
            monitor = started;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start NFC monitor for {Reader}", readerName);
            if (started is not null)
            {
                Detach(started, OnInserted, OnRemoved, OnMonitorException);
                TryStop(started);
                DisposeMonitor(started);
            }

            monitor = null;
            return false;
        }
    }

    private static void Detach(
        ISCardMonitor monitor,
        CardInsertedEvent onInserted,
        CardRemovedEvent onRemoved,
        MonitorExceptionEvent onMonitorException)
    {
        monitor.CardInserted -= onInserted;
        monitor.CardRemoved -= onRemoved;
        monitor.MonitorException -= onMonitorException;
    }

    private async Task<List<NfcReaderEvent>> ReadEventsUntilDeviceChangeAsync(
        Channel<NfcReaderEvent> channel,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        done.CancelAfter(wait);
        var deviceChange = Task.Run(() =>
        {
            if (WaitForReaderChange(wait, done.Token))
            {
                try
                {
                    done.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // the wait already finished
                }
            }
        }, CancellationToken.None);

        List<NfcReaderEvent> events;
        try
        {
            events = await ReadEventsAsync(channel, done.Token, cancellationToken);
        }
        finally
        {
            try
            {
                done.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already completed
            }
        }

        try
        {
            await deviceChange.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("PC/SC reader-change wait did not return in time");
        }

        return events;
    }

    private bool WaitForReaderChange(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            using var context = ContextFactory.Instance.Establish(SCardScope.System);
            using var state = new SCardReaderState
            {
                ReaderName = @"\\?PnP?\Notification",
                CurrentState = SCRState.Unaware
            };

            var states = new[] { state };
            if (context.GetStatusChange(IntPtr.Zero, states) != SCardError.Success)
            {
                return false;
            }

            state.CurrentState = state.EventState & ~SCRState.Changed;
            using var registration = cancellationToken.Register(static cardContext =>
            {
                if (cardContext is ISCardContext active)
                {
                    try
                    {
                        active.Cancel();
                    }
                    catch (PCSCException)
                    {
                        // the wait already finished
                    }
                }
            }, context);

            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            return context.GetStatusChange((IntPtr)milliseconds, states) == SCardError.Success;
        }
        catch (PCSCException ex) when (ex.SCardError is SCardError.Timeout
                                       or SCardError.Cancelled
                                       or SCardError.NoReadersAvailable
                                       or SCardError.NoService)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PC/SC reader change wait failed");
            return false;
        }
    }

    private static async Task<List<NfcReaderEvent>> ReadEventsAsync(
        Channel<NfcReaderEvent> channel,
        CancellationToken waitToken,
        CancellationToken cancellationToken)
    {
        var events = new List<NfcReaderEvent>();
        try
        {
            while (await channel.Reader.WaitToReadAsync(waitToken))
            {
                while (channel.Reader.TryRead(out var evt))
                {
                    events.Add(evt);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            while (channel.Reader.TryRead(out var evt))
            {
                events.Add(evt);
            }
        }

        return events;
    }

    private static void DisposeMonitor(ISCardMonitor monitor)
    {
        try
        {
            monitor.Dispose();
        }
        catch (PCSCException)
        {
            // the monitor context is already gone
        }
    }

    private void TryReadAndPublish(string readerName, Action<NfcReaderEvent> publish, ref string? presentUid)
    {
        try
        {
            var result = ReadTag(readerName);
            if (result.Uid is not null &&
                string.Equals(presentUid, result.Uid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            presentUid = result.Uid;
            publish(new NfcReaderEvent(NfcReaderEventKind.TagPresent, readerName, result.Uid));

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Payload))
            {
                publish(new NfcReaderEvent(
                    NfcReaderEventKind.TagRead,
                    readerName,
                    result.Uid,
                    result.Payload));
            }
            else
            {
                publish(new NfcReaderEvent(
                    NfcReaderEventKind.TagReadFailed,
                    readerName,
                    result.Uid,
                    ErrorMessage: result.ErrorMessage,
                    TechnicalDetails: result.TechnicalDetails));
            }
        }
        catch (Exception ex) when (IsNoCardPresent(ex))
        {
            _logger.LogDebug(ex, "No NFC tag present on {Reader}", readerName);
            if (presentUid is not null)
            {
                presentUid = null;
                publish(new NfcReaderEvent(NfcReaderEventKind.TagRemoved, readerName));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NFC read failed on {Reader}", readerName);
            publish(new NfcReaderEvent(
                NfcReaderEventKind.TagReadFailed,
                readerName,
                ErrorMessage: "NFC-Tag konnte nicht gelesen werden.",
                TechnicalDetails: ex.Message));
        }
    }

    private static bool IsNoCardPresent(Exception ex)
    {
        if (ex is RemovedCardException or NoSmartcardException)
        {
            return true;
        }

        if (ex is PCSCException pcsc &&
            pcsc.SCardError is SCardError.RemovedCard or SCardError.NoSmartcard)
        {
            return true;
        }

        return ex.Message.Contains("smart card has been removed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("no smart card inserted", StringComparison.OrdinalIgnoreCase);
    }

    private TagReadOutcome ReadTag(string readerName)
    {
        using var context = ContextFactory.Instance.Establish(SCardScope.System);
        using var reader = context.ConnectReader(readerName, SCardShareMode.Shared, SCardProtocol.Any);

        var uid = GetUid(reader);
        _logger.LogInformation("Tag UID {Uid} on {Reader}", uid, readerName);

        if (TryReadType4Ndef(reader, out var type4, out var type4Error) && type4 is not null)
        {
            return new TagReadOutcome(true, uid, type4, null, null);
        }

        if (TryReadViaPcscBinary(reader, out var type2, out var type2Error) && type2 is not null)
        {
            return new TagReadOutcome(true, uid, type2, null, null);
        }

        if (TryReadViaTransparentSession(reader, out var transparent, out var transparentError) &&
            transparent is not null)
        {
            return new TagReadOutcome(true, uid, transparent, null, null);
        }

        var details = string.Join(
            Environment.NewLine,
            new[] { type4Error, type2Error, transparentError }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new TagReadOutcome(
            false,
            uid,
            null,
            "NFC-Tag enthält kein lesbares JSON / NDEF.",
            details);
    }

    private static string? GetUid(ICardReader reader)
    {
        var response = Transmit(reader, [0xFF, 0xCA, 0x00, 0x00, 0x00]);
        if (!response.IsSuccess || response.Data.Length == 0)
        {
            return null;
        }

        return Convert.ToHexString(response.Data);
    }

    private static bool TryReadType4Ndef(ICardReader reader, out string? json, out string? error)
    {
        json = null;
        error = null;

        var selectApp = Transmit(reader, [0x00, 0xA4, 0x04, 0x00, 0x07, 0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01, 0x00]);
        if (!selectApp.IsSuccess)
        {
            error = $"Type 4 SELECT NDEF App SW={selectApp.Status}";
            return false;
        }

        var selectCc = Transmit(reader, [0x00, 0xA4, 0x00, 0x0C, 0x02, 0xE1, 0x03]);
        if (!selectCc.IsSuccess)
        {
            error = $"Type 4 SELECT CC SW={selectCc.Status}";
            return false;
        }

        var cc = Transmit(reader, [0x00, 0xB0, 0x00, 0x00, 0x0F]);
        if (!cc.IsSuccess || cc.Data.Length < 15)
        {
            error = $"Type 4 READ CC SW={cc.Status}";
            return false;
        }

        var fileId0 = cc.Data[9];
        var fileId1 = cc.Data[10];
        var selectNdef = Transmit(reader, [0x00, 0xA4, 0x00, 0x0C, 0x02, fileId0, fileId1]);
        if (!selectNdef.IsSuccess)
        {
            error = $"Type 4 SELECT NDEF SW={selectNdef.Status}";
            return false;
        }

        var lengthResp = Transmit(reader, [0x00, 0xB0, 0x00, 0x00, 0x02]);
        if (!lengthResp.IsSuccess || lengthResp.Data.Length < 2)
        {
            error = $"Type 4 READ NLEN SW={lengthResp.Status}";
            return false;
        }

        var nlen = (lengthResp.Data[0] << 8) | lengthResp.Data[1];
        if (nlen <= 0 || nlen > 4096)
        {
            error = $"Type 4 NLEN={nlen} is invalid.";
            return false;
        }

        var ndef = ReadBinaryRange(reader, 2, nlen);
        if (ndef is null)
        {
            error = "Type 4 NDEF body could not be read.";
            return false;
        }

        return NdefDecoder.TryDecodeToText(ndef, out json, out error) && !string.IsNullOrWhiteSpace(json);
    }

    private static bool TryReadViaPcscBinary(ICardReader reader, out string? json, out string? error)
    {
        json = null;
        error = null;
        var pages = new List<byte>(256);

        for (var page = 0; page < 64; page++)
        {
            var response = Transmit(reader, [0xFF, 0xB0, 0x00, (byte)page, 0x04]);
            if (!response.IsSuccess || response.Data.Length == 0)
            {
                if (page == 0)
                {
                    error = $"FF B0 page 0 SW={response.Status}";
                    return false;
                }

                break;
            }

            pages.AddRange(response.Data);
        }

        return TryDecodeType2Memory(pages.ToArray(), out json, out error);
    }

    private static bool TryReadViaTransparentSession(ICardReader reader, out string? json, out string? error)
    {
        json = null;
        error = null;

        var start = Transmit(reader, [0xFF, 0xC2, 0x00, 0x00, 0x02, 0x81, 0x00]);
        if (!start.IsSuccess)
        {
            start = Transmit(reader, [0xFF, 0xC2, 0x00, 0x00, 0x81, 0x00]);
        }

        if (!start.IsSuccess)
        {
            error = $"Manage Session start SW={start.Status}";
            return false;
        }

        try
        {
            var switchProtocol = Transmit(reader, [0xFF, 0xC2, 0x00, 0x02, 0x04, 0x8F, 0x02, 0x00, 0x03]);
            if (!switchProtocol.IsSuccess)
            {
                error = $"Switch Protocol SW={switchProtocol.Status}";
                return false;
            }

            var memory = new List<byte>(256);
            for (var page = 0; page < 64; page += 4)
            {
                var read = Transmit(reader, [0xFF, 0xC2, 0x00, 0x01, 0x04, 0x95, 0x02, 0x30, (byte)page]);
                var data = ExtractTransparentPayload(read);
                if (data is null || data.Length == 0)
                {
                    if (page == 0)
                    {
                        error = $"Transparent READ page 0 SW={read.Status}";
                        return false;
                    }

                    break;
                }

                memory.AddRange(data);
            }

            return TryDecodeType2Memory(memory.ToArray(), out json, out error);
        }
        finally
        {
            Transmit(reader, [0xFF, 0xC2, 0x00, 0x00, 0x02, 0x82, 0x00]);
        }
    }

    private static bool TryDecodeType2Memory(byte[] memory, out string? json, out string? error)
    {
        json = null;
        if (NdefDecoder.TryExtractNdefFromType2(memory, out var ndef, out error) &&
            ndef is not null &&
            NdefDecoder.TryDecodeToText(ndef, out json, out error) &&
            !string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        var ascii = Encoding.UTF8.GetString(memory);
        var jsonStart = ascii.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        json = ascii[jsonStart..].Trim('\0', ' ', '\r', '\n');
        var end = json.LastIndexOf('}');
        if (end <= 0)
        {
            return false;
        }

        json = json[..(end + 1)];
        error = null;
        return true;
    }

    private static byte[]? ReadBinaryRange(ICardReader reader, int offset, int length)
    {
        var buffer = new List<byte>(length);
        var remaining = length;
        var pos = offset;
        while (remaining > 0)
        {
            var chunk = (byte)Math.Min(remaining, 250);
            var p1 = (byte)((pos >> 8) & 0x7F);
            var p2 = (byte)(pos & 0xFF);
            var response = Transmit(reader, [0x00, 0xB0, p1, p2, chunk]);
            if (!response.IsSuccess || response.Data.Length == 0)
            {
                return buffer.Count > 0 ? buffer.ToArray() : null;
            }

            buffer.AddRange(response.Data);
            pos += response.Data.Length;
            remaining -= response.Data.Length;
        }

        return buffer.ToArray();
    }

    private static ApduResponse Transmit(ICardReader reader, byte[] command)
    {
        var receive = new byte[512];
        var received = reader.Transmit(command, receive);
        if (received < 2)
        {
            var raw = new byte[received];
            Array.Copy(receive, raw, received);
            return new ApduResponse(raw, 0x00, 0x00);
        }

        var data = new byte[received - 2];
        Array.Copy(receive, data, received - 2);
        return new ApduResponse(data, receive[received - 2], receive[received - 1]);
    }

    private static byte[]? ExtractTransparentPayload(ApduResponse response)
    {
        var bytes = response.Data;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0x97 && i + 1 < bytes.Length)
            {
                var len = bytes[i + 1];
                if (i + 2 + len <= bytes.Length)
                {
                    return bytes[(i + 2)..(i + 2 + len)];
                }
            }
        }

        return response.IsSuccess && bytes.Length > 0 ? bytes : null;
    }

    private static string? FindReaderName()
    {
        try
        {
            using var context = ContextFactory.Instance.Establish(SCardScope.System);
            var readers = context.GetReaders();
            if (readers is null || readers.Length == 0)
            {
                return null;
            }

            return readers.FirstOrDefault(r =>
                       r.Contains("ACR1552", StringComparison.OrdinalIgnoreCase) &&
                       r.Contains("PICC", StringComparison.OrdinalIgnoreCase))
                   ?? readers.FirstOrDefault(r =>
                       r.Contains("PICC", StringComparison.OrdinalIgnoreCase)
                       || r.Contains("Contactless", StringComparison.OrdinalIgnoreCase)
                       || r.Contains("CL ", StringComparison.OrdinalIgnoreCase))
                   ?? readers[0];
        }
        catch (PCSCException)
        {
            return null;
        }
    }

    private static void TryStop(ISCardMonitor monitor)
    {
        try
        {
            if (monitor.Monitoring)
            {
                monitor.Cancel();
            }
        }
        catch
        {
            // ignored
        }
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    private sealed record TagReadOutcome(
        bool Succeeded,
        string? Uid,
        string? Payload,
        string? ErrorMessage,
        string? TechnicalDetails);

    private readonly record struct ApduResponse(byte[] Data, byte Sw1, byte Sw2)
    {
        public bool IsSuccess => Sw1 == 0x90 && Sw2 == 0x00;
        public string Status => $"{Sw1:X2}{Sw2:X2}";
    }
}
