#if DEBUG
using CalFit.Models;

namespace CalFit.Services;

public enum CalFitBotMockScenario { Normal, MissingWeight, PhotoFailures, CanceledScan, AcknowledgementFailures, WrongPhotoSize }

public sealed class MockCalFitBotService(byte[] sampleJpeg) : ICalFitBotService
{
    private CalFitBotScan? pending;
    private CalFitBotScan? last;
    private CalFitBotMockScenario scenario;
    public bool Connected { get; set; } = true;
    public bool Standby { get; set; }
    public bool SimulateTimeout { get; set; }
    public double? WeightGrams { get; set; } = 385;
    public int RemainingPhotoFailures { get; set; }
    public int RemainingAckFailures { get; set; }
    public int PhotoDownloads { get; private set; }
    public int AckCalls { get; private set; }
    public string? PendingScanId => pending?.ScanId;

    public string Submit(CalFitBotMockScenario value = CalFitBotMockScenario.Normal)
    {
        scenario = value;
        RemainingPhotoFailures = value == CalFitBotMockScenario.PhotoFailures ? 2 : 0;
        RemainingAckFailures = value == CalFitBotMockScenario.AcknowledgementFailures ? 2 : 0;
        pending = new CalFitBotScan(Guid.NewGuid().ToString("N"),
            value == CalFitBotMockScenario.MissingWeight ? null : WeightGrams,
            sampleJpeg.Length + (value == CalFitBotMockScenario.WrongPhotoSize ? 1 : 0));
        last = pending;
        return pending.ScanId;
    }
    public void ReplayLastScan() { pending = last; scenario = CalFitBotMockScenario.Normal; }
    public void CancelScan() => pending = null;
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Connected) throw new HttpRequestException("Simulated robot disconnection.");
        if (SimulateTimeout) throw new TimeoutException("Simulated network timeout.");
    }
    public Task<Uri?> DiscoverAsync(CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult<Uri?>(new Uri("http://calfitbot.local/"));
    }
    public Task<CalFitBotStatus> GetStatusAsync(Uri address, CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult(new CalFitBotStatus("CalFitBot", 1, pending != null));
    }
    public Task<CalFitBotWeight> GetWeightAsync(Uri address, CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult(new CalFitBotWeight(Standby ? null : WeightGrams));
    }
    public Task<CalFitBotScan?> GetScanAsync(Uri address, CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult(pending);
    }
    public async Task<byte[]> GetPhotoAsync(Uri address, CalFitBotScan scan, CancellationToken ct)
    {
        Check(ct);
        PhotoDownloads++;
        await Task.Delay(100, ct);
        Check(ct);
        if (scenario == CalFitBotMockScenario.CanceledScan) pending = null;
        if (pending?.ScanId != scan.ScanId) throw new CalFitBotScanCanceledException();
        if (RemainingPhotoFailures > 0)
        {
            RemainingPhotoFailures--;
            throw new HttpRequestException("Simulated interrupted photo transfer.");
        }
        return sampleJpeg.ToArray();
    }
    public Task AcknowledgeAsync(Uri address, string scanId, CancellationToken ct)
    {
        Check(ct);
        AckCalls++;
        if (RemainingAckFailures > 0)
        {
            RemainingAckFailures--;
            throw new HttpRequestException("Simulated acknowledgement failure.");
        }
        if (last?.ScanId != scanId) throw new CalFitBotScanCanceledException();
        if (pending?.ScanId == scanId) pending = null;
        return Task.CompletedTask;
    }
}
#endif
