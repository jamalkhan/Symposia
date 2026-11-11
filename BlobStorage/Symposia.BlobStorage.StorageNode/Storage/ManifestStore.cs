using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.StorageNode.Storage;

/// <summary>
/// Per-node local blob manifest (Layer 1 of Requirements/BlobStorage/metadata-architecture.md).
/// Embedded SQLite, not a central database — every node owns and serves only its own manifest.
/// </summary>
public sealed class ManifestStore
{
    private readonly string _connectionString;

    // Writes are serialised so the used-bytes counter (#197) applies each record's exact delta.
    private readonly object _writeGate = new();
    private long _usedBytes;
    private long _writeSeq;

    public ManifestStore(IOptions<StorageNodeOptions> options)
    {
        var dbPath = Path.GetFullPath(options.Value.ManifestDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS blob_manifest (
                cid                   TEXT PRIMARY KEY,
                size_bytes            INTEGER NOT NULL,
                tenant_id             TEXT NOT NULL,
                bucket                TEXT NOT NULL,
                key                   TEXT NOT NULL,
                region_tags           TEXT NOT NULL,
                stored_at             TEXT NOT NULL,
                checksum_verified_at  TEXT NULL,
                status                TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        ReconcileUsedBytes();
    }

    /// <summary>
    /// Bytes recorded in the manifest, kept in memory (issue #197) so Probe, heartbeats and the
    /// capacity monitor don't each run a SUM over the whole table. Seeded at <see cref="Initialize"/>
    /// and corrected by <see cref="ReconcileUsedBytes"/>.
    /// </summary>
    public long UsedBytes => Interlocked.Read(ref _usedBytes);

    /// <summary>
    /// Recomputes <see cref="UsedBytes"/> from the table; returns the drift it corrected. The SUM runs
    /// outside the write lock so writes aren't stalled by a full scan; if a write lands meanwhile, the
    /// counter (already exact for that write) is kept and the next reconcile tries again.
    /// </summary>
    public long ReconcileUsedBytes()
    {
        var seq = Interlocked.Read(ref _writeSeq);
        var actual = SumSizeBytes();
        lock (_writeGate)
        {
            return _writeSeq != seq ? 0 : actual - Interlocked.Exchange(ref _usedBytes, actual);
        }
    }

    public bool IsAccessible()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Upsert(BlobRecord record)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            var previous = SizeOf(connection, record.Cid);
            UpsertRow(connection, record);
            Interlocked.Add(ref _usedBytes, record.SizeBytes - (previous ?? 0));
            Interlocked.Increment(ref _writeSeq);
        }
    }

    private static long? SizeOf(SqliteConnection connection, Cid cid)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT size_bytes FROM blob_manifest WHERE cid = $cid";
        command.Parameters.AddWithValue("$cid", cid.Value);
        return command.ExecuteScalar() is long size ? size : null;
    }

    private static void UpsertRow(SqliteConnection connection, BlobRecord record)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO blob_manifest
                (cid, size_bytes, tenant_id, bucket, key, region_tags, stored_at, checksum_verified_at, status)
            VALUES
                ($cid, $size_bytes, $tenant_id, $bucket, $key, $region_tags, $stored_at, $checksum_verified_at, $status)
            ON CONFLICT(cid) DO UPDATE SET
                size_bytes = excluded.size_bytes,
                tenant_id = excluded.tenant_id,
                bucket = excluded.bucket,
                key = excluded.key,
                region_tags = excluded.region_tags,
                stored_at = excluded.stored_at,
                checksum_verified_at = excluded.checksum_verified_at,
                status = excluded.status;
            """;

        command.Parameters.AddWithValue("$cid", record.Cid.Value);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$tenant_id", record.TenantId);
        command.Parameters.AddWithValue("$bucket", record.Bucket);
        command.Parameters.AddWithValue("$key", record.Key);
        command.Parameters.AddWithValue("$region_tags", string.Join(',', record.RegionTags));
        command.Parameters.AddWithValue("$stored_at", record.StoredAt.ToString("O"));
        command.Parameters.AddWithValue("$checksum_verified_at", (object?)record.ChecksumVerifiedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());

        command.ExecuteNonQuery();
    }

    public BlobRecord? Get(Cid cid)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT size_bytes, tenant_id, bucket, key, region_tags, stored_at, checksum_verified_at, status " +
            "FROM blob_manifest WHERE cid = $cid";
        command.Parameters.AddWithValue("$cid", cid.Value);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return MapRecord(cid, reader);
    }

    public bool Delete(Cid cid)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            var previous = SizeOf(connection, cid);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM blob_manifest WHERE cid = $cid";
            command.Parameters.AddWithValue("$cid", cid.Value);
            var deleted = command.ExecuteNonQuery() > 0;
            if (deleted)
            {
                Interlocked.Add(ref _usedBytes, -(previous ?? 0));
                Interlocked.Increment(ref _writeSeq);
            }

            return deleted;
        }
    }

    public long CountBlobs()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM blob_manifest";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public long SumSizeBytes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM blob_manifest";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Returns a page of CIDs ordered lexicographically, starting after <paramref name="afterCid"/>.</summary>
    public IReadOnlyList<string> ListCidsPaged(string afterCid, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = afterCid.Length == 0
            ? "SELECT cid FROM blob_manifest ORDER BY cid LIMIT $limit"
            : "SELECT cid FROM blob_manifest WHERE cid > $after ORDER BY cid LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (afterCid.Length > 0)
            command.Parameters.AddWithValue("$after", afterCid);

        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>Returns all blobs with the given status.</summary>
    public IReadOnlyList<BlobRecord> ListByStatus(BlobStatus status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cid, size_bytes, tenant_id, bucket, key, region_tags, stored_at, checksum_verified_at, status " +
            "FROM blob_manifest WHERE status = $status";
        command.Parameters.AddWithValue("$status", status.ToString());

        using var reader = command.ExecuteReader();
        var results = new List<BlobRecord>();
        while (reader.Read())
        {
            if (Cid.TryParse(reader.GetString(0), out var cid))
                results.Add(MapRecord(cid, reader, offset: 1));
        }
        return results;
    }

    public void SetStatus(Cid cid, BlobStatus status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE blob_manifest SET status = $status WHERE cid = $cid";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$cid", cid.Value);
        command.ExecuteNonQuery();
    }

    public void UpdateChecksumVerifiedAt(Cid cid, DateTimeOffset verifiedAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE blob_manifest SET checksum_verified_at = $t WHERE cid = $cid";
        command.Parameters.AddWithValue("$t", verifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$cid", cid.Value);
        command.ExecuteNonQuery();
    }

    private static BlobRecord MapRecord(Cid cid, SqliteDataReader reader, int offset = 0)
    {
        // offset: columns before size_bytes (ListByStatus selects cid first).
        int Col(int i) => i + offset;

        var regionTagsRaw = reader.GetString(Col(4));
        var regionTags = regionTagsRaw.Length == 0
            ? Array.Empty<string>()
            : regionTagsRaw.Split(',');

        return new BlobRecord(
            cid,
            reader.GetInt64(Col(0)),
            reader.GetString(Col(1)),
            reader.GetString(Col(2)),
            reader.GetString(Col(3)),
            regionTags,
            DateTimeOffset.Parse(reader.GetString(Col(5))),
            reader.IsDBNull(Col(6)) ? null : DateTimeOffset.Parse(reader.GetString(Col(6))),
            Enum.Parse<BlobStatus>(reader.GetString(Col(7))));
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
