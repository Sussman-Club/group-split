using System.Text.Json;

namespace GroupSplit.Shared;

/// <summary>
/// The settings every generated client reads and writes with, and the few places outside one
/// that read part of a response by hand -- the extension members of a problem.
/// <para>
/// It lives here, beside the DTOs, rather than in either client, because the NSwag templates
/// name it in <c>JsonSerializerSettingsTransformationMethod</c>: a copy per client would mean
/// a template per client, and those templates are patches against NSwag internals rather than
/// configuration. One class here keeps one set of templates for both.
/// </para>
/// </summary>
public static class GroupSplitSerializer
{
    public static readonly JsonSerializerOptions Options = Transform(new JsonSerializerOptions());

    public static JsonSerializerOptions Transform(JsonSerializerOptions options)
    {
        return new JsonSerializerOptions(options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
