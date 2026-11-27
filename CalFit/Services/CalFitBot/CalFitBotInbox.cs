using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalFit.Models;

namespace CalFit.Services;

// A receipt directory is committed only after BOTH metadata and JPEG are durable.
public sealed class CalFitBotInbox(string root)
{
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string DirectoryFor(string owner, string id) => Path.Combine(root, Key(owner), Key(id));

    public async Task<CalFitBotReceipt?> FindAsync(string owner, string id, CancellationToken ct)
    {
        string directory = DirectoryFor(owner, id);
        string metadata = Path.Combine(directory, "receipt.json");
        if (!File.Exists(metadata)) return null;
        var receipt = JsonSerializer.Deserialize<CalFitBotReceipt>(await File.ReadAllTextAsync(metadata, ct), CalFitBotJsonContext.Default.CalFitBotReceipt)
            ?? throw new InvalidDataException("Invalid local scan receipt.");
        if (receipt.Scan.ScanId != id) throw new InvalidDataException("Local scan identity mismatch.");
        string photo = Path.Combine(directory, "photo.jpg");
        CalFitBotService.ValidatePhoto(receipt.Scan, await File.ReadAllBytesAsync(photo, ct));
        return receipt with { PhotoPath = photo };
    }

    public async Task<CalFitBotReceipt> StoreAsync(string owner, Uri address, CalFitBotScan scan, byte[] photo, CancellationToken ct)
    {
        CalFitBotService.ValidatePhoto(scan, photo);
        var existing = await FindAsync(owner, scan.ScanId, ct);
        if (existing != null)
        {
            if (existing.Scan != scan) throw new InvalidDataException("The robot reused a scan ID with different metadata.");
            return existing;
        }
        string destination = DirectoryFor(owner, scan.ScanId);
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, "photo.jpg"), photo, ct);
            var receipt = new CalFitBotReceipt(scan, address.AbsoluteUri, Path.Combine(destination, "photo.jpg"), DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(staging, "receipt.json"), JsonSerializer.Serialize(receipt, CalFitBotJsonContext.Default.CalFitBotReceipt), ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return receipt;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public async Task UpdateAsync(string owner, CalFitBotReceipt receipt, CancellationToken ct)
    {
        string destination = Path.Combine(DirectoryFor(owner, receipt.Scan.ScanId), "receipt.json");
        string temporary = destination + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt, CalFitBotJsonContext.Default.CalFitBotReceipt), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<CalFitBotReceipt?> LatestAsync(string owner, CancellationToken ct)
    {
        string directory = Path.Combine(root, Key(owner));
        if (!Directory.Exists(directory)) return null;
        var receipts = new List<CalFitBotReceipt>();
        foreach (var file in Directory.EnumerateFiles(directory, "receipt.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(Path.GetDirectoryName(file)!).StartsWith('.')) continue;
            var receipt = JsonSerializer.Deserialize<CalFitBotReceipt>(await File.ReadAllTextAsync(file, ct), CalFitBotJsonContext.Default.CalFitBotReceipt);
            if (receipt is { Consumed: false, Canceled: false })
            {
                var verified = await FindAsync(owner, receipt.Scan.ScanId, ct);
                if (verified != null) receipts.Add(verified);
            }
        }
        return receipts.OrderBy(x => x.ReceivedAt).FirstOrDefault();
    }
}
