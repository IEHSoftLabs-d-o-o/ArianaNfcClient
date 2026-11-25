using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArianaNfcClient.Services.Validation;

internal static class JsonUnicodeEscapes
{
    private static readonly Regex UnicodeEscape = new(
        @"\\u([dD][89abAB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})|\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled);

    public static readonly JsonSerializerOptions RelaxedWrite = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions RelaxedWriteCompact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void UnescapeInPlace(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Decode(text);
                    }
                    else
                    {
                        UnescapeInPlace(obj[key]);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = Decode(text);
                    }
                    else
                    {
                        UnescapeInPlace(array[i]);
                    }
                }

                break;
        }
    }

    public static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("\\u", StringComparison.Ordinal))
        {
            return value;
        }

        return UnicodeEscape.Replace(value, match =>
        {
            if (match.Groups[1].Success)
            {
                return new string([ParseCodeUnit(match.Groups[1].Value), ParseCodeUnit(match.Groups[2].Value)]);
            }

            var unit = ParseCodeUnit(match.Groups[3].Value);
            return char.IsSurrogate(unit) ? match.Value : unit.ToString();
        });
    }

    private static char ParseCodeUnit(string hex) =>
        (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
