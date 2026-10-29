using Microsoft.AspNetCore.Http.HttpResults;
using Vndb.Contracts;
using Vndb.Library;

namespace Vndb.Web;

public static class QualityEndpoints
{
    public static void MapQuality(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/quality").WithTags("Data quality and client observations");
        group.MapGet("", Read).WithSummary("Read data gaps without upstream requests or client filesystem access");
    }
    private static Ok<QualityPage> Read(QualityReview review, string? device_id, string? kind, string? q, int? page, int? page_size) =>
        TypedResults.Ok(review.Read(device_id, kind, q, page ?? 1, page_size ?? 30));
}
