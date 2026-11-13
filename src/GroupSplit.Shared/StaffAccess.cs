using System.Security.Claims;
using System.Text.Json;

namespace GroupSplit.Shared;

/// <summary>Only the application's explicit Keycloak realm role grants staff access.</summary>
public static class StaffAccess
{
    public const string Role = "groupsplit-staff";

    public static bool IsStaff(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (user.IsInRole(Role))
        {
            return true;
        }

        foreach (var claim in user.FindAll("realm_access"))
        {
            try
            {
                using var json = JsonDocument.Parse(claim.Value);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("roles", out var roles)
                    && roles.ValueKind == JsonValueKind.Array
                    && roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == Role))
                {
                    return true;
                }
            }
            catch (JsonException) { }
        }
        return false;
    }
}

public sealed record StaffAccessResponse(bool IsStaff);
