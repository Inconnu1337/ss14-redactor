using System;
using System.IO;
using System.Threading.Tasks;
using Content.Editor.Editor;

namespace Content.Editor;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // Running with no args starts the editor in "setup mode":
        // the browser opens a project-picker page instead of the editor.
        if (args.Length == 0)
        {
            await EditorServer.StartAsync(solutionRoot: null, port: 2701);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "extract":
                var extractRoot = args.Length > 1 ? args[1] : FindSolutionRoot();
                if (extractRoot == null)
                {
                    Console.Error.WriteLine("Could not find solution root. Pass it as argument.");
                    return;
                }
                MetadataExtractor.Extract(extractRoot, EditorServer.ProjectDataDir(extractRoot));
                break;

            case "scandefaults":
            {
                if (args.Length < 3) { Console.Error.WriteLine("Usage: scandefaults <dll> <TypeFullName>"); return; }
                var sc = new CtorDefaultsScanner();
                sc.ScanAssembly(args[1]);
                var m = sc.GetDefaultsFor(args[2]);
                if (m == null) { Console.WriteLine($"(no entry for {args[2]}; {sc.TypesWithDefaults} types had defaults)"); return; }
                foreach (var kv in m) Console.WriteLine($"  {kv.Key} = {kv.Value ?? "null"} ({kv.Value?.GetType().Name ?? "<null>"})");
                return;
            }

            case "serve":
                var serveRoot = args.Length > 1 ? args[1] : FindSolutionRoot();
                // serveRoot == null is intentional: the server starts in "setup mode"
                // and lets the user pick a project folder in the browser.
                var port = args.Length > 2 ? int.Parse(args[2]) : 2701;

                if (serveRoot != null)
                    MetadataExtractor.Extract(serveRoot, EditorServer.ProjectDataDir(serveRoot));

                await EditorServer.StartAsync(serveRoot, port);
                break;

            default:
                PrintUsage();
                break;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SS14 Prototype Editor");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ss14-editor extract [solutionRoot]  - Extract prototype metadata to Editor/metadata.json");
        Console.WriteLine("  ss14-editor serve [solutionRoot] [port] - Start the visual editor (default port: 2701)");
        Console.WriteLine();
        Console.WriteLine("When run without arguments the editor starts and lets you pick the project folder in the browser.");
    }

    /// <summary>
    /// Walks up the directory tree from the current working directory looking for
    /// any folder that looks like an SS14 project root. Works with any fork —
    /// not just the vanilla crystalledge repository.
    /// </summary>
    private static string? FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (IsSolutionRoot(dir))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static bool IsSolutionRoot(string dir)
    {
        // Primary indicator: SS14 content structure
        if (Directory.Exists(Path.Combine(dir, "Resources", "Prototypes")))
            return true;
        // Secondary: any solution file at the root
        if (Directory.GetFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0)
            return true;
        if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0)
            return true;
        return false;
    }
}
