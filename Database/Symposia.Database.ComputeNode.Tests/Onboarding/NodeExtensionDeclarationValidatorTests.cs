using Symposia.Database.ComputeNode.Lifecycle;
using Symposia.Database.ComputeNode.Onboarding;

namespace Symposia.Database.ComputeNode.Tests.Onboarding;

/// <summary>Traces to #103's FR2.3 (Core-tier enforcement) and FR5 (structural verification), AC6.</summary>
public sealed class NodeExtensionDeclarationValidatorTests
{
    [Fact]
    public void Validate_MissingCoreExtension_Invalid()
    {
        var allowlist = new InMemoryExtensionAllowlist();
        var declared = new Dictionary<string, string> { ["pgvector"] = "0.7.0" }; // no pg_stat_statements

        var result = NodeExtensionDeclarationValidator.Validate(declared, allowlist, postgresMajorVersion: 17);

        Assert.False(result.IsValid);
        Assert.Contains("pg_stat_statements", result.MissingCoreExtensions);
    }

    [Fact]
    public void Validate_CoreOnly_Valid()
    {
        // FR2.2: Core-only is a valid, if minimally competitive, declaration.
        var allowlist = new InMemoryExtensionAllowlist();
        var declared = new Dictionary<string, string> { ["pg_stat_statements"] = "1.10" };

        var result = NodeExtensionDeclarationValidator.Validate(declared, allowlist, postgresMajorVersion: 17);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_FullDeclaration_AllPresentAndValid()
    {
        var allowlist = new InMemoryExtensionAllowlist();
        var declared = new Dictionary<string, string>
        {
            ["pg_stat_statements"] = "1.10",
            ["pgvector"] = "0.7.0",
            ["postgis"] = "3.4.0",
        };

        var result = NodeExtensionDeclarationValidator.Validate(declared, allowlist, postgresMajorVersion: 17);

        Assert.True(result.IsValid);
        Assert.Empty(result.InvalidDeclaredExtensions);
    }

    [Fact]
    public void Validate_DeclaredExtensionNotAllowlisted_FlaggedInvalidButDoesNotFailCoreCheck()
    {
        var allowlist = new InMemoryExtensionAllowlist();
        var declared = new Dictionary<string, string>
        {
            ["pg_stat_statements"] = "1.10",
            ["dblink"] = "1.2", // not allowlisted (Untrusted, structurally excluded)
        };

        var result = NodeExtensionDeclarationValidator.Validate(declared, allowlist, postgresMajorVersion: 17);

        Assert.True(result.IsValid); // Core satisfied
        Assert.Contains("dblink", result.InvalidDeclaredExtensions);
    }

    [Fact]
    public void Validate_DeclaredVersionOutsideAllowlistRange_FlaggedInvalid()
    {
        var allowlist = new InMemoryExtensionAllowlist();
        var declared = new Dictionary<string, string>
        {
            ["pg_stat_statements"] = "1.10",
            ["pgvector"] = "99.0.0", // outside the allowlist's declared range
        };

        var result = NodeExtensionDeclarationValidator.Validate(declared, allowlist, postgresMajorVersion: 17);

        Assert.Contains("pgvector", result.InvalidDeclaredExtensions);
    }

    [Fact]
    public void Validate_DeclaredForIncompatibleMajor_FlaggedInvalid()
    {
        var allowlist = new InMemoryExtensionAllowlist([
            new ExtensionAllowlistEntry("pg_stat_statements", "1.0", "1.11", new HashSet<int> { 17 }, ExtensionSupportTier.Core, ExtensionPrivilegeClass.Trusted),
        ]);
        var declared = new Dictionary<string, string> { ["pg_stat_statements"] = "1.10" };

        var result = NodeExtensionDeclarationValidator.Validate(declared, allowlist, postgresMajorVersion: 15); // entry only compatible with 17

        Assert.Contains("pg_stat_statements", result.InvalidDeclaredExtensions);
        Assert.False(result.IsValid); // still missing a *valid* Core declaration
    }

    [Fact]
    public void Validate_NullDeclaration_MissingAllCoreExtensions()
    {
        var allowlist = new InMemoryExtensionAllowlist();

        var result = NodeExtensionDeclarationValidator.Validate(null, allowlist, postgresMajorVersion: 17);

        Assert.False(result.IsValid);
        Assert.Contains("pg_stat_statements", result.MissingCoreExtensions);
    }
}
