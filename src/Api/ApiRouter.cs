using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

/// <summary>
/// Thin dispatcher: looks up <c>/api/*</c> path in the route table and
/// delegates to handler methods. The handler methods themselves live in
/// partial-class files grouped by domain (see <c>src/Api/*Api.cs</c>).
/// </summary>
internal sealed partial class ApiRouter
{
    private volatile EditorContext? _ctx;
    private readonly Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, Task>> _routes;

    /// <summary>Endpoints that work even when no project is configured yet.</summary>
    private static readonly HashSet<string> AlwaysAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/status",
        "/api/configure",
        "/api/browse-folder",
    };

    public ApiRouter(EditorContext? initialCtx)
    {
        _ctx = initialCtx;
        _routes = new(StringComparer.OrdinalIgnoreCase)
        {
            // Status / setup (StatusApi.cs)
            ["/api/status"] = HandleStatusAsync,
            ["/api/configure"] = HandleConfigureAsync,
            ["/api/browse-folder"] = HandleBrowseFolderAsync,

            // Tree + prototype index (TreeApi.cs)
            ["/api/tree"] = HandleTreeAsync,
            ["/api/metadata"] = HandleMetadataAsync,
            ["/api/proto-index"] = HandleProtoIndexAsync,
            ["/api/search-protos"] = HandleSearchProtosAsync,
            ["/api/refresh-index"] = HandleRefreshIndexAsync,
            ["/api/rename-proto-id"] = HandleRenameProtoIdAsync,
            ["/api/git-status"] = HandleGitStatusAsync,

            // File CRUD (FileApi.cs)
            ["/api/file"] = HandleFileAsync,
            ["/api/rename-file"] = HandleRenameFileAsync,
            ["/api/delete-file"] = HandleDeleteFileAsync,
            ["/api/create-file"] = HandleCreateFileAsync,
            ["/api/file-stamps"] = HandleFileStampsAsync,

            // Folder CRUD (FolderApi.cs)
            ["/api/create-folder"] = HandleCreateFolderAsync,
            ["/api/rename-folder"] = HandleRenameFolderAsync,
            ["/api/delete-folder"] = HandleDeleteFolderAsync,

            // Assets (AssetApi.cs)
            ["/api/texture"] = HandleTextureAsync,
            ["/api/texture-browse"] = HandleTextureBrowseAsync,
            ["/api/audio"] = HandleAudioAsync,
            ["/api/audio-browse"] = HandleAudioBrowseAsync,
            ["/api/resource"] = HandleResourceAsync,
            ["/api/res-browse"] = HandleResourceBrowseAsync,

            // OS integration (SourceApi.cs)
            ["/api/open-in-explorer"] = HandleOpenInExplorerAsync,
            ["/api/open-default"] = HandleOpenDefaultAsync,
            ["/api/open-source"] = HandleOpenSourceAsync,

            // SSE (EventsApi.cs)
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
        Logger.Error($"Unknown API endpoint: {path}");
        await HttpJson.WriteErrorAsync(res, 404, "Unknown API endpoint");
        return false;
    }
}
