using System.Reflection;

namespace Stamp.Domain.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_depends_only_on_the_base_class_library()
    {
        var domain = Assembly.Load("Stamp.Domain");

        var nonFrameworkReferences = domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && name is not ("netstandard" or "mscorlib"))
            .ToList();

        Assert.Empty(nonFrameworkReferences);
    }
}
