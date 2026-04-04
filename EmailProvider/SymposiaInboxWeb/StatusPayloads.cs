using System.Diagnostics;

namespace InboxWeb;

internal static class StatusPayloads
{
    public static object Create()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            appMemory = new
            {
                workingSetBytes = process.WorkingSet64,
                privateMemoryBytes = process.PrivateMemorySize64
            },
            appCpu = new
            {
                totalProcessorTimeMs = (long)process.TotalProcessorTime.TotalMilliseconds
            }
        };
    }
}
