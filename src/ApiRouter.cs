using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Content.Redactor.Redactor;

/// <summary>
/// Maps <c>/api/*</c> paths to handler delegates and dispatches incoming
/// requests. Endpoint handlers are deliberately small; heavy lifting lives in
/// dedicated services (<see cref="ProtoIndexService"/>, <see cref="SourceLocator"/>, ...).
/// </summary>
internal sealed class ApiRouter
{
    private volatile RedactorContext? _ctx;
    private readonly Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, Task>> _routes;

    /// <summary>Endpoints that work even when no project is configured yet.</summary>
    private static readonly HashSet<string> AlwaysAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/status",
        "/api/configure",
    };

    public ApiRouter(RedactorContext? initialCtx)
    {
        _ctx = initialCtx;
        _routes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/status"] = HandleStatusAsync,
            ["/api/configure"] = HandleConfigureAsync,
            ["/api/tree"] = HandleTreeAsync,
            ["/api/file"] = HandleFileAsync,
            ["/api/metadata"] = HandleMetadataAsync,
            ["/api/proto-index"] = HandleProtoIndexAsync,
            ["/api/search-protos"] = HandleSearchProtosAsync,
            ["/api/refresh-index"] = HandleRefreshIndexAsync,
            ["/api/open-in-explorer"] = HandleOpenInExplorerAsync,
            ["/api/open-default"] = HandleOpenDefaultAsync,
            ["/api/open-source"] = HandleOpenSourceAsync,
            ["/api/rename-file"] = HandleRenameFileAsync,
            ["/api/delete-file"] = HandleDeleteFileAsync,
            ["/api/create-file"] = HandleCreateFileAsync,
            ["/api/file-stamps"] = HandleFileStampsAsync,
            ["/api/rename-proto-id"] = HandleRenameProtoIdAsync,
            ["/api/create-folder"] = HandleCreateFolderAsync,
            ["/api/rename-folder"] = HandleRenameFolderAsync,
            ["/api/delete-folder"] = HandleDeleteFolderAsync,
            ["/api/texture"] = HandleTextureAsync,
            ["/api/texture-browse"] = HandleTextureBrowseAsync,
            ["/api/audio"] = HandleAudioAsync,
            ["/api/audio-browse"] = HandleAudioBrowseAsync,
            ["/api/events"] = HandleEventsAsync,
        };
    }

    public async Task<bool> DispatchAsync(string path, HttpListenerRequest req, HttpListenerResponse res)
    {
        res.ContentType = "application/json; charset=utf-8";
        if (_routes.TryGetValue(path, out var handler))
        {
            if (!AlwaysAllowed.Contains(path) && _ctx == null)
            {
                await HttpJson.WriteErrorAsync(res, 503, "No project configured. Open the editor in your browser and select a project folder.");
                return false;
            }
            await handler(req, res);
            // The events endpoint hijacks the response for the lifetime of the
            // connection; tell the caller not to close it.
            return path.Equals("/api/events", StringComparison.OrdinalIgnoreCase);
        }
        Console.Error.WriteLine($"[Redactor] Unknown API endpoint: {path}");
        await HttpJson.WriteErrorAsync(res, 404, "Unknown API endpoint");
        return false;
    }

    // ---------------------------------------------------------------------
    // Setup / status
    // ---------------------------------------------------------------------

    private Task HandleStatusAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx;
        if (ctx == null)
            return HttpJson.WriteAsync(res, new { configured = false });

        return HttpJson.WriteAsync(res, new
        {
            configured = true,
            projectPath = ctx.SolutionRoot,
            prototypes = ctx.ProtoIndex.TotalCount,
            typeCount = ctx.ProtoIndex.TypeCount,
        });
    }

    private async Task HandleConfigureAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var doc = await HttpJson.ReadBodyAsync(req);
        if (!doc.TryGetProperty("projectPath", out var pathEl))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing 'projectPath'");
            return;
        }

        var projectPath = pathEl.GetString()?.Trim();
        if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Directory not found");
            return;
        }

        // Validate it looks like an SS14 project
        var prototypesDir = Path.Combine(projectPath, "Resources", "Prototypes");
        if (!Directory.Exists(prototypesDir))
        {
            await HttpJson.WriteErrorAsync(res, 400,
                "Not a valid SS14 project: Resources/Prototypes folder not found. " +
                "Make sure you selected the project root (the folder containing Resources/, Content.Server/, etc.).");
            return;
        }

        var binServer = Path.Combine(projectPath, "bin", "Content.Server");
        var binClient = Path.Combine(projectPath, "bin", "Content.Client");
        if (!Directory.Exists(binServer) && !Directory.Exists(binClient))
        {
            await HttpJson.WriteErrorAsync(res, 400,
                "Project has not been built yet. Run 'dotnet build' in the project folder first, then try again.");
            return;
        }

        // Extract metadata (writes Redactor/metadata.json inside the project)
        try
        {
            Console.WriteLine($"[Redactor] Extracting metadata for: {projectPath}");
            MetadataExtractor.Extract(projectPath);
        }
        catch (Exception ex)
        {
            await HttpJson.WriteErrorAsync(res, 500, $"Failed to extract metadata: {ex.Message}");
            return;
        }

        // Build and activate the new context
        var newCtx = RedactorServer.BuildContext(projectPath);
        newCtx.FileWatcher.Changed += evt =>
        {
            var rel = evt.RelativePath;
            switch (evt.Kind)
            {
                case FileChangeKind.Deleted:
                    newCtx.ProtoIndex.RefreshFile(evt.FullPath, rel);
                    break;
                case FileChangeKind.Created:
                case FileChangeKind.Changed:
                    if (File.Exists(evt.FullPath))
                        newCtx.ProtoIndex.RefreshFile(evt.FullPath, rel);
                    break;
            }
            newCtx.Events.Broadcast(new { type = "file-change", kind = evt.Kind.ToString().ToLowerInvariant(), path = rel });
        };

        Console.WriteLine("[Redactor] Building prototype index...");
        newCtx.ProtoIndex.Rebuild();
        Console.WriteLine($"[Redactor] Indexed {newCtx.ProtoIndex.TotalCount} prototypes across {newCtx.ProtoIndex.TypeCount} types");
        newCtx.FileWatcher.Start();

        // Swap context (dispose old watcher if there was one)
        var oldCtx = _ctx;
        _ctx = newCtx;
        oldCtx?.FileWatcher.Dispose();

        Console.WriteLine($"[Redactor] Project configured: {projectPath}");
        await HttpJson.WriteAsync(res, new
        {
            success = true,
            projectPath,
            prototypes = newCtx.ProtoIndex.TotalCount,
            typeCount = newCtx.ProtoIndex.TypeCount,
        });
    }

    // ---------------------------------------------------------------------
    // Tree & metadata
    // ---------------------------------------------------------------------

    private Task HandleTreeAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var tree = FileTreeService.Build(ctx.PrototypesDir);
        if (Directory.Exists(ctx.EnginePrototypesDir))
        {
            var engineTree = FileTreeService.Build(ctx.EnginePrototypesDir, "", ProtoIndexService.EnginePrefix);
            FileTreeService.MarkReadOnly(engineTree);
            tree.Add(new FileTreeNode
            {
                Name = "⚙ Engine (read-only)",
                Path = "__engine__",
                IsDir = true,
                ReadOnly = true,
                Children = engineTree,
            });
        }
        return HttpJson.WriteAsync(res, tree);
    }

    private async Task HandleMetadataAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var metaPath = Path.Combine(ctx.RedactorDir, "metadata.json");
        if (!File.Exists(metaPath))
        {
            await HttpJson.WriteErrorAsync(res, 404, "metadata.json not found. Build the project first.");
            return;
        }
        var bytes = await File.ReadAllBytesAsync(metaPath);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private Task HandleProtoIndexAsync(HttpListenerRequest req, HttpListenerResponse res)
        => HttpJson.WriteAsync(res, _ctx!.ProtoIndex.Index);

    private Task HandleSearchProtosAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var q = req.QueryString["q"] ?? "";
        var type = req.QueryString["type"] ?? "entity";
        var limit = int.TryParse(req.QueryString["limit"], out var l) ? l : 50;
        return HttpJson.WriteAsync(res, _ctx!.ProtoIndex.Search(type, q, limit));
    }

    private Task HandleRefreshIndexAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        ctx.ProtoIndex.Rebuild();
        return HttpJson.WriteAsync(res, new { count = ctx.ProtoIndex.TotalCount });
    }

    // ---------------------------------------------------------------------
    // File operations
    // ---------------------------------------------------------------------

    private async Task HandleFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing 'path' query parameter");
            return;
        }

        bool isEngine = relPath.StartsWith(ProtoIndexService.EnginePrefix);
        var baseDir = isEngine ? ctx.EnginePrototypesDir : ctx.PrototypesDir;
        var actualRel = isEngine ? relPath[ProtoIndexService.EnginePrefix.Length..] : relPath;

        var fullPath = PathSecurity.Resolve(baseDir, actualRel);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }

        if (req.HttpMethod == "GET")
        {
            if (!File.Exists(fullPath))
            {
                await HttpJson.WriteErrorAsync(res, 404, "File not found");
                return;
            }
            var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            await HttpJson.WriteAsync(res, new { content, path = relPath, readOnly = isEngine });
        }
        else if (req.HttpMethod == "POST")
        {
            if (isEngine)
            {
                await HttpJson.WriteErrorAsync(res, 403, "Engine prototypes are read-only");
                return;
            }
            var doc = await HttpJson.ReadBodyAsync(req);
            if (!doc.TryGetProperty("content", out var contentEl))
            {
                await HttpJson.WriteErrorAsync(res, 400, "Missing 'content' in body");
                return;
            }
            var content = contentEl.GetString()!;
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            ctx.FileWatcher.SuppressNext(fullPath);
            await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false));
            ctx.ProtoIndex.RefreshFile(fullPath, relPath);
            await HttpJson.WriteAsync(res, new { success = true });
        }
    }

    private async Task HandleRenameFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var oldRel = doc.GetProperty("oldPath").GetString()!;
        var newName = doc.GetProperty("newName").GetString()!;

        var oldFull = PathSecurity.Resolve(ctx.PrototypesDir, oldRel);
        if (oldFull == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        var newFull = Path.Combine(Path.GetDirectoryName(oldFull)!, newName);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, newFull)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!File.Exists(oldFull))
        {
            await HttpJson.WriteErrorAsync(res, 404, "File not found");
            return;
        }
        File.Move(oldFull, newFull);
        Console.WriteLine($"[Redactor] File renamed: {oldRel} -> {newName}");
        var newRel = Path.GetRelativePath(ctx.PrototypesDir, newFull).Replace('\\', '/');
        await HttpJson.WriteAsync(res, new { success = true, newPath = newRel });
    }

    private async Task HandleDeleteFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing path");
            return;
        }
        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            Console.WriteLine($"[Redactor] File deleted: {relPath}");
        }
        await HttpJson.WriteAsync(res, new { success = true });
    }

    private async Task HandleCreateFileAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var parentDir = doc.TryGetProperty("dir", out var dirEl) ? dirEl.GetString() ?? "" : "";
        var fileName = doc.GetProperty("name").GetString()!;
        var content = doc.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";

        var dirFull = string.IsNullOrEmpty(parentDir)
            ? Path.GetFullPath(ctx.PrototypesDir)
            : PathSecurity.Resolve(ctx.PrototypesDir, parentDir);
        if (dirFull == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        var fileFull = Path.Combine(dirFull, fileName);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, fileFull)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        Directory.CreateDirectory(dirFull);
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        ctx.FileWatcher.SuppressNext(fileFull);
        await File.WriteAllTextAsync(fileFull, content, new UTF8Encoding(false));
        var rel = Path.GetRelativePath(ctx.PrototypesDir, fileFull).Replace('\\', '/');
        ctx.ProtoIndex.RefreshFile(fileFull, rel);
        await HttpJson.WriteAsync(res, new { success = true, path = rel });
    }

    private async Task HandleFileStampsAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var paths = doc.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToList();
        var stamps = new Dictionary<string, long>();
        foreach (var rp in paths)
        {
            var fp = PathSecurity.Resolve(ctx.PrototypesDir, rp);
            stamps[rp] = (fp != null && File.Exists(fp))
                ? File.GetLastWriteTimeUtc(fp).Ticks
                : -1;
        }
        await HttpJson.WriteAsync(res, stamps);
    }

    private async Task HandleRenameProtoIdAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var filePath = doc.GetProperty("path").GetString()!;
        var oldId = doc.GetProperty("oldId").GetString()!;
        var newId = doc.GetProperty("newId").GetString()!;
        var protoType = doc.GetProperty("type").GetString()!;

        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, filePath);
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

        ctx.ProtoIndex.RefreshFile(fullPath, filePath);
        Console.WriteLine($"[Redactor] Renamed prototype ID: {protoType}/{oldId} -> {newId} in {filePath}");
        await HttpJson.WriteAsync(res, new { success = true });
    }

    // ---------------------------------------------------------------------
    // Folder CRUD
    // ---------------------------------------------------------------------

    private async Task HandleCreateFolderAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var parentDir = doc.TryGetProperty("dir", out var dirEl) ? dirEl.GetString() ?? "" : "";
        var name = doc.GetProperty("name").GetString()!;

        if (!IsValidLeafName(name))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Invalid folder name");
            return;
        }

        var baseDir = string.IsNullOrEmpty(parentDir)
            ? Path.GetFullPath(ctx.PrototypesDir)
            : PathSecurity.Resolve(ctx.PrototypesDir, parentDir);
        if (baseDir == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        var target = Path.Combine(baseDir, name);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, target)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (Directory.Exists(target))
        {
            await HttpJson.WriteErrorAsync(res, 409, "Folder already exists");
            return;
        }
        Directory.CreateDirectory(target);
        var rel = Path.GetRelativePath(ctx.PrototypesDir, target).Replace('\\', '/');
        Console.WriteLine($"[Redactor] Folder created: {rel}");
        await HttpJson.WriteAsync(res, new { success = true, path = rel });
    }

    private async Task HandleRenameFolderAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var doc = await HttpJson.ReadBodyAsync(req);
        var oldRel = doc.GetProperty("oldPath").GetString()!;
        var newName = doc.GetProperty("newName").GetString()!;

        if (!IsValidLeafName(newName))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Invalid folder name");
            return;
        }

        var oldFull = PathSecurity.Resolve(ctx.PrototypesDir, oldRel);
        if (oldFull == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!Directory.Exists(oldFull))
        {
            await HttpJson.WriteErrorAsync(res, 404, "Folder not found");
            return;
        }
        var newFull = Path.Combine(Path.GetDirectoryName(oldFull)!, newName);
        if (PathSecurity.Resolve(ctx.PrototypesDir, Path.GetRelativePath(ctx.PrototypesDir, newFull)) == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (Directory.Exists(newFull) || File.Exists(newFull))
        {
            await HttpJson.WriteErrorAsync(res, 409, "Target already exists");
            return;
        }
        Directory.Move(oldFull, newFull);
        // Rebuild affected entries: any indexed file under oldRel must now be remapped.
        ctx.ProtoIndex.Rebuild();
        var newRel = Path.GetRelativePath(ctx.PrototypesDir, newFull).Replace('\\', '/');
        Console.WriteLine($"[Redactor] Folder renamed: {oldRel} -> {newRel}");
        await HttpJson.WriteAsync(res, new { success = true, newPath = newRel });
    }

    private async Task HandleDeleteFolderAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        // Accept either query (?path=) or JSON body { path, recursive }.
        string? relPath = req.QueryString["path"];
        bool recursive = false;
        if (req.HttpMethod == "POST")
        {
            var doc = await HttpJson.ReadBodyAsync(req);
            if (doc.TryGetProperty("path", out var p)) relPath = p.GetString();
            if (doc.TryGetProperty("recursive", out var r)) recursive = r.GetBoolean();
        }

        if (string.IsNullOrEmpty(relPath))
        {
            await HttpJson.WriteErrorAsync(res, 400, "Missing path");
            return;
        }

        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, relPath);
        if (fullPath == null)
        {
            await HttpJson.WriteErrorAsync(res, 403, "Access denied");
            return;
        }
        if (!Directory.Exists(fullPath))
        {
            await HttpJson.WriteAsync(res, new { success = true });
            return;
        }
        // Refuse to delete a non-empty folder unless recursive=true is set.
        var hasContents = Directory.EnumerateFileSystemEntries(fullPath).Any();
        if (hasContents && !recursive)
        {
            await HttpJson.WriteErrorAsync(res, 409, "Folder not empty");
            return;
        }
        Directory.Delete(fullPath, recursive);
        ctx.ProtoIndex.Rebuild();
        Console.WriteLine($"[Redactor] Folder deleted: {relPath} (recursive={recursive})");
        await HttpJson.WriteAsync(res, new { success = true });
    }

    /// <summary>
    /// Validates a single path segment supplied as a folder or file name. Rejects
    /// empty strings, path separators, parent-directory references, and Windows
    /// reserved characters.
    /// </summary>
    private static bool IsValidLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name == "." || name == "..") return false;
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    // ---------------------------------------------------------------------
    // OS integrations
    // ---------------------------------------------------------------------

    private async Task HandleOpenInExplorerAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, relPath);
        if (fullPath != null)
        {
            var target = File.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath;
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start("explorer.exe", $"/select,\"{target}\"");
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", $"-R \"{target}\"");
                else
                    Process.Start("xdg-open", Path.GetDirectoryName(target) ?? target);
            }
            catch { /* non-critical */ }
        }
        await HttpJson.WriteAsync(res, new { success = true });
    }

    private async Task HandleOpenDefaultAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var relPath = req.QueryString["path"];
        var fullPath = PathSecurity.Resolve(ctx.PrototypesDir, relPath);
        if (fullPath != null && File.Exists(fullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            }
            catch { /* non-critical */ }
        }
        await HttpJson.WriteAsync(res, new { success = true });
    }

    private async Task HandleOpenSourceAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ctx = _ctx!;
        var className = req.QueryString["class"];
        if (!string.IsNullOrEmpty(className))
        {
            var found = ctx.SourceLocator.Find(className);
            if (found != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = found, UseShellExecute = true });
                }
                catch { /* non-critical */ }
                await HttpJson.WriteAsync(res, new { success = true, path = found });
                return;
            }
        }
        await HttpJson.WriteAsync(res, new { success = false, error = "Source file not found" });
    }

    // ---------------------------------------------------------------------
    // Textures
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Audio (SoundSpecifier preview + browser)
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Event stream (SSE)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Long-lived <c>text/event-stream</c> endpoint. Replaces client-side polling
    /// for file change detection. Connection stays open until the client closes
    /// it; the response is not auto-closed by the dispatcher.
    /// </summary>
    private Task HandleEventsAsync(HttpListenerRequest req, HttpListenerResponse res)
        => _ctx!.Events.SubscribeAsync(res, default);

    /// <summary>
    /// Normalises a texture path supplied by the client so both
    /// <c>"Objects/Tools/wrench.rsi"</c> and the SS14-style absolute form
    /// <c>"/Textures/Objects/Tools/wrench.rsi"</c> resolve to the same file
    /// under <see cref="RedactorContext.TexturesDir"/>.
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
}
