using MiniMes.Application.Execution;

namespace MiniMes.Infrastructure.Execution;

// SOP files from one folder (Sop:RootPath). Only documents a kiosk can show (PDF, images), and never a path
// outside the folder: the URL comes from master data, but the folder sits next to the server's own files.
public sealed class SopFileStore(string rootPath) : ISopFileStore
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;

    public SopFile? Open(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;   // "../" tricks
        if (!ContentTypes.TryGetValue(Path.GetExtension(full), out var contentType) || !File.Exists(full)) return null;
        return new SopFile(File.OpenRead(full), contentType);
    }
}
