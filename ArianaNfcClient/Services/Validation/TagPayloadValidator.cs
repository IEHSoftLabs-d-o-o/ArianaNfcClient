using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.Validation;

public sealed class TagPayloadValidator : ITagPayloadValidator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly JsonNode? _scheme;

    public TagPayloadValidator()
    {
        _scheme = LoadEmbeddedScheme();
    }

    public TagValidationResult Validate(string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return new TagValidationResult(
                false,
                rawPayload,
                null,
                "Das NFC-Tag enthält keine Daten.",
                "Payload is null or empty.");
        }

        var trimmed = rawPayload.Trim();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(trimmed);
            JsonUnicodeEscapes.UnescapeInPlace(node);
        }
        catch (JsonException ex)
        {
            return new TagValidationResult(
                false,
                trimmed,
                null,
                "Das NFC-Tag enthält kein gültiges JSON.",
                ex.Message);
        }

        if (node is not JsonObject)
        {
            return new TagValidationResult(
                false,
                trimmed,
                null,
                "Das JSON muss ein Objekt sein.",
                $"Root JSON kind is {node?.GetType().Name ?? "null"}.");
        }

        var schemeErrors = ValidateAgainstScheme(node, _scheme, "$");
        schemeErrors.AddRange(ValidateGesundheitsamt(node));
        if (schemeErrors.Count > 0)
        {
            return new TagValidationResult(
                false,
                Pretty(node),
                null,
                schemeErrors[0],
                string.Join(Environment.NewLine, schemeErrors));
        }

        NfcTagPayload? payload;
        try
        {
            payload = node.Deserialize<NfcTagPayload>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            return new TagValidationResult(
                false,
                Pretty(node),
                null,
                "Das JSON entspricht nicht dem Auftragsschema.",
                ex.Message);
        }

        if (payload is null)
        {
            return new TagValidationResult(
                false,
                Pretty(node),
                null,
                "Das JSON konnte nicht in ein Auftrag-Objekt gelesen werden.",
                "Deserializer returned null.");
        }

        if (!string.Equals(payload.Format, "berns-wasser", StringComparison.Ordinal))
        {
            return new TagValidationResult(
                false,
                Pretty(node),
                null,
                "Feld \"Format\" muss \"berns-wasser\" sein.",
                $"Format={payload.Format ?? "(null)"}");
        }

        if (payload.Version != 5)
        {
            return new TagValidationResult(
                false,
                Pretty(node),
                null,
                "Feld \"Version\" muss 5 sein.",
                $"Version={payload.Version?.ToString() ?? "(null)"}");
        }

        if (payload.Analysen is null || payload.Analysen.Count == 0)
        {
            return new TagValidationResult(
                false,
                Pretty(node),
                null,
                "Feld \"Analysen\" muss mindestens einen Eintrag enthalten.",
                "Analysen is missing or empty.");
        }

        return new TagValidationResult(true, Pretty(node), payload, null, null);
    }

    private static List<string> ValidateGesundheitsamt(JsonNode instance)
    {
        var errors = new List<string>();
        if (instance is not JsonObject root || root["Gesundheitsamt"] is not JsonObject amt)
        {
            return errors;
        }

        if (!string.Equals(ReadString(amt["Weitergabe"]), "Ja", StringComparison.OrdinalIgnoreCase))
        {
            return errors;
        }

        foreach (var name in new[] { "Kurzname", "TeisKreisNummer" })
        {
            if (string.IsNullOrWhiteSpace(ReadString(amt[name])))
            {
                errors.Add($"Feld \"{name}\" fehlt, wenn Weitergabe \"Ja\" ist.");
                errors.Add($"$.Gesundheitsamt.{name} is required when Weitergabe is Ja.");
            }
        }

        return errors;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> ValidateAgainstScheme(JsonNode instance, JsonNode? scheme, string path)
    {
        var errors = new List<string>();
        if (scheme is null)
        {
            return errors;
        }

        if (scheme is JsonObject schemeObject && schemeObject["type"] is JsonValue typeValue)
        {
            var expected = typeValue.GetValue<string>();
            if (!MatchesType(instance, expected))
            {
                errors.Add($"{GermanPath(path)}: erwartet {expected}, war {DescribeKind(instance)}.");
                errors.Add($"{path}: expected {expected}, was {DescribeKind(instance)}.");
                return errors;
            }
        }

        if (scheme is JsonObject schemaObj && instance is JsonObject instanceObj)
        {
            if (schemaObj["required"] is JsonArray required)
            {
                foreach (var item in required)
                {
                    var name = item?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!instanceObj.ContainsKey(name) || instanceObj[name] is null)
                    {
                        errors.Add($"Feld \"{name}\" fehlt.");
                        errors.Add($"{path}.{name} is required.");
                    }
                }
            }

            if (schemaObj["properties"] is JsonObject properties)
            {
                foreach (var property in properties)
                {
                    if (instanceObj[property.Key] is { } child)
                    {
                        errors.AddRange(ValidateAgainstScheme(child, property.Value, $"{path}.{property.Key}"));
                    }
                }
            }
        }

        if (scheme is JsonObject arraySchema &&
            instance is JsonArray instanceArray &&
            arraySchema["items"] is { } itemsSchema)
        {
            for (var i = 0; i < instanceArray.Count; i++)
            {
                if (instanceArray[i] is { } child)
                {
                    errors.AddRange(ValidateAgainstScheme(child, itemsSchema, $"{path}[{i}]"));
                }
            }
        }

        return errors;
    }

    private static bool MatchesType(JsonNode instance, string expected) =>
        expected switch
        {
            "object" => instance is JsonObject,
            "array" => instance is JsonArray,
            "string" => instance is JsonValue v && v.TryGetValue<string>(out _),
            "integer" => instance is JsonValue v && (v.TryGetValue<int>(out _) || v.TryGetValue<long>(out _)),
            "number" => instance is JsonValue v && (v.TryGetValue<double>(out _) || v.TryGetValue<int>(out _)),
            "boolean" => instance is JsonValue v && v.TryGetValue<bool>(out _),
            _ => true
        };

    private static string DescribeKind(JsonNode instance) =>
        instance switch
        {
            JsonObject => "object",
            JsonArray => "array",
            JsonValue v when v.TryGetValue<string>(out _) => "string",
            JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
            JsonValue v when v.TryGetValue<int>(out _) || v.TryGetValue<long>(out _) => "integer",
            JsonValue => "number",
            _ => "unknown"
        };

    private static string GermanPath(string path) =>
        path == "$" ? "JSON-Wurzel" : path.TrimStart('$', '.');

    private static string Pretty(JsonNode node) =>
        node.ToJsonString(JsonUnicodeEscapes.RelaxedWrite);

    private static JsonNode? LoadEmbeddedScheme()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ReferenceScheme.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonNode.Parse(json);
    }
}
