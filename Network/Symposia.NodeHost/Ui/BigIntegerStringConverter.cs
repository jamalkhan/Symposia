using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Symposia.NodeHost.Ui;

/// <summary>
/// Writes <see cref="BigInteger"/> as a decimal string (issue #202): System.Text.Json would otherwise
/// serialize its properties (<c>isZero</c>, <c>sign</c>, ...) and lose the value, and a JSON number
/// can't carry 256-bit wei amounts losslessly through JavaScript.
/// </summary>
public sealed class BigIntegerStringConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? BigInteger.Parse(reader.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : throw new JsonException("Expected a decimal string for a BigInteger.");

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
