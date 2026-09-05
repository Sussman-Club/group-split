using System.Reflection;
using System.Text.Json;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.App.Web.Test.Errors;

/// <summary>
/// The one place the app turns a failed call into words: the code-to-message table, the
/// fallback for a code this build does not know, and the three failures that are not
/// domain errors at all -- no connection, a lost session, and a bug on the server.
/// </summary>
public class ApiErrorsTest
{
    /// <summary>What the API writes with.</summary>
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> NoHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private const string TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    /// <summary>
    /// A problem as the generated client would have deserialized it: written by the API's
    /// serializer, read by the client's.
    /// </summary>
    private static ProblemDetails Problem(
        int status,
        string? code,
        string? traceId = TraceId,
        params (string Name, object Value)[] extensions)
    {
        var members = new Dictionary<string, object?>
        {
            ["type"] = "https://groupsplit.app/errors/x",
            ["title"] = "A title for developers",
            ["status"] = status,
            ["detail"] = "A detail for developers"
        };

        if (code is not null) members["code"] = code;
        if (traceId is not null) members["traceId"] = traceId;
        foreach (var (name, value) in extensions) members[name] = value;

        return JsonSerializer.Deserialize<ProblemDetails>(
            JsonSerializer.Serialize(members, Api), GroupSplitSerializer.Options)!;
    }

    private static ApiException<ProblemDetails> Typed(int status, ProblemDetails problem) =>
        new("refused", status, JsonSerializer.Serialize(problem, Api), NoHeaders, problem, null!);

    private static ApiException Untyped(int status, string body) =>
        new("unexpected", status, body, NoHeaders, null!);

    // ---- Domain refusals --------------------------------------------------------------------

    [Fact]
    public void A_known_code_maps_to_its_own_message_not_the_servers_wording()
    {
        var error = ApiErrors.Read(Typed(409, Problem(409, ErrorCodes.GroupMemberNotSettled)));

        Assert.Equal(ApiErrorKind.Refused, error.Kind);
        Assert.Equal(ErrorCodes.GroupMemberNotSettled, error.Code);
        Assert.Equal(ErrorMessages.For(ErrorCodes.GroupMemberNotSettled), error.Message);
        Assert.DoesNotContain("developers", error.Message);
        Assert.Equal(409, error.Status);
        Assert.Equal(TraceId, error.TraceId);
    }

