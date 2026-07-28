using Symposia.Identity.Domain;

namespace Symposia.Identity.Gateway.Tests;

public class PermissionTests
{
    // TC-3.6: all 7 permission types round-trip through the wire-name mapping.
    [Theory]
    [InlineData(Permission.EmailMarketing, "email_marketing")]
    [InlineData(Permission.EmailTransactional, "email_transactional")]
    [InlineData(Permission.SmsMarketing, "sms_marketing")]
    [InlineData(Permission.WebTrackingBrand, "web_tracking_brand")]
    [InlineData(Permission.WebTrackingNetwork, "web_tracking_network")]
    [InlineData(Permission.DataRead, "data_read")]
    [InlineData(Permission.DataEnrichment, "data_enrichment")]
    public void WireName_RoundTrips(Permission permission, string wireName)
    {
        Assert.Equal(wireName, permission.ToWireName());
        Assert.Equal(permission, PermissionExtensions.ParseWireName(wireName));
    }

    // TC-3.7: an unrecognized permission string is rejected, not silently accepted.
    [Fact]
    public void ParseWireName_UnknownValue_Throws()
    {
        Assert.Throws<FormatException>(() => PermissionExtensions.ParseWireName("not_a_permission"));
    }
}
