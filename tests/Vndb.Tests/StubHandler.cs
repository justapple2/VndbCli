using System.Net;
using System.Text;

namespace Vndb.Tests;

internal sealed record CapturedRequest(string Method, string Uri, string? Body, string? Authorization, string? ContentType);

internal sealed class StubHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    public StubHandler(string json) : this((_, _) => Task.FromResult(Json(json))) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType));
        return await respond(Requests.Count, cancellationToken);
    }

    public static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}
