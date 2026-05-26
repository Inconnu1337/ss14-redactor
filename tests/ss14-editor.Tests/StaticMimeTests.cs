using Content.Editor.Editor;
using Xunit;

namespace Content.Editor.Tests;

public class StaticMimeTests
{
    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("styles.css", "text/css; charset=utf-8")]
    [InlineData("app.js", "application/javascript; charset=utf-8")]
    [InlineData("data.json", "application/json; charset=utf-8")]
    [InlineData("icon.png", "image/png")]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("sound.ogg", "audio/ogg")]
    [InlineData("clip.wav", "audio/wav")]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("unknown.xyz", "application/octet-stream")]
    [InlineData("noext", "application/octet-stream")]
    public void For_KnownExtension_ReturnsExpectedMime(string path, string expected)
    {
        Assert.Equal(expected, StaticMime.For(path));
    }

    [Fact]
    public void For_IsCaseInsensitive()
    {
        Assert.Equal("text/html; charset=utf-8", StaticMime.For("INDEX.HTML"));
        Assert.Equal("image/png", StaticMime.For("Icon.PNG"));
    }
}
