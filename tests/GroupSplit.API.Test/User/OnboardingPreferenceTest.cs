using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.User;

public sealed class OnboardingPreferenceTest
{
    [Fact]
    public async Task Dismissals_are_independent_and_belong_to_the_account()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsync("/users/me/onboarding/expense/dismiss", null, ct)).StatusCode);
        var user = await host.Client.GetFromJsonAsync<UserInfo>("/users/me", ct);
        Assert.True(user!.OnboardingExpenseDismissed);
        Assert.False(user.OnboardingChecklistDismissed);
        using var other = host.ClientForAnotherUser();
        Assert.False((await other.GetFromJsonAsync<UserInfo>("/users/me", ct))!.OnboardingExpenseDismissed);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsync("/users/me/onboarding/unknown/dismiss", null, ct)).StatusCode);
        using var anonymous = host.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/users/me/onboarding/checklist/dismiss", null, ct)).StatusCode);
    }
}
