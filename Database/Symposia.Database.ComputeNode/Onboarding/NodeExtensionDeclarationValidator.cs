using Symposia.Database.ComputeNode.Lifecycle;

namespace Symposia.Database.ComputeNode.Onboarding;

/// <summary>
/// Issue #103, FR2.3/FR5: validates an operator's per-node extension declaration at
/// registration/re-verification time, mirroring <see cref="CapacityGuardrailEvaluator"/>'s static-
/// validator pattern. Unlike the capacity guardrail (advisory-only), a failing Core-tier check here
/// is a hard rejection (AC6): "a node missing a Core-tier extension fails registration/re-verification."
///
/// FR5's "verification" is modeled as a structural check only (declared version falls within the
/// allowlist entry's supported range for a compatible Postgres major) -- there is no live Postgres
/// process in this in-memory service layer to run an actual <c>CREATE EXTENSION</c> probe against.
/// </summary>
public static class NodeExtensionDeclarationValidator
{
    public static NodeExtensionDeclarationResult Validate(
        IReadOnlyDictionary<string, string>? declaredExtensionVersions,
        IExtensionAllowlist allowlist,
        int postgresMajorVersion)
    {
        var declared = declaredExtensionVersions ?? new Dictionary<string, string>();

        // FR1.3/FR5.2: each declared entry must itself be allowlisted, compatible with the node's
        // major version, and within the allowlist entry's supported version range.
        var invalidDeclarations = new List<string>();
        foreach (var (name, version) in declared)
        {
            var entry = allowlist.Find(name);
            if (entry is null)
            {
                invalidDeclarations.Add(name); // FR1.3: not allowlisted at all -- cannot be declared.
                continue;
            }

            if (!entry.IsCompatibleWithMajor(postgresMajorVersion) || !entry.IsVersionSupported(version))
                invalidDeclarations.Add(name);
        }

        // FR2.3/AC6: every Core-tier allowlist entry must be *validly* declared for the node to be
        // eligible at all -- present-by-name is not enough if the declared version/major failed the
        // structural check above (a Core extension declared for the wrong major is functionally
        // still missing).
        var missingCore = allowlist.GetEntries()
            .Where(e => e.SupportTier == ExtensionSupportTier.Core)
            .Where(e => !declared.Keys.Contains(e.Name, StringComparer.OrdinalIgnoreCase)
                || invalidDeclarations.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .ToList();

        return new NodeExtensionDeclarationResult(missingCore, invalidDeclarations);
    }
}

/// <summary>
/// Result of validating a node's extension declaration. <see cref="IsValid"/> is false only when a
/// mandatory Core extension is missing (AC6) -- <see cref="InvalidDeclaredExtensions"/> entries are
/// individually dropped from the node's placement-eligible capability record (FR5.3) rather than
/// failing the whole node, unless one of them is itself a missing/invalid Core entry.
/// </summary>
public sealed record NodeExtensionDeclarationResult(IReadOnlyList<string> MissingCoreExtensions, IReadOnlyList<string> InvalidDeclaredExtensions)
{
    public bool IsValid => MissingCoreExtensions.Count == 0;
}
