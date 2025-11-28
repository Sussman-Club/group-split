using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.JsonPatch;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GroupSplit.Shared;

public class GroupSplitSerializer
{
    public static JsonSerializerOptions Transform(JsonSerializerOptions options)
    {
        return new JsonSerializerOptions(options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new NewtonsoftJsonConverterFactory<IJsonPatchDocument>(
                    new JsonSerializerSettings
                    {
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    })
            }
        };
    }

    private sealed class NewtonsoftJsonConverter<T>(JsonSerializerSettings? settings)
        : System.Text.Json.Serialization.JsonConverter<T>
    {
        private readonly JsonSerializerSettings _settings = settings ?? new JsonSerializerSettings();

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Handle JSON null
            if (reader.TokenType == JsonTokenType.Null)
            {
                return default;
            }

            // Grab the raw JSON for the current value
            using var document = JsonDocument.ParseValue(ref reader);
            var json = document.RootElement.GetRawText();

            // Let Newtonsoft do the heavy lifting
            return JsonConvert.DeserializeObject<T>(json, _settings);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            // Serialize with Newtonsoft
            var json = JsonConvert.SerializeObject(value, _settings);

            // Re-parse into STJ so we can write to Utf8JsonWriter
            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }
    }

    private sealed class NewtonsoftJsonConverterFactory<TBase>(JsonSerializerSettings? settings = null)
        : System.Text.Json.Serialization.JsonConverterFactory
    {
        private readonly JsonSerializerSettings _settings = settings ?? new JsonSerializerSettings();
        private readonly ConcurrentDictionary<Type, System.Text.Json.Serialization.JsonConverter> _converters = new();


        public override bool CanConvert(Type typeToConvert)
        {
            // Apply to any type that implements/inherits TBase
            return typeof(TBase).IsAssignableFrom(typeToConvert);
        }

        public override System.Text.Json.Serialization.JsonConverter CreateConverter(
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return _converters.GetOrAdd(typeToConvert, type =>
            {
                var converterType = typeof(NewtonsoftJsonConverter<>).MakeGenericType(type);
                return (System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(converterType,
                    _settings)!;
            });
        }
    }
}