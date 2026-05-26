using System.Linq;
using Content.Editor.Editor;
using Xunit;

namespace Content.Editor.Tests;

public class FileTreeServiceTests
{
    [Fact]
    public void Build_MissingDirectory_ReturnsEmpty()
    {
        var result = FileTreeService.Build("/no/such/path/here");
        Assert.Empty(result);
    }

    [Fact]
    public void Build_FlatYamlFiles_ReturnsAllSortedAlphabetically()
    {
        using var tmp = new TempDir();
        tmp.Write("zeta.yml", "[]");
        tmp.Write("alpha.yml", "[]");
        tmp.Write("beta.yaml", "[]");
        tmp.Write("notes.txt", "ignored");

        var nodes = FileTreeService.Build(tmp.Path);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(new[] { "alpha.yml", "beta.yaml", "zeta.yml" }, nodes.Select(n => n.Name));
        Assert.All(nodes, n => Assert.False(n.IsDir));
    }

    [Fact]
    public void Build_TxtFiles_AreFiltered()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "[]");
        tmp.Write("readme.txt", "x");
        tmp.Write("data.json", "{}");

        var nodes = FileTreeService.Build(tmp.Path);

        Assert.Single(nodes);
        Assert.Equal("a.yml", nodes[0].Name);
    }

    [Fact]
    public void Build_DirectoriesComeBeforeFiles()
    {
        using var tmp = new TempDir();
        tmp.Write("zeta.yml", "[]");
        tmp.Mkdir("alpha");
        tmp.Write("alpha/inner.yml", "[]");

        var nodes = FileTreeService.Build(tmp.Path);

        Assert.Equal(2, nodes.Count);
        Assert.True(nodes[0].IsDir);
        Assert.Equal("alpha", nodes[0].Name);
        Assert.False(nodes[1].IsDir);
        Assert.Equal("zeta.yml", nodes[1].Name);
    }

    [Fact]
    public void Build_NestedTree_PathsAreForwardSlashRelative()
    {
        using var tmp = new TempDir();
        tmp.Write("sub/deep/file.yml", "[]");

        var nodes = FileTreeService.Build(tmp.Path);

        var sub = Assert.Single(nodes);
        Assert.Equal("sub", sub.Name);
        Assert.Equal("sub", sub.Path);
        var deep = Assert.Single(sub.Children!);
        Assert.Equal("sub/deep", deep.Path);
        var file = Assert.Single(deep.Children!);
        Assert.Equal("sub/deep/file.yml", file.Path);
    }

    [Fact]
    public void Build_WithPathPrefix_PrefixesAllPaths()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "[]");
        tmp.Write("sub/b.yml", "[]");

        var nodes = FileTreeService.Build(tmp.Path, relativePath: "", pathPrefix: "__engine__/");

        var sub = nodes.First(n => n.IsDir);
        var aFile = nodes.First(n => !n.IsDir);

        Assert.Equal("__engine__/sub", sub.Path);
        Assert.Equal("__engine__/a.yml", aFile.Path);
        Assert.Equal("__engine__/sub/b.yml", sub.Children![0].Path);
    }

    [Fact]
    public void MarkReadOnly_FlagsAllNodesRecursively()
    {
        using var tmp = new TempDir();
        tmp.Write("a.yml", "[]");
        tmp.Write("sub/b.yml", "[]");

        var nodes = FileTreeService.Build(tmp.Path);
        FileTreeService.MarkReadOnly(nodes);

        Assert.All(nodes, n =>
        {
            Assert.True(n.ReadOnly);
            if (n.Children != null)
                Assert.All(n.Children, c => Assert.True(c.ReadOnly));
        });
    }
}
