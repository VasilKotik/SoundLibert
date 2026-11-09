using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube;

/// <summary>
/// YouTube InnerTube responses are inconsistent: the same numeric field
/// (contentLength, audioSampleRate, ...) arrives as a JSON number for some
/// clients and as a JSON string for others (verified: ANDROID sends strings).
/// These converters accept both forms instead of throwing JsonException.
/// </summary>
public class LenientLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var n) ? n : (long?)reader.GetDouble(),
            JsonTokenType.String => long.TryParse(reader.GetString(), out var s) ? s : null,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

public class LenientIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var n) ? n : (int?)reader.GetDouble(),
            JsonTokenType.String => int.TryParse(reader.GetString(), out var s) ? s : null,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

public class LenientDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetDouble(out var n) ? n : null,
            JsonTokenType.String => double.TryParse(
                reader.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var s) ? s : null,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
