namespace Symposia.Network.Node.Alerts;

public enum AlertSeverity
{
    Warning,
    Error,
}

/// <summary>
/// Where a role raises Operator Alerts (issue #196). Alerts are level-triggered state keyed by
/// <see cref="AlertKeys"/>: raising the same key and severity again is a no-op, and the host
/// decides when to notify. Calls are cheap and never block on delivery.
/// </summary>
public interface IOperatorAlertSink
{
    void Raise(string key, AlertSeverity severity, string title, string detail);

    void Clear(string key);
}

/// <summary>Used when a role runs outside the Node Host: alerts go nowhere.</summary>
public sealed class NullOperatorAlertSink : IOperatorAlertSink
{
    public static readonly NullOperatorAlertSink Instance = new();

    public void Raise(string key, AlertSeverity severity, string title, string detail)
    {
    }

    public void Clear(string key)
    {
    }
}

public static class AlertKeys
{
    /// <summary>Free disk can't cover the storage node's unused allocation (#197).</summary>
    public const string StorageDiskLow = "storage.disk-low";

    /// <summary>The storage node refuses writes because of disk space (#199).</summary>
    public const string StorageReadOnly = "storage.read-only";
}
