namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// <see cref="PostgresMajorVersion"/> is the tenant's optional explicit major-version selection at
/// provisioning time (issue #102, FR1.4): <c>null</c> defaults to the catalog's current latest
/// supported major; an explicit value must be in the currently supported set or the request is
/// rejected (FR1.4/AC2 and the spec's "Tenant selects an older supported major" scenario).
/// <see cref="RequiredExtensions"/> is the tenant's optional required-extension-set (issue #103,
/// FR3.1): <c>null</c>/empty means no extension constraint on placement, additive to
/// <see cref="PostgresMajorVersion"/>'s filter, not a replacement.
/// </summary>
public sealed record ProvisionDatabaseRequest(
    string DatabaseId,
    string Region,
    DatabaseSize ComputeSize,
    string DatabaseName,
    int? IdleSuspendSeconds = 900,
    int? PostgresMajorVersion = null,
    IReadOnlySet<string>? RequiredExtensions = null);
