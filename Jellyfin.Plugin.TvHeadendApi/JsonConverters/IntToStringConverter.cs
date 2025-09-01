using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.JsonConverters;

/// <summary>
/// A custom converter for deserializing JSON properties that can be either an integer or a string into a C# string.
/// </summary>
public class IntToStringConverter : JsonConverter<string>
{
    /// <summary>
    /// Reads and converts the JSON value.
    /// </summary>
    /// <param name="reader">The reader to read from.</param>
    /// <param name="typeToConvert">The type of the object to convert.</param>
    /// <param name="options">An object that specifies the serialization options to use.</param>
    /// <returns>The converted string value.</returns>
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // If the JSON token is a number, read it as a long and convert it to a string.
            return reader.GetInt64().ToString(CultureInfo.InvariantCulture);
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            // If the JSON token is a string, read the string directly.
            return reader.GetString() ?? string.Empty;
        }
        else
        {
            throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }

    /// <summary>
    /// Writes a specified string value as JSON.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The value to convert to JSON.</param>
    /// <param name="options">An object that specifies the serialization options to use.</param>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        // When serializing, simply write the string value.
        writer.WriteStringValue(value);
    }
}