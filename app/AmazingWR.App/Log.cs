using System.Diagnostics;
using System.IO;

namespace AmazingWR.App;

/// <summary>Plain-text diagnostics in %LOCALAPPDATA%\AmazingWR\app.log (rotated at 512 KB). Never throws.</summary>
internal static class Log
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmazingWR");
    private static readonly string FilePath = Path.Combine(Dir, "app.log");
    private static readonly object Gate = new();

    public static void Write(object? message) => Append(message?.ToString() ?? "");
    public static void Write(string message, Exception ex) => Append($"{message}: {ex}");

    private static void Append(string text)
    {
        Debug.WriteLine(text);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024) File.Move(FilePath, Path.Combine(Dir, "app.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
