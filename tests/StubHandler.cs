using System.Net;
using ToolsMcp;

/** tools.encycam.com in a box: canned answers by "METHOD /path", and every request kept with its token and body. */
internal sealed class StubHandler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

    public List<Seen> Requests { get; } = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new();

    public StubHandler On(string method, string path, HttpStatusCode status, string body = "",
                          string? contentDisposition = null, string contentType = "application/json",
                          IReadOnlyDictionary<string, string>? headers = null)
    {
        _answers[method + " " + path] = () =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            if (contentDisposition != null)
                response.Content.Headers.TryAddWithoutValidation("Content-Disposition", contentDisposition);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
                response.Headers.TryAddWithoutValidation(name, value);
            return response;
        };
        return this;
    }

    public StubHandler OnJson(string method, string path, string json) => On(method, path, HttpStatusCode.OK, json);

    /** An answer whose body the test makes: one that claims a length, or one that names none, as a chunked one does. */
    public StubHandler OnContent(string method, string path, Func<HttpContent> content)
    {
        _answers[method + " " + path] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = content() };
        return this;
    }

    public static ToolsApi Api(StubHandler handler) => new(new HttpClient(handler), "https://tools.test/api");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return _answers.TryGetValue(request.Method.Method + " " + request.RequestUri!.AbsolutePath, out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"status":404,"message":"no such route in the stub"}""") };
    }
}
