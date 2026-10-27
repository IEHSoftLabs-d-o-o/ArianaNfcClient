using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArianaNfcClient.Models;
using ArianaNfcClient.Services.Validation;

namespace ArianaNfcClient.Services.ArianaLab;

public sealed class ArianaLabClient : IArianaLabClient
{
    public const string DefaultCreatePath = "/Rest/Opd/Probenanlage/Auftraege";
    private const int MaxOriginRedirectHops = 5;

    private readonly HttpClient _httpClient;
    private readonly ArianaLabOptions _options;
    private readonly ILogger<ArianaLabClient> _logger;
    private Uri? _configuredBaseAddress;
    private Uri? _redirectedOrigin;

    public Uri? EffectiveBaseAddress => _redirectedOrigin ?? _configuredBaseAddress;

    public ArianaLabClient(
        HttpClient httpClient,
        IOptions<ArianaLabOptions> options,
        ILogger<ArianaLabClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyConnection();
    }

    public void ApplyConnection()
    {
        _redirectedOrigin = null;
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) ||
            !Uri.TryCreate(EnsureTrailingSlash(_options.BaseUrl.Trim()), UriKind.Absolute, out var baseAddress))
        {
            _configuredBaseAddress = null;
            _httpClient.BaseAddress = null;
        }
        else
        {
            _configuredBaseAddress = baseAddress;
            // HttpClient.BaseAddress cannot change after the first request; keep a local copy for redirects.
            try
            {
                _httpClient.BaseAddress = baseAddress;
            }
            catch (InvalidOperationException)
            {
                // Already sent requests; absolute URIs from ResolveRequestUri still work.
            }
        }

        if (string.IsNullOrWhiteSpace(_options.Username))
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            return;
        }

        var raw = $"{_options.Username}:{_options.Password}";
        // Klims BasicAuthenticationHandler decodes as Windows-1252; Latin-1 matches Western European passwords.
        var token = Convert.ToBase64String(Encoding.Latin1.GetBytes(raw));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task<AuftragCreateResult> CreateAuftragAsync(
        NfcTagPayload payload,
        string jsonText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
        {
            _logger.LogError("Auftrag creation failed. ArianaLab credentials are not configured");
            return new AuftragCreateResult(
                false,
                "ArianaLab-Zugangsdaten fehlen.",
                "Legen Sie Benutzer und Passwort über Einstellungen fest.");
        }

        try
        {
            var protocolId = payload.Protokoll?.Id?.Trim();
            if (string.IsNullOrWhiteSpace(protocolId))
            {
                _logger.LogError("Auftrag creation failed. Tag has no protocol id");
                return new AuftragCreateResult(
                    false,
                    "Das Tag enthält keine Protokoll-Id.",
                    "Protokoll.Id is required before an Auftrag can be created.");
            }

            var existing = await FindExistingByProtokollIdAsync(protocolId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            var template = await LoadEffektiveVorlageAsync(payload.Auftragsvorlage?.Name, cancellationToken);
            var body = BernsWasserAuftragMapper.Map(payload, template, ProtocolAttributeName);
            var createPath = string.IsNullOrWhiteSpace(_options.CreatePath)
                ? DefaultCreatePath
                : _options.CreatePath;

            using var created = await PostJsonAsync(createPath, body, cancellationToken);
            var createdText = await created.Content.ReadAsStringAsync(cancellationToken);
            if (!created.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Auftrag creation failed. HTTP {Status} POST {Path}. {Body}",
                    (int)created.StatusCode,
                    createPath,
                    Trim(createdText));
                return Fail(created.StatusCode, createPath, createdText, "ArianaLab hat den Auftrag abgelehnt.");
            }

            if (LooksLikeLoginPage(createdText))
            {
                _logger.LogError(
                    "Auftrag creation failed. Login page from POST {Path}",
                    createPath);
                return LoginFailed(created.StatusCode, $"POST {createPath}", createdText);
            }

            var createdNode = ParseObject(createdText);
            var auftragId = ReadId(createdNode);
            _logger.LogInformation(
                "Auftrag created. Id {AuftragId}. Protocol {ProtocolId}. HTTP {Status} POST {Path}",
                string.IsNullOrWhiteSpace(auftragId) ? "(none)" : auftragId,
                protocolId,
                (int)created.StatusCode,
                createPath);
            var message = string.IsNullOrWhiteSpace(auftragId)
                ? "Auftrag wurde in ArianaLab angelegt."
                : $"Auftrag {auftragId} wurde in ArianaLab angelegt.";

            return new AuftragCreateResult(
                true,
                message,
                $"HTTP {(int)created.StatusCode} POST {createPath}: {Trim(createdText)}",
                auftragId,
                HttpStatus: (int)created.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Auftrag creation failed. ArianaLab is unreachable");
            return new AuftragCreateResult(false, "ArianaLab ist nicht erreichbar.", ex.Message, HttpStatus: 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auftrag creation failed");
            return new AuftragCreateResult(false, "ArianaLab ist nicht erreichbar.", ex.Message);
        }
    }

    public async Task<AuftragCreateResult> ActivateAuftragAsync(
        string auftragId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(auftragId))
        {
            _logger.LogError("Auftrag activation failed. Auftrag id is missing");
            return new AuftragCreateResult(
                false,
                "Der Auftrag kann nicht aktiviert werden.",
                "Auftrag id is missing.");
        }

        var createPath = string.IsNullOrWhiteSpace(_options.CreatePath)
            ? DefaultCreatePath
            : _options.CreatePath;
        var path = $"{createPath.TrimEnd('/')}/{Uri.EscapeDataString(auftragId)}/Aktivieren";

        try
        {
            using var response = await SendWithOriginRedirectAsync(
                HttpMethod.Post,
                path,
                contentFactory: null,
                cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Auftrag activation failed. Id {AuftragId}. HTTP {Status} POST {Path}. {Body}",
                    auftragId,
                    (int)response.StatusCode,
                    path,
                    Trim(text));
                return Fail(response.StatusCode, path, text, "ArianaLab hat die Aktivierung abgelehnt.") with
                {
                    AuftragNummer = auftragId
                };
            }

            if (LooksLikeLoginPage(text))
            {
                _logger.LogError(
                    "Auftrag activation failed. Id {AuftragId}. Login page from POST {Path}",
                    auftragId,
                    path);
                return LoginFailed(response.StatusCode, $"POST {path}", text) with
                {
                    AuftragNummer = auftragId
                };
            }

            var node = ParseObject(text);
            if (node is null || !ReadBool(node["IsSuccess"]))
            {
                var message = node is null
                    ? "ArianaLab hat die Aktivierung abgelehnt."
                    : ReadActivationError(node) ?? ExtractApiError(text) ?? "ArianaLab hat die Aktivierung abgelehnt.";
                _logger.LogError(
                    "Auftrag activation failed. Id {AuftragId}. POST {Path}. {Message}. {Body}",
                    auftragId,
                    path,
                    message,
                    Trim(text));
                return new AuftragCreateResult(
                    false,
                    message,
                    $"HTTP {(int)response.StatusCode} POST {path}: {Trim(text)}",
                    auftragId,
                    HttpStatus: (int)response.StatusCode);
            }

            var probenNummer = ReadProbenNummern(node["SuccessValue"] as JsonObject);
            _logger.LogInformation(
                "Auftrag activated. Id {AuftragId}. Probe {ProbenNummer}. HTTP {Status} POST {Path}",
                auftragId,
                string.IsNullOrWhiteSpace(probenNummer) ? "(none)" : probenNummer,
                (int)response.StatusCode,
                path);
            var successMessage = string.IsNullOrWhiteSpace(probenNummer)
                ? "Auftrag wurde in ArianaLab aktiviert."
                : $"Probe {probenNummer} wurde in ArianaLab angelegt.";
            return new AuftragCreateResult(
                true,
                successMessage,
                $"HTTP {(int)response.StatusCode} POST {path}: {Trim(text)}",
                auftragId,
                probenNummer,
                (int)response.StatusCode,
                IsActivated: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Auftrag activation failed. Id {AuftragId}. ArianaLab is unreachable", auftragId);
            return new AuftragCreateResult(
                false,
                "ArianaLab ist nicht erreichbar.",
                ex.Message,
                auftragId,
                HttpStatus: 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auftrag activation failed. Id {AuftragId}", auftragId);
            return new AuftragCreateResult(
                false,
                "ArianaLab ist nicht erreichbar.",
                ex.Message,
                auftragId);
        }
    }

    public async Task<bool> DeleteAuftragAsync(string auftragId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(auftragId))
        {
            _logger.LogError("Auftrag delete failed. Auftrag id is missing");
            return false;
        }

        var createPath = string.IsNullOrWhiteSpace(_options.CreatePath)
            ? DefaultCreatePath
            : _options.CreatePath;
        var path = $"{createPath.TrimEnd('/')}/{Uri.EscapeDataString(auftragId)}";

        try
        {
            using var response = await SendWithOriginRedirectAsync(
                HttpMethod.Delete,
                path,
                contentFactory: null,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Auftrag delete failed. Id {AuftragId}. HTTP {Status} DELETE {Path}. {Body}",
                    auftragId,
                    (int)response.StatusCode,
                    path,
                    Trim(text));
                return false;
            }

            _logger.LogInformation(
                "Auftrag deleted. Id {AuftragId}. HTTP {Status} DELETE {Path}",
                auftragId,
                (int)response.StatusCode,
                path);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Auftrag delete failed. Id {AuftragId}. ArianaLab is unreachable", auftragId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auftrag delete failed. Id {AuftragId}", auftragId);
            return false;
        }
    }

    public Task<string?> DiscoverCreatePathAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(string.IsNullOrWhiteSpace(_options.CreatePath)
            ? DefaultCreatePath
            : _options.CreatePath);

    private string ProtocolAttributeName =>
        string.IsNullOrWhiteSpace(_options.ProtocolAttributeName)
            ? ArianaLabOptions.DefaultProtocolAttributeName
            : _options.ProtocolAttributeName;

    private async Task<AuftragCreateResult?> FindExistingByProtokollIdAsync(
        string protocolId,
        CancellationToken cancellationToken)
    {
        var createPath = string.IsNullOrWhiteSpace(_options.CreatePath)
            ? DefaultCreatePath
            : _options.CreatePath;

        var query = JsonSerializer.Serialize(new
        {
            Conditions = new[]
            {
                new { Property = "Beauftragung.Attribute.Wert", Operator = "=", Pattern = protocolId }
            }
        });

        var url = $"{createPath}?q={Uri.EscapeDataString(query)}";
        using var response = await SendWithOriginRedirectAsync(
            HttpMethod.Get,
            url,
            contentFactory: null,
            cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("GET {Path} -> {Status}", createPath, (int)response.StatusCode);

        if (LooksLikeLoginPage(text))
        {
            _logger.LogError(
                "Auftrag creation failed. Existing-order lookup returned login page from GET {Path}",
                createPath);
            return LoginFailed(response.StatusCode, $"GET {url}", text);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Auftrag creation failed. Existing-order lookup returned HTTP {Status} from GET {Path}. {Body}",
                (int)response.StatusCode,
                createPath,
                Trim(text));
            return new AuftragCreateResult(
                false,
                "Bestehende Aufträge konnten nicht geprüft werden.",
                $"HTTP {(int)response.StatusCode} from GET {url}: {Trim(text)}",
                HttpStatus: (int)response.StatusCode);
        }

        var list = ParseObject(text);
        if (list is null)
        {
            _logger.LogError(
                "Auftrag creation failed. Existing-order lookup returned unreadable JSON from GET {Path}",
                createPath);
            return new AuftragCreateResult(
                false,
                "Bestehende Aufträge konnten nicht geprüft werden.",
                $"GET {createPath} returned unreadable JSON: {Trim(text)}");
        }

        if (list["_embedded"] is not JsonArray embedded)
        {
            return null;
        }

        foreach (var item in embedded)
        {
            if (item is not JsonObject auftrag || !HasProtocolAttribute(auftrag, protocolId))
            {
                continue;
            }

            var id = ReadId(auftrag);
            var probenNummer = ReadProbenNummern(auftrag);
            var isActivated = IsActivatedAuftrag(auftrag, probenNummer);
            _logger.LogWarning(
                "Auftrag already exists. Id {AuftragId}. Protocol {ProtocolId}. Status {Status}. Probe {ProbenNummer}",
                string.IsNullOrWhiteSpace(id) ? "(none)" : id,
                protocolId,
                ReadString(auftrag["Status"]) ?? "(none)",
                string.IsNullOrWhiteSpace(probenNummer) ? "(none)" : probenNummer);
            var message = ExistingAuftragMessage(isActivated, probenNummer);
            return new AuftragCreateResult(
                false,
                message,
                string.IsNullOrWhiteSpace(id)
                    ? $"Existing Auftrag matched {ProtocolAttributeName}={protocolId}."
                    : $"Existing Auftrag {id} matched {ProtocolAttributeName}={protocolId}.",
                id,
                probenNummer,
                AlreadyExists: true,
                IsActivated: isActivated);
        }

        return null;
    }

    private bool HasProtocolAttribute(JsonObject auftrag, string protocolId)
    {
        if (auftrag["Attribute"] is not JsonArray attributes)
        {
            return false;
        }

        foreach (var node in attributes)
        {
            if (node is not JsonObject attr)
            {
                continue;
            }

            var name = attr["Name"]?.GetValue<string>();
            var wert = ReadAttributeWert(attr["Wert"]);
            if (string.Equals(name, ProtocolAttributeName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(wert, protocolId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadAttributeWert(JsonNode? wert)
    {
        if (wert is null or JsonObject or JsonArray)
        {
            return null;
        }

        return wert is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : wert.ToString();
    }

    private async Task<JsonObject?> LoadEffektiveVorlageAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var query = JsonSerializer.Serialize(new
        {
            Conditions = new[]
            {
                new { Property = "Name", Operator = "=", Pattern = name }
            }
        });

        var url = $"/Rest/Opd/Probenanlage/Auftragsvorlagen?q={Uri.EscapeDataString(query)}";
        using var listResponse = await SendWithOriginRedirectAsync(
            HttpMethod.Get,
            url,
            contentFactory: null,
            cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Auftragsvorlage lookup failed: HTTP {Status}", (int)listResponse.StatusCode);
            return null;
        }

        var listText = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        if (LooksLikeLoginPage(listText))
        {
            _logger.LogWarning("Auftragsvorlage lookup returned login page");
            return null;
        }

        var list = ParseObject(listText);
        var first = list?["_embedded"] is JsonArray { Count: > 0 } embedded
            ? embedded[0] as JsonObject
            : null;
        if (first is null)
        {
            _logger.LogWarning("Auftragsvorlage '{Name}' was not found.", name);
            return null;
        }

        var effektiveHref = FindLink(first, "effektiveVorlage");
        if (string.IsNullOrWhiteSpace(effektiveHref))
        {
            return first["Auftrag"] as JsonObject;
        }

        using var effektiveResponse = await SendWithOriginRedirectAsync(
            HttpMethod.Get,
            effektiveHref,
            contentFactory: null,
            cancellationToken);
        if (!effektiveResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Effektive Vorlage failed: HTTP {Status}", (int)effektiveResponse.StatusCode);
            return first["Auftrag"] as JsonObject;
        }

        var effektiveText = await effektiveResponse.Content.ReadAsStringAsync(cancellationToken);
        return ParseObject(effektiveText)?["Auftrag"] as JsonObject;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string path,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        var json = body.ToJsonString(JsonUnicodeEscapes.RelaxedWriteCompact);
        return await SendWithOriginRedirectAsync(
            HttpMethod.Post,
            path,
            () => new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithOriginRedirectAsync(
        HttpMethod method,
        string pathAndQuery,
        Func<HttpContent?>? contentFactory,
        CancellationToken cancellationToken)
    {
        var requestUri = ResolveRequestUri(pathAndQuery)
                         ?? throw new InvalidOperationException(
                             "ArianaLab BaseUrl is not configured.");

        for (var hop = 0; hop < MaxOriginRedirectHops; hop++)
        {
            using var request = new HttpRequestMessage(method, requestUri);
            var content = contentFactory?.Invoke();
            if (content is not null)
            {
                request.Content = content;
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!IsRedirectStatus(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                return response;
            }

            var resolved = location.IsAbsoluteUri
                ? location
                : new Uri(requestUri, location);

            if (!IsOriginOnlyRedirect(resolved))
            {
                return response;
            }

            response.Dispose();

            var newOrigin = new UriBuilder(resolved.Scheme, resolved.Host, resolved.Port) { Path = "/" }.Uri;
            _redirectedOrigin = newOrigin;
            requestUri = new Uri(newOrigin, requestUri.PathAndQuery);
            _logger.LogInformation(
                "Replaying {Method} {Path} on redirected origin {Origin}",
                method.Method,
                requestUri.PathAndQuery,
                newOrigin.AbsoluteUri.TrimEnd('/'));
        }

        throw new HttpRequestException(
            $"ArianaLab origin redirect exceeded {MaxOriginRedirectHops} hops for {method} {pathAndQuery}.");
    }

    private Uri? ResolveRequestUri(string pathAndQuery)
    {
        if (Uri.TryCreate(pathAndQuery, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        var origin = _redirectedOrigin ?? _configuredBaseAddress ?? _httpClient.BaseAddress;
        return origin is null ? null : new Uri(origin, pathAndQuery);
    }

    private static bool IsRedirectStatus(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsOriginOnlyRedirect(Uri location)
    {
        var path = location.AbsolutePath;
        return string.IsNullOrEmpty(path) || path == "/";
    }

    private static bool LooksLikeLoginPage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return text.Contains("Login", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("/Home/Login", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static AuftragCreateResult LoginFailed(HttpStatusCode status, string request, string body) =>
        new(
            false,
            "Anmeldung bei ArianaLab fehlgeschlagen.",
            $"HTTP {(int)status} from {request}: {Trim(body)}",
            HttpStatus: (int)status);

    private static AuftragCreateResult Fail(HttpStatusCode status, string path, string body, string fallback)
    {
        if (LooksLikeLoginPage(body))
        {
            return LoginFailed(status, $"POST {path}", body);
        }

        var reason = ExtractApiError(body) ?? fallback;
        return new AuftragCreateResult(
            false,
            reason,
            $"HTTP {(int)status} from POST {path}: {Trim(body)}",
            HttpStatus: (int)status);
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExistingAuftragMessage(bool isActivated, string? probenNummer)
    {
        if (!isActivated)
        {
            return "Für dieses Tag existiert bereits ein Auftrag.";
        }

        return string.IsNullOrWhiteSpace(probenNummer)
            ? "Für dieses Tag existiert bereits eine Probe."
            : $"Für dieses Tag existiert bereits die Probe {probenNummer}.";
    }

    private static bool IsActivatedAuftrag(JsonObject auftrag, string? probenNummer) =>
        string.Equals(ReadString(auftrag["Status"]), "aktiviert", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(probenNummer);

    private static string? ReadProbenNummern(JsonObject? auftrag)
    {
        if (auftrag?["Proben"] is not JsonArray proben)
        {
            return null;
        }

        var numbers = new List<string>();
        foreach (var item in proben)
        {
            if (item is not JsonObject probe)
            {
                continue;
            }

            var number = ReadString(probe["Tagebuchnummer"]);
            if (!string.IsNullOrWhiteSpace(number))
            {
                numbers.Add(number);
            }
        }

        return numbers.Count == 0 ? null : string.Join(", ", numbers);
    }

    private static string? ReadActivationError(JsonObject node)
    {
        if (node["ErrorValue"] is not JsonObject error)
        {
            return null;
        }

        var message = ReadString(error["Message"]);
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return ReadLivecheckMessages(error["Probenvorschau"]);
    }

    private static string? ReadLivecheckMessages(JsonNode? vorschau)
    {
        if (vorschau is not JsonArray items || items.Count == 0)
        {
            return null;
        }

        var messages = new List<string>();
        foreach (var item in items)
        {
            if (item is not JsonObject preview || preview["LivecheckResults"] is not JsonArray results)
            {
                continue;
            }

            foreach (var resultNode in results)
            {
                if (resultNode is not JsonObject result || IsOkLivecheck(result["Result"]))
                {
                    continue;
                }

                var text = ReadString(result["Message"]);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    messages.Add(text);
                }
            }
        }

        return messages.Count == 0 ? "Es gibt fehlschlagende Livechecks" : string.Join(" ", messages.Distinct());
    }

    private static bool IsOkLivecheck(JsonNode? node) =>
        string.Equals(ReadString(node), "OK", StringComparison.OrdinalIgnoreCase) ||
        (node is JsonValue value && value.TryGetValue<int>(out var number) && number == 0);

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string? ReadString(JsonNode? node)
    {
        if (node is null or JsonObject or JsonArray)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToString();
    }

    private static string? ReadId(JsonObject? node)
    {
        if (node is null)
        {
            return null;
        }

        return ReadString(node["_id"])
               ?? ReadString(node["Id"])
               ?? ReadString(node["id"]);
    }

    private static string? FindLink(JsonObject? node, string rel)
    {
        if (node?["_links"] is not JsonArray links)
        {
            return null;
        }

        foreach (var linkNode in links)
        {
            if (linkNode is not JsonObject link)
            {
                continue;
            }

            var linkRel = link["rel"]?.GetValue<string>() ?? link["name"]?.GetValue<string>();
            if (string.Equals(linkRel, rel, StringComparison.OrdinalIgnoreCase))
            {
                return link["href"]?.GetValue<string>();
            }
        }

        return null;
    }

    private static string? ExtractApiError(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("ExceptionMessage", out var exception) &&
                exception.ValueKind == JsonValueKind.String)
            {
                return exception.GetString();
            }

            if (doc.RootElement.TryGetProperty("Message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // not JSON
        }

        return null;
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    private static string Trim(string text, int max = 800) =>
        string.IsNullOrWhiteSpace(text)
            ? "(empty body)"
            : text.Length <= max
                ? text
                : text[..max] + "…";
}
