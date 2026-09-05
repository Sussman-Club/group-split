using System.Text.Json;
using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using ApiProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;
using ApiValidationProblemDetails = Microsoft.AspNetCore.Http.HttpValidationProblemDetails;

namespace GroupSplit.App.Web.Test.Contract;

/// <summary>
/// The two ends of the error contract use different types and different serializer settings:
/// the API writes the framework's <see cref="ApiProblemDetails"/> with web defaults, the
/// generated client reads <see cref="ProblemDetails"/> with camel-case naming laid over plain
/// defaults. Like <see cref="RuleVersionContractTest"/>, this pins the gap between them --
/// and the extension members in particular, which a mistake on either side drops silently
/// rather than loudly.
/// </summary>
public class ProblemDetailsContractTest
{
    /// <summary>What the API writes with.</summary>
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    /// <summary>What the generated client reads with.</summary>
    private static readonly JsonSerializerOptions Client = GroupSplitSerializer.Options;

    private const string TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public void What_the_api_writes_the_client_can_read_code_and_trace_id_included()
    {
        var written = new ApiProblemDetails
        {
            Type = "https://groupsplit.app/errors/group-not-found",
            Title = "Group not found",
            Status = 404,
            Detail = "Group was not found.",
            Instance = "/groups/1"
        };
        written.Extensions[ProblemDetails.CodeExtension] = ErrorCodes.GroupNotFound;
        written.Extensions[ProblemDetails.TraceIdExtension] = TraceId;

        var read = JsonSerializer.Deserialize<ProblemDetails>(JsonSerializer.Serialize(written, Api), Client);

        Assert.NotNull(read);
        Assert.Equal(written.Type, read.Type);
        Assert.Equal(written.Title, read.Title);
        Assert.Equal(404, read.Status);
        Assert.Equal(written.Detail, read.Detail);
        Assert.Equal(written.Instance, read.Instance);
        Assert.Equal(ErrorCodes.GroupNotFound, read.Code);
        Assert.Equal(TraceId, read.TraceId);
    }

    /// <summary>
    /// The extension names are the wire contract, pinned literally: a rename on either
    /// side is invisible in C# and would make every client read null.
    /// </summary>
    [Fact]
    public void The_extension_names_are_the_ones_the_wire_expects()
    {
        Assert.Equal("code", ProblemDetails.CodeExtension);
        Assert.Equal("traceId", ProblemDetails.TraceIdExtension);
        Assert.Equal("errors", ProblemDetails.ErrorsExtension);
        Assert.Equal("outstandingBalances", ProblemDetails.OutstandingBalancesExtension);
    }

    [Fact]
    public void A_structured_extension_survives_the_round_trip_as_the_dto_it_was_written_from()
    {
        var groupId = Guid.NewGuid();
        var written = new ApiProblemDetails { Status = 409 };
        written.Extensions[ProblemDetails.CodeExtension] = ErrorCodes.AccountNotSettled;
        written.Extensions[ProblemDetails.OutstandingBalancesExtension] =
            new List<OutstandingBalance> { new(groupId, "Ski trip", -12.5m) };

        var read = JsonSerializer.Deserialize<ProblemDetails>(JsonSerializer.Serialize(written, Api), Client);

        var balances = read!.GetExtension<List<OutstandingBalance>>(ProblemDetails.OutstandingBalancesExtension, Client);

        var only = Assert.Single(balances!);
        Assert.Equal(groupId, only.GroupId);
        Assert.Equal("Ski trip", only.GroupName);
        Assert.Equal(-12.5m, only.Balance);
    }

    /// <summary>
    /// The framework's validation problem has <c>errors</c> as a real property, and the
    /// shared type declares it the same way; everything else still lands in extensions.
    /// </summary>
    [Fact]
    public void A_validation_problem_keeps_its_field_errors_and_its_code()
    {
        var written = new ApiValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["Amount"] = ["Amount must have no more than 2 decimal places."]
        })
        {
            Status = 400
        };
        written.Extensions[ProblemDetails.CodeExtension] = ErrorCodes.ValidationFailed;
        written.Extensions[ProblemDetails.TraceIdExtension] = TraceId;

        var read = JsonSerializer.Deserialize<HttpValidationProblemDetails>(
            JsonSerializer.Serialize(written, Api), Client);

        Assert.NotNull(read);
        Assert.Equal(ErrorCodes.ValidationFailed, read.Code);
        Assert.Equal(TraceId, read.TraceId);
        Assert.Equal("Amount must have no more than 2 decimal places.", Assert.Single(read.Errors["Amount"]));
    }

    /// <summary>
    /// A problem with no extensions at all -- one a proxy in front of the API wrote, say --
    /// reads cleanly with nulls rather than throwing.
    /// </summary>
    [Fact]
    public void A_bare_problem_reads_with_nulls_not_exceptions()
    {
        var read = JsonSerializer.Deserialize<ProblemDetails>("""{"status":502,"title":"Bad Gateway"}""", Client);

        Assert.NotNull(read);
        Assert.Null(read.Code);
        Assert.Null(read.TraceId);
        Assert.Null(read.GetExtension<List<OutstandingBalance>>(ProblemDetails.OutstandingBalancesExtension, Client));
    }
}
