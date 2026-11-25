using System.Text.Json.Nodes;
using ArianaNfcClient.Services.Validation;

namespace ArianaNfcClient.Tests;

public class JsonUnicodeEscapesTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("\\u00e4", "ä")]
    [InlineData("\\u00E4", "ä")]
    [InlineData("M\\u00fcller und M\\u00f6ller", "Müller und Möller")]
    [InlineData("\\uD83D\\uDE00", "\U0001F600")]
    [InlineData("\\ud83d\\ude00", "\U0001F600")]
    [InlineData("x\\uD83D\\uDE00y", "x\U0001F600y")]
    public void Decode_DecodesEscapes(string input, string expected) =>
        Assert.Equal(expected, JsonUnicodeEscapes.Decode(input));

    [Theory]
    [InlineData("\\uD83D")]
    [InlineData("\\uDE00")]
    [InlineData("\\uDE00\\uD83D")]
    public void Decode_LeavesLoneAndReversedSurrogatesAsText(string input) =>
        Assert.Equal(input, JsonUnicodeEscapes.Decode(input));

    [Fact]
    public void Decode_MixesLoneSurrogateAndNormalEscape() =>
        Assert.Equal("a\\uD83Dä", JsonUnicodeEscapes.Decode("a\\uD83D\\u00e4"));

    [Fact]
    public void Decode_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, JsonUnicodeEscapes.Decode(string.Empty));
        Assert.Null(JsonUnicodeEscapes.Decode(null!));
    }

    [Fact]
    public void UnescapeInPlace_DecodesNestedObjectsAndArrays()
    {
        var node = JsonNode.Parse("""{ "a": "\\u00e4", "b": { "c": [ "\\u00f6", "x" ] } }""");

        JsonUnicodeEscapes.UnescapeInPlace(node);

        Assert.Equal("ä", node!["a"]!.GetValue<string>());
        Assert.Equal("ö", node["b"]!["c"]![0]!.GetValue<string>());
        Assert.Equal("x", node["b"]!["c"]![1]!.GetValue<string>());
    }
}
