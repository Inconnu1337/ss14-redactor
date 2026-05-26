using Content.Editor.Editor;
using Xunit;

namespace Content.Editor.Tests;

public class SourceLocatorTests
{
    [Fact]
    public void Find_NullOrEmpty_ReturnsNull()
    {
        var loc = new SourceLocator("/nowhere");
        Assert.Null(loc.Find(null!));
        Assert.Null(loc.Find(""));
        Assert.Null(loc.Find("   "));
    }

    [Fact]
    public void Find_NoSearchDirsExist_ReturnsNull()
    {
        using var tmp = new TempDir();
        var loc = new SourceLocator(tmp.Path);
        Assert.Null(loc.Find("Foo"));
    }

    [Fact]
    public void Find_TypeInContentServer_ReturnsPath()
    {
        using var tmp = new TempDir();
        var expected = tmp.Write("Content.Server/Sub/FooComponent.cs", "// class FooComponent {}");

        var loc = new SourceLocator(tmp.Path);
        var result = loc.Find("Content.Server.Sub.FooComponent");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Find_PrefersFirstSearchDir()
    {
        using var tmp = new TempDir();
        var serverPath = tmp.Write("Content.Server/Foo.cs", "");
        tmp.Write("Content.Client/Foo.cs", "");
        tmp.Write("Content.Shared/Foo.cs", "");

        var loc = new SourceLocator(tmp.Path);
        Assert.Equal(serverPath, loc.Find("Foo"));
    }

    [Fact]
    public void Find_FallsBackToSharedAndRobustToolbox()
    {
        using var tmp = new TempDir();
        var robust = tmp.Write("RobustToolbox/Robust.Shared/IoC/Container.cs", "");

        var loc = new SourceLocator(tmp.Path);
        Assert.Equal(robust, loc.Find("Container"));
    }

    [Fact]
    public void Find_NestedTypePlusSuffix_StripsToOuter()
    {
        using var tmp = new TempDir();
        var expected = tmp.Write("Content.Shared/Outer.cs", "");

        var loc = new SourceLocator(tmp.Path);
        Assert.Equal(expected, loc.Find("Content.Shared.Outer+Inner"));
    }

    [Fact]
    public void Find_UnknownType_ReturnsNull()
    {
        using var tmp = new TempDir();
        tmp.Mkdir("Content.Server");
        tmp.Mkdir("Content.Client");
        tmp.Mkdir("Content.Shared");
        tmp.Mkdir("RobustToolbox");

        var loc = new SourceLocator(tmp.Path);
        Assert.Null(loc.Find("NoSuchType"));
    }
}
