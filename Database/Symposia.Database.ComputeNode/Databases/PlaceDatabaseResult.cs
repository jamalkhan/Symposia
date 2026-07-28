namespace Symposia.Database.ComputeNode.Databases;

public enum PlaceDatabaseOutcome
{
    Placed,
    Conflict,
    UnsupportedVersion,
    CapacityExceeded,
}

public sealed record PlaceDatabaseResult(PlaceDatabaseOutcome Outcome, TenantDatabase? Database, string? Reason)
{
    public static PlaceDatabaseResult Ok(TenantDatabase database) =>
        new(PlaceDatabaseOutcome.Placed, database, Reason: null);

    public static PlaceDatabaseResult Conflict() =>
        new(PlaceDatabaseOutcome.Conflict, null, "A database with this tenant_db_id is already hosted on this node.");

    public static PlaceDatabaseResult UnsupportedVersion(int supportedVersion) =>
        new(PlaceDatabaseOutcome.UnsupportedVersion, null, $"This node only supports Postgres major version {supportedVersion}.");

    public static PlaceDatabaseResult CapacityExceeded() =>
        new(PlaceDatabaseOutcome.CapacityExceeded, null, "Node is unhealthy or at declared capacity and cannot accept new placements.");
}
