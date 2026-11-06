using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.StorageNode.Grpc;

/// <summary>
/// Requires every storage gRPC call to carry a <see cref="StorageRpcAuth"/> token from a registered
/// member of this node's network (issue #140). Enabled with <c>StorageNode:RequireCallerAuth</c>.
/// </summary>
public sealed class StorageRpcAuthInterceptor(
    NodeIdentity identity,
    IOptions<NodeNetworkOptions> network,
    StorageRpcCallerRegistry callers,
    TimeProvider time,
    ILogger<StorageRpcAuthInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        return await continuation(request, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        return await continuation(requestStream, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        await continuation(request, responseStream, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAsync(context);
        await continuation(requestStream, responseStream, context);
    }

    private async Task AuthorizeAsync(ServerCallContext context)
    {
        var headers = context.RequestHeaders;
        var token = long.TryParse(headers.GetValue(StorageRpcAuth.TimestampHeader), out var ts)
            ? new StorageRpcToken(headers.GetValue(StorageRpcAuth.CallerHeader) ?? "", ts, headers.GetValue(StorageRpcAuth.SignatureHeader) ?? "")
            : null;

        var options = network.Value;
        var reason = StorageRpcAuth.Validate(token, options.ChainId, options.NodeRegistryAddress, identity.NodeId, context.Method, time.GetUtcNow());
        if (reason is null && !await callers.IsRegisteredAsync(token!.Caller, context.CancellationToken))
        {
            reason = "caller is not registered on this network";
        }

        if (reason is not null)
        {
            logger.LogWarning("Refused {Method} from {Peer} (caller {Caller}): {Reason}.", context.Method, context.Peer, token?.Caller ?? "none", reason);
            throw new RpcException(new Status(StatusCode.Unauthenticated, reason));
        }

        logger.LogTrace("Authorized {Method} from {Caller}.", context.Method, token!.Caller);
    }
}
