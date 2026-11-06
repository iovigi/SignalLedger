using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace SignalLedger.Api.IntegrationTests;

public sealed class RoutingTests : IClassFixture<SignalLedgerApiFactory>
{
    private readonly HttpClient client;

    public RoutingTests(SignalLedgerApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsJsonStatus()
    {
        using var response = await client.GetAsync("/api/health");
        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", payload?.Status);
    }

    [Fact]
    public async Task UnknownApiRoute_ReturnsProblemJsonInsteadOfSpa()
    {
        using var response = await client.GetAsync("/api/does-not-exist");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("API endpoint not found", problem?.Title);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DeepClientRoute_ReturnsSpaIndex()
    {
        using var response = await client.GetAsync("/strategy-explorer");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SignalLedger frontend assets", body, StringComparison.Ordinal);
    }

    private sealed record HealthResponse(string Status);
}
