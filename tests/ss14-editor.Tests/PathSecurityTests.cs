using System.IO;
using Content.Editor.Editor;
using Xunit;

namespace Content.Editor.Tests;

public class PathSecurityTests
{
    [Fact]
    public void Resolve_NullRelative_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(PathSecurity.Resolve(tmp.Path, null));
    }

    [Fact]
    public void Resolve_EmptyRelative_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(PathSecurity.Resolve(tmp.Path, ""));
    }

    [Fact]
    public void Resolve_SimpleFile_ReturnsAbsolutePath()
    {
        using var tmp = new TempDir();
        var result = PathSecurity.Resolve(tmp.Path, "file.yml");
        Assert.NotNull(result);
        Assert.Equal(Path.Combine(tmp.Path, "file.yml"), result);
    }

    [Fact]
    public void Resolve_NestedFile_ReturnsAbsolutePath()
    {
        using var tmp = new TempDir();
        var result = PathSecurity.Resolve(tmp.Path, "sub/dir/file.yml");
        Assert.NotNull(result);
        Assert.StartsWith(tmp.Path, result);
        Assert.EndsWith("file.yml", result);
    }

    [Theory]
    [InlineData("../escape.yml")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.yml")]
    [InlineData("sub/../../../escape.yml")]
    public void Resolve_TraversalAttempt_ReturnsNull(string relative)
    {
        using var tmp = new TempDir();
        Assert.Null(PathSecurity.Resolve(tmp.Path, relative));
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideBase_ReturnsNull()
    {
        using var tmp = new TempDir();
        var outside = Path.GetTempPath();
        // Note: Path.Combine with an absolute second arg discards the first.
        // PathSecurity must catch this via the prefix check.
        Assert.Null(PathSecurity.Resolve(tmp.Path, outside));
    }

    [Fact]
    public void Resolve_NormalizesDotSegments()
    {
        using var tmp = new TempDir();
        var result = PathSecurity.Resolve(tmp.Path, "./sub/./file.yml");
        Assert.NotNull(result);
        Assert.Equal(Path.Combine(tmp.Path, "sub", "file.yml"), result);
    }

    [Fact]
    public void Resolve_DotDotInsideStaysWithinBase()
    {
        using var tmp = new TempDir();
        // sub/../file.yml resolves to base/file.yml — valid.
        var result = PathSecurity.Resolve(tmp.Path, "sub/../file.yml");
        Assert.NotNull(result);
        Assert.Equal(Path.Combine(tmp.Path, "file.yml"), result);
    }

    [Fact]
    public void Resolve_BaseDirWithoutTrailingSeparator_StillBlocksSiblingPrefix()
    {
        // Guard against the classic "base=/a/foo, attack=/a/foobar" prefix bug.
        var parent = Path.Combine(Path.GetTempPath(), "ss14-pathsec-" + System.Guid.NewGuid().ToString("N"));
        var baseDir = parent + "foo";
        var sibling = parent + "foobar";
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(sibling);
        try
        {
            // Try to escape via an absolute path to sibling — should be rejected.
            Assert.Null(PathSecurity.Resolve(baseDir, sibling + Path.DirectorySeparatorChar + "x.yml"));
        }
        finally
        {
            Directory.Delete(baseDir, true);
            Directory.Delete(sibling, true);
        }
    }
}
