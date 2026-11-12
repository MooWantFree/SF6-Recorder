using System.IO;

namespace Sf6Prototype;

public static class DataPaths
{
    public static string Resolve(string applicationDirectory, string? explicitDirectory = null)
    {
        if (explicitDirectory != null) return Path.GetFullPath(explicitDirectory);
        // All checkouts' Debug/Release/publish binaries share the portable app data.
        // An app copied outside a source checkout remains portable beside its EXE.
        for (var parent = new DirectoryInfo(applicationDirectory); parent != null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "build.ps1")) &&
                File.Exists(Path.Combine(parent.FullName, "src", "Sf6Prototype", "Sf6Prototype.csproj")))
                return Path.Combine(parent.FullName, "artifacts", "app", "Data");
        return Path.GetFullPath(Path.Combine(applicationDirectory, "Data"));
    }
}
