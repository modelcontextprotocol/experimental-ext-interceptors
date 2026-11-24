using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ModelContextProtocol.Interceptors.Protocol;

// An InterceptorExecutionResult is the natural thing to write to an audit log. A mutator's result carries the
// payload as that mutator left it, which is pre-redaction content when a later mutator redacts, and even when
// the operation was rejected. These converters write interceptor results without `payload`; everything else,
// including the `modified` flag, is kept. Reading is unchanged. The `interceptor/invoke` wire result does not
// use them.

/// <summary>Writes an <see cref="InterceptorResult"/> without a mutation payload; reads it normally.</summary>
internal sealed class PayloadFreeInterceptorResultConverter : JsonConverter<InterceptorResult>
{
    public override InterceptorResult? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(ref reader, PayloadFreeResults.TypeInfo(options));

    public override void Write(Utf8JsonWriter writer, InterceptorResult value, JsonSerializerOptions options) =>
        PayloadFreeResults.Write(writer, value, options);
}

/// <summary>Writes a list of <see cref="InterceptorResult"/> without mutation payloads; reads it normally.</summary>
internal sealed class PayloadFreeInterceptorResultListConverter : JsonConverter<IList<InterceptorResult>>
{
    public override IList<InterceptorResult>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of interceptor results, got '{reader.TokenType}'.");
        }

        var typeInfo = PayloadFreeResults.TypeInfo(options);
        var results = new List<InterceptorResult>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            results.Add(JsonSerializer.Deserialize(ref reader, typeInfo)
                ?? throw new JsonException("An interceptor result was null."));
        }

        return results;
    }

    public override void Write(Utf8JsonWriter writer, IList<InterceptorResult> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var result in value)
        {
            PayloadFreeResults.Write(writer, result, options);
        }

        writer.WriteEndArray();
    }
}

internal static class PayloadFreeResults
{
    internal static JsonTypeInfo<InterceptorResult> TypeInfo(JsonSerializerOptions options) =>
        (JsonTypeInfo<InterceptorResult>)options.GetTypeInfo(typeof(InterceptorResult));

    internal static void Write(Utf8JsonWriter writer, InterceptorResult value, JsonSerializerOptions options)
    {
        var typeInfo = TypeInfo(options);
        if (value is not MutationInterceptorResult { Payload: not null })
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
            return;
        }

        var node = JsonSerializer.SerializeToNode(value, typeInfo)!.AsObject();
        node.Remove("payload");
        node.WriteTo(writer, options);
    }
}
