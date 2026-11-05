using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

/// <summary>
/// Protocol requirements this gateway has seen in effect (issue #145 review). Activations are permanent
/// on chain, so remembering them across restarts keeps the gateway failing closed when it restarts while
/// the Blockchain Gateway is unreachable. Tiny: one row per activated feature plus the minimum version.
/// </summary>
public sealed partial class GatewayMetadataStore
{
    private const string ProtocolTable = """
        CREATE TABLE IF NOT EXISTS protocol_requirements (
            key        TEXT PRIMARY KEY,   -- 'feature:<name>' | 'min_version'
            value      TEXT NOT NULL,
            seen_at    TEXT NOT NULL
        );
        """;

    public (IReadOnlyList<string> Features, int MinVersion) LoadProtocolRequirements()
    {
        using var conn = Open();
        Execute(conn, ProtocolTable);
        using var cmd = Query(conn, "SELECT key, value FROM protocol_requirements");
        using var reader = cmd.ExecuteReader();
        var features = new List<string>();
        var minVersion = 0;
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (key.StartsWith("feature:", StringComparison.Ordinal))
            {
                features.Add(key["feature:".Length..]);
            }
            else if (key == "min_version" && int.TryParse(reader.GetString(1), out var v))
            {
                minVersion = v;
            }
        }

        return (features, minVersion);
    }

    /// <summary>Records requirements seen in effect; never lowers the stored minimum version.</summary>
    public void SaveProtocolRequirements(IEnumerable<string> features, int minVersion)
    {
        using var conn = Open();
        Execute(conn, ProtocolTable);
        using var tx = conn.BeginTransaction();
        foreach (var feature in features)
        {
            Execute(Query(conn, "INSERT OR IGNORE INTO protocol_requirements (key, value, seen_at) VALUES ($k, '1', $t)",
                ("$k", "feature:" + feature), ("$t", Now())));
        }

        Execute(Query(conn, """
            INSERT INTO protocol_requirements (key, value, seen_at) VALUES ('min_version', $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, seen_at = excluded.seen_at
            WHERE CAST(excluded.value AS INTEGER) > CAST(protocol_requirements.value AS INTEGER)
            """, ("$v", minVersion.ToString()), ("$t", Now())));
        tx.Commit();
    }
}
