using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.User;

public sealed class LanguagePreferenceTest
{
    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    public async Task Language_is_saved_on_the_account_and_read_by_a_new_request(string language)
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var response = await host.Client.PutAsJsonAsync("/users/me/language", new LanguagePreferenceRequest(language), ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var user = await host.Client.GetFromJsonAsync<UserInfo>("/users/me", ct);
        Assert.Equal(language, user!.PreferredLanguage);
        using var other = host.ClientForAnotherUser();
        Assert.Null((await other.GetFromJsonAsync<UserInfo>("/users/me", ct))!.PreferredLanguage);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unsupported_preferences_are_refused_without_changing_the_account(string? language)
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var response = await host.Client.PutAsJsonAsync("/users/me/language", new { Language = language }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await host.Client.GetFromJsonAsync<UserInfo>("/users/me", ct))!.PreferredLanguage);
    }

    [Fact]
    public async Task Language_changes_require_authentication()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        using var anonymous = host.AnonymousClient();
        var response = await anonymous.PutAsJsonAsync("/users/me/language", new LanguagePreferenceRequest("es"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
