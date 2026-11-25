using ArianaNfcClient.Models;
using ArianaNfcClient.Services.ArianaLab;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArianaNfcClient.Tests.Support;

internal static class ArianaLabClientFactory
{
    public const string ListPath = "/Rest/Opd/Probenanlage/Auftragsvorlagen";
    public const string AuftraegePath = ArianaLabClient.DefaultCreatePath;

    public static ArianaLabClient Create(
        FakeHttpHandler handler,
        Action<ArianaLabOptions>? configure = null)
    {
        var options = new ArianaLabOptions
        {
            BaseUrl = "https://lab.example",
            Username = "user",
            Password = "secret"
        };
        configure?.Invoke(options);

        var http = new HttpClient(handler, disposeHandler: false);
        return new ArianaLabClient(http, Options.Create(options), NullLogger<ArianaLabClient>.Instance);
    }

    /// <summary>Template Auftrag with a Chemie and a Mibi position.</summary>
    public const string TemplateAuftrag = """
        {
          "_id": "tmpl",
          "Status": "offen",
          "Positionen": [
            { "Name": "Chemie", "Analysen": [ { "Name": "Blei" }, { "Name": "Cadmium" }, { "Name": "Arsen" } ] },
            { "Name": "Mibi", "Analysen": [ { "Name": "E. coli" } ] }
          ]
        }
        """;

    public static string VorlageList(string template = TemplateAuftrag) =>
        $$"""{ "_embedded": [ { "Name": "{{TagJson.VorlageName}}", "Auftrag": {{template}} } ] }""";

    public const string EmptyList = """{ "_embedded": [] }""";

    public static FakeHttpHandler HandlerWithVorlage(string? vorlageList = null) =>
        new FakeHttpHandler()
            .OnJson(HttpMethod.Get, AuftraegePath, EmptyList)
            .OnJson(HttpMethod.Get, ListPath, vorlageList ?? VorlageList());
}
