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
