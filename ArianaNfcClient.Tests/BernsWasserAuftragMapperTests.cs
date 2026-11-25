using System.Globalization;
using System.Text.Json.Nodes;
using ArianaNfcClient.Models;
using ArianaNfcClient.Services.ArianaLab;
using ArianaNfcClient.Tests.Support;

namespace ArianaNfcClient.Tests;

public class BernsWasserAuftragMapperTests
{
    private static JsonObject Template() => JsonNode.Parse(ArianaLabClientFactory.TemplateAuftrag)!.AsObject();

    private static string PositionSummary(JsonObject auftrag) =>
        string.Join(" | ", ((JsonArray)auftrag["Positionen"]!).Select(position =>
            position!["Name"] + ":" + string.Join(",", ((JsonArray)position["Analysen"]!)
                .Select(analyse => analyse!["Name"]!.GetValue<string>()))));

    private static string? Attribute(JsonObject auftrag, string name) =>
        ((JsonArray?)auftrag["Attribute"])?
        .OfType<JsonObject>()
        .FirstOrDefault(a => string.Equals(a["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))?
        ["Wert"]?.GetValue<string>();

    private static NfcTagPayload TagWithAnalysen(params string[] analysen) =>
        TagJson.Payload(tag => tag["Analysen"] = new JsonArray(analysen.Select(a => (JsonNode)a).ToArray()));

    // ---- Analysen ----

    [Fact]
    public void Map_KeepsOnlyTagAnalysenInEveryPosition()
    {
        var auftrag = BernsWasserAuftragMapper.Map(TagWithAnalysen("Blei", "E. coli"), Template());

        Assert.Equal("Chemie:Blei | Mibi:E. coli", PositionSummary(auftrag));
    }

    [Fact]
    public void Map_RemovesPositionWithoutAnyMatch()
    {
        var auftrag = BernsWasserAuftragMapper.Map(TagWithAnalysen("E. coli"), Template());

        Assert.Equal("Mibi:E. coli", PositionSummary(auftrag));
    }

    [Fact]
    public void Map_KeepsFullTemplateWhenTagHasNoAnalysen()
    {
        var payload = TagJson.Payload(tag => tag.Remove("Analysen"));

        var auftrag = BernsWasserAuftragMapper.Map(payload, Template());

        Assert.Equal("Chemie:Blei,Cadmium,Arsen | Mibi:E. coli", PositionSummary(auftrag));
    }

    [Fact]
    public void Map_MatchesAnalysenCaseInsensitiveAndTrimmed()
    {
        var auftrag = BernsWasserAuftragMapper.Map(TagWithAnalysen(" blei ", "E. COLI"), Template());

        Assert.Equal("Chemie:Blei | Mibi:E. coli", PositionSummary(auftrag));
    }

    [Fact]
    public void Map_DoesNotMatchPartialNames()
    {
        var template = JsonNode.Parse("""
            { "Positionen": [ { "Analysen": [ { "Name": "pH Wert" }, { "Name": "Blei" } ] } ] }
            """)!.AsObject();

        var auftrag = BernsWasserAuftragMapper.Map(TagWithAnalysen("pH", "Blei"), template);

        Assert.Equal(":Blei", PositionSummary(auftrag));
    }

    [Fact]
    public void FindUnknownAnalysen_ReportsNamesThatAreNotInTheTemplate()
    {
        var unknown = BernsWasserAuftragMapper.FindUnknownAnalysen(Template(), ["Legionellen", " blei ", "E. coli", "legionellen"]);

        Assert.Equal(["Legionellen"], unknown);
    }

    [Fact]
    public void FindUnknownAnalysen_IsEmptyWithoutTagAnalysen()
    {
        Assert.Empty(BernsWasserAuftragMapper.FindUnknownAnalysen(Template(), null));
        Assert.Empty(BernsWasserAuftragMapper.FindUnknownAnalysen(Template(), []));
    }

    [Fact]
    public void FindUnknownAnalysen_ReportsEverythingWhenTemplateIsMissing()
    {
        var unknown = BernsWasserAuftragMapper.FindUnknownAnalysen(null, ["Blei"]);

        Assert.Equal(["Blei"], unknown);
    }

    // ---- Zeitpunkt ----

    [Fact]
    public void Map_TimeWithoutOffset_IsReadAsLocalTime()
    {
        var payload = TagJson.Payload(tag => tag["Entnahme"]!["Zeitpunkt"] = "2026-11-25T00:30");

        var auftrag = BernsWasserAuftragMapper.Map(payload, Template());

        var sent = DateTime.Parse(
            auftrag["Probenahmedatum"]!.GetValue<string>(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, sent.Kind);
        Assert.Equal(new DateTime(2026, 11, 25, 0, 30, 0), sent.ToLocalTime());
        Assert.Equal("00:30", Attribute(auftrag, "Probenahme (Zeit)"));
    }

    [Fact]
    public void Map_TimeWithUtcOffset_IsShownAsLocalTime()
    {
        var payload = TagJson.Payload(tag => tag["Entnahme"]!["Zeitpunkt"] = "2026-11-25T23:30:00Z");

        var auftrag = BernsWasserAuftragMapper.Map(payload, Template());

        var local = DateTimeOffset.Parse("2026-11-25T23:30:00Z", CultureInfo.InvariantCulture).ToLocalTime();
        Assert.Equal(local.ToString("HH:mm", CultureInfo.InvariantCulture), Attribute(auftrag, "Probenahme (Zeit)"));
        Assert.EndsWith("Z", auftrag["Probenahmedatum"]!.GetValue<string>());
    }

    [Fact]
    public void Map_ProbeneingangComesFromProtokollErstellt()
    {
        var auftrag = BernsWasserAuftragMapper.Map(TagJson.Payload(), Template());

        var sent = DateTimeOffset.Parse(auftrag["Probeneingang"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T09:27:42+01:00", CultureInfo.InvariantCulture), sent);
    }

    // ---- template handling ----

    [Fact]
    public void Map_DoesNotChangeTheTemplateAndRemovesServerFields()
    {
        var template = JsonNode.Parse("""
            {
              "_id": "tmpl", "_links": [], "Status": "offen", "Proben": [ { "Tagebuchnummer": "1" } ],
              "Positionen": [ { "Analysen": [ { "Name": "Blei" }, { "Name": "Arsen" } ] } ]
            }
            """)!.AsObject();
        var before = template.ToJsonString();

        var auftrag = BernsWasserAuftragMapper.Map(TagWithAnalysen("Blei"), template);

        Assert.Equal(before, template.ToJsonString());
        Assert.False(auftrag.ContainsKey("_id"));
        Assert.False(auftrag.ContainsKey("_links"));
        Assert.False(auftrag.ContainsKey("Proben"));
        Assert.False(auftrag.ContainsKey("Status"));
    }

    [Fact]
    public void Map_SetsKundeVorlageProbenehmerAndProtocolId()
    {
        var auftrag = BernsWasserAuftragMapper.Map(TagJson.Payload(), Template());

        Assert.Equal("Testkunde - Standort", auftrag["Auftraggeber"]!.GetValue<string>());
        Assert.Equal("30001", auftrag["AuftraggeberIdRef"]!.GetValue<string>());
        Assert.Equal(TagJson.VorlageName, auftrag["Auftragsvorlage"]!.GetValue<string>());
        Assert.Equal("Herr Dr. J. Dietrich (Dr. Berns Laboratorium)", auftrag["Probenehmer"]!.GetValue<string>());
        Assert.Equal(TagJson.ProtocolId, Attribute(auftrag, ArianaLabOptions.DefaultProtocolAttributeName));
    }

    [Theory]
    [InlineData("Kaltwasser", "PWC")]
    [InlineData("Heisswasser", "PWH")]
    [InlineData("Mischwasser", "MW")]
    public void Map_AbbreviatesWassertypInProbenbezeichnung(string wassertyp, string expected)
    {
        var payload = TagJson.Payload(tag => tag["Entnahme"]!["Wassertyp"] = wassertyp);

        var auftrag = BernsWasserAuftragMapper.Map(payload, Template());

        Assert.Equal($"Haus, Badezimmer, {expected}", auftrag["Probenbezeichnung"]!.GetValue<string>());
    }

    // ---- Kopie / Nachprobe ----

    [Fact]
    public void Map_SetsKopieWhenWeitergabeIsJaAndAuthorityIsConfigured()
    {
        var auftrag = BernsWasserAuftragMapper.Map(
            TagJson.Payload(),
            Template(),
            healthAuthorities: ["Gesundheitsamt Moers", "Gesundheitsamt Kleve, Abt. 1"]);

        Assert.Equal("Gesundheitsamt Kleve, Abt. 1", Attribute(auftrag, "Kopie"));
    }

    [Fact]
    public void Map_SetsNoKopieWhenAuthorityIsNotConfigured()
    {
        var auftrag = BernsWasserAuftragMapper.Map(
            TagJson.Payload(),
            Template(),
            healthAuthorities: ["Gesundheitsamt Moers"]);

        Assert.Null(Attribute(auftrag, "Kopie"));
    }

    [Fact]
    public void Map_SetsNoKopieWhenWeitergabeIsNein()
    {
        var payload = TagJson.Payload(tag => tag["Gesundheitsamt"]!["Weitergabe"] = "Nein");

        var auftrag = BernsWasserAuftragMapper.Map(payload, Template(), healthAuthorities: ["Gesundheitsamt Kleve"]);

        Assert.Null(Attribute(auftrag, "Kopie"));
        Assert.False(BernsWasserAuftragMapper.RequiresKopie(payload.Gesundheitsamt));
    }

    [Fact]
    public void Map_AddsNachprobeAsBemerkung()
    {
        var payload = TagJson.Payload(tag => tag["Entnahme"]!["Nachprobe"] = "Ja");

        var auftrag = BernsWasserAuftragMapper.Map(payload, Template());

        Assert.Equal("Nachprobe", Attribute(auftrag, "Bemerkung"));
    }

    [Fact]
    public void Map_AddsNoBemerkungWhenNachprobeIsNein()
    {
        var auftrag = BernsWasserAuftragMapper.Map(TagJson.Payload(), Template());

        Assert.Null(Attribute(auftrag, "Bemerkung"));
    }
}
