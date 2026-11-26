using Android.Graphics;
using CalFit.Services;

namespace CalFit.Platforms.Android;

public sealed class AndroidCalFitBotPhotoValidator : ICalFitBotPhotoValidator
{
    public Task ValidateAsync(byte[] jpeg, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using var ignored = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0 || (long)bounds.OutWidth * bounds.OutHeight > 64_000_000)
            throw new InvalidDataException("The robot photo cannot be decoded or has excessive dimensions.");
        int sample = 1;
        while (bounds.OutWidth / sample > 2048 || bounds.OutHeight / sample > 2048) sample *= 2;
        using var options = new BitmapFactory.Options { InSampleSize = sample };
        using var bitmap = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length, options);
        if (bitmap == null) throw new InvalidDataException("The robot photo is not a decodable JPEG.");
        ct.ThrowIfCancellationRequested();
    }, ct);
}
