using System.Net.Http.Json;

namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// Calls the #95 provisioning/lifecycle service's resume endpoint over HTTP to satisfy
/// wake-on-connect (FR8). This is the proxy's only synchronous, on-the-critical-path call into
/// #95 per the #93 architectural plan's integration points.
/// </summary>
public sealed class HttpLifecycleClient(HttpClient httpClient) : ILifecycleClient
{
    public async Task<ComputeEndpoint> ResumeAsync(string databaseId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/internal/databases/{databaseId}/resume", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var endpoint = await response.Content.ReadFromJsonAsync<ComputeEndpoint>(cancellationToken);
        return endpoint ?? throw new InvalidOperationException($"Lifecycle service returned no endpoint for database '{databaseId}'.");
    }
}
