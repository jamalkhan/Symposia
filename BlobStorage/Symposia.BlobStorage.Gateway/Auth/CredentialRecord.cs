namespace Symposia.BlobStorage.Gateway.Auth;

public sealed class CredentialRecord
{
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string TenantId { get; set; } = "";
}
