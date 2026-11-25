using System.Net;
using System.Text.Json.Nodes;
using ArianaNfcClient.Models;
using ArianaNfcClient.Tests.Support;

namespace ArianaNfcClient.Tests;

public class ArianaLabClientTests
{
    private const string AuftraegePath = ArianaLabClientFactory.AuftraegePath;
    private const string ListPath = ArianaLabClientFactory.ListPath;
    private const string LoginHtml = "<!DOCTYPE html><html><body>Login</body></html>";

    private static Task<AuftragCreateResult> CreateAsync(
        Services.ArianaLab.ArianaLabClient client,
        NfcTagPayload? payload = null) =>
        client.CreateAuftragAsync(payload ?? TagJson.Payload(), TagJson.Create(), CancellationToken.None);

    private static string PositionSummary(string postBody)
    {
        var body = JsonNode.Parse(postBody)!.AsObject();
        return string.Join(" | ", ((JsonArray)body["Positionen"]!).Select(position =>
            position!["Name"] + ":" + string.Join(",", ((JsonArray)position["Analysen"]!)
                .Select(analyse => analyse!["Name"]!.GetValue<string>()))));
    }

    // ---- CreateAuftragAsync ----

    [Fact]
    public async Task Create_PostsTheFilteredAuftragAndReturnsItsId()
    {
        var handler = ArianaLabClientFactory.HandlerWithVorlage()
            .OnJson(HttpMethod.Post, AuftraegePath, """{ "_id": "A-42", "Probenbezeichnung": "Haus" }""", HttpStatusCode.Created);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.True(result.Succeeded, result.TechnicalDetails);
        Assert.Equal("A-42", result.AuftragNummer);
        Assert.Equal("Auftrag A-42 wurde in ArianaLab angelegt.", result.Message);
        var post = Assert.Single(handler.Sent(HttpMethod.Post, AuftraegePath));
        Assert.Equal("Chemie:Blei,Cadmium", PositionSummary(post.Body!));
        Assert.StartsWith("Basic ", post.Authorization);
        Assert.Empty(handler.Unexpected);
    }

    [Fact]
    public async Task Create_UsesTheEffektiveVorlageWhenLinked()
    {
        var list = """
            { "_embedded": [ {
                "Name": "Vorlage A",
                "Auftrag": { "Positionen": [ { "Name": "Basis", "Analysen": [ { "Name": "Nur Basis" } ] } ] },
                "_links": [ { "rel": "effektiveVorlage", "href": "/Rest/Opd/Probenanlage/Auftragsvorlagen/1/Effektiv" } ]
            } ] }
            """;
        var handler = ArianaLabClientFactory.HandlerWithVorlage(list)
            .OnJson(
                HttpMethod.Get,
                "/Rest/Opd/Probenanlage/Auftragsvorlagen/1/Effektiv",
                $$"""{ "Auftrag": {{ArianaLabClientFactory.TemplateAuftrag}} }""")
            .OnJson(HttpMethod.Post, AuftraegePath, """{ "_id": "A-1" }""", HttpStatusCode.Created);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.True(result.Succeeded, result.TechnicalDetails);
        var post = Assert.Single(handler.Sent(HttpMethod.Post, AuftraegePath));
        Assert.Equal("Chemie:Blei,Cadmium", PositionSummary(post.Body!));
    }

