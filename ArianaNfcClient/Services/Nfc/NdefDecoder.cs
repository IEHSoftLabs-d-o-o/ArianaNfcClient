using System.Text;

namespace ArianaNfcClient.Services.Nfc;

public static class NdefDecoder
{
    public static bool TryDecodeToText(ReadOnlySpan<byte> ndefMessage, out string? text, out string? error)
    {
        text = null;
        error = null;
        if (ndefMessage.IsEmpty)
        {
            error = "NDEF message is empty.";
            return false;
        }

        var offset = 0;
        while (offset < ndefMessage.Length)
        {
            var header = ndefMessage[offset++];
            var messageBegin = (header & 0x80) != 0;
            var messageEnd = (header & 0x40) != 0;
            var chunked = (header & 0x20) != 0;
            var shortRecord = (header & 0x10) != 0;
            var idLengthPresent = (header & 0x08) != 0;
            var tnf = header & 0x07;

            _ = messageBegin;
            _ = chunked;

            if (offset >= ndefMessage.Length)
            {
                error = "NDEF record is truncated (type length).";
                return false;
            }

            var typeLength = ndefMessage[offset++];
            int payloadLength;
            if (shortRecord)
            {
                if (offset >= ndefMessage.Length)
                {
                    error = "NDEF record is truncated (payload length).";
                    return false;
                }

                payloadLength = ndefMessage[offset++];
            }
            else
            {
                if (offset + 4 > ndefMessage.Length)
                {
                    error = "NDEF record is truncated (payload length).";
                    return false;
                }

                payloadLength = (ndefMessage[offset] << 24)
                                | (ndefMessage[offset + 1] << 16)
                                | (ndefMessage[offset + 2] << 8)
                                | ndefMessage[offset + 3];
                offset += 4;
            }

            var idLength = 0;
            if (idLengthPresent)
            {
                if (offset >= ndefMessage.Length)
                {
                    error = "NDEF record is truncated (id length).";
                    return false;
                }

                idLength = ndefMessage[offset++];
            }

            if (offset + typeLength + idLength + payloadLength > ndefMessage.Length)
            {
                error = "NDEF record is truncated (type/id/payload).";
                return false;
            }

            var type = Encoding.UTF8.GetString(ndefMessage.Slice(offset, typeLength));
            offset += typeLength + idLength;
            var payload = ndefMessage.Slice(offset, payloadLength);
            offset += payloadLength;

            if (TryPayloadToText(tnf, type, payload, out text))
            {
                return true;
            }

            if (messageEnd)
            {
                break;
            }
        }

        error = "NDEF message contains no text or JSON record.";
        return false;
    }

    public static bool TryExtractNdefFromType2(ReadOnlySpan<byte> memory, out byte[]? ndef, out string? error)
    {
        ndef = null;
        error = null;

        // Capability Container is at page 3 (offset 12) on NTAG / Ultralight.
        var start = memory.Length >= 16 ? 16 : 0;
        var i = start;
        while (i < memory.Length)
        {
            var tlvType = memory[i];
            if (tlvType == 0x00)
            {
                i++;
                continue;
            }

            if (tlvType == 0xFE)
            {
                break;
            }

            if (i + 1 >= memory.Length)
            {
                error = "Type 2 TLV is truncated.";
                return false;
            }

            int headerSize;
            int length;
            if (memory[i + 1] == 0xFF)
            {
                if (i + 3 >= memory.Length)
                {
                    error = "Type 2 TLV long-length is truncated.";
                    return false;
                }

                length = (memory[i + 2] << 8) | memory[i + 3];
                headerSize = 4;
            }
            else
            {
                length = memory[i + 1];
                headerSize = 2;
            }

            if (tlvType == 0x03)
            {
                var available = Math.Min(length, memory.Length - i - headerSize);
                if (available <= 0)
                {
                    error = "NDEF TLV has no payload.";
                    return false;
                }

                ndef = memory.Slice(i + headerSize, available).ToArray();
                return true;
            }

            i += headerSize + length;
        }

        error = "No NDEF TLV found on Type 2 tag.";
        return false;
    }

    private static bool TryPayloadToText(int tnf, string type, ReadOnlySpan<byte> payload, out string? text)
    {
        text = null;
        if (payload.IsEmpty)
        {
            return false;
        }

        // NFC Forum well-known Text
        if (tnf == 0x01 && type == "T")
        {
            var status = payload[0];
            var langLength = status & 0x3F;
            if (1 + langLength > payload.Length)
            {
                return false;
            }

            var utf16 = (status & 0x80) != 0;
            var body = payload[(1 + langLength)..];
            text = utf16 ? Encoding.Unicode.GetString(body) : Encoding.UTF8.GetString(body);
            return !string.IsNullOrWhiteSpace(text);
        }

        // MIME
        if (tnf == 0x02 &&
            (type.Contains("json", StringComparison.OrdinalIgnoreCase) ||
             type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
        {
            text = Encoding.UTF8.GetString(payload);
            return !string.IsNullOrWhiteSpace(text);
        }

        // Absolute URI or UTF-8 fallback if it looks like JSON
        var asUtf8 = Encoding.UTF8.GetString(payload);
        var trimmed = asUtf8.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            text = asUtf8;
            return true;
        }

        return false;
    }
}
