using System.IO;
using SemanticStart.Core;

namespace SemanticStart.App;

internal static class Log
{
    private static readonly object Gate = new();
    private static string _path = Path.Combine(AppPaths.Root, "logs", "app.log");

    public static void Initialize()
    {
        AppPaths.EnsureCreated();
        _path = Path.Combine(AppPaths.LogDirectory, "app.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception ex, string message) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTimeOffset.Now:u} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine($"{level}: {message}");
        }
    }
}
