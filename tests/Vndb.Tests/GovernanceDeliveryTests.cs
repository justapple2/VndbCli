using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class GovernanceDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", directory)
            .UseSetting("Library:WriteKey", "delivery-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Theory]
    [InlineData("/api/v1/installations", "POST")]
    [InlineData("/api/v1/installations/synthetic", "PATCH")]
    [InlineData("/api/v1/installation-bundles", "POST")]
    [InlineData("/api/v1/legacy-locations/synthetic", "GET")]
    [InlineData("/api/v1/archive-runs/synthetic/evidence/archive", "PUT")]
    [InlineData("/api/v1/quality/installation-audits", "POST")]
    [InlineData("/api/archives", "POST")]
    [InlineData("/api/catalog/local", "POST")]
    public async Task ClosedRoutesRejectBeforeParsingOrPersistingAnyClientLocation(string path, string method)
    {
        using var directory = new TestDirectory(); await using var app = new Factory(directory.Path); using var http = app.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Library-Key", "delivery-test-key"); request.Headers.Add("X-Library-Request", "1");
        request.Content = new StringContent("{invalid JSON", Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("local_only", await response.Content.ReadAsStringAsync(Ct));
        var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        Assert.Empty(store.Installations()); Assert.Empty(store.Components()); Assert.Empty(store.ArchiveRuns()); Assert.Empty(store.PersonalItems());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "organization-evidence")));
    }
    [Fact]
    public async Task RuntimeRoutesAndPublicQualityContractsCannotRestorePrivateUploadHandlers()
    {
        using var directory = new TestDirectory(); await using var app = new Factory(directory.Path); using var http = app.CreateClient();
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "").ToArray();
        Assert.Contains("/api/v1/info", routes);
        Assert.DoesNotContain(routes, r => r.StartsWith("/api/v1/installations", StringComparison.Ordinal) || r.StartsWith("/api/v1/archive-runs", StringComparison.Ordinal) || r.StartsWith("/api/archives", StringComparison.Ordinal));
        Assert.Null(typeof(QualityReview).GetMethod("Apply"));
        Assert.Null(typeof(Vndb.Contracts.QualityPage).Assembly.GetType("Vndb.Contracts.InstallationAudit"));
        var schema = await http.GetStringAsync("/openapi/v1.json", Ct); Assert.DoesNotContain("root_path", schema); Assert.DoesNotContain("installation-audits", schema);
    }
    [Fact]
    public async Task OldPagesAndCachedScriptsLeadToReadOnlyQualityWithoutSendingPaths()
    {
        using var directory = new TestDirectory(); await using var app = new Factory(directory.Path);
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var page in new[] { "/installations.html", "/organization.html" })
        {
            using var response = await http.GetAsync(page, Ct); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/quality.html", response.Headers.Location?.OriginalString);
        }
        foreach (var script in new[] { "/installations.js", "/organization.js" })
        {
            var content = await http.GetStringAsync(script, Ct); Assert.Contains("window.location.replace('/quality.html')", content);
            Assert.DoesNotContain("fetch(", content); Assert.DoesNotContain("/api/", content);
        }
    }
}
