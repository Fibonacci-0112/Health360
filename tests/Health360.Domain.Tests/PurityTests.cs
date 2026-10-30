using System.Reflection;

namespace Health360.Domain.Tests;

public class PurityTests
{
    /// <summary>
    /// Health360.Domain runs in the app and the API, so it may depend only on the base class library.
    /// BannedSymbols.txt catches I/O and ambient clock/randomness inside it; this catches new
    /// package or project references.
    /// </summary>
    [Fact]
    public void Domain_references_only_the_base_class_library()
    {
        var domain = Assembly.Load("Health360.Domain");

        var nonBcl = domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name != "System.Runtime" && name != "netstandard" && !name.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(nonBcl);
    }

    [Fact]
    public void Domain_does_not_reference_IO_assemblies()
    {
        var domain = Assembly.Load("Health360.Domain");
        string[] banned = ["System.Net.", "System.IO.FileSystem", "System.Data.", "System.Console"];

        var io = domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => banned.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(io);
    }
}
