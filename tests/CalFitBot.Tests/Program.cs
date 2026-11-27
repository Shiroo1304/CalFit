using System.Net;
using System.Text;
using System.Text.Json;
using CalFit.Models;
using CalFit.Services;

var tests = new List<(string, Func<Task>)>();
void Test(string name, Func<Task> action) => tests.Add((name, action));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
var jpeg = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "sample.jpg"));
var roots = new List<string>();
(CalFitBotSession session, MockCalFitBotService mock, CalFitBotInbox inbox, string root) Setup()
{
    string root = Path.Combine(Path.GetTempPath(), "calfitbot-tests-" + Guid.NewGuid()); roots.Add(root);
    var mock = new MockCalFitBotService(jpeg);
    var inbox = new CalFitBotInbox(root);
    return (new CalFitBotSession(mock, inbox, new AddressStore()), mock, inbox, root);
}
Uri address = new("http://192.168.43.2/");
CancellationToken ct = CancellationToken.None;

Test("successful scan persists exact photo and captured weight before ACK", async () =>
{
    var (session, mock, inbox, root) = Setup();
    string id = mock.Submit();
    await session.TickAsync(ct);
    Assert(session.CanAnalyze && mock.PendingScanId == null);
    var stored = await inbox.FindAsync("", id, ct);
    Assert(stored is { Acknowledged: true } && stored.Scan.WeightGrams == 385);
    Assert((await File.ReadAllBytesAsync(stored!.PhotoPath)).SequenceEqual(jpeg));
    Assert(mock.PhotoDownloads == 1 && mock.AckCalls == 1);
    await session.DisposeAsync();
});
Test("failed photo transfer is retried without ACK; succeeds on third attempt", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(CalFitBotMockScenario.PhotoFailures);
    await s.TickAsync(ct); Assert(!s.CanAnalyze && m.AckCalls == 0);
    await s.TickAsync(ct); Assert(m.AckCalls == 0);
    await s.TickAsync(ct); Assert(s.CanAnalyze && m.PhotoDownloads == 3 && m.AckCalls == 1);
    await s.DisposeAsync();
});
Test("photo retries are bounded and explicit retry resumes", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(); m.RemainingPhotoFailures = 4;
    for (int i = 0; i < 6; i++) await s.TickAsync(ct);
    Assert(m.PhotoDownloads == 3 && s.State == CalFitBotConnectionState.RetryRequired);
    m.RemainingPhotoFailures = 0;
    await s.RetryAsync(ct); Assert(s.CanAnalyze);
    await s.DisposeAsync();
});
Test("size mismatch never commits or acknowledges a photo", async () =>
{
    var (s, m, inbox, _) = Setup(); string id = m.Submit(CalFitBotMockScenario.WrongPhotoSize);
    await s.TickAsync(ct); Assert(m.AckCalls == 0 && await inbox.FindAsync("", id, ct) == null && !s.CanAnalyze);
    await s.DisposeAsync();
});
Test("canceled photo does not acknowledge or appear as successful", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(CalFitBotMockScenario.CanceledScan);
    await s.TickAsync(ct); Assert(m.AckCalls == 0 && s.Receipt == null && !s.CanAnalyze);
    await s.DisposeAsync();
});
Test("ACK failures retry only ACK using durable receipt", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(CalFitBotMockScenario.AcknowledgementFailures);
    await s.TickAsync(ct); Assert(s.Receipt != null && !s.CanAnalyze && File.Exists(s.Receipt.PhotoPath));
    await s.TickAsync(ct); await s.TickAsync(ct);
    Assert(s.CanAnalyze && m.PhotoDownloads == 1 && m.AckCalls == 3);
    await s.DisposeAsync();
});
Test("ACK retries are bounded", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(); m.RemainingAckFailures = 8;
    for (int i = 0; i < 6; i++) await s.TickAsync(ct);
    Assert(m.AckCalls == 3 && m.PhotoDownloads == 1 && !s.CanAnalyze);
    m.RemainingAckFailures = 0; await s.RetryAsync(ct); Assert(s.CanAnalyze);
    await s.DisposeAsync();
});
Test("consumed duplicate cannot create a second scan or meal", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(); await s.TickAsync(ct);
    await s.CompleteAsync(ct); m.ReplayLastScan(); await s.TickAsync(ct);
    Assert(s.Receipt == null && !s.CanAnalyze && m.PhotoDownloads == 1 && m.AckCalls == 2);
    await s.DisposeAsync();
});
Test("new submission cannot overwrite an unsaved received meal", async () =>
{
    var (s, m, _, _) = Setup(); string first = m.Submit(); await s.TickAsync(ct);
    m.WeightGrams = 120; string second = m.Submit(); await s.TickAsync(ct);
    Assert(s.Receipt!.Scan.ScanId == first && s.Receipt.Scan.WeightGrams == 385 && m.PendingScanId == second);
    await s.CompleteAsync(ct); await s.TickAsync(ct);
    Assert(s.Receipt!.Scan.ScanId == second && s.Receipt.Scan.WeightGrams == 120);
    await s.DisposeAsync();
});
Test("standby does not disconnect or replace captured weight", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(); await s.TickAsync(ct);
    m.Standby = true; m.WeightGrams = 20; await s.TickAsync(ct);
    Assert(s.Address != null && s.LiveWeight == null && s.Receipt!.Scan.WeightGrams == 385 && s.CanAnalyze);
    await s.DisposeAsync();
});
Test("unavailable captured weight stays null", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(CalFitBotMockScenario.MissingWeight); await s.TickAsync(ct);
    Assert(s.CanAnalyze && s.Receipt!.Scan.WeightGrams == null);
    await s.DisposeAsync();
});
Test("disconnection retains photo; reconnect resumes", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit(); await s.TickAsync(ct); var receipt = s.Receipt;
    m.Connected = false; await s.TickAsync(ct);
    Assert(s.Address == null && s.Receipt == receipt);
    m.Connected = true; await s.ReconnectAsync(null, ct); Assert(s.Address != null && s.Receipt == receipt);
    await s.DisposeAsync();
});
Test("parallel polls do not overlap or duplicate download", async () =>
{
    var (s, m, _, _) = Setup(); m.Submit();
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => s.TickAsync(ct)));
    Assert(m.PhotoDownloads == 1 && m.AckCalls == 1);
    await s.DisposeAsync();
});
Test("invalid metadata and corrupt JPEG are rejected", async () =>
{
    foreach (double bad in new[] { -1d, double.NaN, double.PositiveInfinity })
        await Throws<InvalidDataException>(() => { CalFitBotService.ValidateWeight(bad); return Task.CompletedTask; });
    var scan = new CalFitBotScan("id", 0, jpeg.Length);
    var corrupt = jpeg.ToArray(); corrupt[0] = 0;
    await Throws<InvalidDataException>(() => { CalFitBotService.ValidatePhoto(scan, corrupt); return Task.CompletedTask; });
});
Test("receipt restart recovery retains ACK state and user isolation", async () =>
{
    var (_, _, inbox, _) = Setup(); var scan = new CalFitBotScan("../unsafe/path", null, jpeg.Length);
    var receipt = await inbox.StoreAsync("user1", address, scan, jpeg, ct);
    await inbox.UpdateAsync("user1", receipt with { Acknowledged = true }, ct);
    var restored = await inbox.LatestAsync("user1", ct);
    Assert(restored is { Acknowledged: true } && restored.Scan.WeightGrams == null);
    Assert(await inbox.LatestAsync("user2", ct) == null);
    Assert(Path.GetFullPath(restored!.PhotoPath).StartsWith(Path.GetFullPath(roots.Last())));
});
Test("cancellation cannot commit partial receipt", async () =>
{
    var (_, _, inbox, root) = Setup(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => inbox.StoreAsync("u", address, new("id", 1, jpeg.Length), jpeg, canceled.Token));
    Assert(!Directory.Exists(root) || !Directory.EnumerateFiles(root, "photo.jpg", SearchOption.AllDirectories).Any());
});
Test("HTTP contract uses only existing routes and escapes IDs", async () =>
{
    var seen = new List<string>();
    using var http = new HttpClient(new Handler(request =>
    {
        seen.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        return request.RequestUri.AbsolutePath switch
        {
            "/status" => JsonResponse(new { device = "CalFitBot", protocol = 1, pending = true }),
            "/weight" => JsonResponse(new { weightGrams = (double?)null }),
            "/scan" => JsonResponse(new { scanId = "a?b", weightGrams = 385d, photoBytes = jpeg.Length }),
            "/photo" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) },
            "/ack" => JsonResponse(new { ok = true }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }));
    var service = new CalFitBotService(http, new Discovery());
    Assert((await service.GetStatusAsync(address, ct)).Pending);
    Assert((await service.GetWeightAsync(address, ct)).WeightGrams == null);
    var scan = (await service.GetScanAsync(address, ct))!;
    await service.GetPhotoAsync(address, scan, ct); await service.AcknowledgeAsync(address, scan.ScanId, ct);
    Assert(seen.SequenceEqual(new[] { "GET /status", "GET /weight", "GET /scan", "GET /photo?id=a%3Fb", "POST /ack?id=a%3Fb" }));
});
Test("204 means no scan; 404 means canceled, never ACK success", async () =>
{
    using var http = new HttpClient(new Handler(request => new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/scan" ? HttpStatusCode.NoContent : HttpStatusCode.NotFound)));
    var service = new CalFitBotService(http, new Discovery());
    Assert(await service.GetScanAsync(address, ct) == null);
    await Throws<CalFitBotScanCanceledException>(() => service.GetPhotoAsync(address, new("id", 1, jpeg.Length), ct));
    await Throws<CalFitBotScanCanceledException>(() => service.AcknowledgeAsync(address, "id", ct));
});
Test("wrong device and negative ACK rejected", async () =>
{
    using var http = new HttpClient(new Handler(_ => JsonResponse(new { device = "Other", protocol = 1, pending = false, ok = false })));
    var service = new CalFitBotService(http, new Discovery());
    await Throws<InvalidDataException>(() => service.GetStatusAsync(address, ct));
    await Throws<InvalidDataException>(() => service.AcknowledgeAsync(address, "id", ct));
});
Test("address validation rejects cloud/backend and endpoint URLs", async () =>
{
    Assert(CalFitBotService.ParseAddress("10.165.223.123").AbsoluteUri == "http://10.165.223.123/");
    Assert(CalFitBotService.ParseAddress("calfitbot.local").Host == "calfitbot.local");
    foreach (string invalid in new[] { "https://cloud.example.com", "http://8.8.8.8/", "10.0.0.2/status", "http://user:pass@10.0.0.2/" })
        await Throws<ArgumentException>(() => { CalFitBotService.ParseAddress(invalid); return Task.CompletedTask; });
});

Test("decoder rejection prevents storage and acknowledgement", async () =>
{
    var (_, m, inbox, _) = Setup(); m.Submit();
    await using var session = new CalFitBotSession(m, inbox, new AddressStore(), new RejectingValidator());
    await session.TickAsync(ct);
    Assert(session.Receipt == null && m.AckCalls == 0);
});
Test("acknowledgement only occurs after an atomic photo/metadata commit", async () =>
{
    var (_, m, inbox, _) = Setup(); string id = m.Submit();
    var inspected = new InspectingService(m, async receivedId =>
    {
        var receipt = await inbox.FindAsync("", receivedId, ct);
        Assert(receipt != null && File.Exists(receipt.PhotoPath) && !receipt.Acknowledged);
    });
    await using var session = new CalFitBotSession(inspected, inbox, new AddressStore());
    await session.TickAsync(ct); Assert(session.CanAnalyze && inspected.Inspections == 1);
});
Test("restart restores an unacknowledged receipt without downloading again", async () =>
{
    var (_, m, inbox, _) = Setup(); string id = m.Submit();
    await inbox.StoreAsync("user", address, new(id, 385, jpeg.Length), jpeg, ct);
    await using var session = new CalFitBotSession(m, inbox, new AddressStore());
    await session.StartAsync("user", ct);
    for (int i = 0; i < 30 && !session.CanAnalyze; i++) await Task.Delay(10);
    Assert(session.CanAnalyze && m.PhotoDownloads == 0 && m.AckCalls == 1);
});
Test("acknowledgement 404 retains receipt but blocks successful submission", async () =>
{
    var (_, m, inbox, _) = Setup(); string id = m.Submit();
    var canceledAck = new InspectingService(m, _ => throw new CalFitBotScanCanceledException());
    await using var session = new CalFitBotSession(canceledAck, inbox, new AddressStore());
    await session.TickAsync(ct);
    Assert(!session.CanAnalyze && session.Receipt is { Canceled: true });
    Assert((await inbox.FindAsync("", id, ct)) is { Canceled: true, Acknowledged: false });
});
Test("disposing the shared receiver session cancels its poll loop", async () =>
{
    var (_, m, inbox, _) = Setup(); var counter = new InspectingService(m, _ => Task.CompletedTask);
    var session = new CalFitBotSession(counter, inbox, new AddressStore());
    await session.StartAsync("user", ct);
    for (int i = 0; i < 30 && counter.StatusCalls == 0; i++) await Task.Delay(10);
    await session.DisposeAsync();
    int calls = counter.StatusCalls;
    await session.TickAsync(ct);
    Assert(counter.StatusCalls == calls);
});
Test("standby preserves a pending submitted scan", async () =>
{
    var (session, m, _, _) = Setup(); m.Standby = true; m.Submit();
    await session.TickAsync(ct);
    Assert(session.CanAnalyze && session.Receipt!.Scan.WeightGrams == 385 && m.Standby);
    await session.DisposeAsync();
});


Test("V4.1 handlers: capture becomes pending, ACK is repeatable, standby weight may be null", async () =>
{
    bool pending = true, acknowledged = false;
    const string id = "a82f45-b1239c";
    using var client = new HttpClient(new Handler(request =>
    {
        string path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/status")
            return JsonResponse(new { device = "CalFitBot", protocol = 1, pending });
        if (request.Method == HttpMethod.Get && path == "/weight")
            return JsonResponse(new { weightGrams = (double?)null });
        if (request.Method == HttpMethod.Get && path == "/scan")
            return pending ? JsonResponse(new { scanId = id, weightGrams = 385.25, photoBytes = jpeg.Length })
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        if (request.Method == HttpMethod.Get && path == "/photo")
            return pending && request.RequestUri.Query == "?id=" + id
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Post && path == "/ack")
        {
            if (request.RequestUri.Query != "?id=" + id || (!pending && !acknowledged))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            pending = false; acknowledged = true;
            return JsonResponse(new { ok = true });
        }
        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }));
    var robot = new CalFitBotService(client, new Discovery());
    Assert((await robot.GetStatusAsync(address, ct)).Pending);
    Assert((await robot.GetWeightAsync(address, ct)).WeightGrams == null);
    var scan = await robot.GetScanAsync(address, ct);
    Assert(scan!.WeightGrams == 385.25);
    Assert((await robot.GetPhotoAsync(address, scan, ct)).SequenceEqual(jpeg));
    await robot.AcknowledgeAsync(address, id, ct);
    await robot.AcknowledgeAsync(address, id, ct);
    Assert(!(await robot.GetStatusAsync(address, ct)).Pending);
    Assert(await robot.GetScanAsync(address, ct) == null);
    await Throws<CalFitBotScanCanceledException>(() => robot.GetPhotoAsync(address, scan, ct));
});
Test("foreground receiver detects capture without a scanner view and notifies once", async () =>
{
    var (session, mock, _, _) = Setup();
    var activity = new CalFitAppActivity(); activity.SetActive(true);
    await using var receiver = new CalFitBotForegroundReceiver(session, activity);
    int notifications = 0;
    receiver.ScanReady += id => { notifications++; receiver.MarkPresented(id); };
    await receiver.SetOwnerAsync("user");
    mock.Submit();
    await session.TickAsync(ct);
    await Until(() => session.CanAnalyze);
    Assert(notifications == 1 && mock.AckCalls == 1);
    await session.TickAsync(ct);
    Assert(notifications == 1);
    activity.SetActive(false); await receiver.SetOwnerAsync("user");
    activity.SetActive(true); await receiver.SetOwnerAsync("user");
    Assert(notifications == 1 && session.CanAnalyze);
});
Test("background capture stays on robot; foreground resume receives and presents it", async () =>
{
    var (session, mock, _, _) = Setup();
    var activity = new CalFitAppActivity(); activity.SetActive(true);
    await using var receiver = new CalFitBotForegroundReceiver(session, activity);
    int notifications = 0; receiver.ScanReady += _ => notifications++;
    await receiver.SetOwnerAsync("user");
    activity.SetActive(false); await receiver.SetOwnerAsync("user");
    mock.Submit(); await session.TickAsync(ct);
    Assert(session.IsPaused && mock.PhotoDownloads == 0 && mock.PendingScanId != null && notifications == 0);
    activity.SetActive(true); await receiver.SetOwnerAsync("user");
    await Until(() => session.CanAnalyze);
    Assert(mock.PhotoDownloads == 1 && notifications == 1 && mock.PendingScanId == null);
});
Test("background interrupts transfer without ACK and resume retries safely", async () =>
{
    var (_, mock, inbox, _) = Setup(); mock.Submit();
    var blocking = new BlockingPhotoService(mock);
    await using var session = new CalFitBotSession(blocking, inbox, new AddressStore());
    var activity = new CalFitAppActivity(); activity.SetActive(true);
    await using var receiver = new CalFitBotForegroundReceiver(session, activity);
    await receiver.SetOwnerAsync("user");
    await Until(() => blocking.Started);
    activity.SetActive(false); await receiver.SetOwnerAsync("user");
    Assert(session.IsPaused && session.Receipt == null && mock.AckCalls == 0 && blocking.Canceled);
    blocking.Block = false;
    activity.SetActive(true); await receiver.SetOwnerAsync("user");
    await Until(() => session.CanAnalyze);
    Assert(mock.AckCalls == 1 && mock.PhotoDownloads == 1);
});
Test("sign-out stops reception and another account cannot see the previous receipt", async () =>
{
    var (session, mock, _, _) = Setup();
    var activity = new CalFitAppActivity(); activity.SetActive(true);
    await using var receiver = new CalFitBotForegroundReceiver(session, activity);
    await receiver.SetOwnerAsync("first"); mock.Submit(); await session.TickAsync(ct);
    await Until(() => session.CanAnalyze);
    string first = session.Receipt!.Scan.ScanId;
    await receiver.SetOwnerAsync(null);
    Assert(session.Receipt == null && session.IsPaused);
    await receiver.SetOwnerAsync("second");
    Assert(session.Receipt == null);
    await receiver.SetOwnerAsync("first");
    Assert(session.Receipt?.Scan.ScanId == first && session.CanAnalyze);
});

async Task Until(Func<bool> predicate)
{
    for (int i = 0; i < 300 && !predicate(); i++) await Task.Delay(10);
    Assert(predicate(), "Timed out waiting for receiver state.");
}

Test("photo-only reception does not poll the weight endpoint", async () =>
{
    var (_, mock, inbox, _) = Setup();
    var transport = new InspectingService(mock, _ => Task.CompletedTask);
    await using var receiver = new CalFitBotSession(transport, inbox, new AddressStore());
    await receiver.TickAsync(ct);
    mock.Submit(CalFitBotMockScenario.MissingWeight);
    await receiver.TickAsync(ct);
    await receiver.TickAsync(ct);
    Assert(transport.WeightCalls == 0 && receiver.CanAnalyze && receiver.Receipt!.Scan.WeightGrams == null);
});

int failures = 0;
foreach (var (name, action) in tests)
{
    try { await action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + "\n" + ex); }
}
foreach (string root in roots) if (Directory.Exists(root)) Directory.Delete(root, true);
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
{ Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
sealed class AddressStore : ICalFitBotAddressStore { public string? LastAddress { get; set; } }
sealed class Discovery : ICalFitBotDiscovery { public Task<Uri?> DiscoverAsync(CancellationToken ct) => Task.FromResult<Uri?>(null); }
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handler(request));
}

