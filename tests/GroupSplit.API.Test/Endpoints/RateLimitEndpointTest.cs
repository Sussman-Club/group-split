using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Extensions;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// The per-person allowances on the requests that cost money or invite guessing, over the
/// wire.
/// </summary>
/// <remarks>
/// Each limit is tightened to one or two here so the test reaches it in a couple of calls; the
/// production figures are larger and only change how many. What is pinned is which routes are
/// counted, that they share an allowance per kind, that it is counted per signed-in person,
/// and what the refusal looks like -- which is what the web app and the CLI read.
/// </remarks>
public class RateLimitEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await ApiEndpointHost.StartAsync(services => services.Configure<ApiRateLimits>(limits =>
        {
            limits.Transcription = new RateLimit { PermitLimit = 1, Window = TimeSpan.FromHours(1) };
            limits.Bank = new RateLimit { PermitLimit = 1, Window = TimeSpan.FromHours(1) };
            limits.Invitations = new RateLimit { PermitLimit = 2, Window = TimeSpan.FromHours(1) };
        }));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_third_join_link_in_the_window_is_refused_with_a_rate_limit_problem()
    {
        var group = await CreateGroup();

        (await Client.PostAsync($"/groups/{group}/join-links", null, Ct)).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/groups/{group}/join-links", null, Ct)).EnsureSuccessStatusCode();

        using var refused = await Client.PostAsync($"/groups/{group}/join-links", null, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);

        var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>(Json, Ct))!;

        Assert.Equal(ErrorCodes.RateLimited, problem.Code);
        Assert.Equal(429, problem.Status);
        Assert.NotNull(problem.TraceId);

        // Said twice, in the header a generic client honours and in the body the app reads,
        // and the two agree.
        var header = Assert.Single(refused.Headers.GetValues("Retry-After"));
        var seconds = problem.GetExtension<int>(ProblemDetails.RetryAfterExtension);
        Assert.True(seconds > 0);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), header);
    }

    /// <summary>
    /// Inviting, issuing links and answering them draw on one allowance, so spending it on one
    /// route cannot be sidestepped by switching to another.
    /// </summary>
    [Fact]
    public async Task The_invitation_routes_share_one_allowance()
    {
        var group = await CreateGroup();

        (await Client.PostAsync($"/groups/{group}/join-links", null, Ct)).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/groups/{group}/join-links", null, Ct)).EnsureSuccessStatusCode();

        using var accept = await Client.PostAsync("/invitations/links/not-a-token/accept", null, Ct);
        using var claim = await Client.PostAsync("/invitations/claims/not-a-token", null, Ct);
        using var guests = await Client.PostAsJsonAsync(
            $"/groups/{group}/guests", new { names = new[] { "Ana" } }, Json, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, accept.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, claim.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, guests.StatusCode);
    }

    /// <summary>
    /// Counted against the token's subject. One person running out says nothing about anybody
    /// else, and a different forwarded address does not buy a fresh allowance -- the
    /// deployment believes whatever <c>X-Forwarded-For</c> says, so an address-keyed limit
    /// would be one nobody had to respect.
    /// </summary>
    [Fact]
    public async Task The_allowance_is_per_person_and_not_per_address()
    {
        var group = await CreateGroup();

        (await Client.PostAsync($"/groups/{group}/join-links", null, Ct)).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/groups/{group}/join-links", null, Ct)).EnsureSuccessStatusCode();

        using var spoofed = new HttpRequestMessage(HttpMethod.Post, $"/groups/{group}/join-links");
        spoofed.Headers.Add("X-Forwarded-For", "203.0.113.7");
        using var stillRefused = await Client.SendAsync(spoofed, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, stillRefused.StatusCode);

        // Somebody else is answered on their own merits: a 404, because the group is not
        // theirs, and not a 429.
        using var other = _host.ClientForAnotherUser();
        using var otherAnswer = await other.PostAsync("/invitations/links/not-a-token/accept", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, otherAnswer.StatusCode);
    }

    [Theory]
    [InlineData("/receipts/transcribe")]
    [InlineData("/transactions/7b0f9c3e-0000-4000-8000-000000000001/receipt-attachments/7b0f9c3e-0000-4000-8000-000000000002/transcribe")]
    [InlineData("/inbox/7b0f9c3e-0000-4000-8000-000000000001/receipt-attachments/7b0f9c3e-0000-4000-8000-000000000002/transcribe")]
    public async Task Every_transcription_route_is_limited(string path)
    {
        // Only the draft takes a file; the other two name one already stored.
        HttpContent? Body() => path == "/receipts/transcribe" ? ReceiptForm() : null;

        using var first = await Client.PostAsync(path, Body(), Ct);
        using var second = await Client.PostAsync(path, Body(), Ct);

        // The first is answered however it is answered here -- no provider is configured, and
        // the ids name nothing -- but it was counted.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Theory]
    [InlineData("/bank-connections/link-token")]
    [InlineData("/bank-connections")]
    [InlineData("/bank-connections/7b0f9c3e-0000-4000-8000-000000000001/refresh")]
    [InlineData("/bank-connections/7b0f9c3e-0000-4000-8000-000000000001/sync")]
    public async Task Every_bank_provider_route_is_limited(string path)
    {
        using var first = await Client.PostAsJsonAsync(path, new { }, Json, Ct);
        using var second = await Client.PostAsJsonAsync(path, new { }, Json, Ct);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    /// <summary>
    /// Reading is not limited: the limits are on what costs money or hands something out,
    /// and a page that lists links must keep loading.
    /// </summary>
    [Fact]
    public async Task Reading_is_not_limited()
    {
        var group = await CreateGroup();

        for (var i = 0; i < 5; i++)
        {
            using var response = await Client.GetAsync($"/groups/{group}/join-links", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// The upload that is transcribed and never stored had no ceiling of its own, so it took
    /// Kestrel's 30 MB default and buffered the form before anything looked at the length.
    /// It now has the attachment upload's.
    /// </summary>
    [Fact]
    public void Transcribing_a_draft_has_the_attachment_uploads_size_ceiling()
    {
        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        long? Limit(string name) => endpoints
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name)
            .Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        Assert.Equal(11 * 1024 * 1024, Limit("TranscribeReceiptDraft"));
        Assert.Equal(Limit("UploadReceiptAttachment"), Limit("TranscribeReceiptDraft"));
    }

    private async Task<Guid> CreateGroup()
    {
        var response = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = "Lisbon" }, Json, Ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent ReceiptForm()
    {
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        return new MultipartFormDataContent { { file, "file", "dinner.jpg" } };
    }
}
