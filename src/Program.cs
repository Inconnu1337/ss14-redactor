using System;
using System.IO;
using System.Threading.Tasks;
using Content.Redactor.Redactor;

namespace Content.Redactor;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // Running with no args starts the editor in "setup mode":
        // the browser opens a project-picker page instead of the editor.
        if (args.Length == 0)
        {
            await RedactorServer.StartAsync(solutionRoot: null, port: 5555);
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
                MetadataExtractor.Extract(extractRoot);
                break;

            case "serve":
                var serveRoot = args.Length > 1 ? args[1] : FindSolutionRoot();
                // serveRoot == null is intentional: the server starts in "setup mode"
                // and lets the user pick a project folder in the browser.
                var port = args.Length > 2 ? int.Parse(args[2]) : 5555;

                if (serveRoot != null)
                    EnsureMetadataFresh(serveRoot);

                await RedactorServer.StartAsync(serveRoot, port);
                break;

            default:
                PrintUsage();
                break;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SS14 Prototype Redactor");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ss14-redactor extract [solutionRoot]  - Extract prototype metadata to Redactor/metadata.json");
        Console.WriteLine("  ss14-redactor serve [solutionRoot] [port] - Start the visual editor (default port: 5555)");
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

    /// <summary>
    /// Re-extracts metadata.json if missing or if any scanned binary is newer
    /// than the cache. Keeps the redactor's view in sync with the latest build
    /// without forcing the user to remember the manual extract step.
    /// </summary>
    private static void EnsureMetadataFresh(string solutionRoot)
    {
        try
        {
            var metaPath = Path.Combine(solutionRoot, "Redactor", "metadata.json");
            var metaTime = File.Exists(metaPath) ? File.GetLastWriteTimeUtc(metaPath) : DateTime.MinValue;

            var newest = DateTime.MinValue;
            foreach (var rel in new[] { "bin/Content.Server", "bin/Content.Client" })
            {
                var dir = Path.Combine(solutionRoot, rel);
                if (!Directory.Exists(dir)) continue;
                foreach (var dll in Directory.EnumerateFiles(dir, "Content.*.dll", SearchOption.TopDirectoryOnly))
                {
                    var t = File.GetLastWriteTimeUtc(dll);
                    if (t > newest) newest = t;
                }
            }

            // Also include this redactor assembly itself: changes to
            // FieldExtractor / MetadataExtractor classifier logic should
            // trigger a regen even if the game DLLs haven't changed.
            try
            {
                var selfDll = typeof(Program).Assembly.Location;
                if (!string.IsNullOrEmpty(selfDll) && File.Exists(selfDll))
                {
                    var t = File.GetLastWriteTimeUtc(selfDll);
                    if (t > newest) newest = t;
                }
            }
            catch { /* ignore */ }

            if (newest > metaTime)
            {
                Console.WriteLine("[Redactor] metadata.json out of date — regenerating...");
                MetadataExtractor.Extract(solutionRoot);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Redactor] EnsureMetadataFresh failed: {ex.Message}");
        }
    }
}
