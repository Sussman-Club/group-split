using System.Text.Json;
using GroupSplit.Cli.Api;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;
using SharedErrorCodes = GroupSplit.Shared.Errors.ErrorCodes;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The API already publishes a stable error-code contract. These pin that the CLI passes
/// it through rather than inventing a second vocabulary for the same conditions.
/// </summary>
public sealed class ApiErrorMapperTests
{
    [Fact]
    public void The_problem_is_read_from_the_typed_exception_when_the_body_was_not_buffered()
    {
        // The path every declared status actually takes: the generated client deserializes
        // the body into Result and leaves Response null, having streamed rather than
        // buffered it. Reading only Response loses the code, the detail and the traceId.
        var mapped = ApiErrorMapper.Map(new ApiException<ProblemDetails>(
            "Not Found",
            404,
            response: null,
            Headers,
            new ProblemDetails
            {
                Title = "Not found",
                Detail = "No group with that id.",
                Status = 404,
                Extensions = new Dictionary<string, JsonElement>
                {
                    ["code"] = Element(SharedErrorCodes.GroupNotFound),
                    ["traceId"] = Element("00-abc-def-01")
                }
            },
            null));

        Assert.Equal(SharedErrorCodes.GroupNotFound, mapped.Error.Code);
        Assert.Equal("No group with that id.", mapped.Error.Message);
        Assert.Equal("00-abc-def-01", mapped.Error.TraceId);
        Assert.Equal(ExitCodes.InvalidInput, mapped.ExitCode);
    }

    [Fact]
    public void Validation_details_are_read_from_the_typed_exception_too()
    {
        var mapped = ApiErrorMapper.Map(new ApiException<HttpValidationProblemDetails>(
            "Bad Request",
            400,
            response: null,
            Headers,
            new HttpValidationProblemDetails
            {
                Title = "Validation failed",
                Errors = new Dictionary<string, string[]> { ["Name"] = ["Name is required."] }
            },
            null));

        Assert.Contains("Name: Name is required.", mapped.Error.Details!);
    }

    [Fact]
    public void The_api_error_code_is_passed_through_unchanged()
    {
        var mapped = ApiErrorMapper.Map(Exception(404, new
        {
            title = "Not found",
            detail = "No group with that id.",
            code = SharedErrorCodes.GroupNotFound,
            traceId = "00-abc-def-01"
        }));

        Assert.Equal(SharedErrorCodes.GroupNotFound, mapped.Error.Code);
        Assert.Equal("No group with that id.", mapped.Error.Message);
        Assert.Equal("00-abc-def-01", mapped.Error.TraceId);
    }

    [Theory]
    [InlineData(401, ExitCodes.AuthRequired)]
    [InlineData(403, ExitCodes.AuthRequired)]
    [InlineData(400, ExitCodes.InvalidInput)]
    [InlineData(404, ExitCodes.InvalidInput)]
    [InlineData(409, ExitCodes.InvalidInput)]
    [InlineData(500, ExitCodes.Error)]
    [InlineData(503, ExitCodes.Error)]
    public void Status_codes_map_to_the_documented_exit_codes(int status, int expected)
    {
        Assert.Equal(expected, ApiErrorMapper.Map(Exception(status, new { title = "x" })).ExitCode);
    }

    [Fact]
    public void A_401_tells_the_caller_how_to_recover()
    {
        Assert.Contains("auth login", ApiErrorMapper.Map(Exception(401, new { title = "x" })).Error.Remediation);
    }

    /// <summary>
    /// Removing yourself is a 403, but nothing is wrong with the account: the way out of a
    /// group is leaving it, and that is what the caller is pointed at.
    /// </summary>
    [Fact]
    public void Removing_yourself_points_at_leaving_rather_than_at_auth()
    {
        var remediation = ApiErrorMapper.Map(Exception(403, new
        {
            title = "Forbidden",
            code = SharedErrorCodes.GroupCannotRemoveSelf
        })).Error.Remediation;

        Assert.Contains("groups leave", remediation);
        Assert.DoesNotContain("auth status", remediation);
    }

    /// <summary>
    /// One code answers a stale join link and a stale invitation alike, so the way back
    /// names the command for each rather than assuming the join link.
    /// </summary>
    [Fact]
    public void A_stale_link_names_both_ways_back_in()
    {
        var remediation = ApiErrorMapper.Map(Exception(409, new
        {
            title = "Conflict",
            code = SharedErrorCodes.GroupRejoinNeedsNewInvitation
        })).Error.Remediation;

        Assert.Contains("invitations join", remediation);
        Assert.Contains("invitations claim", remediation);
    }

