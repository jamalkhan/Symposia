using System.Numerics;
using System.Text.Json;
using Symposia.NodeHost.Ledger;
using Symposia.NodeHost.Ui;

namespace Symposia.NodeHost.Tests;

/// <summary>The UI API's JSON settings (issue #202).</summary>
public sealed class ApiJsonTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        UiApp.ConfigureJson(options);
        return options;
    }

    [Fact]
    public void LedgerEntry_AmountWei_IsALosslessDecimalString()
    {
        var wei = -BigInteger.Pow(2, 255) + 7; // beyond double and long
        var entry = new LedgerEntry(1, 0, "0x01", 0, 3, "Storage", LedgerCategory.Penalty, "Slash", wei, "");

        var json = JsonDocument.Parse(JsonSerializer.Serialize(entry, Options())).RootElement;

        Assert.Equal(JsonValueKind.String, json.GetProperty("amountWei").ValueKind);
        Assert.Equal(wei, BigInteger.Parse(json.GetProperty("amountWei").GetString()!));
        Assert.Equal("Penalty", json.GetProperty("category").GetString());
    }

    [Fact]
    public void BigInteger_RoundTrips()
    {
        var value = BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935");
        Assert.Equal(value, JsonSerializer.Deserialize<BigInteger>(JsonSerializer.Serialize(value, Options()), Options()));
    }
}
