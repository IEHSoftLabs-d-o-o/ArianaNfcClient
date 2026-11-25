using System.Text.Json.Nodes;
using ArianaNfcClient.Services.Validation;
using ArianaNfcClient.Tests.Support;

namespace ArianaNfcClient.Tests;

public class TagPayloadValidatorTests
{
    private readonly TagPayloadValidator _validator = new();

    [Fact]
    public void Validate_AcceptsAValidTag()
    {
        var result = _validator.Validate(TagJson.Create());

        Assert.True(result.IsValid, result.TechnicalDetails);
        Assert.NotNull(result.Payload);
        Assert.Null(result.Reason);
        Assert.Equal(TagJson.ProtocolId, result.Payload!.Protokoll!.Id);
        Assert.Equal(["Blei", "Cadmium"], result.Payload.Analysen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyPayload(string? payload)
    {
        var result = _validator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.Equal("Das NFC-Tag enthält keine Daten.", result.Reason);
    }

    [Fact]
    public void Validate_RejectsInvalidJson()
    {
        var result = _validator.Validate("this is not json");

        Assert.False(result.IsValid);
        Assert.Equal("Das NFC-Tag enthält kein gültiges JSON.", result.Reason);
    }

    [Fact]
    public void Validate_RejectsNonObjectRoot()
    {
        var result = _validator.Validate("[1, 2]");

        Assert.False(result.IsValid);
        Assert.Equal("Das JSON muss ein Objekt sein.", result.Reason);
    }

    [Fact]
    public void Validate_RejectsMissingRequiredProperty()
    {
        var result = _validator.Validate(TagJson.Create(tag => ((JsonObject)tag["Entnahme"]!).Remove("Stelle")));

        Assert.False(result.IsValid);
        Assert.Equal("Feld \"Stelle\" fehlt.", result.Reason);
    }

    [Fact]
    public void Validate_RejectsUnknownWassertyp()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Entnahme"]!["Wassertyp"] = "Regenwasser"));

        Assert.False(result.IsValid);
        Assert.Equal(
            "Feld \"Wassertyp\" muss \"Kaltwasser\", \"Heisswasser\" oder \"Mischwasser\" sein.",
            result.Reason);
    }

    [Fact]
    public void Validate_RejectsWrongVersion()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Version"] = 10));

        Assert.False(result.IsValid);
        Assert.Equal("Feld \"Version\" muss 11 sein.", result.Reason);
    }

    [Fact]
    public void Validate_RejectsWrongFormat()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Format"] = "something-else"));

        Assert.False(result.IsValid);
        Assert.Equal("Feld \"Format\" muss \"berns-wasser\" sein.", result.Reason);
    }

    [Fact]
    public void Validate_RequiresKurznameWhenWeitergabeIsNotNein()
    {
        var result = _validator.Validate(TagJson.Create(tag => ((JsonObject)tag["Gesundheitsamt"]!).Remove("Kurzname")));

        Assert.False(result.IsValid);
        Assert.Equal("Feld \"Kurzname\" fehlt, wenn Weitergabe nicht \"Nein\" ist.", result.Reason);
    }

    [Fact]
    public void Validate_AcceptsWeitergabeNeinWithoutKurzname()
    {
        var result = _validator.Validate(TagJson.Create(tag =>
        {
            tag["Gesundheitsamt"] = new JsonObject { ["Weitergabe"] = "Nein" };
        }));

        Assert.True(result.IsValid, result.TechnicalDetails);
    }

    [Fact]
    public void Validate_RejectsEmptyAnalysen()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Analysen"] = new JsonArray()));

        Assert.False(result.IsValid);
        Assert.Equal("Feld \"Analysen\" muss mindestens einen Eintrag enthalten.", result.Reason);
    }

    // ---- unicode escapes written as literal text on the tag ----

    [Fact]
    public void Validate_DecodesLiteralUnicodeEscapes()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Entnahme"]!["Objekt"] = "M\\u00fcller"));

        Assert.True(result.IsValid, result.TechnicalDetails);
        Assert.Equal("Müller", result.Payload!.Entnahme!.Objekt);
    }

    [Fact]
    public void Validate_DecodesSurrogatePairsWithoutThrowing()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Entnahme"]!["Objekt"] = "Haus \\uD83D\\uDE00"));

        Assert.True(result.IsValid, result.TechnicalDetails);
        Assert.Equal("Haus \U0001F600", result.Payload!.Entnahme!.Objekt);
    }

    [Fact]
    public void Validate_KeepsLoneSurrogateEscapeAsTextWithoutThrowing()
    {
        var result = _validator.Validate(TagJson.Create(tag => tag["Entnahme"]!["Objekt"] = "Haus \\uD83D"));

        Assert.True(result.IsValid, result.TechnicalDetails);
        Assert.Equal("Haus \\uD83D", result.Payload!.Entnahme!.Objekt);
    }
}
