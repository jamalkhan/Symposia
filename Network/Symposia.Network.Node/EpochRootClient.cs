using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Symposia.Network.Node;

/// <summary>How a root submission ended at the Blockchain Gateway.</summary>
public enum EpochRootSubmission
{
    /// <summary>Relayed on-chain now.</summary>
    Submitted,

    /// <summary>The chain already holds exactly this root for the epoch; nothing was relayed.</summary>
    AlreadyRecorded,

    /// <summary>The chain holds a different root for the epoch; roots are never overwritten.</summary>
    Conflict,

    /// <summary>The contract refused it (unregistered node, bad signature).</summary>
    Rejected,
}

/// <summary>Epoch manifest roots on EpochRootRegistry (#12 Layer 3), read and written through the Blockchain Gateway.</summary>
public interface IEpochRootClient
{
    Task<EpochRootSubmission> SubmitAsync(string node, ulong epoch, byte[] root, byte[] signature, CancellationToken ct = default);

    /// <summary>The root <paramref name="node"/> recorded for <paramref name="epoch"/>, or null when it recorded none.</summary>
    Task<byte[]?> GetRootAsync(string node, ulong epoch, CancellationToken ct = default);

    /// <summary>The newest epoch and root <paramref name="node"/> recorded, or null when it never submitted.</summary>
    Task<(ulong Epoch, byte[] Root)?> GetLatestAsync(string node, CancellationToken ct = default);
}

/// <summary>
/// Talks to the Bootstrap Chain Gateway's <c>/v1/nodes/{address}/epoch-roots</c> surface (issue #110). As with
/// registration, only the node's own signature crosses the wire; the gateway relays it and pays gas.
/// </summary>
public sealed class EpochRootClient(HttpClient http) : IEpochRootClient
{
    public async Task<EpochRootSubmission> SubmitAsync(string node, ulong epoch, byte[] root, byte[] signature, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"/v1/nodes/{node}/epoch-roots",
            new SubmitRequest(epoch, Hex(root), Hex(signature)), ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict:
                return EpochRootSubmission.Conflict;
            case HttpStatusCode.BadRequest:
                return EpochRootSubmission.Rejected;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SubmitResponse>(ct);
        return body?.Relayed == false ? EpochRootSubmission.AlreadyRecorded : EpochRootSubmission.Submitted;
    }

    public async Task<byte[]?> GetRootAsync(string node, ulong epoch, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/v1/nodes/{node}/epoch-roots/{epoch}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RootResponse>(ct)) is { Root: { } root } ? FromHex(root) : null;
    }

    public async Task<(ulong Epoch, byte[] Root)?> GetLatestAsync(string node, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/v1/nodes/{node}/epoch-roots/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RootResponse>(ct)) is { Root: { } root } latest ? (latest.Epoch, FromHex(root)) : null;
    }

    private static string Hex(byte[] value) => "0x" + Convert.ToHexStringLower(value);

    private static byte[] FromHex(string value) => Convert.FromHexString(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value);

    private sealed record SubmitRequest(
        [property: JsonPropertyName("epoch")] ulong Epoch,
        [property: JsonPropertyName("root")] string Root,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record SubmitResponse([property: JsonPropertyName("relayed")] bool? Relayed);

    private sealed record RootResponse(
        [property: JsonPropertyName("epoch")] ulong Epoch,
        [property: JsonPropertyName("root")] string? Root);
}
