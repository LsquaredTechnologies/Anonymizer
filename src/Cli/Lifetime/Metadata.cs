using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anonymizer.Cli.Lifetime;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed record class Metadata(
    DateTime? InstalledAt,
    Version? Version,
    string? Source)
{
    public static async Task<Metadata> Load()
    {
        using var stream = File.OpenRead(Application.Metadata.Path);
        var metadata = await JsonSerializer.DeserializeAsync<Metadata>(stream, MetadataJsonContext.Default.Metadata);
        return metadata ?? new(null, null, string.Empty);
    }

    public async Task Save()
    {
        using var stream = File.Open(Application.Metadata.Path, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, this, MetadataJsonContext.Default.Metadata);
    }
}

[JsonSerializable(typeof(Metadata))]
[JsonSourceGenerationOptions(Converters = new[] { typeof(VersionJsonConverter) })]
internal partial class MetadataJsonContext : JsonSerializerContext
{
}

internal sealed class VersionJsonConverter : JsonConverter<Version>
{
    public override Version? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Version.TryParse(reader.GetString(), out var v) ? v : null;

    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
