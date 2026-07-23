using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelContextProtocol.Interceptors.Protocol;

/// <summary>
/// Priority hint for ordering mutation interceptors. Lower values execute first; ties are broken
/// alphabetically by interceptor name. Per the SEP, a hint is either a single number applying to
/// both phases or an object with separate <c>request</c>/<c>response</c> values; an unset phase
/// value resolves to 0.
/// </summary>
[JsonConverter(typeof(Converter))]
public sealed class PriorityHint
{
    /// <summary>Creates a scalar hint applying the same priority to both phases.</summary>
    public PriorityHint(int value)
    {
        Request = value;
        Response = value;
        IsScalar = true;
    }

    /// <summary>Creates a per-phase hint. An unset phase resolves to 0.</summary>
    public PriorityHint(int? request, int? response)
    {
        Request = request;
        Response = response;
    }

    /// <summary>Gets the request-phase priority, or <see langword="null"/> if unset (resolves to 0).</summary>
    public int? Request { get; }

    /// <summary>Gets the response-phase priority, or <see langword="null"/> if unset (resolves to 0).</summary>
    public int? Response { get; }

    /// <summary>
    /// Whether this hint was created or parsed in scalar (single number) form. Controls whether it
    /// serializes back as a bare number or an object, preserving round-trip fidelity.
    /// </summary>
    internal bool IsScalar { get; }

    /// <summary>Resolves the effective priority for the given phase per the SEP algorithm.</summary>
    public int GetEffective(InterceptorPhase phase) =>
        phase == InterceptorPhase.Response ? Response ?? 0 : Request ?? 0;

    /// <summary>Converts a plain number to a scalar hint applying to both phases.</summary>
    public static implicit operator PriorityHint(int value) => new(value);

    internal sealed class Converter : JsonConverter<PriorityHint>
    {
        public override PriorityHint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return new PriorityHint(reader.GetInt32());

                case JsonTokenType.StartObject:
                    int? request = null, response = null;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var propertyName = reader.GetString();
                        reader.Read();
                        switch (propertyName)
                        {
                            case "request": request = reader.GetInt32(); break;
                            case "response": response = reader.GetInt32(); break;
                            default: reader.Skip(); break;
                        }
                    }
                    return new PriorityHint(request, response);

                default:
                    throw new JsonException($"Unexpected token '{reader.TokenType}' for priorityHint; expected a number or an object.");
            }
        }

        public override void Write(Utf8JsonWriter writer, PriorityHint value, JsonSerializerOptions options)
        {
            if (value.IsScalar)
            {
                writer.WriteNumberValue(value.Request!.Value);
                return;
            }

            writer.WriteStartObject();
            if (value.Request is int request) writer.WriteNumber("request", request);
            if (value.Response is int response) writer.WriteNumber("response", response);
            writer.WriteEndObject();
        }
    }
}
