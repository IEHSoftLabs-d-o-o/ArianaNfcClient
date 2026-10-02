using System.Text.Json.Serialization;

namespace NfcTagReader.Models;

public sealed class NfcTagPayload
{
    [JsonPropertyName("Format")]
    public string? Format { get; set; }

    [JsonPropertyName("Version")]
    public int? Version { get; set; }

    [JsonPropertyName("Protokoll")]
    public ProtokollPayload? Protokoll { get; set; }

    [JsonPropertyName("Probenehmer")]
    public ProbenehmerPayload? Probenehmer { get; set; }

    [JsonPropertyName("Kunde")]
    public KundePayload? Kunde { get; set; }

    [JsonPropertyName("Gesundheitsamt")]
    public GesundheitsamtPayload? Gesundheitsamt { get; set; }

    [JsonPropertyName("Auftragsvorlage")]
    public AuftragsvorlagePayload? Auftragsvorlage { get; set; }

    [JsonPropertyName("Entnahme")]
    public EntnahmePayload? Entnahme { get; set; }

    [JsonPropertyName("Beschaffenheit")]
    public BeschaffenheitPayload? Beschaffenheit { get; set; }

    [JsonPropertyName("Analysen")]
    public List<string>? Analysen { get; set; }

    [JsonPropertyName("Bericht")]
    public BerichtPayload? Bericht { get; set; }
}

public sealed class ProtokollPayload
{
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Erstellt")]
    public string? Erstellt { get; set; }
}

public sealed class ProbenehmerPayload
{
    [JsonPropertyName("Bezeichnung")]
    public string? Bezeichnung { get; set; }
}

public sealed class KundePayload
{
    [JsonPropertyName("Nummer")]
    public string? Nummer { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

public sealed class GesundheitsamtPayload
{
    [JsonPropertyName("Kurzname")]
    public string? Kurzname { get; set; }

    [JsonPropertyName("TeisKreisNummer")]
    public string? TeisKreisNummer { get; set; }

    [JsonPropertyName("Weitergabe")]
    public string? Weitergabe { get; set; }
}

public sealed class AuftragsvorlagePayload
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

public sealed class EntnahmePayload
{
    [JsonPropertyName("Zeitpunkt")]
    public string? Zeitpunkt { get; set; }

    [JsonPropertyName("Wassertyp")]
    public string? Wassertyp { get; set; }

    [JsonPropertyName("Aufbereitung")]
    public string? Aufbereitung { get; set; }

    [JsonPropertyName("Stelle")]
    public string? Stelle { get; set; }
}

public sealed class BeschaffenheitPayload
{
    [JsonPropertyName("Klarheit")]
    public string? Klarheit { get; set; }

    [JsonPropertyName("Färbung")]
    public string? Faerbung { get; set; }

    [JsonPropertyName("Geruch")]
    public string? Geruch { get; set; }

    [JsonPropertyName("Temperatur")]
    public double? Temperatur { get; set; }
}

public sealed class BerichtPayload
{
    [JsonPropertyName("Bestätigen")]
    public bool? Bestaetigen { get; set; }

    [JsonPropertyName("Probenahmegebuehr")]
    public bool? Probenahmegebuehr { get; set; }

    [JsonPropertyName("ProbenahmegebuehrUeberLabor")]
    public bool? ProbenahmegebuehrUeberLabor { get; set; }

    [JsonPropertyName("UnterschriftDatum")]
    public string? UnterschriftDatum { get; set; }
}
