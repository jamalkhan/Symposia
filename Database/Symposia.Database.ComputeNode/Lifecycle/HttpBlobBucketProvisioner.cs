using System.Net.Http.Json;

namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// Calls the platform's existing tenant credential/bucket system to provision the Tier 1 page/WAL
/// bucket (FR2) and to trigger its standard soft-delete mechanism on database deletion (FR12/13).
/// This issue is a consumer of that system, not its owner, per the spec's out-of-scope note.
/// </summary>
public sealed class HttpBlobBucketProvisioner(HttpClient httpClient) : IBlobBucketProvisioner
{
    public async Task<string> ProvisionBucketAsync(string databaseId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/internal/buckets", new { tier = 1, scope = databaseId }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bucket = await response.Content.ReadFromJsonAsync<BucketProvisionedResponse>(cancellationToken);
        return bucket?.BucketId ?? throw new InvalidOperationException($"Bucket system returned no bucket id for database '{databaseId}'.");
    }

    public async Task SoftDeleteBucketAsync(string bucketId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"/internal/buckets/{bucketId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private sealed record BucketProvisionedResponse(string BucketId);
}
