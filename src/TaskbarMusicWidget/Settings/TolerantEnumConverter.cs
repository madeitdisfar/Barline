using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarMusicWidget.Settings;

/// <summary>
/// Reads enums by name, falling back to the first member instead of throwing when the
/// name is not one this build knows.
/// </summary>
/// <remarks>
/// <para>
/// The stock converter throws on an unrecognised name, and because these enums sit
/// inside the settings file, one unknown value takes the whole file down with it —
/// every unrelated setting reverts to its default. That is a harsh penalty for a
/// setting having been renamed or, as with the acrylic background, removed.
/// </para>
/// <para>
/// Falling back means an option that no longer exists quietly becomes the first one,
/// which is the closest thing to "the default" an enum has, and everything else the
/// user configured survives.
/// </para>
/// </remarks>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value))
        {
            return value;
        }

        // Also accept a number, since that is what an older file written without a
        // string converter would contain.
        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out int number) &&
            Enum.IsDefined(typeof(T), number))
        {
            return (T)Enum.ToObject(typeof(T), number);
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Applies <see cref="TolerantEnumConverter{T}"/> to every enum.</summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsEnum;

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(type))!;
}
