using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Stamp.IntegrationTests;

public sealed class SmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Home_page_renders()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Stamp", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
