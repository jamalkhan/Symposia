using Grpc.Core;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Protocol.Credentials;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>
/// Pushes each new state version to a gateway (#3 FR5.18). It watches the version in Postgres rather than an
/// in-process event, so any Authority replica serves any gateway and sees every replica's writes.
/// </summary>
public sealed class WatchService(AuthorityStore store, IOptions<AuthorityOptions> options, InternalTls tls) : Protocol.Credentials.CredentialAuthority.CredentialAuthorityBase
{
    public override async Task Watch(WatchRequest request, IServerStreamWriter<StateVersion> responseStream, ServerCallContext context)
    {
        if (tls.Refuse(context.GetHttpContext()) is { } why)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, why)); // #245 FR6
        }

        if (!Tokens.Matches(context.RequestHeaders.GetValue("x-service-token"), options.Value.ServiceToken))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "service token required"));
        }

        var sent = request.SinceVersion;
        var delay = TimeSpan.FromMilliseconds(Math.Max(50, options.Value.WatchPollMilliseconds));
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var current = await store.VersionAsync(context.CancellationToken);
            if (current != sent)
            {
                await responseStream.WriteAsync(new StateVersion { Version = current }, context.CancellationToken);
                sent = current;
            }

            await Task.Delay(delay, context.CancellationToken);
        }
    }
}

internal static class Tokens
{
    /// <summary>Constant-time token check; an unset expected token matches nothing.</summary>
    public static bool Matches(string? presented, string expected) =>
        expected.Length > 0 && presented is not null &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented)),
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected)));
}
