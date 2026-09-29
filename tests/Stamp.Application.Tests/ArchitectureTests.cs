using System.Reflection;

namespace Stamp.Application.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Stamp.Infrastructure")]
    [InlineData("Stamp.Web")]
    [InlineData("Stripe.net")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("Npgsql")]
    public void Application_does_not_depend_on_outer_layers_or_vendors(string forbiddenPrefix)
    {
        var application = Assembly.Load("Stamp.Application");

        var offending = application.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offending);
    }
}