sealed class RejectingValidator : ICalFitBotPhotoValidator
{
    public Task ValidateAsync(byte[] jpeg, CancellationToken ct) => throw new InvalidDataException("Cannot decode JPEG.");
}
sealed class InspectingService(ICalFitBotService inner, Func<string, Task> inspect) : ICalFitBotService
{
    public int Inspections, StatusCalls, WeightCalls;
    public Task<Uri?> DiscoverAsync(CancellationToken ct) => inner.DiscoverAsync(ct);
    public Task<CalFitBotStatus> GetStatusAsync(Uri a, CancellationToken ct) { StatusCalls++; return inner.GetStatusAsync(a, ct); }
    public Task<CalFitBotWeight> GetWeightAsync(Uri a, CancellationToken ct) { WeightCalls++; return inner.GetWeightAsync(a, ct); }
    public Task<CalFitBotScan?> GetScanAsync(Uri a, CancellationToken ct) => inner.GetScanAsync(a, ct);
    public Task<byte[]> GetPhotoAsync(Uri a, CalFitBotScan s, CancellationToken ct) => inner.GetPhotoAsync(a, s, ct);
    public async Task AcknowledgeAsync(Uri a, string id, CancellationToken ct) { Inspections++; await inspect(id); await inner.AcknowledgeAsync(a, id, ct); }
}

sealed class BlockingPhotoService(ICalFitBotService inner) : ICalFitBotService
{
    public bool Block = true, Started, Canceled;
    public Task<Uri?> DiscoverAsync(CancellationToken ct) => inner.DiscoverAsync(ct);
    public Task<CalFitBotStatus> GetStatusAsync(Uri a, CancellationToken ct) => inner.GetStatusAsync(a, ct);
    public Task<CalFitBotWeight> GetWeightAsync(Uri a, CancellationToken ct) => inner.GetWeightAsync(a, ct);
    public Task<CalFitBotScan?> GetScanAsync(Uri a, CancellationToken ct) => inner.GetScanAsync(a, ct);
    public async Task<byte[]> GetPhotoAsync(Uri a, CalFitBotScan scan, CancellationToken ct)
    {
        Started = true;
        if (Block)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        return await inner.GetPhotoAsync(a, scan, ct);
    }
    public Task AcknowledgeAsync(Uri a, string id, CancellationToken ct) => inner.AcknowledgeAsync(a, id, ct);
}
