using System.Diagnostics;

namespace NativeSmtpReceiver;

internal static class StatusPayloads
{
    public static StatusPayload Create()
    {
        using var process = Process.GetCurrentProcess();
        return new StatusPayload(
            new StatusAppMemory(process.WorkingSet64, process.PrivateMemorySize64),
            new StatusAppCpu((long)process.TotalProcessorTime.TotalMilliseconds));
    }
}

internal sealed record StatusPayload(
    StatusAppMemory appMemory,
    StatusAppCpu appCpu);

internal sealed record StatusAppMemory(
    long workingSetBytes,
    long privateMemoryBytes);

internal sealed record StatusAppCpu(
    long totalProcessorTimeMs);
