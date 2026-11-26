using System.Net;
using System.Text.Json;
using CalFit.Models;

namespace CalFit.Services;

// Robot traffic is separate from CalFitApi: no backend URL or JWT is used here.
public sealed class CalFitBotService(HttpClient http, ICalFitBotDiscovery discovery) : ICalFitBotService
{
    public const int MaximumPhotoBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<Uri?> DiscoverAsync(CancellationToken ct) => discovery.DiscoverAsync(ct);

    public static Uri ParseAddress(string value)
    {
        string input = value.Trim();
        if (!input.Contains("://", StringComparison.Ordinal)) input = "http://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter only the robot IP or calfitbot.local, without an endpoint path.");
        bool allowed = uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            byte[] b = ip.GetAddressBytes();
            allowed = IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
                || b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168
                    || b[0] == 172 && b[1] >= 16 && b[1] <= 31);
        }
        if (!allowed) throw new ArgumentException("Use a local robot IP or .local hostname.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public async Task<CalFitBotStatus> GetStatusAsync(Uri address, CancellationToken ct)
    {
        var status = await GetJsonAsync<CalFitBotStatus>(address, "status", ct);
        if (status.Device != "CalFitBot" || status.Protocol != 1)
            throw new InvalidDataException("This device does not speak CalFitBot protocol 1.");
        return status;
    }
    public Task<CalFitBotWeight> GetWeightAsync(Uri a, CancellationToken ct) => GetJsonAsync<CalFitBotWeight>(a, "weight", ct);

    public async Task<CalFitBotScan?> GetScanAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var request = Request(HttpMethod.Get, address, "scan");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new CalFitBotScanCanceledException();
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<CalFitBotScan>(await ReadBoundedAsync(response, 16 * 1024, timeout.Token), Json)
            ?? throw new InvalidDataException("Empty scan metadata.");
    }

    public async Task<byte[]> GetPhotoAsync(Uri address, CalFitBotScan scan, CancellationToken ct)
    {
        ValidateScan(scan);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(HttpMethod.Get, address, "photo?id=" + Uri.EscapeDataString(scan.ScanId));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new CalFitBotScanCanceledException();
        response.EnsureSuccessStatusCode();
        byte[] photo = await ReadBoundedAsync(response, MaximumPhotoBytes, timeout.Token);
        ValidatePhoto(scan, photo);
        return photo;
    }

    public async Task AcknowledgeAsync(Uri address, string scanId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var request = Request(HttpMethod.Post, address, "ack?id=" + Uri.EscapeDataString(scanId));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new CalFitBotScanCanceledException();
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await ReadBoundedAsync(response, 16 * 1024, timeout.Token));
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The robot did not confirm acknowledgement.");
    }

    private async Task<T> GetJsonAsync<T>(Uri address, string route, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var request = Request(HttpMethod.Get, address, route);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(await ReadBoundedAsync(response, 16 * 1024, timeout.Token), Json)
            ?? throw new InvalidDataException("Empty robot response.");
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri address, string route)
    {
        var request = new HttpRequestMessage(method, new Uri(address, route)) { Version = HttpVersion.Version11 };
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Robot response exceeds its size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (destination.Length + count > limit) throw new InvalidDataException("Robot response exceeds its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return destination.ToArray();
    }

    public static void ValidateWeight(double? weight)
    {
        if (weight.HasValue && (!double.IsFinite(weight.Value) || weight.Value < 0))
            throw new InvalidDataException("The robot returned an invalid weight.");
    }
    public static void ValidateScan(CalFitBotScan scan)
    {
        if (string.IsNullOrWhiteSpace(scan.ScanId) || scan.ScanId.Length > 200 || scan.ScanId.Any(char.IsControl))
            throw new InvalidDataException("Invalid robot scan ID.");
        ValidateWeight(scan.WeightGrams);
        if (scan.PhotoBytes < 4 || scan.PhotoBytes > MaximumPhotoBytes) throw new InvalidDataException("Invalid photoBytes.");
    }
    public static void ValidatePhoto(CalFitBotScan scan, byte[] photo)
    {
        ValidateScan(scan);
        if (photo.Length != scan.PhotoBytes || photo[0] != 0xff || photo[1] != 0xd8 || photo[^2] != 0xff || photo[^1] != 0xd9)
            throw new InvalidDataException("Photo size or JPEG markers do not match the pending scan.");
    }
}
