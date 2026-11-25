using System.Text;
using ArianaNfcClient.Services.Nfc;

namespace ArianaNfcClient.Tests;

public class NdefDecoderTests
{
    private static byte[] MimeRecord(string json)
    {
        var type = Encoding.ASCII.GetBytes("application/json");
        var payload = Encoding.UTF8.GetBytes(json);
        return [0xD2, (byte)type.Length, (byte)payload.Length, .. type, .. payload];
    }

    private static byte[] TextRecord(string text, string language = "en")
    {
        var lang = Encoding.ASCII.GetBytes(language);
        var body = Encoding.UTF8.GetBytes(text);
        byte[] payload = [(byte)lang.Length, .. lang, .. body];
        return [0xD1, 0x01, (byte)payload.Length, (byte)'T', .. payload];
    }

    /// <summary>Type 2 memory: 16 bytes of header pages, then the TLV area.</summary>
    private static byte[] Type2Memory(params byte[] tlv)
    {
        var memory = new byte[16 + tlv.Length];
        Array.Copy(tlv, 0, memory, 16, tlv.Length);
        return memory;
    }

    // ---- TryDecodeToText ----

    [Fact]
    public void Decode_ReadsAMimeJsonRecord()
    {
        var ok = NdefDecoder.TryDecodeToText(MimeRecord("{\"a\":1}"), out var text, out var error);

        Assert.True(ok, error);
        Assert.Equal("{\"a\":1}", text);
    }

    [Fact]
    public void Decode_ReadsAWellKnownTextRecord()
    {
        var ok = NdefDecoder.TryDecodeToText(TextRecord("Hallo Welt", "de"), out var text, out var error);

        Assert.True(ok, error);
        Assert.Equal("Hallo Welt", text);
    }

    [Fact]
    public void Decode_RejectsATruncatedRecord()
    {
        byte[] truncated = [0xD2, 0x10, 0xC8, .. Encoding.ASCII.GetBytes("application/json"), 0x7B, 0x7D];

        var ok = NdefDecoder.TryDecodeToText(truncated, out var text, out var error);

        Assert.False(ok);
        Assert.Null(text);
        Assert.Equal("NDEF record is truncated (type/id/payload).", error);
    }

    [Fact]
    public void Decode_RejectsAnEmptyMessage()
    {
        var ok = NdefDecoder.TryDecodeToText(ReadOnlySpan<byte>.Empty, out _, out var error);

        Assert.False(ok);
        Assert.Equal("NDEF message is empty.", error);
    }

    // ---- TryExtractNdefFromType2 ----

    [Fact]
    public void Extract_ReturnsTheCompleteNdefMessage()
    {
        var record = MimeRecord("{\"a\":1}");
        var memory = Type2Memory([0x03, (byte)record.Length, .. record, 0xFE]);

        var ok = NdefDecoder.TryExtractNdefFromType2(memory, out var ndef, out var found, out var error);

        Assert.True(ok, error);
        Assert.True(found);
        Assert.Equal(record, ndef);
    }

    [Fact]
    public void Extract_SkipsPaddingAndOtherTlvsBeforeTheNdefTlv()
    {
        var record = MimeRecord("{}");
        var memory = Type2Memory([0x00, 0x00, 0x01, 0x03, 0xA0, 0x0C, 0x34, 0x03, (byte)record.Length, .. record]);

        var ok = NdefDecoder.TryExtractNdefFromType2(memory, out var ndef, out _, out var error);

        Assert.True(ok, error);
        Assert.Equal(record, ndef);
    }

    [Fact]
    public void Extract_ReadsALongFormLength()
    {
        var record = MimeRecord("{}");
        var memory = Type2Memory([0x03, 0xFF, 0x00, (byte)record.Length, .. record]);

        var ok = NdefDecoder.TryExtractNdefFromType2(memory, out var ndef, out _, out var error);

        Assert.True(ok, error);
        Assert.Equal(record, ndef);
    }

    [Fact]
    public void Extract_RejectsATlvLongerThanTheBytesRead()
    {
        var record = MimeRecord("{\"a\":1}");
        var memory = Type2Memory([0x03, (byte)record.Length, .. record.Take(record.Length - 4)]);

        var ok = NdefDecoder.TryExtractNdefFromType2(memory, out var ndef, out var found, out var error);

        Assert.False(ok);
        Assert.Null(ndef);
        Assert.True(found);
        Assert.Contains("declares", error);
        Assert.Contains("were read", error);
    }

    [Fact]
    public void Extract_RejectsAnEmptyNdefTlv()
    {
        var ok = NdefDecoder.TryExtractNdefFromType2(Type2Memory(0x03, 0x00, 0xFE), out var ndef, out var found, out var error);

        Assert.False(ok);
        Assert.Null(ndef);
        Assert.True(found);
        Assert.Equal("NDEF TLV has no payload.", error);
    }

    [Fact]
    public void Extract_ReportsWhenThereIsNoNdefTlv()
    {
        var ok = NdefDecoder.TryExtractNdefFromType2(Type2Memory(0x01, 0x01, 0x00, 0xFE), out _, out var found, out var error);

        Assert.False(ok);
        Assert.False(found);
        Assert.Equal("No NDEF TLV found on Type 2 tag.", error);
    }

    [Fact]
    public void Extract_TreatsACutOffNdefTlvHeaderAsIncomplete()
    {
        var ok = NdefDecoder.TryExtractNdefFromType2(Type2Memory(0x03), out _, out var found, out var error);

        Assert.False(ok);
        Assert.True(found);
        Assert.Equal("Type 2 TLV is truncated.", error);
    }
}
