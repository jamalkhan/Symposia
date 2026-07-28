namespace Symposia.Database.ComputeNode.Archival;

public enum UploadOutcome
{
    Success,

    /// <summary>Timeout, 5xx, connection reset -- retry with backoff, never drop the segment (FR7).</summary>
    TransientFailure,

    /// <summary>Auth failure, misconfigured bucket, quota exceeded -- needs operator escalation, not a tight retry loop.</summary>
    PermanentFailure,
}
