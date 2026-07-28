namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>Lifecycle status of a database's routing entry (FR4/FR5/FR8 of #93).</summary>
public enum RoutingStatus
{
    Active,
    Migrating,
    Suspended,
    Unreachable,
}
