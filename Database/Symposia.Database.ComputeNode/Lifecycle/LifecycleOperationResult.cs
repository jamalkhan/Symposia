namespace Symposia.Database.ComputeNode.Lifecycle;

public enum LifecycleOperationOutcome
{
    Ok,
    Rejected,
    NotFound,
    NoQualifyingNode,
    /// <summary>Issue #102, FR1.4: the requested Postgres major version is not in the platform's currently supported set.</summary>
    UnsupportedMajorVersion,
    /// <summary>Issue #103, FR3.4/TC-03: a required extension is not on the platform allowlist at all.</summary>
    ExtensionNotAllowlisted,
    /// <summary>Issue #103, FR3.3/AC8/TC-14: allowlisted, but no qualifying node has it available in the requested region/tier.</summary>
    ExtensionUnavailable,
}

/// <summary>Result of a lifecycle transition attempt (provision/resize/suspend/resume/delete).</summary>
public sealed record LifecycleOperationResult(LifecycleOperationOutcome Outcome, DatabaseLifecycleRecord? Record, string? Reason)
{
    public static LifecycleOperationResult Ok(DatabaseLifecycleRecord record) => new(LifecycleOperationOutcome.Ok, record, null);

    public static LifecycleOperationResult Rejected(string reason, DatabaseLifecycleRecord? record = null) =>
        new(LifecycleOperationOutcome.Rejected, record, reason);

    public static LifecycleOperationResult NotFound(string databaseId) =>
        new(LifecycleOperationOutcome.NotFound, null, $"No database '{databaseId}' exists.");

    public static LifecycleOperationResult NoQualifyingNode() =>
        new(LifecycleOperationOutcome.NoQualifyingNode, null, "No qualifying compute node is available for the requested region/tier.");

    public static LifecycleOperationResult UnsupportedMajorVersion(int major) =>
        new(LifecycleOperationOutcome.UnsupportedMajorVersion, null, $"Postgres major version {major} is not currently supported by the platform.");

    public static LifecycleOperationResult ExtensionNotAllowlisted(string extensionName) =>
        new(LifecycleOperationOutcome.ExtensionNotAllowlisted, null, $"Extension '{extensionName}' is not on the platform's extension allowlist.");

    public static LifecycleOperationResult ExtensionUnavailable(IReadOnlyList<string> unavailableExtensions) =>
        new(LifecycleOperationOutcome.ExtensionUnavailable, null,
            $"No compute node in the requested region/tier has extension(s) {string.Join(", ", unavailableExtensions)} declared and verified.");
}
