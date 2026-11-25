using System.Text.Json;
using System.Text.Json.Nodes;
using ArianaNfcClient.Models;

namespace ArianaNfcClient.Tests.Support;

/// <summary>Builds a valid version 11 tag document (based on a real tag) with optional edits.</summary>
internal static class TagJson
{
    public const string ProtocolId = "01a1f811-643d-7965-8587-49ab45ba5918";
    public const string VorlageName = "Vorlage A";

    private const string Base = """
        {
          "Format": "berns-wasser",
          "Version": 11,
          "Protokoll": { "Id": "01a1f811-643d-7965-8587-49ab45ba5918", "Erstellt": "2026-11-20T09:27:42+01:00" },
          "Probenehmer": { "Bezeichnung": "Herr Dr. J. Dietrich (Dr. Berns Laboratorium)" },
          "Kunde": { "Nummer": "30001", "Name": "Testkunde - Standort" },
          "Gesundheitsamt": { "Kurzname": "Kleve", "TeisKreisNummer": "05154", "Weitergabe": "Ja" },
          "Auftragsvorlage": { "Name": "Vorlage A" },
          "Entnahme": {
            "Zeitpunkt": "2026-11-20T09:27:00+01:00",
            "Objekt": "Haus",
            "Nachprobe": "Nein",
            "ArtDesWassers": "Badebeckenwasser",
            "Wassertyp": "Kaltwasser",
            "Aufbereitung": "Eisenreduktion",
            "Stelle": "Badezimmer"
          },
          "Beschaffenheit": { "Klarheit": "klar", "Faerbung": "farblos", "Geruch": "ohne", "Temperatur": 18 },
          "Analysen": [ "Blei", "Cadmium" ],
          "Bericht": {
            "An": "s.o.",
            "Kostentraeger": "s.o.",
            "ArtDerProbenahme": "Zweck a",
            "ProbenahmegebuehrUeberLabor": false,
            "UnterschriftDatum": "2026-11-20"
          }
        }
        """;

    public static JsonObject Node(Action<JsonObject>? edit = null)
    {
        var node = JsonNode.Parse(Base)!.AsObject();
        edit?.Invoke(node);
        return node;
    }

    public static string Create(Action<JsonObject>? edit = null) =>
        Node(edit).ToJsonString();

    public static NfcTagPayload Payload(Action<JsonObject>? edit = null) =>
        Node(edit).Deserialize<NfcTagPayload>()!;
}
