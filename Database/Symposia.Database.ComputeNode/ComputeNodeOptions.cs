namespace Symposia.Database.ComputeNode;

/// <summary>
/// Startup configuration for symposia-computed (Requirements/Database/postgres-architecture.md, FR7).
/// Loaded once at daemon start; not hot-reloaded.
/// </summary>
public sealed class ComputeNodeOptions
{
    public string DataRoot { get; set; } = "data/compute";

    public string NodeIdentityKeyPath { get; set; } = "data/compute/node-identity.pem";

    /// <summary>Path to the vendored pageserver binary (Neon, Apache 2.0).</summary>
    public string PageserverExecutablePath { get; set; } = "pageserver";

    /// <summary>Path to the vendored safekeeper binary (Neon, Apache 2.0).</summary>
    public string SafekeeperExecutablePath { get; set; } = "safekeeper";

    /// <summary>Path to the Neon-patched Postgres binary loading neon.so via shared_preload_libraries.</summary>
    public string PostgresExecutablePath { get; set; } = "postgres";

    /// <summary>Single supported Postgres major version this daemon will start (policy owned by #103).</summary>
    public int SupportedPostgresMajorVersion { get; set; } = 16;

    /// <summary>Extensions this node advertises as installed (policy owned by #103).</summary>
    public string[] InstalledExtensions { get; set; } = [];

    /// <summary>Upper bound on simultaneously hosted tenant databases on this node (per #90 capacity limits).</summary>
    public int MaxHostedDatabases { get; set; } = 8;

    /// <summary>Detection window for a process failing to report healthy (FR4/FR6).</summary>
    public int CrashDetectionWindowSeconds { get; set; } = 10;

    /// <summary>Base delay for exponential backoff between restart attempts.</summary>
    public int RestartBackoffBaseSeconds { get; set; } = 2;

    /// <summary>Consecutive restarts within the crash-loop window before the process (and node) is marked unhealthy instead of retried again.</summary>
    public int MaxRestartAttempts { get; set; } = 5;

    /// <summary>Window in which MaxRestartAttempts consecutive crashes are counted as a crash loop.</summary>
    public int CrashLoopWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Total sustained-measurement window for POST /benchmark/run (issue #89, FR9). Deliberately
    /// long enough to outlast a burstable/shared-vCPU credit window rather than being gameable by
    /// a short instantaneous sample -- see BenchmarkBurstDiscardSeconds.
    /// </summary>
    public int BenchmarkSustainedWindowSeconds { get; set; } = 90;

    /// <summary>
    /// Leading portion of BenchmarkSustainedWindowSeconds discarded before averaging (FR9's
    /// burst-absorption window); only samples after this point count toward the reported rate.
    /// </summary>
    public int BenchmarkBurstDiscardSeconds { get; set; } = 30;

    /// <summary>Interval between samples taken during the sustained benchmark window.</summary>
    public int BenchmarkSampleIntervalSeconds { get; set; } = 5;
}
