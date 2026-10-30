using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Memorizer.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Memorizer.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public class SearchEvalControllerTests(IntegrationTestFixture fixture)
{
    private WebApplicationFactory<Program> CreateFactory(string? enabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Storage"] = fixture.PostgresConnectionString,
                    ["Embeddings:ApiUrl"] = fixture.OllamaApiUrl,
                    ["Embeddings:Model"] = "all-minilm",
                    ["SearchEval:Enabled"] = enabled,
                }))
            .ConfigureTestServices(services =>
            {
                // The fixture owns migrations and model setup. Keep background seeding out of these requests.
                services.Remove(services.Single(service => service.ImplementationType == typeof(InitializationService)));
            }));

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Disabled_endpoints_return_404_before_validation_or_storage(string? enabled)
    {
        using var factory = CreateFactory(enabled).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStorage>();
                services.AddScoped<IStorage>(_ => throw new InvalidOperationException("STORAGE WAS TOUCHED"));
            }));
        using var client = factory.CreateClient();
        var endpoints = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "seed-corpus"),
            (HttpMethod.Post, "run"),
            (HttpMethod.Delete, "corpus"),
            (HttpMethod.Get, "corpus-status"),
            (HttpMethod.Post, "compare"),
            (HttpMethod.Post, "compare-org-search"),
        };

        foreach (var (method, path) in endpoints)
        {
            foreach (var body in new[] { "", "{}", "{", "{\"query\":\"test query\"}" })
            {
                using var request = new HttpRequestMessage(method, $"/api/search-eval/{path}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("Search evaluation endpoints are disabled.", payload.GetProperty("message").GetString());
            }
        }
    }

    [Fact]
    public async Task Enabled_endpoints_validate_requests_and_manage_the_corpus()
    {
        using var factory = CreateFactory("true");
        using var client = factory.CreateClient();
        // Seeding makes 135 sequential embedding requests.
        client.Timeout = TimeSpan.FromMinutes(5);
        foreach (var path in new[] { "compare", "compare-org-search" })
        {
            using var invalid = await client.PostAsJsonAsync($"/api/search-eval/{path}", new { });
            Assert.True(invalid.StatusCode == HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync());
        }

        try
        {
            using var seed = await client.PostAsync("/api/search-eval/seed-corpus", null);
            seed.EnsureSuccessStatusCode();
            var seeded = await seed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(seeded.GetProperty("mapping").EnumerateObject().Any());

            var status = await client.GetFromJsonAsync<JsonElement>("/api/search-eval/corpus-status");
            Assert.True(status.GetProperty("isFullySeeded").GetBoolean());
            foreach (var path in new[] { "compare", "compare-org-search" })
            {
                using var compare = await client.PostAsJsonAsync($"/api/search-eval/{path}", new { query = "search" });
                compare.EnsureSuccessStatusCode();
            }
        }
        catch
        {
            try
            {
                using var cleanup = await client.DeleteAsync("/api/search-eval/corpus");
            }
            catch
            {
                // Preserve the original failure if cleanup also fails.
            }
            throw;
        }

        using var delete = await client.DeleteAsync("/api/search-eval/corpus");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var deleted = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        Assert.True(deleted.RootElement.GetProperty("deleted").GetInt32() > 0);

        var empty = await client.GetFromJsonAsync<JsonElement>("/api/search-eval/corpus-status");
        Assert.Equal(0, empty.GetProperty("seededEntries").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("workspaceId").ValueKind);
        using var run = await client.PostAsync("/api/search-eval/run", null);
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.Contains("Corpus not seeded", await run.Content.ReadAsStringAsync());
    }
}
