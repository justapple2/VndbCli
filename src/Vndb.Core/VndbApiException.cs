using System.Net;

namespace Vndb.Core;

public sealed class VndbApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
