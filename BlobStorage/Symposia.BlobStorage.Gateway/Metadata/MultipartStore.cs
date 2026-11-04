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
        // Entering Completing takes a lease (completing_since) so a crash mid-assembly can't strand
        // the upload: ReapUploads releases leases older than the configured limit.
        return Execute(Query(conn,
            "UPDATE multipart_uploads SET status = $to, completing_since = CASE WHEN $to = 'Completing' THEN $now ELSE NULL END " +
            "WHERE upload_id = $id AND status = $from",
            ("$to", to.ToString()), ("$now", Now()), ("$id", uploadId), ("$from", from.ToString()))) > 0;
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

        CancelPendingDeletion(conn, tx, part.Cid);
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

        if (replaced is not null && replaced.Cid != part.Cid)
        {
            CollectIfUnreferenced(conn, tx, replaced.Cid, replaced.NodeIds);
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
    /// Commits an assembled upload in one IMMEDIATE transaction (#5 review): re-checks that this
    /// request still holds the Completing lease, re-reads the parts inside the transaction, enforces
    /// <c>If-None-Match: *</c>, writes the object through the shared upsert (which also GCs an
    /// overwritten blob), marks the upload Completed with its committed-parts snapshot, deletes the
    /// part rows and queues any part blob nothing else references. Nothing is written unless all succeed.
    /// </summary>
    public CompleteOutcome CompleteUpload(string uploadId, ObjectRecord record, string finalEtag, string committedPartsJson, bool ifNoneMatch)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        using (var status = Query(conn, "SELECT status FROM multipart_uploads WHERE upload_id = $id", ("$id", uploadId)))
        {
            status.Transaction = tx;
            if (status.ExecuteScalar() as string != nameof(MultipartStatus.Completing))
            {
                return CompleteOutcome.LostLease;
            }
        }

        if (ifNoneMatch)
        {
            using var exists = Query(conn, "SELECT 1 FROM objects WHERE bucket = $b AND key = $k", ("$b", record.Bucket), ("$k", record.Key));
            exists.Transaction = tx;
            if (exists.ExecuteScalar() is not null)
            {
                return CompleteOutcome.PreconditionFailed;
            }
        }

        var parts = new List<MultipartPart>();
        using (var list = Query(conn,
                   "SELECT upload_id, part_number, cid, size_bytes, node_ids, checksum, uploaded_at FROM multipart_parts WHERE upload_id = $id",
                   ("$id", uploadId)))
        {
            list.Transaction = tx;
            using var r = list.ExecuteReader();
            while (r.Read())
            {
                parts.Add(ReadPart(r));
            }
        }

        UpsertObject(conn, tx, record with { EtagOverride = finalEtag });
        Run(conn, tx, """
            UPDATE multipart_uploads SET status = 'Completed', completing_since = NULL, completed_at = $at, final_cid = $cid,
                final_etag = $etag, committed_parts_json = $parts WHERE upload_id = $id AND status = 'Completing'
            """,
            ("$at", Now()), ("$cid", record.Cid), ("$etag", finalEtag), ("$parts", committedPartsJson), ("$id", uploadId));
        Run(conn, tx, "DELETE FROM multipart_parts WHERE upload_id = $id", ("$id", uploadId));
        foreach (var part in parts)
        {
            CollectIfUnreferenced(conn, tx, part.Cid, part.NodeIds);
        }

        tx.Commit();
        return CompleteOutcome.Committed;
    }

    /// <summary>
    /// Aborts an in-progress upload (or one whose Completing lease is older than
    /// <paramref name="staleCompleting"/>) and removes its parts, queuing unreferenced part blobs
    /// for deletion, all in one transaction. False if there was nothing to abort.
    /// </summary>
    public bool AbortUpload(string uploadId, TimeSpan staleCompleting)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var aborted = AbortInTransaction(conn, tx, uploadId, DateTimeOffset.UtcNow - staleCompleting);
        tx.Commit();
        return aborted;
    }

    private static bool AbortInTransaction(SqliteConnection conn, SqliteTransaction tx, string uploadId, DateTimeOffset staleBefore)
    {
        using (var update = Query(conn, """
                   UPDATE multipart_uploads SET status = 'Aborted', completing_since = NULL, completed_at = $now
                   WHERE upload_id = $id AND (status = 'InProgress' OR (status = 'Completing' AND completing_since < $stale))
                   """, ("$now", Now()), ("$id", uploadId), ("$stale", staleBefore.ToString("O"))))
        {
            update.Transaction = tx;
            if (update.ExecuteNonQuery() == 0)
            {
                return false;
            }
        }

        var parts = new List<MultipartPart>();
        using (var list = Query(conn,
                   "SELECT upload_id, part_number, cid, size_bytes, node_ids, checksum, uploaded_at FROM multipart_parts WHERE upload_id = $id",
                   ("$id", uploadId)))
        {
            list.Transaction = tx;
            using var r = list.ExecuteReader();
            while (r.Read())
            {
                parts.Add(ReadPart(r));
            }
        }

        Run(conn, tx, "DELETE FROM multipart_parts WHERE upload_id = $id", ("$id", uploadId));
        foreach (var part in parts)
        {
            CollectIfUnreferenced(conn, tx, part.Cid, part.NodeIds);
        }

        return true;
    }

    /// <summary>
    /// Housekeeping (run periodically by <see cref="Repair.MultipartReaper"/>; #5 review):
    /// releases Completing leases older than <paramref name="completingLease"/> back to InProgress (a
    /// gateway crashed mid-assembly), aborts expired in-progress uploads (their parts would otherwise
    /// pin blobs forever) and purges finished upload rows older than <paramref name="retention"/>.
    /// Indexed by idx_multipart_uploads_status_expiry.
    /// </summary>
    public (int Released, int Expired, int Purged) ReapUploads(DateTimeOffset now, TimeSpan completingLease, TimeSpan retention)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        int released;
        using (var release = Query(conn,
                   "UPDATE multipart_uploads SET status = 'InProgress', completing_since = NULL WHERE status = 'Completing' AND completing_since < $stale",
                   ("$stale", (now - completingLease).ToString("O"))))
        {
            release.Transaction = tx;
            released = release.ExecuteNonQuery();
        }

        var expired = new List<string>();
        using (var find = Query(conn, "SELECT upload_id FROM multipart_uploads WHERE status = 'InProgress' AND expires_at < $now", ("$now", now.ToString("O"))))
        {
            find.Transaction = tx;
            using var r = find.ExecuteReader();
            while (r.Read())
            {
                expired.Add(r.GetString(0));
            }
        }

        foreach (var id in expired)
        {
            AbortInTransaction(conn, tx, id, now - completingLease);
        }

        int purged;
        using (var purge = Query(conn,
                   "DELETE FROM multipart_uploads WHERE status IN ('Completed', 'Aborted') AND completed_at < $before",
                   ("$before", (now - retention).ToString("O"))))
        {
            purge.Transaction = tx;
            purged = purge.ExecuteNonQuery();
        }

        tx.Commit();
        return (released, expired.Count, purged);
    }

    /// <summary>
    /// A tenant's in-progress uploads for a bucket in (key, upload_id) order, S3 marker semantics
    /// (key-marker alone: keys strictly after it). Uses the partial index idx_multipart_uploads_open;
    /// the prefix is a case-sensitive range, not LIKE (#5 review).
    /// </summary>
    public IReadOnlyList<MultipartUpload> ListUploads(
        string tenantId, string bucket, string prefix, string? keyMarker, string? uploadIdMarker, int limit, DateTimeOffset now)
    {
        using var conn = Open();
        var marker = string.IsNullOrEmpty(uploadIdMarker)
            ? "AND key > $km"
            : "AND (key, upload_id) > ($km, $um)";
        using var cmd = Query(conn,
            $"""
            SELECT upload_id, tenant_id, bucket, key, content_type, checksum_algorithm, checksum_type, initiated_at, expires_at,
                   status, final_etag, committed_parts_json
            FROM multipart_uploads INDEXED BY idx_multipart_uploads_open
            WHERE bucket = $b AND status = 'InProgress' AND tenant_id = $t AND expires_at > $now
              AND key >= $prefix AND ($prefix = '' OR key < $prefix || char(1114111))
              {(keyMarker is null ? "" : marker)}
            ORDER BY key, upload_id LIMIT $limit
            """,
            ("$b", bucket), ("$t", tenantId), ("$now", now.ToString("O")), ("$prefix", prefix),
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

public enum CompleteOutcome
{
    Committed,
    LostLease,
    PreconditionFailed,
}
