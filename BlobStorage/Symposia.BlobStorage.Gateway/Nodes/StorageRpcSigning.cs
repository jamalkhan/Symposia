using Grpc.Core;
using Grpc.Core.Interceptors;
using Symposia.Network.Node;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>Signs storage RPCs with the gateway host's node key (issue #140).</summary>
public interface IStorageRpcSigner
{
    StorageRpcToken Sign(string audience, string method);
}

public sealed class NodeIdentityRpcSigner(NodeIdentity identity, TimeProvider? time = null) : IStorageRpcSigner
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public StorageRpcToken Sign(string audience, string method) =>
        identity.SignStorageRpc(audience, method, _time.GetUtcNow().ToUnixTimeSeconds());
}

/// <summary>Adds a fresh <see cref="StorageRpcAuth"/> token to every call made on a node's channel.</summary>
internal sealed class StorageRpcSigningInterceptor(IStorageRpcSigner signer, string audience) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, Signed(context));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, Signed(context));

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(Signed(context));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, Signed(context));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(Signed(context));

    private ClientInterceptorContext<TRequest, TResponse> Signed<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var token = signer.Sign(audience, context.Method.FullName);
        // A fresh Metadata: never mutate the caller's (a reused instance would accumulate stale tokens).
        var headers = new Grpc.Core.Metadata();
        foreach (var entry in context.Options.Headers ?? [])
        {
            if (!entry.Key.StartsWith("x-symposia-", StringComparison.OrdinalIgnoreCase))
            {
                headers.Add(entry);
            }
        }

        headers.Add(StorageRpcAuth.CallerHeader, token.Caller);
        headers.Add(StorageRpcAuth.TimestampHeader, token.UnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        headers.Add(StorageRpcAuth.SignatureHeader, token.Signature);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, context.Options.WithHeaders(headers));
    }
}
