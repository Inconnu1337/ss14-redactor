using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Content.Editor.Tests;

using Content.Editor.Editor;

public class ApiRouterTests
{
    /// <summary>
    /// Build a fully-wired EditorContext over a temp solution root with the
    /// Resources/Prototypes structure SS14 expects. The FileWatcher is
    /// constructed but not started so the prototypes dir must exist.
    /// </summary>
    private static (TempDir tmp, EditorContext ctx) BuildCtx()
    {
        var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Resources", "Prototypes"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Resources", "Textures"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Resources", "Audio"));
        var ctx = EditorServer.BuildContext(tmp.Path);
        return (tmp, ctx);
    }

    private static async Task<(HttpStatusCode status, JsonDocument body)> GetJsonAsync(HttpClient c, string url)
    {
        var resp = await c.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(text));
    }

    private static async Task<(HttpStatusCode status, JsonDocument body)> PostJsonAsync(HttpClient c, string url, object payload)
    {
        var resp = await c.PostAsync(url, JsonContent.Create(payload));
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(text));
    }

    [Fact]
    public async Task Status_NoContext_ReturnsConfiguredFalse()
    {
        var router = new ApiRouter(null);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await GetJsonAsync(server.Client, "/api/status");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Status_WithContext_ReturnsProjectInfo()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp;
        using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await GetJsonAsync(server.Client, "/api/status");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal(tmp.Path, body.RootElement.GetProperty("projectPath").GetString());
    }

    [Fact]
    public async Task UnknownEndpoint_Returns404()
    {
        var router = new ApiRouter(null);
        using var server = ApiTestServer.Start(router);

        var resp = await server.Client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_NoContext_Returns503()
    {
        var router = new ApiRouter(null);
        using var server = ApiTestServer.Start(router);

        var resp = await server.Client.GetAsync("/api/refresh-index");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Configure_MissingProjectPath_Returns400()
    {
        var router = new ApiRouter(null);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client, "/api/configure", new { });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Configure_NonexistentDir_Returns400()
    {
        var router = new ApiRouter(null);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client, "/api/configure",
            new { projectPath = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid()) });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Configure_DirWithoutPrototypes_Returns400()
    {
        using var tmp = new TempDir();
        var router = new ApiRouter(null);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await PostJsonAsync(server.Client, "/api/configure",
            new { projectPath = tmp.Path });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Resources/Prototypes", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task File_Get_ReadsContent()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var rel = "items/foo.yml";
        var full = Path.Combine(ctx.PrototypesDir, "items", "foo.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "- type: entity\n  id: Foo\n");
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await GetJsonAsync(server.Client, "/api/file?path=" + rel);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("id: Foo", body.RootElement.GetProperty("content").GetString());
        Assert.False(body.RootElement.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task File_Get_Nonexistent_Returns404()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var resp = await server.Client.GetAsync("/api/file?path=nope.yml");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task File_Get_PathTraversal_Returns403()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var resp = await server.Client.GetAsync("/api/file?path=" + Uri.EscapeDataString("../escape.yml"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task File_Post_WritesContent()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client, "/api/file?path=new.yml",
            new { content = "- type: entity\n  id: New\n" });

        Assert.Equal(HttpStatusCode.OK, status);
        var written = await File.ReadAllTextAsync(Path.Combine(ctx.PrototypesDir, "new.yml"));
        Assert.Contains("id: New", written);
    }

    [Fact]
    public async Task File_Post_EngineFile_Returns403()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        Directory.CreateDirectory(ctx.EnginePrototypesDir);
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client,
            "/api/file?path=" + ProtoIndexService.EnginePrefix + "anything.yml",
            new { content = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task SearchProtos_FindsByPrefix()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        await File.WriteAllTextAsync(Path.Combine(ctx.PrototypesDir, "x.yml"),
            "- type: entity\n  id: AppleFood\n- type: entity\n  id: BananaFood\n");
        ctx.ProtoIndex.Rebuild();
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await GetJsonAsync(server.Client, "/api/search-protos?type=entity&q=App&limit=5");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.True(body.RootElement.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task RefreshIndex_ReturnsCount()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        await File.WriteAllTextAsync(Path.Combine(ctx.PrototypesDir, "a.yml"),
            "- type: entity\n  id: A\n");
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await PostJsonAsync(server.Client, "/api/refresh-index", new { });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task Tree_ReturnsNodes()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        await File.WriteAllTextAsync(Path.Combine(ctx.PrototypesDir, "root.yml"), "[]");
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await GetJsonAsync(server.Client, "/api/tree");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Contains(body.RootElement.EnumerateArray(),
            n => n.GetProperty("name").GetString() == "root.yml");
    }

    [Fact]
    public async Task CreateFile_WritesAndIndexes()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, body) = await PostJsonAsync(server.Client, "/api/create-file",
            new { dir = "", name = "fresh.yml", content = "[]" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("fresh.yml", body.RootElement.GetProperty("path").GetString());
        Assert.True(File.Exists(Path.Combine(ctx.PrototypesDir, "fresh.yml")));
    }

    [Fact]
    public async Task DeleteFile_RemovesFromDisk()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var full = Path.Combine(ctx.PrototypesDir, "doomed.yml");
        await File.WriteAllTextAsync(full, "[]");
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var resp = await server.Client.DeleteAsync("/api/delete-file?path=doomed.yml");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(File.Exists(full));
    }

    [Fact]
    public async Task CreateFolder_InvalidName_Returns400()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client, "/api/create-folder",
            new { dir = "", name = "../escape" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task CreateFolder_Valid_Creates()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client, "/api/create-folder",
            new { dir = "", name = "newdir" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Directory.Exists(Path.Combine(ctx.PrototypesDir, "newdir")));
    }

    [Fact]
    public async Task DeleteFolder_NonEmptyWithoutRecursive_Returns409()
    {
        var (tmp, ctx) = BuildCtx();
        using var _ = tmp; using var __ = ctx.FileWatcher;
        var sub = Path.Combine(ctx.PrototypesDir, "stuff");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "x.yml"), "[]");
        var router = new ApiRouter(ctx);
        using var server = ApiTestServer.Start(router);

        var (status, _) = await PostJsonAsync(server.Client, "/api/delete-folder",
            new { path = "stuff", recursive = false });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(Directory.Exists(sub));
    }
}
