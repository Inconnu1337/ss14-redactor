using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Content.Editor.Editor;

internal sealed partial class ApiRouter
{
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
}
