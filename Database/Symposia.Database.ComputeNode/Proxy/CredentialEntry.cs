namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>
/// A cached database-user credential (FR3). <see cref="SecretHash"/> stands in for a SCRAM-SHA-256
/// verifier -- this control-plane skeleton never handles or stores the tenant's raw password.
/// </summary>
public sealed record CredentialEntry(string DatabaseId, string Username, string SecretHash, bool Revoked);
