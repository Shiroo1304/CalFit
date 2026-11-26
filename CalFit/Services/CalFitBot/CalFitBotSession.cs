using CalFit.Models;

namespace CalFit.Services;

// One instance per scanner. A single gate covers polling, manual reconnect and receipt updates.
public sealed class CalFitBotSession(ICalFitBotService robot, CalFitBotInbox inbox, ICalFitBotAddressStore settings, ICalFitBotPhotoValidator? photoValidator = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, int> attempts = new();
    private CancellationTokenSource? lifetime;
    private Task? pollTask;
    private string owner = "";
    private string? manualAddress;
    private int reconnectTicks;
    private int metadataFailures;
    private string? canceledNotice;
    private bool disposed;
    public event Action? Changed;
    public Uri? Address { get; private set; }
    public CalFitBotConnectionState State { get; private set; } = CalFitBotConnectionState.Disconnected;
    public string Message { get; private set; } = "Waiting to connect to CalFitBot.";
    public double? LiveWeight { get; private set; }
    public CalFitBotReceipt? Receipt { get; private set; }
    public bool CanAnalyze => Receipt is { Acknowledged: true, Canceled: false, Consumed: false };

    private void Set(CalFitBotConnectionState state, string message)
    {
        State = state; Message = message; Changed?.Invoke();
    }

    public async Task StartAsync(string userId, CancellationToken ct = default)
    {
        if (lifetime != null) return;
        owner = userId;
        Receipt = await inbox.LatestAsync(owner, ct);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollTask = PollAsync(lifetime.Token);
    }

    public async Task ReconnectAsync(string? address, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(address)) _ = CalFitBotService.ParseAddress(address);
        await gate.WaitAsync(ct);
        try
        {
            manualAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
            Address = null; reconnectTicks = 0; LiveWeight = null; attempts.Clear(); metadataFailures = 0; canceledNotice = null;
            Set(CalFitBotConnectionState.Disconnected, "Reconnecting…");
        }
        finally { gate.Release(); }
        await TickAsync(ct);
    }

    public async Task RetryAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { attempts.Clear(); metadataFailures = 0; canceledNotice = null; }
        finally { gate.Release(); }
        await TickAsync(ct);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do { await TickAsync(ct); } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<bool> ConnectAsync(CancellationToken ct)
    {
        Set(CalFitBotConnectionState.Discovering, "Looking for CalFitBot…");
        var candidates = new List<Uri>();
        if (manualAddress != null) candidates.Add(CalFitBotService.ParseAddress(manualAddress));
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.LastAddress))
            {
                try { candidates.Add(CalFitBotService.ParseAddress(settings.LastAddress)); }
                catch (ArgumentException) { }
            }
        }
        foreach (var candidate in candidates)
            if (await ProbeAsync(candidate, ct)) return true;
        if (manualAddress != null) return false;
        Uri? discovered = await robot.DiscoverAsync(ct);
        if (discovered != null && await ProbeAsync(discovered, ct)) return true;
        return await ProbeAsync(new Uri("http://calfitbot.local/"), ct);
    }

    private async Task<bool> ProbeAsync(Uri address, CancellationToken ct)
    {
        Set(CalFitBotConnectionState.Connecting, "Checking CalFitBot…");
        try
        {
            var status = await robot.GetStatusAsync(address, ct);
            if (status.Device != "CalFitBot" || status.Protocol != 1) return false;
            Address = address;
            settings.LastAddress = address.AbsoluteUri; // Only after a successful protocol handshake.
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    // Public for deterministic simulator/transport tests; simultaneous callers are skipped.
    public async Task TickAsync(CancellationToken ct)
    {
        if (disposed || !await gate.WaitAsync(0, ct)) return;
        try
        {
            if (Address == null)
            {
                if (reconnectTicks++ % 3 != 0) return;
                if (!await ConnectAsync(ct))
                {
                    Set(CalFitBotConnectionState.Disconnected, "CalFitBot not found. Check the hotspot or enter its IP address.");
                    return;
                }
                reconnectTicks = 0;
            }
            CalFitBotStatus status;
            try { status = await robot.GetStatusAsync(Address!, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                Address = null; LiveWeight = null;
                Set(CalFitBotConnectionState.Disconnected, "Robot disconnected. Your received scan is retained. Reconnecting…");
                return;
            }

            // Retry a durable but unacknowledged receipt, even if a previous ACK response was lost.
            if (Receipt is { Acknowledged: false, Canceled: false, Consumed: false })
            {
                await AckAsync(Receipt, ct);
                return;
            }
            if (status.Pending)
            {
                if (metadataFailures >= 3)
                {
                    Set(CalFitBotConnectionState.RetryRequired, "Scan metadata failed three times. Tap Retry transfer.");
                    return;
                }
                CalFitBotScan? scan;
                try
                {
                    scan = await robot.GetScanAsync(Address!, ct);
                    if (scan != null) CalFitBotService.ValidateScan(scan);
                    metadataFailures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (CalFitBotScanCanceledException) { metadataFailures = 0; throw; }
                catch { metadataFailures++; throw; }
                if (scan == null)
                {
                    Set(CalFitBotConnectionState.Waiting, "No submitted scan is pending.");
                    return;
                }
                canceledNotice = null;
                if (Receipt is { Consumed: false, Canceled: false } current && current.Scan.ScanId != scan.ScanId)
                {
                    Set(CalFitBotConnectionState.Received, "Another scan is waiting on the robot. Finish or discard the current meal first.");
                    return;
                }
                if (attempts.GetValueOrDefault(scan.ScanId) >= 3)
                {
                    Set(CalFitBotConnectionState.RetryRequired, "Transfer paused after three failures. Tap Retry transfer.");
                    return;
                }
                var stored = await inbox.FindAsync(owner, scan.ScanId, ct);
                if (stored != null && stored.Scan != scan) throw new InvalidDataException("Scan ID was reused with different metadata.");
                if (stored is { Canceled: true })
                {
                    Set(CalFitBotConnectionState.Error, "This scan was canceled. Submit a new scan on the robot.");
                    return;
                }
                if (stored == null)
                {
                    attempts[scan.ScanId] = attempts.GetValueOrDefault(scan.ScanId) + 1;
                    Set(CalFitBotConnectionState.Receiving, "Receiving the robot photo and captured weight…");
                    var photo = await robot.GetPhotoAsync(Address!, scan, ct);
                    CalFitBotService.ValidatePhoto(scan, photo);
                    if (photoValidator != null) await photoValidator.ValidateAsync(photo, ct);
                    stored = await inbox.StoreAsync(owner, Address!, scan, photo, ct);
                    attempts.Remove(scan.ScanId);
                }
                if (!stored.Consumed) Receipt = stored;
                // Already consumed duplicates are acknowledged but never redisplayed as a new meal.
                await AckAsync(stored, ct);
                return;
            }
            try
            {
                var weight = await robot.GetWeightAsync(Address!, ct);
                CalFitBotService.ValidateWeight(weight.WeightGrams);
                LiveWeight = weight.WeightGrams;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { LiveWeight = null; } // Standby/sensor unavailable is not a disconnection.
            if (Receipt is { Canceled: true }) Set(CalFitBotConnectionState.Error, "Scan unavailable before acknowledgement. Submit a new scan on the robot.");
            else Set(Receipt == null ? CalFitBotConnectionState.Waiting : CalFitBotConnectionState.Received,
                Receipt == null ? canceledNotice ?? "Connected. Capture and SUBMIT a meal on CalFitBot." : "Photo and captured weight received. Analyze when ready.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CalFitBotScanCanceledException)
        {
            canceledNotice = "The scan was canceled or removed on the robot. Waiting for a new submission.";
            Set(CalFitBotConnectionState.Waiting, canceledNotice);
        }
        catch (Exception ex)
        {
            Set(CalFitBotConnectionState.Error, "Transfer failed: " + ex.Message);
        }
        finally { gate.Release(); }
    }

    private async Task AckAsync(CalFitBotReceipt receipt, CancellationToken ct)
    {
        string id = receipt.Scan.ScanId;
        if (attempts.GetValueOrDefault("ack:" + id) >= 3)
        {
            Set(CalFitBotConnectionState.RetryRequired, "Photo and weight are stored. Acknowledgement needs a retry; the photo will not be downloaded again.");
            return;
        }
        attempts["ack:" + id] = attempts.GetValueOrDefault("ack:" + id) + 1;
        Set(CalFitBotConnectionState.Acknowledging, "Photo and weight stored. Confirming receipt to the robot…");
        try
        {
            await robot.AcknowledgeAsync(Address!, id, ct);
            receipt = receipt with { Acknowledged = true };
            await inbox.UpdateAsync(owner, receipt, ct);
            attempts.Remove("ack:" + id);
            if (!receipt.Consumed) Receipt = receipt;
            Set(receipt.Consumed ? CalFitBotConnectionState.Waiting : CalFitBotConnectionState.Received,
                receipt.Consumed ? "Duplicate submission acknowledged; no duplicate meal created." : "Photo and captured weight received. Analyze when ready.");
        }
        catch (CalFitBotScanCanceledException)
        {
            receipt = receipt with { Canceled = true };
            await inbox.UpdateAsync(owner, receipt, ct);
            if (!receipt.Consumed) Receipt = receipt;
            Set(CalFitBotConnectionState.Error, "Scan unavailable before acknowledgement. Receipt retained, but not accepted as a successful submission.");
        }
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Receipt == null) return;
            await inbox.UpdateAsync(owner, Receipt with { Consumed = true }, ct);
            Receipt = null;
            Set(CalFitBotConnectionState.Waiting, "Ready for the next robot submission.");
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (lifetime != null) await lifetime.CancelAsync();
        if (pollTask != null) { try { await pollTask; } catch (OperationCanceledException) { } }
        lifetime?.Dispose();
    }
}
