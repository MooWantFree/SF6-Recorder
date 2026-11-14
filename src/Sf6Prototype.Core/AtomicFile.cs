using System.IO;

namespace Sf6Prototype;

public static class AtomicFile
{
    public static void WriteText(string destination, string text)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(text); writer.Flush(); stream.Flush(flushToDisk: true);
            }
            Commit(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Commit(string temporary, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                return;
            }
            // Only retry Windows sharing/lock errors and ERROR_UNABLE_TO_REMOVE_REPLACED.
            // These preserve both names. Do not retry 1176/1177 or fall back to deleting
            // the destination: ReplaceFile can leave different states for those errors.
            // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew
            catch (IOException ex) when (attempt < 4 && (ex.HResult & 0xffff) is 32 or 33 or 1175)
            {
                Diagnostics.Write("cache", "replace_retry", ex, attempt: attempt + 1);
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }
}
