using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Sf6Prototype;

// Only caller-supplied stage names and numeric metadata are accepted. Exception messages,
// URLs, headers and response bodies can contain credentials and are deliberately excluded.
public sealed class DiagnosticLog(string directory, long maxBytes = 2 * 1024 * 1024, int backups = 3)
{
    private readonly object _gate = new();
    public string FilePath { get; } = Path.Combine(directory, "diagnostics.jsonl");
    public void Write(string operation, string stage, Exception? error = null, string? taskId = null,
        double? elapsedMs = null, int? statusCode = null, int? attempt = null, int? page = null, string? category = null)
    {
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow, pid = Environment.ProcessId, taskId,
                operation, stage, elapsedMs, statusCode, attempt, page, category, error = Error(error, 0)
            }) + "\n";
            var size = Encoding.UTF8.GetByteCount(entry);
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + size > maxBytes)
                {
                    for (var i = Math.Max(1, backups); i >= 1; i--)
                    {
                        var source = i == 1 ? FilePath : FilePath + "." + (i - 1);
                        var destination = FilePath + "." + i;
                        if (File.Exists(source)) File.Move(source, destination, true);
                    }
                }
                File.AppendAllText(FilePath, entry, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { /* A diagnostic failure must never interrupt saving, cancellation or shutdown. */ }
    }
    private static object? Error(Exception? error, int depth)
    {
        if (error == null || depth >= 4) return null;
        var stack = error.StackTrace;
        if (stack?.Length > 12000) stack = stack[..12000];
        return new
        {
            type = error.GetType().FullName, code = error.HResult, stack,
            httpStatus = error is HttpRequestException http ? (int?)http.StatusCode : null,
            inner = Error(error.InnerException, depth + 1)
        };
    }
}

public static class Diagnostics
{
    private static readonly AsyncLocal<string?> TaskId = new();
    public static DiagnosticLog? Log { get; set; }
    public static void Write(string operation, string stage, Exception? error = null,
        double? elapsedMs = null, int? statusCode = null, int? attempt = null, int? page = null, string? category = null) =>
        Log?.Write(operation, stage, error, TaskId.Value, elapsedMs, statusCode, attempt, page, category);
    public static IDisposable Begin(string operation)
    {
        var previous = TaskId.Value;
        TaskId.Value = Guid.NewGuid().ToString("N");
        Write(operation, "started");
        return new Scope(operation, previous);
    }
    private sealed class Scope(string operation, string? previous) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            Write(operation, "finished", elapsedMs: Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
            TaskId.Value = previous;
        }
    }
}
