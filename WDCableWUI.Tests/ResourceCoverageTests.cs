using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class ResourceCoverageTests
{
    [TestMethod]
    public void AllXamlUidsHaveEnglishAndChineseResourceRoots()
    {
        var root = FindRepositoryRoot();
        var uids = ReadXamlUids(root);
        var englishRoots = ReadResourceRoots(Path.Combine(root, "Strings", "en", "Resources.resw"));
        var chineseRoots = ReadResourceRoots(Path.Combine(root, "Strings", "zh-CN", "Resources.resw"));

        CollectionAssert.IsSubsetOf(uids.ToList(), englishRoots.ToList(), "Missing English resources for one or more x:Uid roots.");
        CollectionAssert.IsSubsetOf(uids.ToList(), chineseRoots.ToList(), "Missing Chinese resources for one or more x:Uid roots.");
    }

    [TestMethod]
    public void PackageAllowsInboundTrafficOnPublicNetworks()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Package.appxmanifest"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var capabilities = document
            .Descendants(foundation + "Capability")
            .Select(element => element.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        CollectionAssert.Contains(capabilities, "internetClientServer");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WDCableWUI.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate WDCableWUI.csproj from the test output directory.");
    }

    private static SortedSet<string> ReadXamlUids(string root)
    {
        var files = new[]
        {
            Path.Combine(root, "App.xaml"),
            Path.Combine(root, "MainWindow.xaml")
        }
        .Concat(Directory.EnumerateFiles(Path.Combine(root, "UI"), "*.xaml", SearchOption.AllDirectories));

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "x:Uid=\"([^\"]+)\""))
            {
                result.Add(match.Groups[1].Value);
            }
        }

        return result;
    }

    private static SortedSet<string> ReadResourceRoots(string path)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var text = File.ReadAllText(path);
        foreach (Match match in Regex.Matches(text, "<data name=\"([^\"]+)\""))
        {
            result.Add(match.Groups[1].Value.Split('.')[0]);
        }

        return result;
    }
}
