namespace Health360.UI.Data;

/// <summary>
/// A photo taken or picked on this device, ready for the upload queue: JPEG, rotated upright and resized, plus a
/// smaller thumbnail. The queue strips any metadata either still carries.
/// </summary>
public sealed record CapturedPhoto(byte[] Photo, byte[] Thumbnail);

/// <summary>
/// The camera and photo library. The MAUI app supplies the platform implementation (MediaPicker); the shared screens
/// only see this. Both calls return null when the user cancels.
/// </summary>
public interface IPhotoCapture
{
    /// <summary>The longest side, in pixels, of a stored photo.</summary>
    public const int PhotoMaxSide = 2048;

    /// <summary>The longest side, in pixels, of its thumbnail.</summary>
    public const int ThumbnailMaxSide = 320;

    /// <summary>JPEG quality for both.</summary>
    public const int JpegQuality = 85;

    /// <summary>False on a device without a camera (most desktops); the screen then offers only picking.</summary>
    bool CanTakePhoto { get; }

    Task<CapturedPhoto?> TakeAsync();

    Task<CapturedPhoto?> PickAsync();
}
