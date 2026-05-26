using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

/// <summary>
/// Lightweight HTTP server that serves the Redactor web UI and exposes
/// REST endpoints for file browsing, reading/writing YAML, and searching
/// prototype IDs. Routing logic lives in <see cref="ApiRouter"/>; service
/// implementations are split into dedicated classes.
/// </summary>
public static class EditorServer
{
    /// <summary>
    /// Embedded resource name prefix for web UI files.
    /// Matches the &lt;Link&gt; WebUI\... attribute in the .csproj.
    /// </summary>
    private const string EmbeddedWebPrefix = "Content.Editor.WebUI.";

    public static async Task StartAsync(string? solutionRoot, int port)
    {
        EditorContext? initialCtx = null;
        if (solutionRoot != null)
        {
            initialCtx = BuildContext(solutionRoot);
            Logger.Info("Building prototype index...");
            initialCtx.ProtoIndex.Rebuild();
            Logger.Info($"Indexed {initialCtx.ProtoIndex.TotalCount} prototypes across {initialCtx.ProtoIndex.TypeCount} types");

            initialCtx.FileWatcher.Changed += evt =>
            {
                var rel = evt.RelativePath;
                switch (evt.Kind)
                {
                    case FileChangeKind.Deleted:
                        initialCtx.ProtoIndex.RefreshFile(evt.FullPath, rel);
                        break;
                    case FileChangeKind.Created:
                    case FileChangeKind.Changed:
                        if (File.Exists(evt.FullPath))
                            initialCtx.ProtoIndex.RefreshFile(evt.FullPath, rel);
                        break;
                }
                initialCtx.Events.Broadcast(new { type = "file-change", kind = evt.Kind.ToString().ToLowerInvariant(), path = rel });
            };
            initialCtx.FileWatcher.Start();
        }
        else
        {
            Logger.Info("No project configured. Open the editor in your browser to select a project.");
        }

        var router = new ApiRouter(initialCtx);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Logger.Info($"Editor running at http://localhost:{port}/");
        Logger.Info("Press Ctrl+C to stop.");

        TryOpenBrowser($"http://localhost:{port}/");

        while (true)
        {
            var httpCtx = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestAsync(httpCtx, router));
        }
    }

    /// <summary>
    /// Builds a fully-configured <see cref="EditorContext"/> for the given project root.
    /// Called both at startup (when a root is provided) and by <see cref="ApiRouter"/>
    /// when the user configures a project via the browser UI.
    /// </summary>
    /// <summary>
    /// Returns a per-project data directory stored in the OS user's local
    /// application data folder (never inside the scanned project tree).
    /// </summary>
    internal static string ProjectDataDir(string solutionRoot)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var normalized = solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectName = Path.GetFileName(normalized);
        var safe = new System.Text.StringBuilder();
        foreach (var c in projectName)
            safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(normalized.ToLowerInvariant());
        var hash = System.Security.Cryptography.SHA256.HashData(pathBytes);
        var hashStr = Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        return Path.Combine(appData, "ss14-editor", $"{safe}-{hashStr}");
    }

    internal static EditorContext BuildContext(string solutionRoot)
    {
        var EditorDir = ProjectDataDir(solutionRoot);
        var prototypesDir = Path.Combine(solutionRoot, "Resources", "Prototypes");
        var resourcesDir = Path.Combine(solutionRoot, "Resources");
        var texturesDir = Path.Combine(solutionRoot, "Resources", "Textures");
        var audioDir = Path.Combine(solutionRoot, "Resources", "Audio");
        var enginePrototypesDir = Path.Combine(solutionRoot, "RobustToolbox", "Resources", "EnginePrototypes");

        return new EditorContext
        {
            SolutionRoot = solutionRoot,
            EditorDir = EditorDir,
            PrototypesDir = prototypesDir,
            ResourcesDir = resourcesDir,
            TexturesDir = texturesDir,
            AudioDir = audioDir,
            EnginePrototypesDir = enginePrototypesDir,
            ProtoIndex = new ProtoIndexService(prototypesDir, enginePrototypesDir),
            SourceLocator = new SourceLocator(solutionRoot),
            Events = new EventStreamService(),
            FileWatcher = new FileWatcherService(prototypesDir),
        };
    }

    private static async Task HandleRequestAsync(HttpListenerContext httpCtx, ApiRouter router)
    {
        var req = httpCtx.Request;
        var res = httpCtx.Response;

        // No CORS headers: the WebUI is served by this same listener (same
        // origin), so cross-origin requests are not legitimate. Allowing them
        // with "Access-Control-Allow-Origin: *" would let any website the user
        // visits hit our /api/file POST and rewrite their prototype files.
        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 405;
            res.Close();
            return;
        }

        bool keepAlive = false;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/api/"))
            {
                Logger.Info($"{req.HttpMethod} {path}");
                keepAlive = await router.DispatchAsync(path, req, res);
            }
            else
            {
                await ServeStaticAsync(path, res);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"handling {req.HttpMethod} {req.Url}", ex);
            res.StatusCode = 500;
            res.ContentType = "application/json";
            await HttpJson.WriteAsync(res, new { error = ex.Message });
        }
        finally
        {
            if (!keepAlive) res.Close();
        }
    }

    private static async Task ServeStaticAsync(string urlPath, HttpListenerResponse res)
    {
        if (urlPath == "/") urlPath = "/index.html";

        // 1. Try dev-mode: look for files in the WebUI/ folder one level above the binary.
        //    Binary is at bin/; WebUI/ is at the repo root.
        //    This lets developers edit JS/CSS without recompiling.
        var devPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "WebUI",
            urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(devPath))
        {
            res.ContentType = StaticMime.For(urlPath);
            var devBytes = await File.ReadAllBytesAsync(devPath);
            res.ContentLength64 = devBytes.Length;
            await res.OutputStream.WriteAsync(devBytes);
            return;
        }

        // 2. Serve from embedded resources (production / standalone mode).
        var stream = GetEmbeddedWebFile(urlPath);
        if (stream == null)
        {
            res.StatusCode = 404;
            res.ContentType = "text/plain";
            await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("404 Not Found"));
            return;
        }

        res.ContentType = StaticMime.For(urlPath);
        using (stream)
        {
            // ContentLength64 is unknown for embedded streams — let the OS close the connection.
            await stream.CopyToAsync(res.OutputStream);
        }
    }

    /// <summary>
    /// Loads a web UI file from the assembly's embedded resources.
    /// <para>
    /// Mapping: <c>/js/api.js</c> → <c>Content.Editor.WebUI.js.api.js</c>
    /// (path separators become dots, matching the MSBuild Link attribute).
    /// </para>
    /// </summary>
    private static Stream? GetEmbeddedWebFile(string urlPath)
    {
        // /js/api.js → "js.api.js"
        var rel = urlPath.TrimStart('/').Replace('/', '.').Replace('\\', '.');
        if (string.IsNullOrEmpty(rel)) return null;
        var resourceName = EmbeddedWebPrefix + rel;
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* non-critical */ }
    }
}

public sealed class FileTreeNode
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public bool ReadOnly { get; set; }
    public List<FileTreeNode>? Children { get; set; }
}

public sealed class ProtoIndexEntry
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string File { get; set; } = "";
    public string[]? Parents { get; set; }
    public bool Abstract { get; set; }
    public bool ReadOnly { get; set; }
}

public sealed class ProtoSearchResult
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
}
