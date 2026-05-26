using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

internal sealed partial class ApiRouter
{
    private async Task HandleTextureAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing 'path' query parameter");
            return;
        }
        relPath = NormalizeTexturesPath(relPath);

        var fullPath = PathSecurity.Resolve(ctx.TexturesDir, relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!File.Exists(fullPath))
        {
            await HttpJson.WriteErrorAsync(res, 404, "File not found");
            return;
        }

        res.ContentType = StaticMime.For(fullPath);
        res.AddHeader("Cache-Control", "public, max-age=300");
        var bytes = await File.ReadAllBytesAsync(fullPath);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private async Task HandleTextureBrowseAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = NormalizeTexturesPath(req.QueryString["path"] ?? "");

        var fullPath = PathSecurity.Resolve(ctx.TexturesDir, relPath.Length == 0 ? "." : relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!Directory.Exists(fullPath))
        {
            await HttpJson.WriteAsync(res, new { dirs = Array.Empty<string>(), files = Array.Empty<string>() });
            return;
        }
        var dirs = Directory.GetDirectories(fullPath).Select(Path.GetFileName).OrderBy(n => n).ToList();
        var files = Directory.GetFiles(fullPath).Select(Path.GetFileName)
            .Where(n => n != null && !n.StartsWith('.'))
            .OrderBy(n => n).ToList();
        await HttpJson.WriteAsync(res, new { dirs, files });
    }

    private async Task HandleAudioAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing 'path' query parameter");
            return;
        }
        relPath = NormalizeAudioPath(relPath);

        var fullPath = PathSecurity.Resolve(ctx.AudioDir, relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!File.Exists(fullPath))
        {
            await HttpJson.WriteErrorAsync(res, 404, "File not found");
            return;
        }

        res.ContentType = StaticMime.For(fullPath);
        res.AddHeader("Cache-Control", "public, max-age=300");
        var bytes = await File.ReadAllBytesAsync(fullPath);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private async Task HandleAudioBrowseAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = NormalizeAudioPath(req.QueryString["path"] ?? "");

        var fullPath = PathSecurity.Resolve(ctx.AudioDir, relPath.Length == 0 ? "." : relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!Directory.Exists(fullPath))
        {
            await HttpJson.WriteAsync(res, new { dirs = Array.Empty<string>(), files = Array.Empty<string>() });
            return;
        }
        var dirs = Directory.GetDirectories(fullPath).Select(Path.GetFileName).OrderBy(n => n).ToList();
        var files = Directory.GetFiles(fullPath).Select(Path.GetFileName)
            .Where(n => n != null && !n.StartsWith('.'))
            .OrderBy(n => n).ToList();
        await HttpJson.WriteAsync(res, new { dirs, files });
    }

    /// <summary>
    /// Normalises a texture path supplied by the client so both
    /// <c>"Objects/Tools/wrench.rsi"</c> and the SS14-style absolute form
    /// <c>"/Textures/Objects/Tools/wrench.rsi"</c> resolve to the same file
    /// under <see cref="EditorContext.TexturesDir"/>.
    /// </summary>
    private static string NormalizeTexturesPath(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            p = p["Textures/".Length..];
        return p.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Same idea as <see cref="NormalizeTexturesPath"/> but for audio paths.
    /// Accepts <c>"Effects/foo.ogg"</c>, <c>"/Audio/Effects/foo.ogg"</c>, etc.
    /// </summary>
    private static string NormalizeAudioPath(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("Audio/", StringComparison.OrdinalIgnoreCase))
            p = p["Audio/".Length..];
        return p.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Generic <c>ResPath</c> serving: any file under <c>Resources/</c>. Used
    /// by the generic ResPath autocomplete to preview images / play audio
    /// without caring which sub-tree the file lives in.
    /// </summary>
    private async Task HandleResourceAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing 'path' query parameter");
            return;
        }
        var normalized = relPath.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        var fullPath = PathSecurity.Resolve(ctx.ResourcesDir, normalized);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!File.Exists(fullPath))
        {
            await HttpJson.WriteErrorAsync(res, 404, "File not found");
            return;
        }

        res.ContentType = StaticMime.For(fullPath);
        res.AddHeader("Cache-Control", "public, max-age=300");
        var bytes = await File.ReadAllBytesAsync(fullPath);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    /// <summary>
    /// Browse directories under <c>Resources/</c>. Mirrors
    /// <see cref="HandleTextureBrowseAsync"/> but rooted at the
    /// project-wide resource tree so any <c>ResPath</c> field can find files
    /// across Textures/, Audio/, Prototypes/, etc.
    /// </summary>
    private async Task HandleResourceBrowseAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var rel = (req.QueryString["path"] ?? "").Replace('\\', '/').TrimStart('/');
        var normalized = rel.Replace('/', Path.DirectorySeparatorChar);

        var fullPath = PathSecurity.Resolve(ctx.ResourcesDir, normalized.Length == 0 ? "." : normalized);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!Directory.Exists(fullPath))
        {
            await HttpJson.WriteAsync(res, new { dirs = Array.Empty<string>(), files = Array.Empty<string>() });
            return;
        }
        var dirs = Directory.GetDirectories(fullPath).Select(Path.GetFileName).OrderBy(n => n).ToList();
        var files = Directory.GetFiles(fullPath).Select(Path.GetFileName)
            .Where(n => n != null && !n.StartsWith('.'))
            .OrderBy(n => n).ToList();
        await HttpJson.WriteAsync(res, new { dirs, files });
    }
}
