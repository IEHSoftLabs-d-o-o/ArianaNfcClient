using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.ArianaLab;

public static class BernsWasserAuftragMapper
{
    private static readonly (string AttributeName, Func<NfcTagPayload, string?> Value)[] AttributeMappings =
    [
        (".Probenahmeort Wasserprobe", p => p.Entnahme?.Stelle),
        ("Probenahmeort Wasserprobe", p => p.Entnahme?.Stelle),
        ("Wassertyp", p => p.Entnahme?.Wassertyp),
        ("Aufbereitung", p => p.Entnahme?.Aufbereitung),
        ("Gesundheitsamt", p => p.Gesundheitsamt?.Kurzname),
        ("TeisKreisNummer", p => p.Gesundheitsamt?.TeisKreisNummer),
        ("Weitergabe", p => p.Gesundheitsamt?.Weitergabe),
        ("Bestaetigen", p => FormatBool(p.Bericht?.Bestaetigen)),
        ("Bestätigen", p => FormatBool(p.Bericht?.Bestaetigen)),
        ("Probenahmegebuehr", p => FormatBool(p.Bericht?.Probenahmegebuehr)),
        ("ProbenahmegebuehrUeberLabor", p => FormatBool(p.Bericht?.ProbenahmegebuehrUeberLabor)),
        ("UnterschriftDatum", p => p.Bericht?.UnterschriftDatum)
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
}
