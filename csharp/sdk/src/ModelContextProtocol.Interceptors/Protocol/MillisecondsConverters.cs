using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

// SEP-2624 types millisecond fields as `number`. This SDK writes integers, but reads any JSON number
// (rounding fractions), so that it interoperates with SDKs that send e.g. `5000.0` or `0.0072`
// (experimental-ext-interceptors#49). A positive value never rounds to zero: `timeoutMs: 0.1` is a valid
// timeout, and zero is not.

/// <summary>Reads any JSON number into an <see cref="int"/>, rounding fractions; writes an integer.</summary>
internal sealed class LenientInt32MillisecondsConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
        {
            return value;
        }

        return checked((int)LenientMilliseconds.Round(LenientMilliseconds.ReadDouble(ref reader)));
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is int v) writer.WriteNumberValue(v); else writer.WriteNullValue();
    }
}

/// <summary>Reads any JSON number into a <see cref="long"/>, rounding fractions; writes an integer.</summary>
internal sealed class LenientInt64MillisecondsConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value))
        {
            return value;
        }

        return checked((long)LenientMilliseconds.Round(LenientMilliseconds.ReadDouble(ref reader)));
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is long v) writer.WriteNumberValue(v); else writer.WriteNullValue();
    }
}

internal static class LenientMilliseconds
{
    /// <summary>Rounds to the nearest whole millisecond, but never rounds a positive value down to zero.</summary>
    internal static double Round(double milliseconds)
    {
        var rounded = Math.Round(milliseconds, MidpointRounding.AwayFromZero);
        return rounded == 0 && milliseconds > 0 ? 1 : rounded;
    }

    internal static double ReadDouble(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetDouble(),
        JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) => parsed,
        _ => throw new JsonException($"Expected a number of milliseconds, got '{reader.TokenType}'."),
    };
}