    /// <summary>
    /// A newer API sends a code this build has never seen. The status still says what kind
    /// of thing happened, so the message comes from that rather than being blank.
    /// </summary>
    [Fact]
    public void An_unknown_code_falls_back_to_the_message_for_its_status()
    {
        var error = ApiErrors.Read(Typed(409, Problem(409, "SOMETHING_FROM_THE_FUTURE")));

        Assert.Equal(ApiErrorKind.Refused, error.Kind);
        Assert.Equal("SOMETHING_FROM_THE_FUTURE", error.Code);
        Assert.Equal(ErrorMessages.ForStatus(409), error.Message);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void A_problem_without_a_code_is_unknown_but_still_has_words()
    {
        var error = ApiErrors.Read(Typed(404, Problem(404, code: null)));

        Assert.Equal(ApiErrorKind.Unknown, error.Kind);
        Assert.Null(error.Code);
        Assert.Equal(ErrorMessages.ForStatus(404), error.Message);
    }

    /// <summary>
    /// An endpoint that did not declare a status hands the client the raw body. The API
    /// still wrote problem details, and they are still read.
    /// </summary>
    [Fact]
    public void An_undeclared_status_is_read_from_the_raw_body()
    {
        var body = $$$"""{"status":422,"title":"x","code":"{{{ErrorCodes.RuleNotEditable}}}","traceId":"{{{TraceId}}}"}""";

        var error = ApiErrors.Read(Untyped(422, body));

        Assert.Equal(ApiErrorKind.Refused, error.Kind);
        Assert.Equal(ErrorCodes.RuleNotEditable, error.Code);
        Assert.Equal(ErrorMessages.For(ErrorCodes.RuleNotEditable), error.Message);
        Assert.Equal(TraceId, error.TraceId);
    }

    [Fact]
    public void A_body_that_is_not_json_does_not_break_the_reader()
    {
        var error = ApiErrors.Read(Untyped(409, "<html>nope</html>"));

        Assert.Equal(ApiErrorKind.Unknown, error.Kind);
        Assert.Equal(ErrorMessages.ForStatus(409), error.Message);
    }

    /// <summary>
    /// The one refusal a page branches on: the groups that block deleting an account ride
    /// on the problem, and come back as the DTO the API wrote them from.
    /// </summary>
    [Fact]
    public void A_structured_extension_member_is_read_back_as_its_dto()
    {
        var groupId = Guid.NewGuid();
        var problem = Problem(409, ErrorCodes.AccountNotSettled, TraceId,
            (ProblemDetails.OutstandingBalancesExtension,
                new List<OutstandingBalance> { new(groupId, "Ski trip", 50m) }));

        var error = ApiErrors.Read(Typed(409, problem));

        var blocking = error.Extension<List<OutstandingBalance>>(ProblemDetails.OutstandingBalancesExtension);

        var only = Assert.Single(blocking!);
        Assert.Equal(groupId, only.GroupId);
        Assert.Equal("Ski trip", only.GroupName);
        Assert.Equal(50m, only.Balance);
    }

    // ---- Validation --------------------------------------------------------------------------

    [Fact]
    public void A_validation_problem_carries_the_field_messages_and_leads_with_the_first()
    {
        var json = $$$"""
            {"status":400,"title":"One or more validation errors occurred.","code":"{{{ErrorCodes.ValidationFailed}}}",
             "traceId":"{{{TraceId}}}","errors":{"Amount":["Amount must have no more than 2 decimal places."],"Name":["Name is required."]}}
            """;
        var problem = JsonSerializer.Deserialize<HttpValidationProblemDetails>(json, GroupSplitSerializer.Options)!;
        var exception = new ApiException<HttpValidationProblemDetails>("invalid", 400, json, NoHeaders, problem, null!);

        var error = ApiErrors.Read(exception);

        Assert.Equal(ApiErrorKind.Validation, error.Kind);
        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        Assert.NotNull(error.FieldErrors);
        Assert.Equal("Amount must have no more than 2 decimal places.", Assert.Single(error.FieldErrors["Amount"]));
        Assert.Equal("Amount must have no more than 2 decimal places.", error.Message);
    }

    // ---- The three that are not domain errors ---------------------------------------------------

    [Fact]
    public void A_401_means_sign_in_again_whatever_the_body_says()
    {
        var error = ApiErrors.Read(Untyped(401, string.Empty));

        Assert.Equal(ApiErrorKind.Unauthenticated, error.Kind);
        Assert.Equal(ErrorMessages.SessionExpired, error.Message);
    }

    [Fact]
    public void A_5xx_apologises_and_quotes_the_trace_id()
    {
        var error = ApiErrors.Read(Typed(500, Problem(500, ErrorCodes.InternalError)));

        Assert.Equal(ApiErrorKind.Server, error.Kind);
        Assert.Contains(TraceId, error.Message);
        Assert.DoesNotContain("developers", error.Message);
    }

    [Fact]
    public void A_5xx_with_no_problem_body_still_apologises()
    {
        var error = ApiErrors.Read(Untyped(503, "Service Unavailable"));

        Assert.Equal(ApiErrorKind.Server, error.Kind);
        Assert.Equal(ErrorMessages.Server(null), error.Message);
    }

    [Fact]
    public void No_connection_is_a_network_error()
    {
        var error = ApiErrors.Read(new HttpRequestException("Connection refused"));

        Assert.Equal(ApiErrorKind.Network, error.Kind);
        Assert.Equal(ErrorMessages.Network, error.Message);
    }

    /// <summary>HttpClient reports a timeout as a cancellation wrapping a TimeoutException.</summary>
    [Fact]
    public void A_timeout_is_a_network_error()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured timeout.",
            new TimeoutException());

        Assert.True(ApiErrors.IsApiFailure(timeout));
        Assert.False(ApiErrors.IsCancellation(timeout));
        Assert.Equal(ApiErrorKind.Network, ApiErrors.Read(timeout).Kind);
    }

    /// <summary>A page that went away mid-call is not a failure, and nobody is there to tell.</summary>
    [Fact]
    public void A_plain_cancellation_is_recognised_as_such()
    {
        Assert.True(ApiErrors.IsCancellation(new TaskCanceledException()));
        Assert.True(ApiErrors.IsCancellation(new OperationCanceledException()));
    }

    /// <summary>A bug is not an API failure and is left to the error boundary.</summary>
    [Fact]
    public void A_bug_is_not_an_api_failure()
    {
        Assert.False(ApiErrors.IsApiFailure(new InvalidOperationException("No group is selected.")));
        Assert.False(ApiErrors.IsApiFailure(new NullReferenceException()));
    }

    // ---- The table itself -----------------------------------------------------------------------

    /// <summary>
    /// Every code the shared catalog declares has a message here. Adding a code without one
    /// would not fail to compile; it would fall back to the status message at runtime, which
    /// is what this is for.
    /// </summary>
    [Fact]
    public void Every_code_in_the_catalog_has_a_message()
    {
        var codes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);

        var missing = codes.Where(code => !ErrorMessages.Knows(code)).ToList();

        Assert.True(missing.Count == 0, $"codes without a message: {string.Join(", ", missing)}");
    }
}
