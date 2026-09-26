using System.Text.Json.Nodes;
using GroupSplit.Keycloak;

namespace GroupSplit.Seeder.Test.Realm;

public sealed class KeycloakPlaceholdersTests
{
    [Fact]
    public void Placeholders_are_resolved_inside_strings_arrays_and_nested_objects()
    {
        var node = JsonNode.Parse("""
            { "uris": ["${ORIGIN}/a", "${ORIGIN}/b"], "attributes": { "x": "${ORIGIN}" }, "count": 3, "on": true }
            """);

        var expanded = KeycloakPlaceholders.Expand(node, name => name == "ORIGIN" ? "https://app" : null)!;

        Assert.Equal("https://app/a", expanded["uris"]![0]!.GetValue<string>());
        Assert.Equal("https://app/b", expanded["uris"]![1]!.GetValue<string>());
        Assert.Equal("https://app", expanded["attributes"]!["x"]!.GetValue<string>());
        Assert.Equal(3, expanded["count"]!.GetValue<int>());
        Assert.True(expanded["on"]!.GetValue<bool>());
    }

    [Fact]
    public void A_default_is_used_when_the_variable_is_unset_or_empty()
    {
        var node = JsonValue.Create("${PORT:587}");

        Assert.Equal("587", KeycloakPlaceholders.Expand(node, _ => null)!.GetValue<string>());
        Assert.Equal("587", KeycloakPlaceholders.Expand(node, _ => "")!.GetValue<string>());
        Assert.Equal("25", KeycloakPlaceholders.Expand(node, _ => "25")!.GetValue<string>());
    }

    [Fact]
    public void Every_missing_variable_is_named()
    {
        var node = JsonNode.Parse("""["${B}", "${A}", "${A}"]""");

        var failure = Assert.Throws<InvalidOperationException>(() => KeycloakPlaceholders.Expand(node, _ => null));

        Assert.Contains("A, B", failure.Message);
    }

    [Fact]
    public void The_input_is_left_untouched()
    {
        var node = JsonNode.Parse("""{ "uri": "${ORIGIN}" }""")!;

        KeycloakPlaceholders.Expand(node, _ => "https://app");

        Assert.Equal("${ORIGIN}", node["uri"]!.GetValue<string>());
    }
}
