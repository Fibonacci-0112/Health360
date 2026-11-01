using Health360.UI.Data;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace Health360.Maui.Data;

/// <summary>
/// The camera and photo library through MAUI's MediaPicker. The picker turns the image upright from its EXIF
/// orientation, resizes it and re-encodes it as JPEG, without copying metadata (Windows ignores that option; the
/// upload queue strips whatever is left either way). The thumbnail is scaled down from that result.
/// </summary>
public sealed class MauiPhotoCapture : IPhotoCapture
{
    private static readonly MediaPickerOptions Options = new()
    {
        Title = "Progress photo",
        RotateImage = true,
        PreserveMetaData = false,
        MaximumWidth = IPhotoCapture.PhotoMaxSide,
        MaximumHeight = IPhotoCapture.PhotoMaxSide,
        // Below 90 a PNG is converted to JPEG too, so the queue always gets a JPEG.
        CompressionQuality = IPhotoCapture.JpegQuality,
    };

    public bool CanTakePhoto => MediaPicker.Default.IsCaptureSupported;

    public Task<CapturedPhoto?> TakeAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () => await ReadAsync(await MediaPicker.Default.CapturePhotoAsync(Options).ConfigureAwait(true)).ConfigureAwait(false));

    public Task<CapturedPhoto?> PickAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picked = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
            {
                Title = Options.Title,
                RotateImage = Options.RotateImage,
                PreserveMetaData = Options.PreserveMetaData,
                MaximumWidth = Options.MaximumWidth,
                MaximumHeight = Options.MaximumHeight,
                CompressionQuality = Options.CompressionQuality,
                SelectionLimit = 1,
            }).ConfigureAwait(true);
            return await ReadAsync(picked?.FirstOrDefault()).ConfigureAwait(false);
        });

    private static async Task<CapturedPhoto?> ReadAsync(FileResult? file)
    {
        if (file is null)
        {
            return null;
        }

        byte[] photo;
        await using (var stream = await file.OpenReadAsync().ConfigureAwait(false))
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
            photo = buffer.ToArray();
        }

        using var source = new MemoryStream(photo);
        using var image = PlatformImage.FromStream(source, ImageFormat.Jpeg);
        using var thumbnail = image.Downsize(IPhotoCapture.ThumbnailMaxSide, IPhotoCapture.ThumbnailMaxSide, disposeOriginal: false);
        using var output = new MemoryStream();
        await thumbnail.SaveAsync(output, ImageFormat.Jpeg, IPhotoCapture.JpegQuality / 100f).ConfigureAwait(false);
        return new CapturedPhoto(photo, output.ToArray());
    }
}
