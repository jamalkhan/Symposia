using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Symposia.BlobStorage.Gateway.Metadata;

public enum MultipartStatus
{
    InProgress,
    Completing,
    Completed,
    Aborted,
}

public sealed record MultipartUpload(
    string UploadId,
    string TenantId,
    string Bucket,
    string Key,
    string ContentType,
    string? ChecksumAlgorithm,
    string? ChecksumType,
    DateTimeOffset InitiatedAt,
    DateTimeOffset ExpiresAt,
    MultipartStatus Status,
    string? FinalEtag,
    string? CommittedPartsJson);

public sealed record MultipartPart(
    string UploadId, int PartNumber, string Cid, long SizeBytes, IReadOnlyList<string> NodeIds, string? Checksum, DateTimeOffset UploadedAt);

/// <summary>
/// Multipart upload sessions and their parts (issue #5). Parts are ordinary blobs referenced only
/// here (never from <c>objects</c>), so they can't appear in GET/LIST and GC keeps them while referenced.
/// Lookups by primary key; listings page on (bucket, key, upload_id) and (upload_id, part_number).
/// </summary>
public sealed partial class GatewayMetadataStore
{
    public void CreateUpload(MultipartUpload upload)
    {
        using var conn = Open();
        Execute(Query(conn,
            """
            INSERT INTO multipart_uploads (upload_id, tenant_id, bucket, key, content_type, checksum_algorithm, checksum_type,
                                           initiated_at, expires_at, status)
            VALUES ($id, $t, $b, $k, $ct, $ca, $cty, $i, $e, 'InProgress')
            """,
            ("$id", upload.UploadId), ("$t", upload.TenantId), ("$b", upload.Bucket), ("$k", upload.Key), ("$ct", upload.ContentType),
            ("$ca", (object?)upload.ChecksumAlgorithm ?? DBNull.Value), ("$cty", (object?)upload.ChecksumType ?? DBNull.Value),
            ("$i", upload.InitiatedAt.ToString("O")), ("$e", upload.ExpiresAt.ToString("O"))));
    }

    public MultipartUpload? GetUpload(string uploadId)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            """
            SELECT upload_id, tenant_id, bucket, key, content_type, checksum_algorithm, checksum_type, initiated_at, expires_at,
                   status, final_etag, committed_parts_json
            FROM multipart_uploads WHERE upload_id = $id
            """,
            ("$id", uploadId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUpload(r) : null;
    }

    /// <summary>Slides the expiry forward on activity (a slow, large upload isn't killed mid-way).</summary>
    public void TouchUpload(string uploadId, DateTimeOffset expiresAt)
    {
        using var conn = Open();
        Execute(Query(conn, "UPDATE multipart_uploads SET expires_at = $e WHERE upload_id = $id AND status = 'InProgress'",
            ("$e", expiresAt.ToString("O")), ("$id", uploadId)));
    }

    /// <summary>
    /// Moves an upload between states only if it's still in <paramref name="from"/>; false if another
    /// request got there first (two concurrent Completes, Complete racing Abort).
    /// </summary>
    public bool TransitionUpload(string uploadId, MultipartStatus from, MultipartStatus to)
    {
        using var conn = Open();
        return Execute(Query(conn, "UPDATE multipart_uploads SET status = $to WHERE upload_id = $id AND status = $from",
            ("$to", to.ToString()), ("$id", uploadId), ("$from", from.ToString()))) > 0;
    }

    /// <summary>
    /// Records a part, replacing any earlier upload of the same part number. Returns the replaced
    /// part's CID and nodes so the caller can GC it, or null. Fails (false) if the upload isn't in progress.
    /// </summary>
    public (bool Accepted, MultipartPart? Replaced) PutPart(MultipartPart part)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using (var check = Query(conn, "SELECT 1 FROM multipart_uploads WHERE upload_id = $id AND status = 'InProgress'", ("$id", part.UploadId)))
        {
            check.Transaction = tx;
            if (check.ExecuteScalar() is null)
            {
                return (false, null);
            }
        }

        MultipartPart? replaced;
        using (var old = Query(conn,
                   "SELECT upload_id, part_number, cid, size_bytes, node_ids, checksum, uploaded_at FROM multipart_parts WHERE upload_id = $id AND part_number = $n",
                   ("$id", part.UploadId), ("$n", part.PartNumber)))
        {
            old.Transaction = tx;
            using var r = old.ExecuteReader();
            replaced = r.Read() ? ReadPart(r) : null;
        }

        using (var upsert = Query(conn,
                   """
                   INSERT INTO multipart_parts (upload_id, part_number, cid, size_bytes, node_ids, checksum, uploaded_at)
                   VALUES ($id, $n, $cid, $sz, $ni, $ck, $at)
                   ON CONFLICT(upload_id, part_number) DO UPDATE SET
                       cid = excluded.cid, size_bytes = excluded.size_bytes, node_ids = excluded.node_ids,
                       checksum = excluded.checksum, uploaded_at = excluded.uploaded_at
                   """,
                   ("$id", part.UploadId), ("$n", part.PartNumber), ("$cid", part.Cid), ("$sz", part.SizeBytes),
                   ("$ni", string.Join(',', part.NodeIds)), ("$ck", (object?)part.Checksum ?? DBNull.Value), ("$at", part.UploadedAt.ToString("O"))))
        {
            upsert.Transaction = tx;
            upsert.ExecuteNonQuery();
        }

