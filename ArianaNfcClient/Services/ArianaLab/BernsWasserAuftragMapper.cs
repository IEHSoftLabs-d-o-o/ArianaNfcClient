using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.ArianaLab;

public static class BernsWasserAuftragMapper
{
    private static readonly (string AttributeName, Func<NfcTagPayload, string?> Value)[] AttributeMappings =
    [
        ("Wassertyp", p => p.Entnahme?.Wassertyp),
        ("Aufbereitung", p => p.Entnahme?.Aufbereitung),
        ("Gesundheitsamt", p => p.Gesundheitsamt?.Kurzname),
        ("TeisKreisNummer", p => p.Gesundheitsamt?.TeisKreisNummer),
        ("Weitergabe", p => p.Gesundheitsamt?.Weitergabe),
        ("Probenahme (Zeit)", p => FormatSampleTime(p.Entnahme?.Zeitpunkt))
    ];

    public static JsonObject Map(
        NfcTagPayload payload,
        JsonObject? templateAuftrag,
        string? protocolAttributeName = null)
    {
        var auftrag = templateAuftrag is null
            ? new JsonObject()
            : (JsonObject)templateAuftrag.DeepClone();

        auftrag.Remove("_id");
        auftrag.Remove("_links");
        auftrag.Remove("Proben");
        auftrag.Remove("Status");

        SetKunde(auftrag, payload.Kunde);
        SetString(auftrag, "Auftragsvorlage", payload.Auftragsvorlage?.Name);
        SetString(auftrag, "Probenehmer", payload.Probenehmer?.Bezeichnung);
        SetString(auftrag, "Probenbezeichnung", payload.Entnahme?.Stelle);
        SetString(auftrag, "Eingangstemperatur", payload.Beschaffenheit?.Temperatur?.ToString(CultureInfo.InvariantCulture));

        if (DateTimeOffset.TryParse(payload.Entnahme?.Zeitpunkt, out var entnahme))
        {
            auftrag["Probenahmedatum"] = entnahme.UtcDateTime.ToString("o");
        }

        if (DateTimeOffset.TryParse(payload.Protokoll?.Erstellt, out var erstellt))
        {
            auftrag["Probeneingang"] = erstellt.UtcDateTime.ToString("o");
        }

        var attributes = auftrag["Attribute"] as JsonArray ?? [];
        if (auftrag["Attribute"] is not JsonArray)
        {
            auftrag["Attribute"] = attributes;
        }

        RemoveAttributes(attributes, SensoryAttributeNames);
        SetProbenahmeort(attributes, payload.Entnahme?.Stelle);
        SetNamedAttribute(attributes, "Bestätigen", FormatBool(payload.Bericht?.Bestaetigen), "Bestätigen", "Bestättigen", "Bestaetigen");
        SetNamedAttribute(attributes, "Probenahmegebühr", FormatBool(payload.Bericht?.Probenahmegebuehr), "Probenahmegebühr", "Probenahmegebuehr");
        SetNamedAttribute(
            attributes,
            "Probenahmegebühr über Labor",
            FormatBool(payload.Bericht?.ProbenahmegebuehrUeberLabor),
            "Probenahmegebühr über Labor",
            "ProbenahmegebuehrUeberLabor");
        SetNamedAttribute(attributes, "Datum der Unterschrift", FormatGermanDate(payload.Bericht?.UnterschriftDatum), "Datum der Unterschrift", "UnterschriftDatum");

        foreach (var (name, getter) in AttributeMappings)
        {
            var value = getter(payload);
            if (!string.IsNullOrWhiteSpace(value))
            {
                UpsertAttribute(attributes, name, value);
            }
        }

        var protocolName = string.IsNullOrWhiteSpace(protocolAttributeName)
            ? ArianaLabOptions.DefaultProtocolAttributeName
            : protocolAttributeName;
        if (!string.IsNullOrWhiteSpace(payload.Protokoll?.Id))
        {
            UpsertAttribute(attributes, protocolName, payload.Protokoll.Id);
        }

        FilterAnalysen(auftrag, payload.Analysen);
        EnsureAnalysenFromTag(auftrag, payload.Analysen);
        ApplyBeschaffenheitToAnalysen(auftrag, payload.Beschaffenheit);
        return auftrag;
    }

    private static readonly string[] SensoryAttributeNames = ["Klarheit", "Faerbung", "Färbung", "Geruch", "Temperatur"];

    private static readonly (string[] Names, Func<BeschaffenheitPayload, string?> Value)[] BeschaffenheitAnalysen =
    [
        (["Klarheit"], b => b.Klarheit),
        (["Färbung", "Faerbung"], b => b.Faerbung),
        (["Geruch"], b => b.Geruch),
        (["Temperatur"], b => b.Temperatur?.ToString(CultureInfo.InvariantCulture))
    ];

