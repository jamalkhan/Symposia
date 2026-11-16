using System.Reflection;
using System.Xml.Linq;

namespace Symposia.BlobStorage.CredentialAuthority.Tests;

/// <summary>
/// The Authority's dependency graph stays free of the network stack and Nethereum (#252 FR5, AC5). Checked on the
/// project files, where a new reference first appears even before any type from it is used, and on compiled metadata.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] Forbidden = ["Nethereum", "Symposia.Network.Node", "Symposia.Blockchain"];

    private static bool IsForbidden(string name) => Forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void AuthorityProjectClosure_HasNoNetworkNodeOrNethereum()
    {
        var start = Path.Combine(RepoRoot(), "BlobStorage", "Symposia.BlobStorage.CredentialAuthority", "Symposia.BlobStorage.CredentialAuthority.csproj");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([Path.GetFullPath(start)]);
        var names = new List<string>();
        while (pending.TryPop(out var project))
        {
            if (!seen.Add(project))
            {
                continue;
            }

            names.Add(Path.GetFileNameWithoutExtension(project));
            var xml = XDocument.Load(project);
            names.AddRange(xml.Descendants("PackageReference").Select(p => (string?)p.Attribute("Include") ?? ""));
            foreach (var reference in xml.Descendants("ProjectReference").Select(p => (string?)p.Attribute("Include")).OfType<string>())
            {
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        Assert.Contains("Symposia.Security.TlsPinning", names);
        Assert.DoesNotContain(names, IsForbidden);
    }

    [Fact]
    public void AuthorityAssemblyGraph_HasNoNetworkNodeOrNethereum()
    {
        var seen = new HashSet<string>();
        var pending = new Stack<AssemblyName>([typeof(AuthorityStore).Assembly.GetName()]);
        while (pending.TryPop(out var name))
        {
            if (!seen.Add(name.Name!) || !name.Name!.StartsWith("Symposia"))
            {
                continue; // only our own assemblies decide what is pulled in
            }

            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies())
            {
                pending.Push(reference);
            }
        }

        Assert.DoesNotContain(seen, IsForbidden);
    }
}
