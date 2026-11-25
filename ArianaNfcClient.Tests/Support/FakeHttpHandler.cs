using System.Net;
using System.Text;

namespace ArianaNfcClient.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>Routes requests by method and absolute path, records everything that is sent.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public List<RecordedRequest> Unexpected { get; } = [];

    public FakeHttpHandler On(HttpMethod method, string absolutePath, Func<HttpResponseMessage> respond) =>
        On(method, absolutePath, _ => respond());

    public FakeHttpHandler On(HttpMethod method, string absolutePath, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((method, absolutePath, respond));
        return this;
    }

    public FakeHttpHandler OnJson(HttpMethod method, string absolutePath, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, absolutePath, () => Json(json, status));

    public IEnumerable<RecordedRequest> Sent(HttpMethod method, string absolutePath) =>
        Requests.Where(r => r.Method == method &&
                            string.Equals(r.Uri.AbsolutePath, absolutePath, StringComparison.OrdinalIgnoreCase));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            body);
        Requests.Add(recorded);

        foreach (var (method, path, respond) in _routes)
        {
            if (method == request.Method &&
                string.Equals(request.RequestUri!.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return respond(request);
            }
        }

        Unexpected.Add(recorded);
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected {request.Method} {request.RequestUri}")
        };
    }
}
