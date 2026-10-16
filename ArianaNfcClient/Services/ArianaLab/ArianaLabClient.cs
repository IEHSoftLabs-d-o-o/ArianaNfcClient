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

    private readonly HttpClient _httpClient;
    private readonly ArianaLabOptions _options;
    private readonly ILogger<ArianaLabClient> _logger;

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
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) ||
            !Uri.TryCreate(EnsureTrailingSlash(_options.BaseUrl.Trim()), UriKind.Absolute, out var baseAddress))
        {
            _httpClient.BaseAddress = null;
        }
        else
        {
            _httpClient.BaseAddress = baseAddress;
        }

        if (string.IsNullOrWhiteSpace(_options.Username))
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            return;
        }

        var raw = $"{_options.Username}:{_options.Password}";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
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
            using var response = await _httpClient.PostAsync(path, content: null, cancellationToken);
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
            using var response = await _httpClient.DeleteAsync(path, cancellationToken);
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
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("GET {Path} -> {Status}", createPath, (int)response.StatusCode);

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
        using var listResponse = await _httpClient.GetAsync(url, cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Auftragsvorlage lookup failed: HTTP {Status}", (int)listResponse.StatusCode);
            return null;
        }

        var listText = await listResponse.Content.ReadAsStringAsync(cancellationToken);
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

        using var effektiveResponse = await _httpClient.GetAsync(effektiveHref, cancellationToken);
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
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync(path, content, cancellationToken);
    }

    private static AuftragCreateResult Fail(HttpStatusCode status, string path, string body, string fallback)
    {
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
