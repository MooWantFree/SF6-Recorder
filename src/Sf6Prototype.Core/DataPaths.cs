using System.IO;

namespace Sf6Prototype;

public static class DataPaths
{
    public static string Resolve(string applicationDirectory, string? explicitDirectory = null, string? updateRoot = null)
    {
        if (explicitDirectory != null) return Path.GetFullPath(explicitDirectory);
        if (updateRoot != null) return Path.GetFullPath(Path.Combine(updateRoot, "Data"));
        // All checkouts' Debug/Release/publish binaries share the portable app data.
        // An app copied outside a source checkout remains portable beside its EXE.
        for (var parent = new DirectoryInfo(applicationDirectory); parent != null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "build.ps1")) &&
                File.Exists(Path.Combine(parent.FullName, "src", "Sf6Prototype", "Sf6Prototype.csproj")))
                return Path.Combine(parent.FullName, "artifacts", "app", "Data");
        // Velopack replaces current/ during an update. Keep the existing portable
        // Data beside the launcher at the root, never among versioned binaries.
        return Path.GetFullPath(Path.Combine(applicationDirectory, "Data"));
    }
}