        tx.Commit();
        return (true, replaced is not null && replaced.Cid != part.Cid ? replaced : null);
    }

    /// <summary>Parts in part-number order after <paramref name="afterPartNumber"/> (primary key order).</summary>
    public IReadOnlyList<MultipartPart> ListParts(string uploadId, int afterPartNumber = 0, int limit = 10_000)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            "SELECT upload_id, part_number, cid, size_bytes, node_ids, checksum, uploaded_at FROM multipart_parts " +
            "WHERE upload_id = $id AND part_number > $after ORDER BY part_number LIMIT $limit",
            ("$id", uploadId), ("$after", afterPartNumber), ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var parts = new List<MultipartPart>();
        while (r.Read())
        {
            parts.Add(ReadPart(r));
        }

        return parts;
    }

    /// <summary>
    /// Commits a completed upload in one transaction: the object row, the upload's Completed state with
    /// its committed-parts snapshot (for idempotent retries), and removal of its part rows. Returns the
    /// removed parts so the caller can GC blobs no longer referenced.
    /// </summary>
    public IReadOnlyList<MultipartPart> CompleteUpload(string uploadId, ObjectRecord record, string finalEtag, string committedPartsJson)
    {
        var parts = ListParts(uploadId);
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        void Run(string sql, params (string, object?)[] args)
        {
            using var cmd = Query(conn, sql, args);
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        Run("""
            INSERT INTO objects (bucket, key, cid, size_bytes, content_type, last_modified, node_ids, replica_count, etag_override)
            VALUES ($b, $k, $cid, $sz, $ct, $lm, $ni, $rc, $eo)
            ON CONFLICT(bucket, key) DO UPDATE SET
                cid = excluded.cid, size_bytes = excluded.size_bytes, content_type = excluded.content_type,
                last_modified = excluded.last_modified, node_ids = excluded.node_ids,
                replica_count = excluded.replica_count, etag_override = excluded.etag_override
            """,
            ("$b", record.Bucket), ("$k", record.Key), ("$cid", record.Cid), ("$sz", record.SizeBytes), ("$ct", record.ContentType),
            ("$lm", record.LastModified.ToString("O")), ("$ni", string.Join(',', record.NodeIds)), ("$rc", record.NodeIds.Count),
            ("$eo", finalEtag));
        Run("""
            UPDATE multipart_uploads SET status = 'Completed', completed_at = $at, final_cid = $cid, final_etag = $etag,
                committed_parts_json = $parts WHERE upload_id = $id
            """,
            ("$at", Now()), ("$cid", record.Cid), ("$etag", finalEtag), ("$parts", committedPartsJson), ("$id", uploadId));
        Run("DELETE FROM multipart_parts WHERE upload_id = $id", ("$id", uploadId));
        tx.Commit();
        return parts;
    }

    /// <summary>Marks the upload aborted and removes its parts (returned for GC). False if not in progress.</summary>
    public (bool Aborted, IReadOnlyList<MultipartPart> Parts) AbortUpload(string uploadId)
    {
        if (!TransitionUpload(uploadId, MultipartStatus.InProgress, MultipartStatus.Aborted))
        {
            return (false, []);
        }

        var parts = ListParts(uploadId);
        using var conn = Open();
        Execute(Query(conn, "DELETE FROM multipart_parts WHERE upload_id = $id", ("$id", uploadId)));
        return (true, parts);
    }

    /// <summary>In-progress uploads for a bucket in (key, upload_id) order (idx_multipart_uploads_bucket_key).</summary>
    public IReadOnlyList<MultipartUpload> ListUploads(string bucket, string prefix, string? keyMarker, string? uploadIdMarker, int limit, DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = Query(conn,
            """
            SELECT upload_id, tenant_id, bucket, key, content_type, checksum_algorithm, checksum_type, initiated_at, expires_at,
                   status, final_etag, committed_parts_json
            FROM multipart_uploads
            WHERE bucket = $b AND status = 'InProgress' AND expires_at > $now
              AND key LIKE $prefix ESCAPE '\'
              AND (key, upload_id) > ($km, $um)
            ORDER BY key, upload_id LIMIT $limit
            """,
            ("$b", bucket), ("$now", now.ToString("O")), ("$prefix", EscapeLike(prefix) + "%"),
            ("$km", keyMarker ?? ""), ("$um", uploadIdMarker ?? ""), ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var uploads = new List<MultipartUpload>();
        while (r.Read())
        {
            uploads.Add(ReadUpload(r));
        }

        return uploads;
    }

    private static MultipartUpload ReadUpload(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture),
        Enum.Parse<MultipartStatus>(r.GetString(9)),
        r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11));

    private static MultipartPart ReadPart(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3), SplitNodeIds(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture));
}
