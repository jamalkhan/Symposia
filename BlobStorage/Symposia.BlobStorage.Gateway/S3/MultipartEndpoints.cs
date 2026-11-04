using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.Nodes;
using Symposia.BlobStorage.Gateway.Quorum;
using Symposia.BlobStorage.Gateway.Repair;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>
/// S3 multipart upload (issue #5): Create, UploadPart, Complete, Abort, ListParts, ListMultipartUploads.
/// Parts are ordinary blobs written at quorum and verified as they stream (#139); Complete re-streams
/// them in order through <see cref="QuorumWriter"/> to materialize one blob with its own content-hash
/// CID, and records the S3 multipart ETag separately. Shares routes with the object endpoints, which
/// dispatch here on the <c>uploads</c> / <c>uploadId</c> / <c>partNumber</c> query parameters.
/// </summary>
internal static class MultipartEndpoints
{
    public const int MaxParts = 10_000;
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromDays(7);

    internal static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/{bucket}/{**key}", (string bucket, string key, HttpContext ctx) =>
            ctx.Request.Query.ContainsKey("uploads") ? CreateAsync(bucket, key, ctx)
            : ctx.Request.Query.ContainsKey("uploadId") ? CompleteAsync(bucket, key, ctx)
            : Task.FromResult(S3Xml.Error(400, "InvalidRequest", "Unsupported POST on an object.")));

    internal static bool IsPartUpload(HttpRequest request) =>
        request.Query.ContainsKey("partNumber") && request.Query.ContainsKey("uploadId");

    internal static bool HasUploadId(HttpRequest request) => request.Query.ContainsKey("uploadId");

    // ── Create ────────────────────────────────────────────────────────────────

    private static Task<IResult> CreateAsync(string bucket, string key, HttpContext ctx)
    {
        if (!Tenant(ctx, out var tenantId)) return Task.FromResult(S3Xml.AccessDenied());
        var store = Store(ctx);
        if (!store.BucketExists(bucket)) return Task.FromResult(S3Xml.NoSuchBucket(bucket));

        var algorithm = ctx.Request.Headers["x-amz-checksum-algorithm"].FirstOrDefault()?.ToLowerInvariant();
        if (algorithm is not null && !Checksums.Algorithms.Contains(algorithm))
        {
            return Task.FromResult(S3Xml.Error(400, "InvalidRequest", "Unsupported checksum algorithm."));
        }

        var now = DateTimeOffset.UtcNow;
        var upload = new MultipartUpload(
            "up" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), tenantId, bucket, key,
            ctx.Request.ContentType ?? "application/octet-stream", algorithm,
            ctx.Request.Headers["x-amz-checksum-type"].FirstOrDefault(), now, now + UploadLifetime, MultipartStatus.InProgress, null, null);
        store.CreateUpload(upload);
        Logger(ctx).LogInformation("Multipart upload {UploadId} started for {Bucket}/{Key}.", upload.UploadId, bucket, key);

        if (algorithm is not null)
        {
            ctx.Response.Headers["x-amz-checksum-algorithm"] = algorithm.ToUpperInvariant();
        }

        if (upload.ChecksumType is not null)
        {
            ctx.Response.Headers["x-amz-checksum-type"] = upload.ChecksumType;
        }

        return Task.FromResult(Xml(new XElement(Ns + "InitiateMultipartUploadResult",
            new XElement(Ns + "Bucket", bucket), new XElement(Ns + "Key", key), new XElement(Ns + "UploadId", upload.UploadId))));
    }

    // ── UploadPart ────────────────────────────────────────────────────────────

    internal static async Task<IResult> UploadPartAsync(string bucket, string key, HttpContext ctx)
    {
        if (!Tenant(ctx, out var tenantId)) return S3Xml.AccessDenied();
        if (ctx.Request.Headers.ContainsKey("x-amz-copy-source"))
        {
            return S3Xml.Error(501, "NotImplemented", "UploadPartCopy is not supported yet.");
        }

        var store = Store(ctx);
        if (Find(store, ctx, tenantId, bucket, key) is not { Status: MultipartStatus.InProgress } upload)
        {
            return NoSuchUpload();
        }

        if (!int.TryParse(ctx.Request.Query["partNumber"], NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber)
            || partNumber is < 1 or > MaxParts)
        {
            return S3Xml.Error(400, "InvalidArgument", $"Part number must be an integer between 1 and {MaxParts}.");
        }

        var writer = ctx.RequestServices.GetRequiredService<QuorumWriter>();
        ChecksumVerifyingStream body;
        BlobWriteResult? blob;
        try
        {
            body = UploadBody.Open(ctx);
            blob = await writer.WriteBlobAsync(tenantId, bucket, key, body, ctx.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            return S3Xml.IncompleteBody(ex.Message);
        }
        catch (S3RequestRejectedException ex)
        {
            Logger(ctx).LogWarning("Rejected part {Part} of upload {UploadId}: {Code} {Message}", partNumber, upload.UploadId, ex.Code, ex.Message);
            return S3Xml.Error(ex.Status, ex.Code, ex.Message);
        }

        if (blob is null)
        {
            return S3Xml.ServiceUnavailable();
        }

        var checksum = upload.ChecksumAlgorithm is { } alg && body.Computed.TryGetValue(alg, out var value) ? value : null;
        var (accepted, replaced) = store.PutPart(new MultipartPart(
            upload.UploadId, partNumber, blob.Cid, blob.SizeBytes, blob.NodeIds, checksum, DateTimeOffset.UtcNow));
        if (!accepted)
        {
            // Completed or aborted while this part was streaming: its blob is unreferenced.
            CollectIfUnreferenced(store, blob.Cid, blob.NodeIds);
            return NoSuchUpload();
        }

        if (replaced is not null)
        {
            CollectIfUnreferenced(store, replaced.Cid, replaced.NodeIds);
        }

        store.TouchUpload(upload.UploadId, DateTimeOffset.UtcNow + UploadLifetime);
        Logger(ctx).LogDebug("Upload {UploadId} part {Part}: {Size} bytes, CID {Cid}.", upload.UploadId, partNumber, blob.SizeBytes, blob.Cid);

        foreach (var (algorithm, computed) in body.Computed.Where(c => c.Key != "md5"))
        {
            ctx.Response.Headers[$"x-amz-checksum-{algorithm}"] = computed;
        }

        ctx.Response.Headers.ETag = $"\"{blob.Cid}\"";
        return Results.Ok();
    }

    // ── Complete ──────────────────────────────────────────────────────────────

    private static async Task<IResult> CompleteAsync(string bucket, string key, HttpContext ctx)
    {
        if (!Tenant(ctx, out var tenantId)) return S3Xml.AccessDenied();
        var store = Store(ctx);
        var upload = Find(store, ctx, tenantId, bucket, key);
        if (upload is null or { Status: MultipartStatus.Aborted })
        {
            return NoSuchUpload();
        }

        List<(int PartNumber, string ETag)> requested;
        try
        {
            requested = await ParseCompleteAsync(ctx.Request);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or InvalidOperationException)
        {
            return S3Xml.Error(400, "MalformedXML", "The XML you provided was not well-formed or did not validate.");
        }

        var requestedJson = CommittedJson(requested.Select(p => (p.PartNumber, p.ETag)));
        if (upload.Status == MultipartStatus.Completed)
        {
            // Idempotent retry (e.g. the client timed out waiting): same list → same result.
            return upload.CommittedPartsJson == requestedJson
                ? CompleteResult(ctx, bucket, key, upload.FinalEtag!)
                : S3Xml.Error(409, "UploadAlreadyCompleted", "This upload was already completed with a different part list.");
        }

        if (requested.Count == 0 || requested.Count > MaxParts)
        {
            return S3Xml.Error(400, "MalformedXML", $"A Complete request must list between 1 and {MaxParts} parts.");
        }

        if (!store.TransitionUpload(upload.UploadId, MultipartStatus.InProgress, MultipartStatus.Completing))
        {
            return S3Xml.Error(409, "OperationAborted", "A conflicting operation is in progress on this upload; try again.");
        }

        var options = ctx.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var stored = store.ListParts(upload.UploadId).ToDictionary(p => p.PartNumber);
        var invalid = Validate(requested, stored, options.MultipartMinPartSizeBytes);
        if (invalid is not null)
        {
            store.TransitionUpload(upload.UploadId, MultipartStatus.Completing, MultipartStatus.InProgress);
            return invalid;
        }

        var parts = requested.Select(p => stored[p.PartNumber]).ToList();
        var finalEtag = MultipartEtag(parts.Select(p => p.Cid));

        var assembly = RunAssemblyAsync(ctx, upload, parts, finalEtag);

        // Small uploads finish quickly: answer normally, with the right status code.
        if (await Task.WhenAny(assembly, Task.Delay(QuickCompleteWindow, ctx.RequestAborted)) == assembly)
        {
            var (ok, status, code, message) = await assembly;
            return ok ? CompleteResult(ctx, bucket, key, finalEtag) : S3Xml.Error(status, code, message);
        }

        // Assembly re-streams every part (node → gateway → nodes), which can outlast a client's read
        // timeout. Like S3, answer 200 and send whitespace while working; the result (or an <Error>)
        // follows in the body, which S3 SDKs handle for this operation.
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/xml";
        await ctx.Response.StartAsync(ctx.RequestAborted);
        using var keepAlive = new CancellationTokenSource();
        var heartbeat = KeepAliveAsync(ctx, keepAlive.Token);
        var result = await assembly;
        await keepAlive.CancelAsync();
        await heartbeat;
        await ctx.Response.WriteAsync(
            result.Ok ? CompleteDocument(ctx, bucket, key, finalEtag) : ErrorDocument(result.Code, result.Message), ctx.RequestAborted);
        return Results.Empty;
    }

    private static readonly TimeSpan QuickCompleteWindow = TimeSpan.FromSeconds(5);

    /// <summary>Assembles and commits; on failure the upload returns to InProgress so the client can retry.</summary>
    private static async Task<(bool Ok, int Status, string Code, string Message)> RunAssemblyAsync(
        HttpContext ctx, MultipartUpload upload, List<MultipartPart> parts, string finalEtag)
    {
        (bool, int, string, string) result;
        try
        {
            result = await AssembleAsync(ctx, upload, parts, finalEtag) is null
                ? (false, 503, "ServiceUnavailable", "Write quorum was not reached while assembling the object.")
                : (true, 200, "", "");
        }
        catch (S3RequestRejectedException ex)
        {
            result = (false, ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException or InvalidDataException or OperationCanceledException)
        {
            Logger(ctx).LogError(ex, "Assembling upload {UploadId} failed.", upload.UploadId);
            result = (false, 500, "InternalError", "Assembling the object failed; retry the Complete request.");
        }

        if (!result.Item1)
        {
            Store(ctx).TransitionUpload(upload.UploadId, MultipartStatus.Completing, MultipartStatus.InProgress);
        }

        return result;
    }

    private static async Task<ObjectRecord?> AssembleAsync(HttpContext ctx, MultipartUpload upload, List<MultipartPart> parts, string finalEtag)
    {
        var store = Store(ctx);
        var nodes = ctx.RequestServices.GetRequiredService<INodeRegistry>();
        var writer = ctx.RequestServices.GetRequiredService<QuorumWriter>();
        var started = System.Diagnostics.Stopwatch.StartNew();

        // A full-object checksum on Complete (FULL_OBJECT type) is checked over the assembled bytes.
        // Composite values ("<base64>-<n>") are checksums of part checksums and aren't verified here.
        var expectations = new List<ChecksumExpectation>();
        foreach (var header in ctx.Request.Headers.Keys.Where(k => k.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase)))
        {
            var algorithm = header["x-amz-checksum-".Length..].ToLowerInvariant();
            var value = ctx.Request.Headers[header].ToString();
            if (Checksums.Algorithms.Contains(algorithm) && !value.Contains('-', StringComparison.Ordinal))
            {
                expectations.Add(new ChecksumExpectation(algorithm, () => value, Hex: false, "BadDigest", header));
            }
        }

        await using var assembled = new ChecksumVerifyingStream(new PartsStream(parts, nodes, Logger(ctx)), expectations);
        var blob = await writer.WriteBlobAsync(upload.TenantId, upload.Bucket, upload.Key, assembled, ctx.RequestAborted);
        if (blob is null)
        {
            return null;
        }

        // Quota (quota-management.md) would be enforced here once a quota engine exists; descoped (#5 addendum).
        var record = new ObjectRecord(upload.Bucket, upload.Key, blob.Cid, blob.SizeBytes, upload.ContentType,
            DateTimeOffset.UtcNow, blob.NodeIds, finalEtag);
        var committedJson = CommittedJson(parts.Select(p => (p.PartNumber, p.Cid)));
        var removed = store.CompleteUpload(upload.UploadId, record, finalEtag, committedJson);

        // Parts are no longer needed once assembled (unless another object or upload shares the bytes).
        foreach (var part in removed)
        {
            CollectIfUnreferenced(store, part.Cid, part.NodeIds);
        }

        var options = ctx.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value;
        if (blob.NodeIds.Count < options.MinCopiesPerObject)
        {
            ctx.RequestServices.GetRequiredService<RepairQueue>().Enqueue(new RepairTask(blob.Cid, null, RepairReason.UnderReplicated));
        }

        Logger(ctx).LogInformation("Multipart upload {UploadId} completed: {Parts} parts, {Size} bytes, CID {Cid}, in {ElapsedMs} ms.",
            upload.UploadId, parts.Count, blob.SizeBytes, blob.Cid, started.ElapsedMilliseconds);
        return record;
    }

    /// <summary>The part list in a normal form (unquoted, lower-case ETags) so an identical retry compares equal.</summary>
    private static string CommittedJson(IEnumerable<(int PartNumber, string ETag)> parts) =>
        JsonSerializer.Serialize(parts.Select(p => new[] { p.PartNumber.ToString(CultureInfo.InvariantCulture), p.ETag.Trim('"').ToLowerInvariant() }));

    private static IResult? Validate(List<(int PartNumber, string ETag)> requested, Dictionary<int, MultipartPart> stored, long minPartSize)
    {
        // Same precedence as S3: order, then existence/ETag, then sizes.
        for (var i = 1; i < requested.Count; i++)
        {
            if (requested[i].PartNumber <= requested[i - 1].PartNumber)
            {
                return S3Xml.Error(400, "InvalidPartOrder", "The list of parts was not in ascending order.");
            }
        }

        for (var i = 0; i < requested.Count; i++)
        {
            var (number, etag) = requested[i];
            if (!stored.TryGetValue(number, out var part) || !part.Cid.Equals(etag.Trim('"'), StringComparison.OrdinalIgnoreCase))
            {
                return S3Xml.Error(400, "InvalidPart", $"Part {number} was not uploaded or its ETag does not match.");
            }

            if (i < requested.Count - 1 && part.SizeBytes < minPartSize)
            {
                return S3Xml.Error(400, "EntityTooSmall", $"Part {number} is smaller than the minimum part size ({minPartSize} bytes).");
            }
        }

        return null;
    }

    /// <summary>S3 multipart ETag convention: MD5 over the concatenated part digests, then "-&lt;count&gt;".</summary>
    internal static string MultipartEtag(IEnumerable<string> partCids)
    {
        var parts = partCids.ToList();
        var md5 = MD5.HashData(parts.SelectMany(Convert.FromHexString).ToArray());
        return $"{Convert.ToHexStringLower(md5)}-{parts.Count}";
    }

    private static async Task<List<(int, string)>> ParseCompleteAsync(HttpRequest request)
    {
        var doc = await XDocument.LoadAsync(request.Body, LoadOptions.None, request.HttpContext.RequestAborted);
        return doc.Root!.Elements().Where(e => e.Name.LocalName == "Part")
            .Select(e => (
                int.Parse(e.Elements().Single(x => x.Name.LocalName == "PartNumber").Value, NumberStyles.None, CultureInfo.InvariantCulture),
                e.Elements().Single(x => x.Name.LocalName == "ETag").Value.Trim()))
            .ToList();
    }

    private static async Task KeepAliveAsync(HttpContext ctx, CancellationToken stop)
    {
        try
        {
            await ctx.Response.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n", stop);
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stop);
                await ctx.Response.WriteAsync(" ", stop);
                await ctx.Response.Body.FlushAsync(stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static IResult CompleteResult(HttpContext ctx, string bucket, string key, string etag) =>
        Results.Content(CompleteDocument(ctx, bucket, key, etag, declaration: true), "application/xml");

    private static string CompleteDocument(HttpContext ctx, string bucket, string key, string etag, bool declaration = false)
    {
        var element = new XElement(Ns + "CompleteMultipartUploadResult",
            new XElement(Ns + "Location", $"{ctx.Request.Scheme}://{ctx.Request.Host}/{bucket}/{key}"),
            new XElement(Ns + "Bucket", bucket),
            new XElement(Ns + "Key", key),
            new XElement(Ns + "ETag", $"\"{etag}\""));
        return declaration ? new XDocument(new XDeclaration("1.0", "UTF-8", null), element).ToString() : element.ToString();
    }

    private static string ErrorDocument(string code, string message) =>
        new XElement("Error", new XElement("Code", code), new XElement("Message", message)).ToString();

    // ── Abort ─────────────────────────────────────────────────────────────────

    internal static IResult Abort(string bucket, string key, HttpContext ctx)
    {
        if (!Tenant(ctx, out var tenantId)) return S3Xml.AccessDenied();
        var store = Store(ctx);
        if (Find(store, ctx, tenantId, bucket, key) is not { Status: MultipartStatus.InProgress } upload)
        {
            return NoSuchUpload();
        }

        var (aborted, parts) = store.AbortUpload(upload.UploadId);
        if (!aborted)
        {
            return NoSuchUpload();
        }

        foreach (var part in parts)
        {
            CollectIfUnreferenced(store, part.Cid, part.NodeIds);
        }

        Logger(ctx).LogInformation("Multipart upload {UploadId} aborted; {Parts} part(s) queued for deletion.", upload.UploadId, parts.Count);
        return Results.NoContent();
    }

    // ── ListParts / ListMultipartUploads ──────────────────────────────────────

    internal static IResult ListParts(string bucket, string key, HttpContext ctx)
    {
        if (!Tenant(ctx, out var tenantId)) return S3Xml.AccessDenied();
        var store = Store(ctx);
        if (Find(store, ctx, tenantId, bucket, key) is not { Status: MultipartStatus.InProgress } upload)
        {
            return NoSuchUpload();
        }

        var max = Math.Clamp(IntQuery(ctx, "max-parts", 1000), 1, 1000);
        var marker = IntQuery(ctx, "part-number-marker", 0);
        var page = store.ListParts(upload.UploadId, marker, max + 1);
        var truncated = page.Count > max;
        var parts = page.Take(max).ToList();

        var root = new XElement(Ns + "ListPartsResult",
            new XElement(Ns + "Bucket", bucket), new XElement(Ns + "Key", key), new XElement(Ns + "UploadId", upload.UploadId),
            new XElement(Ns + "PartNumberMarker", marker), new XElement(Ns + "MaxParts", max),
            new XElement(Ns + "IsTruncated", truncated ? "true" : "false"));
        if (truncated)
        {
            root.Add(new XElement(Ns + "NextPartNumberMarker", parts[^1].PartNumber));
        }

        foreach (var part in parts)
        {
            root.Add(new XElement(Ns + "Part",
                new XElement(Ns + "PartNumber", part.PartNumber),
                new XElement(Ns + "LastModified", part.UploadedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                new XElement(Ns + "ETag", $"\"{part.Cid}\""),
                new XElement(Ns + "Size", part.SizeBytes)));
        }

        return Xml(root);
    }

    internal static IResult ListUploads(string bucket, HttpContext ctx)
    {
        if (!Tenant(ctx, out var tenantId)) return S3Xml.AccessDenied();
        var store = Store(ctx);
        if (!store.BucketExists(bucket)) return S3Xml.NoSuchBucket(bucket);

        var max = Math.Clamp(IntQuery(ctx, "max-uploads", 1000), 1, 1000);
        var prefix = ctx.Request.Query["prefix"].FirstOrDefault() ?? "";
        var keyMarker = ctx.Request.Query["key-marker"].FirstOrDefault();
        var idMarker = ctx.Request.Query["upload-id-marker"].FirstOrDefault();
        var page = store.ListUploads(bucket, prefix, keyMarker, idMarker, max + 1, DateTimeOffset.UtcNow)
            .Where(u => u.TenantId == tenantId).ToList();
        var truncated = page.Count > max;
        var uploads = page.Take(max).ToList();

        var root = new XElement(Ns + "ListMultipartUploadsResult",
            new XElement(Ns + "Bucket", bucket), new XElement(Ns + "Prefix", prefix),
            new XElement(Ns + "KeyMarker", keyMarker ?? ""), new XElement(Ns + "UploadIdMarker", idMarker ?? ""),
            new XElement(Ns + "MaxUploads", max), new XElement(Ns + "IsTruncated", truncated ? "true" : "false"));
        if (truncated)
        {
            root.Add(new XElement(Ns + "NextKeyMarker", uploads[^1].Key), new XElement(Ns + "NextUploadIdMarker", uploads[^1].UploadId));
        }

        foreach (var upload in uploads)
        {
            root.Add(new XElement(Ns + "Upload",
                new XElement(Ns + "Key", upload.Key), new XElement(Ns + "UploadId", upload.UploadId),
                new XElement(Ns + "Initiated", upload.InitiatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))));
        }

        return Xml(root);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The upload named in the query, if it belongs to this caller, bucket and key and hasn't expired.</summary>
    private static MultipartUpload? Find(GatewayMetadataStore store, HttpContext ctx, string tenantId, string bucket, string key)
    {
        var id = ctx.Request.Query["uploadId"].FirstOrDefault();
        var upload = id is null ? null : store.GetUpload(id);
        if (upload is null || upload.TenantId != tenantId || upload.Bucket != bucket || upload.Key != key)
        {
            return null;
        }

        return upload.Status == MultipartStatus.InProgress && upload.ExpiresAt <= DateTimeOffset.UtcNow ? null : upload;
    }

    private static void CollectIfUnreferenced(GatewayMetadataStore store, string cid, IEnumerable<string> nodeIds)
    {
        if (store.IsCidReferenced(cid))
        {
            return;
        }

        foreach (var nodeId in nodeIds)
        {
            store.EnqueueNodeDeletion(cid, nodeId);
        }
    }

    private static IResult NoSuchUpload() =>
        S3Xml.Error(404, "NoSuchUpload", "The specified multipart upload does not exist (it may have been aborted, completed or expired).");

    private static IResult Xml(XElement root) =>
        Results.Content(new XDocument(new XDeclaration("1.0", "UTF-8", null), root).ToString(), "application/xml", Encoding.UTF8);

    private static int IntQuery(HttpContext ctx, string name, int fallback) =>
        int.TryParse(ctx.Request.Query[name].FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static GatewayMetadataStore Store(HttpContext ctx) => ctx.RequestServices.GetRequiredService<GatewayMetadataStore>();

    private static ILogger Logger(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.BlobStorage.Gateway.S3.Multipart");

    private static bool Tenant(HttpContext ctx, out string tenantId)
    {
        tenantId = ctx.Items["TenantId"] as string ?? "";
        return tenantId.Length > 0;
    }
}
