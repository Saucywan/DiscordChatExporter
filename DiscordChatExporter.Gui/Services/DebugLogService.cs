using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Gui.Services;

public class DebugLogService : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public string LogFilePath { get; }

    public DebugLogService()
    {
        var logsDirPath = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(logsDirPath);

        LogFilePath = Path.Combine(logsDirPath, "gui-debug.log");
    }

    public Task LogAsync(string message) => WriteAsync(message);

    public Task LogExceptionAsync(Exception ex, string context) =>
        WriteAsync($"{context}: {ex}");

    private async Task WriteAsync(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";

        await _mutex.WaitAsync();

        try
        {
            await File.AppendAllTextAsync(LogFilePath, line);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        _mutex.Dispose();
    }
}
