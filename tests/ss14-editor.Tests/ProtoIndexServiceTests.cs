using System.IO;
using System.Linq;
using Content.Editor.Editor;
using Xunit;

namespace Content.Editor.Tests;

public class ProtoIndexServiceTests
{
    private static ProtoIndexService BuildService(TempDir proto, TempDir? engine = null)
    {
        return new ProtoIndexService(proto.Path, engine?.Path ?? Path.Combine(Path.GetTempPath(), "ss14-no-engine-" + System.Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Rebuild_EmptyDir_ProducesEmptyIndex()
    {
        using var tmp = new TempDir();
        var svc = BuildService(tmp);
        svc.Rebuild();
        Assert.Equal(0, svc.TotalCount);
        Assert.Equal(0, svc.TypeCount);
    }

    [Fact]
    public void Rebuild_PicksUpYmlAndYamlRecursively()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "- type: entity\n  id: A\n");
        tmp.Write("sub/b.yaml", "- type: entity\n  id: B\n");
        tmp.Write("sub/deep/c.yml", "- type: tag\n  id: T\n");
        tmp.Write("notes.txt", "ignore");

        var svc = BuildService(tmp);
        svc.Rebuild();

        Assert.Equal(3, svc.TotalCount);
        Assert.Equal(2, svc.TypeCount);
        Assert.Contains(svc.Index["entity"], e => e.Id == "A" && e.File == "a.yml");
        Assert.Contains(svc.Index["entity"], e => e.Id == "B" && e.File == "sub/b.yaml");
        Assert.Contains(svc.Index["tag"], e => e.Id == "T" && e.File == "sub/deep/c.yml");
    }

    [Fact]
    public void Rebuild_EngineDir_PrefixedAndMarkedReadOnly()
    {
        using var content = new TempDir();
        using var engine = new TempDir();
        content.Write("a.yml", "- type: entity\n  id: ContentEntity\n");
        engine.Write("e.yml", "- type: entity\n  id: EngineEntity\n");

        var svc = new ProtoIndexService(content.Path, engine.Path);
        svc.Rebuild();

        var entities = svc.Index["entity"];
        Assert.Equal(2, entities.Count);
        var engineEntry = entities.Single(e => e.Id == "EngineEntity");
        Assert.True(engineEntry.ReadOnly);
        Assert.StartsWith(ProtoIndexService.EnginePrefix, engineEntry.File);

        var contentEntry = entities.Single(e => e.Id == "ContentEntity");
        Assert.False(contentEntry.ReadOnly);
    }

    [Fact]
    public void Search_PrefixMatchesComeFirst()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", """
            - type: entity
              id: FooBar
            - type: entity
              id: BarFoo
            - type: entity
              id: FooBaz
            """);

        var svc = BuildService(tmp);
        svc.Rebuild();

        var results = svc.Search("entity", "Foo", 10);
        Assert.Equal(3, results.Count);
        // Prefix matches FooBar, FooBaz first, then BarFoo (contains).
        Assert.Equal("BarFoo", results[2].Id);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsFirstLimitEntries()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", """
            - type: entity
              id: A
            - type: entity
              id: B
            - type: entity
              id: C
            """);

        var svc = BuildService(tmp);
        svc.Rebuild();

        var results = svc.Search("entity", "", 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_UnknownType_ReturnsEmpty()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "- type: entity\n  id: A\n");
        var svc = BuildService(tmp);
        svc.Rebuild();

        Assert.Empty(svc.Search("nonexistentType", "A", 10));
    }

    [Fact]
    public void Search_CaseInsensitive()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "- type: entity\n  id: HumanMob\n");
        var svc = BuildService(tmp);
        svc.Rebuild();

        Assert.Single(svc.Search("entity", "humanmob", 10));
        Assert.Single(svc.Search("entity", "HUMAN", 10));
    }

    [Fact]
    public void Search_CaseInsensitiveTypeFallback()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "- type: playTimeTracker\n  id: Overall\n");
        var svc = BuildService(tmp);
        svc.Rebuild();

        // Exact match still works.
        Assert.Single(svc.Search("playTimeTracker", "", 10));
        // And mismatched casing from a metadata-derived key resolves.
        Assert.Single(svc.Search("PlayTimeTracker", "", 10));
        Assert.Single(svc.Search("playtimetracker", "", 10));
    }

    [Fact]
    public void RefreshFile_RemovesOldEntriesAndAddsNew()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.yml", "- type: entity\n  id: Old\n");

        var svc = BuildService(tmp);
        svc.Rebuild();
        Assert.Contains(svc.Index["entity"], e => e.Id == "Old");

        File.WriteAllText(file, "- type: entity\n  id: New\n");
        svc.RefreshFile(file, "a.yml");

        Assert.DoesNotContain(svc.Index["entity"], e => e.Id == "Old");
        Assert.Contains(svc.Index["entity"], e => e.Id == "New");
    }

    [Fact]
    public void RefreshFile_FileDeleted_RemovesOldEntries()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.yml", "- type: entity\n  id: Doomed\n");

        var svc = BuildService(tmp);
        svc.Rebuild();
        Assert.Contains(svc.Index["entity"], e => e.Id == "Doomed");

        File.Delete(file);
        svc.RefreshFile(file, "a.yml");

        // Entry must be gone even though the file no longer exists.
        Assert.DoesNotContain(svc.Index.Values.SelectMany(l => l), e => e.Id == "Doomed");
    }
}
