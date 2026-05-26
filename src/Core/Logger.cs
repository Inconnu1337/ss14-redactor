using System;

namespace Content.Editor.Editor;

internal static class Logger
{
    public static void Info(string message)
        => Console.WriteLine(message);

    public static void Warn(string message)
        => Console.WriteLine($"[WARN] {message}");

    public static void Error(string message)
        => Console.Error.WriteLine($"[ERROR] {message}");

    public static void Error(string message, Exception ex)
        => Console.Error.WriteLine($"[ERROR] {message}: {ex}");
}
