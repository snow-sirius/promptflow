using System.Text;
using System.IO;

namespace PromptFlow.Services;

public static class AppLog
{
    private const int RetentionDays = 14;
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlow", "logs");

    public static string CurrentPath => Path.Combine(DirectoryPath, $"promptflow-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INF", message, null);
    public static void Warn(string message) => Write("WRN", message, null);
    public static void Error(string message, Exception exception) => Write("ERR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                DeleteExpiredFiles();
                using var writer = new StreamWriter(CurrentPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine($"[{DateTime.Now:O}] [{level}] {message}");
                if (exception is not null) writer.WriteLine(exception);
            }
        }
        catch
        {
            // Diagnostics must never interfere with clipboard monitoring.
        }
    }

    private static void DeleteExpiredFiles()
    {
        var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "promptflow-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch
            {
                // An antivirus scan or another process can briefly lock a log.
            }
        }
    }
}
