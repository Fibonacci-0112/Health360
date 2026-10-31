using System.Text.RegularExpressions;

namespace Health360.LocalDb;

/// <summary>
/// Photo files waiting to upload, on the device. Only already-stripped JPEGs are written here, named as in the
/// bucket without the profile folder: <c>&lt;id&gt;.jpg</c> and <c>&lt;id&gt;_thumb.jpg</c>.
/// </summary>
public interface IPhotoFileStore
{
    Task WriteAsync(string name, byte[] jpeg, CancellationToken cancellationToken);

    /// <summary>The file's bytes, or null if it isn't there.</summary>
    Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Deletes the file; one that is already gone is fine.</summary>
    void Delete(string name);

    IReadOnlyList<string> List();

    /// <summary>Deletes every file, for a sign-out or another user's sign-in.</summary>
    void DeleteAll();
}

/// <summary>Pending photo files in one directory of the app's private storage.</summary>
public sealed partial class DirectoryPhotoFileStore : IPhotoFileStore
{
    private readonly string root;

    public DirectoryPhotoFileStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        this.root = root;
    }

    public async Task WriteAsync(string name, byte[] jpeg, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(root);
        // Written aside and moved into place, so a crash never leaves a partial file under the real name.
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, jpeg, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Delete(string name) => File.Delete(PathOf(name));

    /// <summary>The photo files here, and any left half-written (<c>.tmp</c>) by a crash, so the sweep removes them.</summary>
    public IReadOnlyList<string> List() =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root).Select(Path.GetFileName).OfType<string>().Where(n => FileName().IsMatch(n))]
            : [];

    /// <summary>
    /// Deletes every file it can. One that is in use is skipped rather than failing a sign-out halfway; the next
    /// start's sweep removes it, since no queue entry refers to it any more.
    /// </summary>
    public void DeleteAll()
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// True for a name this store accepts: a lower-case UUID, optionally <c>_thumb</c>, then <c>.jpg</c> (and <c>.tmp</c>
    /// for a write a crash interrupted).
    /// </summary>
    public static bool IsValidName(string name) => FileName().IsMatch(name);

    private string PathOf(string name) =>
        IsValidName(name) ? Path.Combine(root, name) : throw new ArgumentException("Not a photo file name.", nameof(name));

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(_thumb)?\\.jpg(\\.tmp)?$")]
    private static partial Regex FileName();
}
