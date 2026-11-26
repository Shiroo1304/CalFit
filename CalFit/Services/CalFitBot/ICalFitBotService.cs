using CalFit.Models;

namespace CalFit.Services;

public interface ICalFitBotService
{
    Task<Uri?> DiscoverAsync(CancellationToken cancellationToken);
    Task<CalFitBotStatus> GetStatusAsync(Uri address, CancellationToken cancellationToken);
    Task<CalFitBotWeight> GetWeightAsync(Uri address, CancellationToken cancellationToken);
    Task<CalFitBotScan?> GetScanAsync(Uri address, CancellationToken cancellationToken);
    Task<byte[]> GetPhotoAsync(Uri address, CalFitBotScan scan, CancellationToken cancellationToken);
    Task AcknowledgeAsync(Uri address, string scanId, CancellationToken cancellationToken);
}

public interface ICalFitBotDiscovery
{
    Task<Uri?> DiscoverAsync(CancellationToken cancellationToken);
}

public interface ICalFitBotAddressStore
{
    string? LastAddress { get; set; }
}

public interface ICalFitBotPhotoValidator
{
    Task ValidateAsync(byte[] jpeg, CancellationToken cancellationToken);
}
