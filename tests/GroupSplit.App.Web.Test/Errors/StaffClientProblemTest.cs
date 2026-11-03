using System.Net;
using System.Text;
using System.Globalization;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Errors;

public class StaffClientProblemTest
{
    [Fact]
    public void Ticket_errors_use_localized_codes_instead_of_English_API_details()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");
            var problem = System.Text.Json.JsonSerializer.Deserialize<ProblemDetails>(
                """{"detail":"This support ticket changed on the server.","code":"TICKET_CHANGED"}""");
            var exception = new ApiProblemException(problem, HttpStatusCode.Conflict);
            Assert.Equal("Este ticket cambió. Actualízalo antes de intentarlo de nuevo.", exception.UserMessage);
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    [Fact]
    public async Task Staff_mutations_preserve_problem_details()
    {
        using var http = new HttpClient(new RefusalHandler()) { BaseAddress = new Uri("https://example.test/") };
        var client = new StaffClient(http);
        var error = await Assert.ThrowsAsync<ApiProblemException>(() => client.AddAliasAsync(Guid.NewGuid(), "Shop"));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("Review this merchant again.", error.Problem?.Detail);
        Assert.Equal("MERCHANT_CHANGED", error.Problem?.GetExtensionString("code"));
    }

    private sealed class RefusalHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"status":409,"detail":"Review this merchant again.","code":"MERCHANT_CHANGED"}""", Encoding.UTF8, "application/problem+json")
            });
    }
}
