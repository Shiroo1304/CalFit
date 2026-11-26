namespace CalFit.Models;

public sealed record CalFitBotStatus(string Device, int Protocol, bool Pending);
public sealed record CalFitBotWeight(double? WeightGrams);
public sealed record CalFitBotScan(string ScanId, double? WeightGrams, int PhotoBytes);
public enum CalFitBotConnectionState { Disconnected, Discovering, Connecting, Waiting, Receiving, Received, Acknowledging, RetryRequired, Error }
public sealed record CalFitBotReceipt(CalFitBotScan Scan, string DeviceAddress, string PhotoPath,
    DateTimeOffset ReceivedAt, bool Acknowledged = false, bool Consumed = false, bool Canceled = false);

public sealed class CalFitBotScanCanceledException : Exception
{
    public CalFitBotScanCanceledException() : base("The robot scan is no longer available. It may have been canceled.") { }
}
