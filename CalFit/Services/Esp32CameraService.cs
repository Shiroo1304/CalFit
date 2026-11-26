using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CalFit.Services;

public sealed class Esp32CameraService : IDisposable
{
    private readonly HttpClient http = new()
    {
        // A live stream remains open until stopped or cancelled.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    private static readonly TimeSpan FrameTimeout =
        TimeSpan.FromSeconds(8);

    private const int MaximumFrameSize =
        10 * 1024 * 1024;

    public async Task<byte[]> CaptureImageAsync(
        string cameraIp,
        CancellationToken cancellationToken = default)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );

        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://{cameraIp}/capture?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        );

        request.Version = HttpVersion.Version11;
        request.VersionPolicy =
            HttpVersionPolicy.RequestVersionExact;

        request.Headers.ConnectionClose = true;

        using var response = await http.SendAsync(
            request,
            timeout.Token
        );

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(
            timeout.Token
        );
    }

    public async Task ReadPreviewAsync(
        string streamUrl,
        Func<byte[], Task> onFrame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );

        timeout.CancelAfter(FrameTimeout);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            streamUrl
        );

        // Avoid keeping the previous preview session's connection alive.
        request.Version = HttpVersion.Version11;

        request.VersionPolicy =
            HttpVersionPolicy.RequestVersionExact;

        request.Headers.ConnectionClose = true;

        request.Headers.TryAddWithoutValidation(
            "Cache-Control",
            "no-cache"
        );

        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token
        );

        response.EnsureSuccessStatusCode();

        var contentType =
            response.Content.Headers.ContentType;

        if (!string.Equals(
                contentType?.MediaType,
                "multipart/x-mixed-replace",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The camera did not return an MJPEG stream."
            );
        }

        string? boundary = contentType?.Parameters
            .FirstOrDefault(parameter =>
                string.Equals(
                    parameter.Name,
                    "boundary",
                    StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim('"');

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidDataException(
                "The stream is missing its frame boundary."
            );
        }

        string frameBoundary = "--" + boundary;

        await using var input =
            await response.Content.ReadAsStreamAsync(
                timeout.Token
            );

        await using var stream = new BufferedStream(
            input,
            32 * 1024
        );

        byte[] singleByte = new byte[1];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Each complete frame must arrive within this interval.
            timeout.CancelAfter(FrameTimeout);

            string line;

            do
            {
                line = await ReadLineAsync(
                    stream,
                    singleByte,
                    timeout.Token
                );
            }
            while (
                line != frameBoundary &&
                line != frameBoundary + "--"
            );

            if (line == frameBoundary + "--")
            {
                throw new EndOfStreamException(
                    "The camera ended the live stream."
                );
            }

            int frameLength = 0;
            int headerCount = 0;

            while (true)
            {
                line = await ReadLineAsync(
                    stream,
                    singleByte,
                    timeout.Token
                );

                if (line.Length == 0)
                    break;

                if (++headerCount > 30)
                {
                    throw new InvalidDataException(
                        "Invalid camera frame headers."
                    );
                }

                const string lengthHeader =
                    "Content-Length:";

                if (line.StartsWith(
                        lengthHeader,
                        StringComparison.OrdinalIgnoreCase))
                {
                    bool validLength = int.TryParse(
                        line[lengthHeader.Length..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out frameLength
                    );

                    if (!validLength)
                    {
                        throw new InvalidDataException(
                            "The camera returned an invalid Content-Length."
                        );
                    }
                }
            }

            if (frameLength < 4
                || frameLength > MaximumFrameSize)
            {
                throw new InvalidDataException(
                    "The camera returned an invalid frame size."
                );
            }

            byte[] frame = new byte[frameLength];

            await stream.ReadExactlyAsync(
                frame.AsMemory(),
                timeout.Token
            );

            if (frame[0] != 0xFF
                || frame[1] != 0xD8
                || frame[^2] != 0xFF
                || frame[^1] != 0xD9)
            {
                throw new InvalidDataException(
                    "The camera returned an incomplete JPEG frame."
                );
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Rendering time is separate from the camera read timeout.
            timeout.CancelAfter(
                System.Threading.Timeout.InfiniteTimeSpan
            );

            await onFrame(frame);
        }
    }

    private static async Task<string> ReadLineAsync(
        Stream stream,
        byte[] singleByte,
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder();

        while (true)
        {
            int count = await stream.ReadAsync(
                singleByte.AsMemory(0, 1),
                cancellationToken
            );

            if (count == 0)
            {
                throw new EndOfStreamException(
                    "The camera disconnected."
                );
            }

            byte value = singleByte[0];

            if (value == '\n')
                return line.ToString().TrimEnd('\r');

            if (line.Length >= 2048)
            {
                throw new InvalidDataException(
                    "The camera returned an oversized header."
                );
            }

            line.Append((char)value);
        }
    }

    public void Dispose()
    {
        http.Dispose();
    }
}