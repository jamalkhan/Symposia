namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>An incoming client connection attempt as seen by the proxy's connection router.</summary>
public sealed record ConnectionRequest(string DatabaseId, string Username, string SecretHash, bool ReadOnly);