    /// <summary>
    /// The same refusal answers leaving and removing, so the hint says how a balance gets
    /// squared without assuming whose it is -- "square them up" would be wrong for somebody
    /// leaving.
    /// </summary>
    [Fact]
    public void A_balance_that_is_not_zero_says_how_to_record_a_repayment_for_either_case()
    {
        var remediation = ApiErrorMapper.Map(Exception(409, new
        {
            title = "Conflict",
            code = SharedErrorCodes.GroupMemberNotSettled
        })).Error.Remediation;

        Assert.Contains("groups balances", remediation);
        Assert.Contains("groups settle", remediation);
        Assert.Contains("settle-between", remediation);
        Assert.DoesNotContain("them", remediation);
    }

    /// <summary>
    /// Somebody recreated in Keycloak: the server refuses the sign-in itself, on every
    /// command. A 409, but not something the arguments can fix, so it exits as an identity
    /// problem -- and says that signing in again as the same person is not the way out, the
    /// one thing an agent seeing exit code 2 would otherwise try first.
    /// </summary>
    [Fact]
    public void A_sign_in_whose_email_another_account_holds_is_an_identity_problem_that_names_support()
    {
        var mapped = ApiErrorMapper.Map(Exception(409, new
        {
            title = "Conflict",
            detail = "A detail for developers.",
            code = SharedErrorCodes.IdentityEmailInUse,
            traceId = "00-abc-def-01"
        }));

        Assert.Equal(SharedErrorCodes.IdentityEmailInUse, mapped.Error.Code);
        Assert.Equal(ExitCodes.AuthRequired, mapped.ExitCode);
        Assert.Equal(ApiErrorMapper.IdentityEmailInUseMessage, mapped.Error.Message);
        Assert.Equal("00-abc-def-01", mapped.Error.TraceId);
        Assert.Contains("will not help", mapped.Error.Remediation);
        Assert.Contains("support", mapped.Error.Remediation);
    }

    [Fact]
    public void Losing_a_race_over_a_guest_says_to_look_again()
    {
        var remediation = ApiErrorMapper.Map(Exception(409, new
        {
            title = "Conflict",
            code = SharedErrorCodes.GroupGuestChanged
        })).Error.Remediation;

        Assert.Contains("groups guests", remediation);
    }

    [Fact]
    public void A_guest_route_given_the_wrong_id_points_at_the_guest_listing()
    {
        var remediation = ApiErrorMapper.Map(Exception(404, new
        {
            title = "Not Found",
            code = SharedErrorCodes.GroupGuestNotFound
        })).Error.Remediation;

        Assert.Contains("groups guests", remediation);
    }

    [Fact]
    public void Validation_messages_are_flattened_into_details()
    {
        var mapped = ApiErrorMapper.Map(Exception(400, new
        {
            title = "Validation failed",
            code = SharedErrorCodes.ValidationFailed,
            errors = new Dictionary<string, string[]>
            {
                ["Name"] = ["Name is required."],
                ["Amount"] = ["Amount must be greater than 0."]
            }
        }));

        Assert.Contains("Name: Name is required.", mapped.Error.Details!);
        Assert.Contains("Amount: Amount must be greater than 0.", mapped.Error.Details!);
    }

    [Fact]
    public void A_response_that_is_not_problem_details_still_produces_a_usable_error()
    {
        // A proxy in front of the API can answer with HTML; falling over on that would
        // hide the real problem behind a parse failure.
        var mapped = ApiErrorMapper.Map(
            new ApiException("failed", 502, "<html>Bad Gateway</html>", Headers, null));

        Assert.Equal(ExitCodes.Error, mapped.ExitCode);
        Assert.Equal(ErrorCodes.ServerError, mapped.Error.Code);
        Assert.Contains("502", mapped.Error.Message);
    }

    [Fact]
    public void A_status_with_no_body_falls_back_to_the_generic_code_for_that_status()
    {
        Assert.Equal(
            SharedErrorCodes.NotFound,
            ApiErrorMapper.Map(new ApiException("failed", 404, null, Headers, null)).Error.Code);
    }

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> Headers =
        new Dictionary<string, IEnumerable<string>>();

    private static JsonElement Element(string value)
        => JsonSerializer.SerializeToElement(value);

    private static ApiException Exception(int status, object problem)
        => new("failed", status, JsonSerializer.Serialize(problem), Headers, null);
}
