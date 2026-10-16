using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

public class InboxBundleTest
{
    private static BankTransactionResponse Charge() => new(
        Guid.NewGuid(), new DateOnly(2026, 10, 12), 30m, "USD", "Shop", "Shop",
        "Groceries", "Groceries", null, "in store", "Miami", null, null,
        false, InboxStatus.New, null, null, "Card", "Bank")
    {
        AccountId = Guid.Parse("50c454e5-63af-4aa6-ad7e-f037ed91c542"),
        Suggestion = new(Guid.NewGuid(), Guid.Empty, "Home", false, Guid.Empty, "Groceries", .9)
    };

    [Fact]
    public void Each_charge_keeps_its_own_destination_even_at_the_same_merchant()
    {
        var first = Charge();
        var second = Charge() with { Amount = 42.18m };
        var personal = Charge() with { Suggestion = Charge().Suggestion! with { IsPersonal = true } };
        var bundle = Assert.Single(InboxBundle.Create([first, second, personal]));
        Assert.Equal(new[] { first.Id, second.Id }, bundle.Rows.Select(row => row.Id));
    }

    [Fact]
    public void Different_context_and_unsafe_charges_stay_individual()
    {
        var first = Charge();
        var candidates = new[]
        {
            first with { Id = Guid.NewGuid(), Pending = true },
            first with { Id = Guid.NewGuid(), RemovedAt = DateTimeOffset.UtcNow },
            first with { Id = Guid.NewGuid(), Amount = -30 },
            first with { Id = Guid.NewGuid(), AccountId = Guid.NewGuid() },
            first with { Id = Guid.NewGuid(), AccountId = null },
            first with { Id = Guid.NewGuid(), City = "Orlando" },
            first with { Id = Guid.NewGuid(), Date = new DateOnly(2026, 9, 12) },
            first with { Id = Guid.NewGuid(), Suggestion = null },
            first with { Id = Guid.NewGuid(), Status = InboxStatus.Filed },
            first with { Id = Guid.NewGuid(), PossibleDuplicates = [new(Guid.NewGuid(), "Shop", 30,
                "USD", DateTimeOffset.UtcNow, null, null, "Ana", 0, 0, MatchConfidence.Confident)] }
        };
        foreach (var candidate in candidates)
            Assert.Empty(InboxBundle.Create([first, candidate]));
    }
}
