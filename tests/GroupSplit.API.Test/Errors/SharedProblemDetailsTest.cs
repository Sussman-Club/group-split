using System.Text.Json;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Errors;

/// <summary>
/// The shared <see cref="ProblemDetails"/> is what every client reads a problem into, and its
/// extension members are where the contract lives. Pinned on its own so a change to the
/// record cannot quietly drop them.
/// </summary>
public class SharedProblemDetailsTest
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Body =
        """{"type":"https://groupsplit.app/errors/not-found","title":"Not Found","status":404,"instance":"/x","code":"NOT_FOUND","traceId":"00-abc-def-00","balance":-50}""";

    [Fact]
    public void The_extension_members_are_read()
    {
        var problem = JsonSerializer.Deserialize<ProblemDetails>(Body, Web);

        Assert.NotNull(problem);
        Assert.NotNull(problem.Extensions);
        Assert.Equal("NOT_FOUND", problem.Code);
        Assert.Equal("00-abc-def-00", problem.TraceId);
        Assert.Equal(-50m, problem.GetExtension<decimal>("balance", Web));
    }

    [Fact]
    public void A_plain_dictionary_target_reads_the_same_body()
    {
        var plain = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body, Web);

        Assert.NotNull(plain);
        Assert.Equal("NOT_FOUND", plain["code"].GetString());
    }
}
