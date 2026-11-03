using Microsoft.Extensions.Options;

namespace MiniMes.Client.Core.Kiosk;

// Where a label the server made (and logged) goes. The kiosk cannot ask where to save (gloves, scan box keeps the
// focus): it writes each print as a file into one folder, the way a print queue would hold it (user decision M3b
// step 6). Returns the file written; throws IOException/UnauthorizedAccessException when the folder is not usable.
public interface ILabelPrinter
{
    Task<string> PrintAsync(string labelKey, string zpl, CancellationToken ct = default);
}

public sealed class FolderLabelPrinter(IOptions<ClientOptions> options, TimeProvider time) : ILabelPrinter
{
    public string Folder => string.IsNullOrWhiteSpace(options.Value.LabelOutputFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniMes", "labels")
        : options.Value.LabelOutputFolder;

    // C261103000123-20261103T202520444.zpl: the label number first (sorted per carton), then the time, so a reprint
    // never overwrites the first print. Local time of the kiosk PC (= plant time), like the times on its screen.
    public async Task<string> PrintAsync(string labelKey, string zpl, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"{labelKey}-{time.GetLocalNow():yyyyMMdd'T'HHmmssfff}.zpl");
        await File.WriteAllTextAsync(file, zpl, ct);
        return file;
    }
}
