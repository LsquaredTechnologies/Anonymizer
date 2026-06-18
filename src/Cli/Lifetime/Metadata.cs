using System.Runtime.Versioning;
using System.Text.Json;

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
        var metadata = await JsonSerializer.DeserializeAsync<Metadata>(stream);
        return metadata ?? new(null, null, string.Empty);
    }

    public async Task Save()
    {
        using var stream = File.Open(Application.Metadata.Path, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, this, Defaults.JsonOptions);
    }
}