    [Fact]
    public async Task Create_FailsWithoutCredentialsAndSendsNothing()
    {
        var handler = new FakeHttpHandler();
        var client = ArianaLabClientFactory.Create(handler, o => o.Password = "");

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("ArianaLab-Zugangsdaten fehlen.", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_FailsWithoutProtocolId()
    {
        var handler = new FakeHttpHandler();
        var client = ArianaLabClientFactory.Create(handler);
        var payload = TagJson.Payload(tag => ((JsonObject)tag["Protokoll"]!).Remove("Id"));

        var result = await CreateAsync(client, payload);

        Assert.False(result.Succeeded);
        Assert.Equal("Das Tag enthält keine Protokoll-Id.", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_ReportsAnExistingAuftragAndDoesNotPost()
    {
        var existing = $$"""
            { "_embedded": [ {
                "_id": "X-9", "Status": "offen",
                "Attribute": [ { "Name": "$Protokoll-Id", "Wert": "{{TagJson.ProtocolId}}" } ]
            } ] }
            """;
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, AuftraegePath, existing);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.True(result.AlreadyExists);
        Assert.False(result.IsActivated);
        Assert.Equal("X-9", result.AuftragNummer);
        Assert.Empty(handler.Sent(HttpMethod.Post, AuftraegePath));
    }

    [Fact]
    public async Task Create_FailsWhenVorlageLookupReturnsServerError()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, AuftraegePath, ArianaLabClientFactory.EmptyList)
            .OnJson(HttpMethod.Get, ListPath, """{ "Message": "boom" }""", HttpStatusCode.InternalServerError);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("Auftragsvorlage konnte nicht geladen werden.", result.Message);
        Assert.Equal(500, result.HttpStatus);
        Assert.Empty(handler.Sent(HttpMethod.Post, AuftraegePath));
    }

    [Fact]
    public async Task Create_FailsWhenVorlageLookupReturnsLoginPage()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, AuftraegePath, ArianaLabClientFactory.EmptyList)
            .On(HttpMethod.Get, ListPath, () => FakeHttpHandler.Html(LoginHtml));
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("Anmeldung bei ArianaLab fehlgeschlagen.", result.Message);
        Assert.Empty(handler.Sent(HttpMethod.Post, AuftraegePath));
    }

    [Fact]
    public async Task Create_FailsWhenVorlageIsUnknown()
    {
        var handler = ArianaLabClientFactory.HandlerWithVorlage(ArianaLabClientFactory.EmptyList);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("Auftragsvorlage 'Vorlage A' ist in ArianaLab nicht vorhanden.", result.Message);
        Assert.Empty(handler.Sent(HttpMethod.Post, AuftraegePath));
    }

    [Fact]
    public async Task Create_FailsWhenEffektiveVorlageCannotBeLoaded()
    {
        var list = """
            { "_embedded": [ {
                "Name": "Vorlage A",
                "Auftrag": { "Positionen": [ { "Analysen": [ { "Name": "Blei" } ] } ] },
                "_links": [ { "rel": "effektiveVorlage", "href": "/Rest/Opd/Probenanlage/Auftragsvorlagen/1/Effektiv" } ]
            } ] }
            """;
        var handler = ArianaLabClientFactory.HandlerWithVorlage(list)
            .OnJson(HttpMethod.Get, "/Rest/Opd/Probenanlage/Auftragsvorlagen/1/Effektiv", "{}", HttpStatusCode.BadGateway);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("Effektive Auftragsvorlage konnte nicht geladen werden.", result.Message);
        Assert.Empty(handler.Sent(HttpMethod.Post, AuftraegePath));
    }

    [Fact]
    public async Task Create_FailsWhenTagListsAnalysenThatAreNotInTheVorlage()
    {
        var handler = ArianaLabClientFactory.HandlerWithVorlage();
        var client = ArianaLabClientFactory.Create(handler);
        var payload = TagJson.Payload(tag => tag["Analysen"] = new JsonArray("Blei", "Legionellen"));

        var result = await CreateAsync(client, payload);

        Assert.False(result.Succeeded);
        Assert.Equal("Analysen nicht in der Auftragsvorlage enthalten: Legionellen", result.Message);
        Assert.Empty(handler.Sent(HttpMethod.Post, AuftraegePath));
    }

    [Fact]
    public async Task Create_ReportsRejectionFromArianaLab()
    {
        var handler = ArianaLabClientFactory.HandlerWithVorlage()
            .OnJson(HttpMethod.Post, AuftraegePath, """{ "ExceptionMessage": "Kunde unbekannt" }""", HttpStatusCode.BadRequest);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await CreateAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("Kunde unbekannt", result.Message);
        Assert.Equal(400, result.HttpStatus);
    }

    // ---- ActivateAuftragAsync ----

    [Fact]
    public async Task Activate_ReturnsProbenummerOnSuccess()
    {
        var handler = new FakeHttpHandler().OnJson(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            """{ "IsSuccess": true, "SuccessValue": { "Proben": [ { "Tagebuchnummer": "P-7" } ] } }""");
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.True(result.Succeeded, result.TechnicalDetails);
        Assert.True(result.IsActivated);
        Assert.Equal("A-1", result.AuftragNummer);
        Assert.Equal("P-7", result.ProbenNummer);
        Assert.Equal("Probe P-7 wurde in ArianaLab angelegt.", result.Message);
    }

    [Fact]
    public async Task Activate_WithoutProbenummerUsesTheGenericSuccessMessage()
    {
        var handler = new FakeHttpHandler().OnJson(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            """{ "IsSuccess": true, "SuccessValue": {} }""");
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ProbenNummer);
        Assert.Equal("Auftrag wurde in ArianaLab aktiviert.", result.Message);
    }

    [Fact]
    public async Task Activate_ReportsTheErrorFromArianaLab()
    {
        var handler = new FakeHttpHandler().OnJson(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            """{ "IsSuccess": false, "ErrorValue": { "Message": "Livecheck fehlgeschlagen" } }""");
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Livecheck fehlgeschlagen", result.Message);
        Assert.Equal("A-1", result.AuftragNummer);
    }

    [Fact]
    public async Task Activate_UsesTheFallbackMessageWhenNoErrorIsGiven()
    {
        var handler = new FakeHttpHandler().OnJson(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            """{ "IsSuccess": false }""");
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ArianaLab hat die Aktivierung abgelehnt.", result.Message);
    }

    [Fact]
    public async Task Activate_KeepsTheIdWhenTheServerReturnsAnError()
    {
        var handler = new FakeHttpHandler().OnJson(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            """{ "Message": "Bad thing" }""",
            HttpStatusCode.BadRequest);
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Bad thing", result.Message);
        Assert.Equal("A-1", result.AuftragNummer);
        Assert.Equal(400, result.HttpStatus);
    }

    [Fact]
    public async Task Activate_ReportsLoginPage()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            () => FakeHttpHandler.Html(LoginHtml));
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Anmeldung bei ArianaLab fehlgeschlagen.", result.Message);
        Assert.Equal("A-1", result.AuftragNummer);
    }

    [Fact]
    public async Task Activate_ReportsUnreachableServer()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            $"{AuftraegePath}/A-1/Aktivieren",
            () => throw new HttpRequestException("boom"));
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ArianaLab ist nicht erreichbar.", result.Message);
        Assert.Equal("A-1", result.AuftragNummer);
        Assert.Equal(0, result.HttpStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Activate_WithoutIdFailsWithoutARequest(string id)
    {
        var handler = new FakeHttpHandler();
        var client = ArianaLabClientFactory.Create(handler);

        var result = await client.ActivateAuftragAsync(id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Der Auftrag kann nicht aktiviert werden.", result.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- origin redirect rule ----

    private const string ActivatePath = $"{AuftraegePath}/A-1/Aktivieren";
    private const string ActivationSuccess = """{ "IsSuccess": true, "SuccessValue": {} }""";

    /// <summary>Answers the first request with a redirect and any follow-up request with success.</summary>
    private static FakeHttpHandler RedirectingHandler(string location)
    {
        var calls = 0;
        return new FakeHttpHandler().On(HttpMethod.Post, ActivatePath, _ =>
            Interlocked.Increment(ref calls) == 1
                ? FakeHttpHandler.Redirect(location)
                : FakeHttpHandler.Json(ActivationSuccess));
    }

    [Fact]
    public async Task Redirect_FollowsSameHostHttpToHttpsAndRemembersTheOrigin()
    {
        var handler = RedirectingHandler("https://lab.example/");
        var client = ArianaLabClientFactory.Create(handler, o => o.BaseUrl = "http://lab.example");

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.True(result.Succeeded, result.TechnicalDetails);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Uri.UriSchemeHttp, handler.Requests[0].Uri.Scheme);
        Assert.Equal(Uri.UriSchemeHttps, handler.Requests[1].Uri.Scheme);
        Assert.Equal(new Uri("https://lab.example/"), client.EffectiveBaseAddress);
    }

    [Theory]
    [InlineData("http://lab.example", "https://evil.example/")]
    [InlineData("https://lab.example", "http://lab.example/")]
    [InlineData("http://lab.example", "http://lab.example/")]
    [InlineData("http://lab.example", "https://lab.example/Home/Login")]
    public async Task Redirect_IsNotFollowedForOtherHostsDowngradesOrPaths(string baseUrl, string location)
    {
        var handler = RedirectingHandler(location);
        var client = ArianaLabClientFactory.Create(handler, o => o.BaseUrl = baseUrl);

        var result = await client.ActivateAuftragAsync("A-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(302, result.HttpStatus);
        Assert.Single(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Equal("lab.example", r.Uri.Host));
        Assert.Equal(new Uri(baseUrl + "/"), client.EffectiveBaseAddress);
    }
}
