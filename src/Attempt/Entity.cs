using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Anonymizer.Extractor.PII;

public sealed class Entity
{
    public required string Label { get; set; }
    public required int Start { get; set; }
    public required int End { get; set; }
    public required string Text { get; set; } 
    public required float Score { get; set; }

    public static readonly JsonTypeInfo JsonContext = EntityJsonContext.Default.ListEntity;
}

[JsonSerializable(typeof(List<Entity>))]
[JsonSerializable(typeof(Entity))]
internal partial class EntityJsonContext : JsonSerializerContext
{
    static EntityJsonContext()
    {
        Default = new EntityJsonContext(new()
        {
            WriteIndented = true,
        });
    }
}
