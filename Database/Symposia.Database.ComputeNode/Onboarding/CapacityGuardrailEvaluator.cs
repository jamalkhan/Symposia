namespace Symposia.Database.ComputeNode.Onboarding;

/// <summary>
/// The over-subscription guardrail an operator's declared capacity limits are checked against
/// (Requirements/Database/compute-nodes.md): total allocated vCPU should not exceed 80% of physical
/// cores, and total allocated RAM should not exceed 85% of available RAM. Per issue #90 FR5/AC4 this
/// is advisory -- a warning, not a rejection -- so the result only ever informs the operator; it never
/// blocks onboarding by itself.
/// </summary>
public static class CapacityGuardrailEvaluator
{
    public const int VcpuGuardrailPercent = 80;
    public const int RamGuardrailPercent = 85;

    public static CapacityGuardrailResult Evaluate(int declaredMaxVcpu, int physicalCores, int declaredMaxRamMB, int physicalRamMB)
    {
        var vcpuOverGuardrail = ExceedsGuardrail(declaredMaxVcpu, physicalCores, VcpuGuardrailPercent);
        var ramOverGuardrail = ExceedsGuardrail(declaredMaxRamMB, physicalRamMB, RamGuardrailPercent);

        return new CapacityGuardrailResult(vcpuOverGuardrail, ramOverGuardrail);
    }

    /// <summary>
    /// True when `declared` exceeds `guardrailPercent` of `physical`. The boundary itself
    /// (exactly at the guardrail percentage) does not trigger a warning -- the spec's guardrail
    /// language ("<= 80%") is inclusive of the threshold.
    /// </summary>
    private static bool ExceedsGuardrail(int declared, int physical, int guardrailPercent)
    {
        if (physical <= 0)
        {
            return declared > 0;
        }

        // Integer-safe check for declared > physical * guardrailPercent / 100
        // without truncation error from dividing first.
        return (long)declared * 100 > (long)physical * guardrailPercent;
    }
}

/// <summary>
/// Result of evaluating a capacity declaration against the over-subscription guardrails.
/// <see cref="RequiresAcknowledgement"/> is true if either dimension exceeds its guardrail --
/// onboarding should surface a warning and require the operator to explicitly confirm before
/// the declaration proceeds, per the Gherkin scenario "warns... but allows them to proceed if they confirm".
/// </summary>
public sealed record CapacityGuardrailResult(bool VcpuOverGuardrail, bool RamOverGuardrail)
{
    public bool RequiresAcknowledgement => VcpuOverGuardrail || RamOverGuardrail;
}
