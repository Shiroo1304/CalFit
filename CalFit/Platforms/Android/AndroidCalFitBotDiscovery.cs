using Android.Content;
using Android.Net.Nsd;
using Android.Net.Wifi;
using CalFit.Services;

namespace CalFit.Platforms.Android;

public sealed class AndroidCalFitBotDiscovery : ICalFitBotDiscovery
{
    public async Task<Uri?> DiscoverAsync(CancellationToken ct)
    {
        var context = global::Android.App.Application.Context;
        var manager = context.GetSystemService(Context.NsdService) as NsdManager;
        if (manager == null) return null;
        var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var registration = timeout.Token.Register(() => completion.TrySetResult(null));
        var listener = new DiscoveryListener(manager, completion);
        WifiManager.MulticastLock? multicast = null;
        bool started = false;
        try
        {
            var wifi = context.GetSystemService(Context.WifiService) as WifiManager;
            multicast = wifi?.CreateMulticastLock("calfitbot-discovery");
            multicast?.SetReferenceCounted(false);
            multicast?.Acquire();
            manager.DiscoverServices("_calfitbot._tcp.", NsdProtocol.DnsSd, listener);
            started = true;
            Uri? result = await completion.Task;
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Java.Lang.RuntimeException) { ct.ThrowIfCancellationRequested(); return null; }
        finally
        {
            if (started)
            {
                try { manager.StopServiceDiscovery(listener); }
                catch (Java.Lang.RuntimeException) { }
            }
            if (multicast?.IsHeld == true) multicast.Release();
            multicast?.Dispose();
        }
    }

    private sealed class DiscoveryListener(NsdManager manager, TaskCompletionSource<Uri?> completion)
        : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        private bool resolving;
        private ResolveListener? resolver;
        public void OnDiscoveryStarted(string? serviceType) { }
        public void OnDiscoveryStopped(string? serviceType) { }
        public void OnServiceLost(NsdServiceInfo? serviceInfo) { }
        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) => completion.TrySetResult(null);
        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode) { }
        public void OnServiceFound(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo == null || resolving || completion.Task.IsCompleted) return;
            if (!(serviceInfo.ServiceType?.Contains("_calfitbot._tcp", StringComparison.OrdinalIgnoreCase) ?? false)) return;
            resolving = true;
            resolver = new ResolveListener(completion);
#pragma warning disable CS0618, CA1422
            manager.ResolveService(serviceInfo, resolver);
#pragma warning restore CS0618, CA1422
        }
    }
    private sealed class ResolveListener(TaskCompletionSource<Uri?> completion)
        : Java.Lang.Object, NsdManager.IResolveListener
    {
        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) => completion.TrySetResult(null);
        public void OnServiceResolved(NsdServiceInfo? serviceInfo)
        {
#pragma warning disable CS0618, CA1422
            string? host = serviceInfo?.Host?.HostAddress;
#pragma warning restore CS0618, CA1422
            if (host == null || serviceInfo?.Port != 80) { completion.TrySetResult(null); return; }
            try { completion.TrySetResult(CalFitBotService.ParseAddress(new UriBuilder("http", host, 80).Uri.AbsoluteUri)); }
            catch (ArgumentException) { completion.TrySetResult(null); }
        }
    }
}
