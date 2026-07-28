using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Tests.Proxy;

/// <summary>Traces to QA plan AUTH-01..AUTH-06: live rotation/drop without a proxy restart.</summary>
public sealed class AuthCacheServiceTests
{
    [Fact]
    public void Authenticate_UnknownUser_Rejected()
    {
        var cache = new AuthCacheService();

        Assert.False(cache.Authenticate("db-1", "ghost", "hash"));
    }

    [Fact]
    public void Rotate_OldPasswordRejected_NewPasswordAccepted()
    {
        var cache = new AuthCacheService();
        cache.Upsert("db-1", "tenant", "old-hash");

        cache.Upsert("db-1", "tenant", "new-hash");

        Assert.False(cache.Authenticate("db-1", "tenant", "old-hash"));
        Assert.True(cache.Authenticate("db-1", "tenant", "new-hash"));
    }

    [Fact]
    public void Drop_RemovesUserEntirely()
    {
        var cache = new AuthCacheService();
        cache.Upsert("db-1", "tenant", "hash");

        cache.Drop("db-1", "tenant");

        Assert.False(cache.Authenticate("db-1", "tenant", "hash"));
    }
}
