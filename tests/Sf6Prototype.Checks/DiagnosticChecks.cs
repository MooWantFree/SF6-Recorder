using System.Text.Json;
using Sf6Prototype;

public static class DiagnosticChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var root = Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"));
        var log = new DiagnosticLog(root, 1024, 2);
        for (var i = 0; i < 50; i++) log.Write("sync", "page_saved", attempt: i);
        Check(Directory.GetFiles(root).Length == 3, "Diagnostic rotation retains only the current file and two backups");
        foreach (var file in Directory.GetFiles(root))
            foreach (var line in File.ReadAllLines(file)) using (JsonDocument.Parse(line)) { }
        Exception failure;
        try { throw new InvalidOperationException("SECRET_COOKIE_AND_RESPONSE"); }
        catch (Exception ex) { failure = ex; }
        log.Write("sync", "profile_parse", failure);
        var text = File.ReadAllText(log.FilePath);
        Check(text.Contains("InvalidOperationException") && text.Contains("DiagnosticChecks"), "Errors retain their type and stack");
        Check(!text.Contains("SECRET_COOKIE_AND_RESPONSE"), "Exception messages cannot leak credentials or response bodies");
        var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "file");
        new DiagnosticLog(blocked).Write("sync", "failed", failure);
        Check(File.ReadAllText(blocked) == "file", "An unwritable log never interrupts the operation");
        var previous = Diagnostics.Log;
        var scoped = new DiagnosticLog(Path.Combine(root, "scopes")); Diagnostics.Log = scoped;
        try
        {
            async Task Work(string stage)
            {
                using var scope = Diagnostics.Begin(stage);
                await Task.Yield(); Diagnostics.Write(stage, "after_await");
            }
            await Task.WhenAll(Work("first"), Work("second"));
            Diagnostics.Write("outside", "done");
            var entries = File.ReadAllLines(scoped.FilePath).Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray();
            string? Id(JsonElement e) => e.GetProperty("taskId").GetString();
            var first = entries.Where(e => e.GetProperty("operation").GetString() == "first").Select(Id).Distinct().ToArray();
            var second = entries.Where(e => e.GetProperty("operation").GetString() == "second").Select(Id).Distinct().ToArray();
            Check(first.Length == 1 && first[0] != null && second.Length == 1 && first[0] != second[0], "Concurrent tasks preserve independent correlation IDs across awaits");
            Check(Id(entries.Last()) == null, "A completed scope does not leak its task ID to subsequent work");
        }
        finally { Diagnostics.Log = previous; }
        return count;
    }
}
