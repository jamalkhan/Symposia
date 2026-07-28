using Symposia.Database.ComputeNode.Onboarding;

namespace Symposia.Database.ComputeNode.Tests.Onboarding;

/// <summary>
/// Traces to the QA test plan's capacity-guardrail section (cases 17-25): 80% vCPU / 85% RAM
/// over-subscription guardrails are advisory (warn, never reject), boundaries are inclusive.
/// </summary>
public sealed class CapacityGuardrailEvaluatorTests
{
    [Fact]
    public void Evaluate_WithinBothGuardrails_DoesNotRequireAcknowledgement()
    {
        // 16 physical cores, declaring 12 vCPU (75%); 100GB RAM, declaring 80GB (80%).
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 12, physicalCores: 16, declaredMaxRamMB: 80_000, physicalRamMB: 100_000);

        Assert.False(result.VcpuOverGuardrail);
        Assert.False(result.RamOverGuardrail);
        Assert.False(result.RequiresAcknowledgement);
    }

    [Fact]
    public void Evaluate_VcpuExactlyAtEightyPercentBoundary_DoesNotWarn()
    {
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 12800, physicalCores: 16000, declaredMaxRamMB: 1, physicalRamMB: 1_000_000);

        Assert.False(result.VcpuOverGuardrail);
    }

    [Fact]
    public void Evaluate_VcpuJustOverEightyPercent_Warns()
    {
        // 16 cores * 80% = 12.8 -> 13 vCPU is just over.
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 13, physicalCores: 16, declaredMaxRamMB: 1, physicalRamMB: 1_000_000);

        Assert.True(result.VcpuOverGuardrail);
        Assert.True(result.RequiresAcknowledgement);
    }

    [Fact]
    public void Evaluate_RamExactlyAtEightyFivePercentBoundary_DoesNotWarn()
    {
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 1, physicalCores: 1_000_000, declaredMaxRamMB: 85_000, physicalRamMB: 100_000);

        Assert.False(result.RamOverGuardrail);
    }

    [Fact]
    public void Evaluate_RamJustOverEightyFivePercent_Warns()
    {
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 1, physicalCores: 1_000_000, declaredMaxRamMB: 86_000, physicalRamMB: 100_000);

        Assert.True(result.RamOverGuardrail);
        Assert.True(result.RequiresAcknowledgement);
    }

    [Fact]
    public void Evaluate_BothOverGuardrail_RequiresAcknowledgement()
    {
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 32, physicalCores: 16, declaredMaxRamMB: 200_000, physicalRamMB: 100_000);

        Assert.True(result.VcpuOverGuardrail);
        Assert.True(result.RamOverGuardrail);
        Assert.True(result.RequiresAcknowledgement);
    }

    [Fact]
    public void Evaluate_DeclaringCapacityWithNoPhysicalResource_Warns()
    {
        var result = CapacityGuardrailEvaluator.Evaluate(declaredMaxVcpu: 1, physicalCores: 0, declaredMaxRamMB: 0, physicalRamMB: 0);

        Assert.True(result.VcpuOverGuardrail);
        Assert.False(result.RamOverGuardrail);
    }
}
