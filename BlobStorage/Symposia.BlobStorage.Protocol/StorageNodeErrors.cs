namespace Symposia.BlobStorage.Protocol;

/// <summary>
/// Wire contract for storage-node refusals the gateway must recognise (issue #199): a write refused
/// for disk space fails with <c>ResourceExhausted</c> and carries <see cref="ReasonTrailer"/> =
/// <see cref="ReadOnly"/>, so it isn't confused with other ResourceExhausted errors (message size, limits).
/// </summary>
public static class StorageNodeErrors
{
    public const string ReasonTrailer = "symposia-reason";

    public const string ReadOnly = "read-only";
}
