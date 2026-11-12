using System.Net.Http;

namespace CalFit.Services;

public class Esp32CameraService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<byte[]> CaptureImageAsync(
        string cameraIp)
    {
        string url = $"http://{cameraIp}/capture";

        using var response = await _httpClient.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync();
    }
}