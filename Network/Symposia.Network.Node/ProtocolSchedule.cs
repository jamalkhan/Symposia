namespace Symposia.Network.Node;

/// <summary>
/// The network's protocol activation schedule (issue #145), as served by the Blockchain Gateway at
/// <see cref="Path"/> from the on-chain ProtocolActivations contract. Times are Unix seconds;
/// <see cref="Now"/> is the chain's clock when it was read.
/// </summary>
public sealed record ProtocolSchedule(
    bool Configured,
    long Now,
    ProtocolSchedule.MinVersion MinProtocolVersion,
    IReadOnlyList<ProtocolSchedule.Activation> Features)
{
    public const string Path = "/v1/protocol";

    public static readonly ProtocolSchedule None = new(false, 0, new MinVersion(0, 0, 0), []);

    /// <param name="ActivatesAt">0 = not scheduled (never scheduled, or cancelled).</param>
    public sealed record Activation(string Name, long ActivatesAt)
    {
        public bool InEffect(DateTimeOffset now) => ActivatesAt > 0 && ActivatesAt <= now.ToUnixTimeSeconds();
    }

    public sealed record MinVersion(int Effective, int Pending, long PendingAt)
    {
        /// <summary>The minimum in effect at <paramref name="now"/> (a pending rise counts once its time passes).</summary>
        public int At(DateTimeOffset now) => Pending > 0 && PendingAt > 0 && PendingAt <= now.ToUnixTimeSeconds() ? Pending : Effective;
    }
}
