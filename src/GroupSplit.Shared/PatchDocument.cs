using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// A JSON Patch as the clients write one: the body of every <c>PATCH</c> route, which the
/// API reads with <c>Microsoft.AspNetCore.JsonPatch.SystemTextJson</c>.
/// </summary>
/// <remarks>
/// The clients cannot use that package themselves. Every released build of it declares a
/// framework reference on <c>Microsoft.AspNetCore.App</c>, and its document type implements
/// an ASP.NET Core endpoint interface: the WebAssembly client has no runtime pack to satisfy
/// the first and no assembly to load the second, and the CLI would need the ASP.NET Core
/// shared runtime just to start. They used to pin the last preview that shipped as a plain
/// library, which left the server and its clients on different builds of the one library
/// that defines the wire format.
/// <para>
/// So the clients write the format here instead, and only the part of it they use: a
/// <c>replace</c> of a top-level member. The output is byte for byte what the package
/// writes for the same calls -- the pointer spelled as the member is, PascalCase, and every
/// operation ending in <c>"from":null</c> -- which <c>ClientPatchWireFormatTest</c> checks
/// against the package the API runs, so the two cannot drift apart unnoticed.
/// </para>
/// <para>
/// The generated clients name this type for every <c>JsonPatchDocumentOf…</c> parameter;
/// see the client templates beside this file.
/// </para>
/// </remarks>
[JsonConverter(typeof(PatchDocumentConverterFactory))]
public sealed class PatchDocument<TModel> where TModel : class
{
    private readonly List<PatchOperation> _operations = [];

    public IReadOnlyList<PatchOperation> Operations => _operations;

    /// <summary>Sets <paramref name="member"/> to <paramref name="value"/>, null included.</summary>
    public PatchDocument<TModel> Replace<TProperty>(Expression<Func<TModel, TProperty>> member, TProperty value)
    {
        _operations.Add(new PatchOperation("replace", PointerTo(member), value));
        return this;
    }

    /// <summary>
    /// The JSON Pointer to a member of the model: its <c>[JsonPropertyName]</c> if it has
    /// one and its own name otherwise, which is how the package names it.
    /// </summary>
    private static string PointerTo(LambdaExpression member)
    {
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } conversion
            ? conversion.Operand
            : member.Body;

        if (body is not MemberExpression { Expression: ParameterExpression } access)
            throw new ArgumentException(
                $"A patch addresses a member of {typeof(TModel).Name} directly, as in x => x.Name; got {member}.",
                nameof(member));

        var name = access.Member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                   ?? access.Member.Name;

        return "/" + name;
    }
}

/// <summary>One operation of a <see cref="PatchDocument{TModel}"/>.</summary>
/// <param name="Op">The operation, as RFC 6902 spells it: <c>replace</c>.</param>
/// <param name="Path">The JSON Pointer it addresses, leading slash and all.</param>
/// <param name="Value">What the member becomes, written with the client's serializer settings.</param>
public sealed record PatchOperation(string Op, string Path, object? Value);

/// <summary>
/// Writes a <see cref="PatchDocument{TModel}"/> as the array of operations RFC 6902 calls a
/// patch, in the member order the server's library writes: value, path, op, from.
/// </summary>
/// <remarks>
/// Write-only. Patches go from the clients to the API, which reads them with its own
/// library; nothing on this side ever has one to read.
/// </remarks>
internal sealed class PatchDocumentConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PatchDocument<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;

    private sealed class Converter<TModel> : JsonConverter<PatchDocument<TModel>> where TModel : class
    {
        public override PatchDocument<TModel> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) =>
            throw new NotSupportedException("A patch is written by the clients and read only by the API.");

        public override void Write(Utf8JsonWriter writer, PatchDocument<TModel> patch, JsonSerializerOptions options)
        {
            writer.WriteStartArray();

            foreach (var operation in patch.Operations)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                // As object, so the value is written as what it is at run time -- a list of
                // shares as its elements -- and with the caller's settings, camelCase and all.
                JsonSerializer.Serialize<object?>(writer, operation.Value, options);
                writer.WriteString("path", operation.Path);
                writer.WriteString("op", operation.Op);
                writer.WriteNull("from");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }
}
