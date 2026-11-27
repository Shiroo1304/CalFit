using System.Text.Json.Serialization;

namespace CalFit.Models;

public sealed record CalFitBotStatus(string Device, int Protocol, bool Pending);
public sealed record CalFitBotWeight(double? WeightGrams);
public sealed record CalFitBotScan(string ScanId, double? WeightGrams, int PhotoBytes);
public enum CalFitBotConnectionState { Disconnected, Discovering, Connecting, Waiting, Receiving, Received, Acknowledging, RetryRequired, Error, Paused }
public sealed record CalFitBotReceipt(CalFitBotScan Scan, string DeviceAddress, string PhotoPath,
    DateTimeOffset ReceivedAt, bool Acknowledged = false, bool Consumed = false, bool Canceled = false);

public sealed class CalFitBotScanCanceledException : Exception
{
    public CalFitBotScanCanceledException() : base("The robot scan is no longer available. It may have been canceled.") { }
}

// Source-generated metadata keeps robot persistence/network JSON usable in trimmed Android builds.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CalFitBotStatus))]
[JsonSerializable(typeof(CalFitBotWeight))]
[JsonSerializable(typeof(CalFitBotScan))]
[JsonSerializable(typeof(CalFitBotReceipt))]
public partial class CalFitBotJsonContext : JsonSerializerContext { }
