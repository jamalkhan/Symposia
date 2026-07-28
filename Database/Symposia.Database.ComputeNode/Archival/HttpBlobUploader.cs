using System.Net;

namespace Symposia.Database.ComputeNode.Archival;

/// <summary>
/// Uploads WAL segment bytes to a tenant's Tier 1 bucket over HTTP using the narrow-scoped,
/// provisioning-time credential already resolved for that tenant database (same pattern the
/// pageserver uses -- see <c>PlaceDatabaseRequest.BlobBucketCredential</c>). This stays off the
/// standard blob-storage fan-out+quorum write path entirely; it is a direct, single-target PUT.
/// </summary>
public sealed class HttpBlobUploader : IBlobUploader
{
    private readonly HttpClient _httpClient;
    private readonly Func<string, WalArchivalDestination> _resolveDestination;

    public HttpBlobUploader(HttpClient httpClient, Func<string, WalArchivalDestination> resolveDestination)
    {
        _httpClient = httpClient;
        _resolveDestination = resolveDestination;
    }

    public async Task<UploadOutcome> UploadAsync(string tenantDatabaseId, WalSegment segment, CancellationToken cancellationToken)
    {
        var destination = _resolveDestination(tenantDatabaseId);
        var objectKey = $"{destination.BucketUrl.TrimEnd('/')}/wal/{segment.TimelineId}/{segment.SequenceNumber:D20}.wal";

        using var request = new HttpRequestMessage(HttpMethod.Put, objectKey)
        {
            Content = new ByteArrayContent(new byte[segment.SizeBytes]),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", destination.Credential);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
                return UploadOutcome.Success;

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => UploadOutcome.PermanentFailure,
                _ => UploadOutcome.TransientFailure,
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return UploadOutcome.TransientFailure;
        }
    }
}

/// <summary>Resolved upload target for a tenant database: its dedicated Tier 1 bucket and narrow-scoped credential.</summary>
public sealed record WalArchivalDestination(string BucketUrl, string Credential);
