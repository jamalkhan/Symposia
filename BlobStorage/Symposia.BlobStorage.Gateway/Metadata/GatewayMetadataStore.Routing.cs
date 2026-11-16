namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>Replica lookups by CID for peer gateways (#12 FR2.6).</summary>
public sealed partial class GatewayMetadataStore
{
    /// <summary>Every node this index lists as holding <paramref name="cid"/>, across objects that share it (idx_objects_cid).</summary>
    public IReadOnlyList<string> ReplicasOfCid(string cid)
    {
        using var conn = Open();
        using var r = Query(conn, "SELECT node_ids FROM objects WHERE cid = $c", ("$c", cid)).ExecuteReader();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            ids.UnionWith(r.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries));
        }

        return [.. ids];
    }
}
