using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class BankInboxDetailTest : ComponentTest
{
    [Fact]
    public void Multiple_matches_keep_every_action_available_and_mark_the_suggestion()
    {
        var id = Guid.NewGuid();
        var first = Match("Team lunch");
        var second = Match("Dinner") with { IsSuggested = true };
        var row = new BankTransactionResponse(id, new DateOnly(2026, 11, 5), 38.73m, "USD", "CAFE", "Cafe", null, null, null, null, null, null, null, false, InboxStatus.New, null, null, "Checking", "Bank")
        { PossibleDuplicates = [first, second] };
        var client = new Mock<IInboxClient>();
        client.Setup(x => x.GetBankTransactionAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        client.Setup(x => x.GetBankTransactionMatchesAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        Services.AddSingleton(client.Object);
        Services.AddSingleton(Mock.Of<IBankCommands>());

        var page = Render<BankInboxDetail>(parameters => parameters.Add(p => p.Id, id));

        page.WaitForAssertion(() => Assert.Contains("Suggested: Dinner", page.Markup));
        Assert.Contains("Team lunch", page.Markup);
        Assert.Contains("Find an expense", page.Markup);
        Assert.Contains("Review and add expense", page.Markup);
        Assert.Contains("Ignore transaction", page.Markup);
    }

    private static ExpenseMatchResponse Match(string name) => new(Guid.NewGuid(), name, 38.73m, "USD", DateTimeOffset.UtcNow, null, null, "", 0m, 0, MatchConfidence.Confident);
}
