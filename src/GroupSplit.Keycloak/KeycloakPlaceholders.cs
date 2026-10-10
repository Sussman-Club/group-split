using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GroupSplit.Keycloak;

/// <summary>
/// Resolves the <c>${NAME}</c> and <c>${NAME:default}</c> placeholders in the realm export
/// the way Keycloak's own import does, so a value reconciled onto an existing realm is the
/// value a fresh import would have written.
/// </summary>
/// <remarks>
/// Refuses rather than passing a placeholder through. Keycloak leaves an unresolved one in
/// place, and a redirect URI stored as <c>${WEB_HOSTNAME}/signin-oidc</c> matches no
/// request, so every sign-in would fail with nothing to say why. Failing here fails the
/// deployment instead, and names the variable that was missing.
/// </remarks>
public static partial class KeycloakPlaceholders
{
    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<default>[^}]*))?\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// A copy of <paramref name="node"/> with every placeholder in its strings resolved
    /// through <paramref name="lookup"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A placeholder names a value <paramref name="lookup"/> has not got and carries no default.
    /// </exception>
    public static JsonNode? Expand(JsonNode? node, Func<string, string?> lookup)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var expanded = Expand(node, lookup, missing);

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Keycloak realm definition refers to {string.Join(", ", missing)}, "
                + $"which {(missing.Count == 1 ? "is" : "are")} not set. "
                + "Give the realm worker the same values the Keycloak container gets.");
        }

        return expanded;
    }

    /// <summary>Whether <paramref name="node"/> is a string that holds a placeholder.</summary>
    public static bool IsPlaceholder(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && Placeholder().IsMatch(text);

    private static JsonNode? Expand(JsonNode? node, Func<string, string?> lookup, ISet<string> missing) =>
        node switch
        {
            JsonObject obj => new JsonObject(obj.Select(property =>
                KeyValuePair.Create(property.Key, Expand(property.Value, lookup, missing)))),
            JsonArray array => new JsonArray(array.Select(item => Expand(item, lookup, missing)).ToArray()),
            JsonValue value when value.TryGetValue<string>(out var text) =>
                JsonValue.Create(Placeholder().Replace(text, match =>
                {
                    var name = match.Groups["name"].Value;

                    if (lookup(name) is { Length: > 0 } resolved) return resolved;
                    if (match.Groups["default"].Success) return match.Groups["default"].Value;

                    missing.Add(name);
                    return match.Value;
                })),
            _ => node?.DeepClone()
        };
}
