using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Health360.Contracts.Ai;

/// <summary>
/// How Claude's structured output is read: every constructor parameter is required, non-nullable
/// properties reject null, and unknown properties are an error, so the model can't drop or add a field.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AiHelloOutput))]
public sealed partial class AiJsonContext : JsonSerializerContext;

/// <summary>JSON schemas for structured output, generated from the same types the output is parsed into.</summary>
public static class StructuredOutputSchema
{
    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (context, node) =>
        {
            // Structured outputs require every object to be closed.
            if (node is JsonObject schema && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
            {
                schema["additionalProperties"] = false;
            }

            return node;
        },
    };

    public static JsonObject For(JsonTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (JsonObject)type.GetJsonSchemaAsNode(ExporterOptions);
    }
}
