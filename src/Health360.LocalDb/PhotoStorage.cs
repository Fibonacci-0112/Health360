using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Health360.LocalDb;

/// <summary>Uploads one file to the <c>progress-photos</c> bucket as the signed-in owner.</summary>
public interface IPhotoStorage
{
    /// <summary>
    /// Stores <paramref name="jpeg"/> at <paramref name="path"/>, replacing a file already there (a retry after an
    /// upload whose answer was lost). Throws <see cref="PhotoUploadException"/> when it fails.
    /// </summary>
    Task UploadAsync(string path, byte[] jpeg, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the file at <paramref name="path"/>; one that isn't there counts as deleted. Throws
    /// <see cref="PhotoUploadException"/> when it can't.
    /// </summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// The file at <paramref name="path"/>, read with the owner's session, or null when Storage has no such file (its
    /// row synced but the device that took it hasn't uploaded it yet). Throws <see cref="PhotoUploadException"/> when
    /// it can't tell.
    /// </summary>
    Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>A failed file upload. <see cref="Permanent"/> ones fail the same way on every retry.</summary>
public sealed class PhotoUploadException : Exception
{
    public PhotoUploadException()
    {
    }

    public PhotoUploadException(string message)
        : base(message)
    {
    }

    public PhotoUploadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PhotoUploadException(string message, bool permanent, string? code)
        : base(message)
    {
        Permanent = permanent;
        Code = code;
    }

    public bool Permanent { get; }

    /// <summary>The HTTP status, or a short reason such as <c>not_stripped</c>. Never row data.</summary>
    public string? Code { get; }
}

/// <summary>
/// Supabase Storage with the user's own session, so the bucket's policies decide: only the owner's profile folder
/// accepts files. Sent with <c>x-upsert</c> so a retried upload replaces rather than fails as a duplicate.
/// </summary>
public sealed class SupabasePhotoStorage(HttpClient http, ISupabaseSession session, IDatabaseOwnerStore owner, string apiKey) : IPhotoStorage
{
    public const string Bucket = "progress-photos";

    public async Task UploadAsync(string path, byte[] jpeg, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(jpeg);
        using var request = await RequestAsync(HttpMethod.Post, path, cancellationToken).ConfigureAwait(false);
        request.Headers.Add("x-upsert", "true");
        request.Content = new ByteArrayContent(jpeg);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var request = await RequestAsync(HttpMethod.Delete, path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // Storage answers a missing object with 400 and an inner statusCode of 404.
        if (response.StatusCode == HttpStatusCode.BadRequest && InnerStatus(body) == "404")
        {
            return;
        }

        var status = (int)response.StatusCode;
        throw new PhotoUploadException(
            $"Storage answered {status}.", IsPermanent(response.StatusCode, body), status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // The authenticated route, not a signed URL: no bearer URL for the photo ever exists to leak into logs or the
        // web view, and the bucket's select policy decides who may read it.
        using var request = await RequestAsync(HttpMethod.Get, path, cancellationToken, "authenticated/").ConfigureAwait(false);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new PhotoUploadException("Storage could not be reached.", error);
            }
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest && InnerStatus(body) == "404")
        {
            return null;
        }

        var status = (int)response.StatusCode;
        throw new PhotoUploadException(
            $"Storage answered {status}.", IsPermanent(response.StatusCode, body), status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken cancellationToken, string route = "")
    {
        // Not the owner's session: wait for them rather than touch their photos as someone else.
        var token = await OwnerSession.TokenAsync(session, owner, cancellationToken).ConfigureAwait(false)
            ?? throw new PhotoUploadException("Not signed in as the local database's owner.", permanent: false, code: null);
        var segments = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(method, $"storage/v1/object/{route}{Bucket}/{segments}");
        request.Headers.Add("apikey", apiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new PhotoUploadException("Storage could not be reached.", error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoUploadException("Storage did not answer in time.", error);
        }
    }

    private static async Task<PhotoUploadException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new PhotoUploadException(
            $"Storage answered {status}.", IsPermanent(response.StatusCode, body), status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? InnerStatus(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) is JsonObject error ? (error["statusCode"] as JsonValue)?.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refusals that a retry can't fix: a malformed request or path, a policy that denies the folder, a file too
    /// large or not a JPEG. 401 (an expired token), 408, 429 and 5xx are retried.
    /// </summary>
    /// <remarks>
    /// Storage also answers 400 for an expired or invalid token, with a body whose <c>statusCode</c> is 401 or whose
    /// error or message names the JWT; that 400 is retried after the session refreshes. The body is only inspected,
    /// never logged.
    /// </remarks>
    public static bool IsPermanent(HttpStatusCode status, string? body = null)
    {
        if (status == HttpStatusCode.BadRequest && IsTokenError(body))
        {
            return false;
        }

        return status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity;
    }

    private static bool IsTokenError(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject error)
            {
                return false;
            }

            var inner = (error["statusCode"] as JsonValue)?.ToString();
            var text = $"{(error["error"] as JsonValue)?.ToString()} {(error["message"] as JsonValue)?.ToString()}";
            return inner == "401" || text.Contains("jwt", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
