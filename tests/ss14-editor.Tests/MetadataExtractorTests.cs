using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Content.Editor.Tests;

using Content.Editor.Editor;

/// <summary>
/// End-to-end: stage the test DLL (which contains fixture types with attribute
/// names matching SS14 conventions) as if it were a built Content.Server DLL,
/// then run <see cref="MetadataExtractor.Extract"/> and verify the produced
/// metadata.json.
/// </summary>
public class MetadataExtractorTests
{
    private static TempDir StageFixtureAsContentServer()
    {
        var tmp = new TempDir();
        var binServer = Path.Combine(tmp.Path, "bin", "Content.Server");
        Directory.CreateDirectory(binServer);

        // Copy the test DLL plus its alongside dependencies (xunit, fixtures all
        // ship in the same assembly so a single copy suffices).
        var testDll = typeof(MetadataExtractorTests).Assembly.Location;
        var srcDir = Path.GetDirectoryName(testDll)!;
        foreach (var dll in Directory.GetFiles(srcDir, "*.dll"))
            File.Copy(dll, Path.Combine(binServer, Path.GetFileName(dll)), overwrite: true);
        return tmp;
    }

    private static JsonDocument RunAndRead(string root)
    {
        var outputDir = Path.Combine(root, "Redactor");
        MetadataExtractor.Extract(root, outputDir);
        var metaPath = Path.Combine(outputDir, "metadata.json");
        Assert.True(File.Exists(metaPath), "metadata.json not written");
        return JsonDocument.Parse(File.ReadAllText(metaPath));
    }

    [Fact]
    public void Extract_WritesMetadataJson()
    {
        using var tmp = StageFixtureAsContentServer();
        using var doc = RunAndRead(tmp.Path);
        Assert.True(doc.RootElement.TryGetProperty("prototypes", out _));
        Assert.True(doc.RootElement.TryGetProperty("components", out _));
        Assert.True(doc.RootElement.TryGetProperty("dataDefinitions", out _));
    }

    [Fact]
    public void Extract_FindsFixturePrototypes()
    {
        using var tmp = StageFixtureAsContentServer();
        using var doc = RunAndRead(tmp.Path);
        var protos = doc.RootElement.GetProperty("prototypes");
        Assert.True(protos.TryGetProperty("fixtureEntity", out var fe));
        Assert.True(fe.GetProperty("inheriting").GetBoolean());
        Assert.True(protos.TryGetProperty("fixtureSimple", out var fs));
        Assert.False(fs.GetProperty("inheriting").GetBoolean());
    }

    [Fact]
    public void Extract_FindsFixtureComponent()
    {
        using var tmp = StageFixtureAsContentServer();
        using var doc = RunAndRead(tmp.Path);
        var comps = doc.RootElement.GetProperty("components");
        // RegisterComponentAttribute on FixtureLightComponent → name "FixtureLight"
        // (trailing "Component" suffix stripped by InferComponentName).
        Assert.True(comps.TryGetProperty("FixtureLight", out var light));
        var fields = light.GetProperty("fields").EnumerateArray().ToList();
        Assert.Contains(fields, f => f.GetProperty("name").GetString() == "Radius");
        Assert.Contains(fields, f => f.GetProperty("name").GetString() == "Sprite");
    }

    [Fact]
    public void Extract_FindsDataDefinitions()
    {
        using var tmp = StageFixtureAsContentServer();
        using var doc = RunAndRead(tmp.Path);
        var dds = doc.RootElement.GetProperty("dataDefinitions");
        // Match by ShortName (key is fullName which depends on namespace).
        bool found = false;
        foreach (var kv in dds.EnumerateObject())
        {
            if (kv.Value.GetProperty("shortName").GetString() == "FixtureDataDef") { found = true; break; }
        }
        Assert.True(found, "FixtureDataDef should be registered as a data definition");
    }

    [Fact]
    public void Extract_PolymorphicTypes_LinkAbstractToConcrete()
    {
        using var tmp = StageFixtureAsContentServer();
        using var doc = RunAndRead(tmp.Path);
        var poly = doc.RootElement.GetProperty("polymorphicTypes");
        // FixtureBaseEffect → [FixtureConcreteEffect]
        bool found = false;
        foreach (var kv in poly.EnumerateObject())
        {
            if (kv.Name.EndsWith("FixtureBaseEffect"))
            {
                var impls = kv.Value.EnumerateArray().Select(e => e.GetString()!).ToList();
                if (impls.Any(s => s.EndsWith("FixtureConcreteEffect"))) { found = true; break; }
            }
        }
        Assert.True(found, "polymorphic mapping FixtureBaseEffect → FixtureConcreteEffect missing");
    }

    [Fact]
    public void Extract_NoBinDirs_NoOp()
    {
        using var tmp = new TempDir();
        var outputDir = Path.Combine(tmp.Path, "Redactor");
        MetadataExtractor.Extract(tmp.Path, outputDir);
        var metaPath = Path.Combine(outputDir, "metadata.json");
        Assert.False(File.Exists(metaPath));
    }
}
