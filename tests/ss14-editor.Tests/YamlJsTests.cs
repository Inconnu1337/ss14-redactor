using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Content.Editor.Tests;

/// <summary>
/// Runs the JavaScript test suite for WebUI YAML helpers via Node.js.
/// The actual assertions live in tests/yaml/yaml-respectful.test.js;
/// this class is just the xUnit entry-point so that `dotnet test` covers
/// JS behaviour automatically.
/// </summary>
public class YamlJsTests
{
    [Fact]
    public void YamlRespectful_AllCommentStylesPreserved()
    {
        var repoRoot   = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tests", "yaml", "yaml-respectful.test.js");

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = "node",
            ArgumentList     = { scriptPath },
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute  = false
        })!;

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        var output = (stdout + "\n" + stderr).Trim();
        Assert.True(proc.ExitCode == 0,
            $"JS tests failed (exit {proc.ExitCode}):\n{output}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ss14-editor.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root (ss14-editor.csproj not found)");
    }
}
