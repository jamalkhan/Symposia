namespace Symposia.Identity.Domain;

/// <summary>
/// The Symposia marketer permission model
/// (Requirements/Identity/user-data-ownership.md § Marketer Permission Types).
/// Ordinal values must stay in sync with the <c>Permission</c> enum in
/// <c>Blockchain/bootstrap-chain/src/ConsentRegistry.sol</c> — the chain is
/// the authoritative source of truth and encodes permissions as this same
/// uint8 ordinal.
/// </summary>
public enum Permission : byte
{
    EmailMarketing = 0,
    EmailTransactional = 1,
    SmsMarketing = 2,
    WebTrackingBrand = 3,
    WebTrackingNetwork = 4,
    DataRead = 5,
    DataEnrichment = 6,
}

public static class PermissionExtensions
{
    public static string ToWireName(this Permission permission) => permission switch
    {
        Permission.EmailMarketing => "email_marketing",
        Permission.EmailTransactional => "email_transactional",
        Permission.SmsMarketing => "sms_marketing",
        Permission.WebTrackingBrand => "web_tracking_brand",
        Permission.WebTrackingNetwork => "web_tracking_network",
        Permission.DataRead => "data_read",
        Permission.DataEnrichment => "data_enrichment",
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    public static Permission ParseWireName(string value) => value switch
    {
        "email_marketing" => Permission.EmailMarketing,
        "email_transactional" => Permission.EmailTransactional,
        "sms_marketing" => Permission.SmsMarketing,
        "web_tracking_brand" => Permission.WebTrackingBrand,
        "web_tracking_network" => Permission.WebTrackingNetwork,
        "data_read" => Permission.DataRead,
        "data_enrichment" => Permission.DataEnrichment,
        _ => throw new FormatException($"Unrecognized permission '{value}'"),
    };
}