    private static void ApplyBeschaffenheitToAnalysen(JsonObject auftrag, BeschaffenheitPayload? beschaffenheit)
    {
        if (beschaffenheit is null || auftrag["Positionen"] is not JsonArray positionen)
        {
            return;
        }

        foreach (var positionNode in positionen)
        {
            if (positionNode is not JsonObject position || position["Analysen"] is not JsonArray analysen)
            {
                continue;
            }

            foreach (var analyseNode in analysen)
            {
                if (analyseNode is not JsonObject analyse)
                {
                    continue;
                }

                var name = ReadString(analyse["Name"]);
                var value = name is null ? null : BeschaffenheitValueFor(name, beschaffenheit);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var analyseAttributes = analyse["Attribute"] as JsonArray ?? [];
                if (analyse["Attribute"] is not JsonArray)
                {
                    analyse["Attribute"] = analyseAttributes;
                }

                UpsertAttribute(analyseAttributes, "$vorbelegung", value);
            }
        }
    }

    private static string? BeschaffenheitValueFor(string analyseName, BeschaffenheitPayload beschaffenheit)
    {
        foreach (var (names, getter) in BeschaffenheitAnalysen)
        {
            if (names.Any(name => string.Equals(name, analyseName, StringComparison.OrdinalIgnoreCase)))
            {
                return getter(beschaffenheit);
            }
        }

        return null;
    }

    private static void RemoveAttributes(JsonArray attributes, IEnumerable<string> names)
    {
        for (var i = attributes.Count - 1; i >= 0; i--)
        {
            var name = attributes[i] is JsonObject existing ? ReadString(existing["Name"]) : null;
            if (name is not null && names.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
            {
                attributes.RemoveAt(i);
            }
        }
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void EnsureAnalysenFromTag(JsonObject auftrag, List<string>? analysen)
    {
        if (analysen is null || analysen.Count == 0)
        {
            return;
        }

        if (auftrag["Positionen"] is JsonArray existing && existing.Count > 0)
        {
            return;
        }

        var items = new JsonArray();
        foreach (var name in analysen)
        {
            items.Add(new JsonObject { ["Name"] = name });
        }

        auftrag["Positionen"] = new JsonArray
        {
            new JsonObject { ["Analysen"] = items }
        };
    }

    private static void FilterAnalysen(JsonObject auftrag, List<string>? wanted)
    {
        if (wanted is null || wanted.Count == 0 || auftrag["Positionen"] is not JsonArray positionen)
        {
            return;
        }

        var anyMatch = false;
        foreach (var positionNode in positionen)
        {
            if (positionNode is not JsonObject position || position["Analysen"] is not JsonArray analysen)
            {
                continue;
            }

            var kept = new JsonArray();
            foreach (var analyseNode in analysen)
            {
                var name = analyseNode is JsonObject o
                    ? o["Name"]?.GetValue<string>()
                    : analyseNode?.GetValue<string>();
                if (name is not null && MatchesAnalyse(name, wanted))
                {
                    kept.Add(analyseNode!.DeepClone());
                    anyMatch = true;
                }
            }

            if (kept.Count > 0)
            {
                position["Analysen"] = kept;
            }
        }

        _ = anyMatch;
    }

    private static bool MatchesAnalyse(string name, IEnumerable<string> wanted) =>
        wanted.Any(w =>
            name.Equals(w, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            w.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static void SetProbenahmeort(JsonArray attributes, string? stelle) =>
        SetNamedAttribute(attributes, "Probenahmeort Wasserprobe", stelle, "Probenahmeort Wasserprobe", ".Probenahmeort Wasserprobe");

    private static void SetNamedAttribute(JsonArray attributes, string canonicalName, string? value, params string[] names)
    {
        JsonObject? kept = null;
        for (var i = attributes.Count - 1; i >= 0; i--)
        {
            if (attributes[i] is not JsonObject existing || !MatchesName(ReadString(existing["Name"]), names))
            {
                continue;
            }

            if (kept is null)
            {
                kept = existing;
                continue;
            }

            attributes.RemoveAt(i);
        }

        if (kept is null)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                UpsertAttribute(attributes, canonicalName, value);
            }

            return;
        }

        kept["Name"] = canonicalName;
        if (!string.IsNullOrWhiteSpace(value))
        {
            kept["Wert"] = value;
        }
    }

    private static bool MatchesName(string? name, IEnumerable<string> names) =>
        name is not null && names.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

    private static void UpsertAttribute(JsonArray attributes, string name, string value)
    {
        foreach (var node in attributes)
        {
            if (node is JsonObject existing &&
                string.Equals(existing["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            {
                existing["Wert"] = value;
                return;
            }
        }

        attributes.Add(new JsonObject
        {
            ["Name"] = name,
            ["Wert"] = JsonValue.Create(value)
        });
    }

    private static void SetKunde(JsonObject target, KundePayload? kunde)
    {
        if (kunde is null)
        {
            return;
        }

        SetString(target, "Auftraggeber", kunde.Name);
        if (!string.IsNullOrWhiteSpace(kunde.Nummer))
        {
            target["AuftraggeberIdRef"] = kunde.Nummer;
        }
        else
        {
            target.Remove("AuftraggeberIdRef");
        }
    }

    private static void SetString(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }

    private static string? FormatBool(bool? value) =>
        value is null ? null : value.Value ? "Ja" : "Nein";

    private static string? FormatGermanDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) &&
            !DateTimeOffset.TryParse(value, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out parsed))
        {
            return null;
        }

        return parsed.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    private static string? FormatSampleTime(string? zeitpunkt)
    {
        if (!DateTimeOffset.TryParse(zeitpunkt, out var entnahme))
        {
            return null;
        }

        return entnahme.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
