using Symposia.BlobStorage.Gateway.Metadata;

namespace Symposia.BlobStorage.Gateway.Placement;

/// <summary>
/// A blob's effective region assignment (#8 FR1.1, Requirements/BlobStorage/region-assignment-for-data.md):
/// the object's own assignment, else the longest matching folder rule, else the bucket's, else none (global).
/// </summary>
public sealed class RegionAssignmentResolver(GatewayMetadataStore store)
{
    /// <summary>The request header carrying an object-level assignment on PUT, e.g. <c>us-east,eu-west</c>.</summary>
    public const string Header = "x-symposia-regions";

    public IReadOnlyList<string> Resolve(string bucket, string key, IReadOnlyList<string>? objectRegions = null)
    {
        if (objectRegions is { Count: > 0 })
        {
            return objectRegions;
        }

        var folder = store.PrefixRegions(bucket)
            .Where(r => key.StartsWith(r.Prefix, StringComparison.Ordinal))
            .OrderByDescending(r => r.Prefix.Length)
            .FirstOrDefault();
        return folder.Regions ?? store.BucketRegions(bucket);
    }

    /// <summary>Parses the object-level header; null when absent.</summary>
    public static IReadOnlyList<string>? FromHeader(HttpRequest request) =>
        request.Headers[Header].FirstOrDefault() is { Length: > 0 } raw
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToLowerInvariant()).Distinct().ToList()
            : null;
}
