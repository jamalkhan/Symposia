using System.Diagnostics;

namespace Symposia.BlobStorage.Gateway.Health;

/// <summary>
/// One structured log line per request (#1 FR-29) and the request metrics (#1 FR-26). Registered before
/// authentication so rejected requests are logged too; the tenant and credential are read after the
/// pipeline ran, once auth has resolved them. Health, readiness and metrics probes log at Debug: they
/// arrive every few seconds from load balancers and aren't tenant traffic.
/// </summary>
public static class RequestLog
{
    public const string SelectedNodeItem = "SelectedNodeId";
    public const string ErrorItem = "RequestError";
    public const string CredentialItem = "CredentialId";

    /// <summary>Records which node served or stored this request, for the log line.</summary>
    public static void SelectedNode(HttpContext ctx, string nodeId) => ctx.Items[SelectedNodeItem] = nodeId;

    /// <summary>Records why this request failed, for the log line.</summary>
    public static void Error(HttpContext ctx, string detail) => ctx.Items[ErrorItem] = detail;

    public static IApplicationBuilder UseRequestLog(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Symposia.BlobStorage.Gateway.RequestLog");
        var metrics = app.ApplicationServices.GetRequiredService<GatewayMetrics>();
        return app.Use(async (ctx, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var requestBody = new CountingStream(ctx.Request.Body);
            var responseBody = new CountingStream(ctx.Response.Body);
            ctx.Request.Body = requestBody;
            ctx.Response.Body = responseBody;
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                Error(ctx, ex.Message);
                throw;
            }
            finally
            {
                ctx.Request.Body = requestBody.Inner;
                ctx.Response.Body = responseBody.Inner;
                var elapsed = Stopwatch.GetElapsedTime(started);
                var path = ctx.Request.Path.Value ?? "";
                var operation = OperationName(ctx);
                var status = ctx.RequestAborted.IsCancellationRequested && !ctx.Response.HasStarted ? 499 : ctx.Response.StatusCode;
                if (!HealthEndpoints.IsHealthPath(path))
                {
                    metrics.RequestsTotal.WithLabels(ctx.Request.Method, status.ToString(), operation).Inc();
                    metrics.RequestDuration.WithLabels(operation).Observe(elapsed.TotalSeconds);
                    metrics.RequestBytes.WithLabels("in").Inc(requestBody.Count);
                    metrics.RequestBytes.WithLabels("out").Inc(responseBody.Count);
                }

                logger.Log(HealthEndpoints.IsHealthPath(path) ? LogLevel.Debug : LogLevel.Information,
                    "request {RequestId} tenant={TenantId} credential={CredentialId} {Operation} {Bucket}/{Key} -> {StatusCode} in={BytesIn} out={BytesOut} latency_ms={LatencyMs} node={SelectedNodeId} error={Error}",
                    Api.RequestIds.Of(ctx),
                    ctx.Items["TenantId"] as string,
                    ctx.Items[CredentialItem] as string ?? (ctx.Items[nameof(Auth.SigV4Context)] as Auth.SigV4Context)?.AccessKeyId,
                    operation,
                    ctx.GetRouteValue("bucket") as string,
                    ctx.GetRouteValue("key") as string,
                    status,
                    requestBody.Count,
                    responseBody.Count,
                    Math.Round(elapsed.TotalMilliseconds, 1),
                    ctx.Items[SelectedNodeItem] as string,
                    ctx.Items[ErrorItem] as string ?? (status >= 400 ? ReasonPhrase(status) : null));
            }
        });
    }

    /// <summary>The S3-style operation name of a request, from its method, route and query.</summary>
    internal static string OperationName(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var q = ctx.Request.Query;
        var method = ctx.Request.Method;
        if (HealthEndpoints.IsHealthPath(path))
        {
            return path.TrimStart('/').Replace('/', '_');
        }

        if (path.StartsWith("/admin/", StringComparison.Ordinal))
        {
            return "Admin";
        }

        var hasKey = ctx.GetRouteValue("key") is string { Length: > 0 };
        var hasBucket = ctx.GetRouteValue("bucket") is string { Length: > 0 };
        var api = path.StartsWith(Api.BlobApiEndpoints.Prefix + "/", StringComparison.Ordinal) ? "V1" : "";
        return (method, hasBucket, hasKey) switch
        {
            ("GET", false, _) => api + "ListBuckets",
            ("PUT", true, false) => api + "CreateBucket",
            ("DELETE", true, false) => api + "DeleteBucket",
            ("HEAD", true, false) => api + "HeadBucket",
            ("GET", true, false) => q.ContainsKey("uploads") ? "ListMultipartUploads" : api + "ListObjects",
            ("PUT", true, true) when q.ContainsKey("partNumber") => "UploadPart",
            ("PUT", true, true) when ctx.Request.Headers.ContainsKey("x-amz-copy-source") => "CopyObject",
            ("PUT", true, true) => api + "PutObject",
            ("GET", true, true) when q.ContainsKey("uploadId") => "ListParts",
            ("GET", true, true) => api + "GetObject",
            ("HEAD", true, true) => api + "HeadObject",
            ("DELETE", true, true) when q.ContainsKey("uploadId") => "AbortMultipartUpload",
            ("DELETE", true, true) => api + "DeleteObject",
            ("POST", true, true) when q.ContainsKey("uploads") => "CreateMultipartUpload",
            ("POST", true, true) when q.ContainsKey("uploadId") => "CompleteMultipartUpload",
            _ => api + method,
        };
    }

    private static string ReasonPhrase(int status) =>
        Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : status.ToString();

    /// <summary>Counts bytes passing through a request or response body.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public Stream Inner { get; } = inner;

        public long Count { get; private set; }

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => Inner.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Inner.Read(buffer, offset, count);
            Count += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await Inner.ReadAsync(buffer, ct);
            Count += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Inner.Write(buffer, offset, count);
            Count += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await Inner.WriteAsync(buffer, ct);
            Count += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
