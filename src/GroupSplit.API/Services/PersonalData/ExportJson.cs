using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GroupSplit.API.Services.PersonalData;

internal static class ExportJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new DecimalStringConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class DecimalStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    internal static JsonNode Schema()
    {
        var schema = Options.GetJsonSchemaAsNode(typeof(ExportRecords), new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, node) => context.TypeInfo.Type == typeof(decimal)
                ? new JsonObject { ["type"] = "string", ["pattern"] = @"^-?[0-9]+(\.[0-9]+)?$" }
                : context.TypeInfo.Type == typeof(decimal?)
                    ? new JsonObject { ["type"] = new JsonArray("string", "null"), ["pattern"] = @"^-?[0-9]+(\.[0-9]+)?$" } : node
        });
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return schema;
    }
}
