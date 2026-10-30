using System.Reflection;
using System.Xml.Linq;

namespace Health360.Domain.Tests;

public class PurityTests
{
    /// <summary>
    /// Health360.Domain runs in the app and the API, so it may depend only on the base class library.
    /// BannedSymbols.txt catches I/O and ambient clock/randomness inside it; this catches new
    /// references the compiled code uses. Domain_project_has_no_dependencies catches unused ones.
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

    /// <summary>
    /// An unused package or project reference never shows up in the compiled assembly, but it still
    /// flows to every project that references Domain, so check the project file as well.
    /// </summary>
    [Fact]
    public void Domain_project_has_no_dependencies()
    {
        var csproj = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Health360.Domain", "Health360.Domain.csproj"));
        string[] allowedPackages = ["Microsoft.CodeAnalysis.BannedApiAnalyzers"];

        var dependencies = csproj.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference" or "FrameworkReference")
            .Where(e => !(e.Name.LocalName == "PackageReference" && allowedPackages.Contains((string?)e.Attribute("Include"))))
            .Select(e => $"{e.Name.LocalName} {(string?)e.Attribute("Include")}")
            .ToList();

        Assert.Empty(dependencies);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Health360.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Health360.slnx not found above " + AppContext.BaseDirectory);
    }
}
