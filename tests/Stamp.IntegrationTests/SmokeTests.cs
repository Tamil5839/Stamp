using System.Net;
using Stamp.IntegrationTests.Web;

namespace Stamp.IntegrationTests;

public sealed class SmokeTests(StampWebFactory factory) : IClassFixture<StampWebFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/auth/login")]
    public async Task Public_pages_render(string path)
    {
        var response = await factory.Browser().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Stamp", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
