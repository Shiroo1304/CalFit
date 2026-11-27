namespace CalFit.Services;

// Owns the existing session across tabs; backgrounding and sign-out stop network work.
public sealed class CalFitBotForegroundReceiver : IAsyncDisposable
{
    private readonly CalFitBotSession session;
    private readonly CalFitAppActivity activity;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly object cancellationSync = new();
    private string? requestedOwner, currentOwner, announcedScan, presentedScan;
    private CancellationTokenSource? runCancellation;
    private bool disposed;
    public event Action<string>? ScanReady;
    public string? LastError { get; private set; }

    public CalFitBotForegroundReceiver(CalFitBotSession session, CalFitAppActivity activity)
    {
        this.session = session; this.activity = activity;
        activity.Changed += OnActivityChanged;
        session.Changed += OnSessionChanged;
    }

    public Task SetOwnerAsync(string? owner)
    {
        requestedOwner = string.IsNullOrWhiteSpace(owner) ? null : owner;
        if (requestedOwner != currentOwner) CancelPending();
        return ReconcileAsync();
    }

    private void OnActivityChanged()
    {
        if (!activity.IsActive)
        {
            if (announcedScan != presentedScan) announcedScan = null;
            CancelPending();
        }
        _ = ReconcileAsync();
    }

    public bool MarkPresented(string scanId)
    {
        if (!activity.IsActive || currentOwner == null || currentOwner != requestedOwner
            || session.Receipt is not { Acknowledged: true, Canceled: false, Consumed: false } receipt
            || receipt.Scan.ScanId != scanId) return false;
        presentedScan = scanId;
        return true;
    }

    private void CancelPending()
    {
        lock (cancellationSync) runCancellation?.Cancel();
        session.CancelPending();
    }

    private async Task ReconcileAsync()
    {
        await transitions.WaitAsync();
        try
        {
            if (disposed) return;
            if (currentOwner != requestedOwner)
            {
                await session.ResetAsync();
                currentOwner = requestedOwner;
                announcedScan = null; presentedScan = null;
            }
            if (activity.IsActive && currentOwner != null)
            {
                CancellationToken token;
                lock (cancellationSync)
                {
                    if (runCancellation == null || runCancellation.IsCancellationRequested)
                    {
                        runCancellation?.Dispose();
                        runCancellation = new CancellationTokenSource();
                    }
                    token = runCancellation.Token;
                }
                await session.StartAsync(currentOwner, token);
                if (!activity.IsActive || currentOwner != requestedOwner) await session.PauseAsync();
            }
            else
                await session.PauseAsync();
            LastError = null;
            OnSessionChanged();
        }
        catch (OperationCanceledException) when (!activity.IsActive || currentOwner != requestedOwner || disposed) { }
        catch (Exception ex) { LastError = ex.Message; }
        finally { transitions.Release(); }
    }

    private void OnSessionChanged()
    {
        if (disposed || !activity.IsActive || session.IsPaused || currentOwner == null || currentOwner != requestedOwner) return;
        if (session.Receipt is not { Acknowledged: true, Consumed: false, Canceled: false } receipt) return;
        if (announcedScan == receipt.Scan.ScanId) return;
        announcedScan = receipt.Scan.ScanId;
        ScanReady?.Invoke(receipt.Scan.ScanId);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        activity.Changed -= OnActivityChanged;
        session.Changed -= OnSessionChanged;
        CancelPending();
        await transitions.WaitAsync();
        try { await session.PauseAsync(); lock (cancellationSync) runCancellation?.Dispose(); }
        finally { transitions.Release(); }
    }
}
