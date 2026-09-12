using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// What the dialog that shows one expense says about where its shares came from.
/// </summary>
/// <remarks>
/// It used to work this out from the amounts: three figures that matched to the penny got
/// the word "evenly" and anything else got nothing. The reading is wrong in both
/// directions. A two-to-one rule on 90.00 divides 60/30 and reads as uneven, which is fine;
/// the same rule on an amount that happens to land equally reads as "evenly" though no even
/// division was involved, and shares somebody typed by hand to be equal read the same way
/// though no rule produced them at all.
/// <para>
/// The API has carried the answer since split rules were versioned:
/// <see cref="TransactionDetailsResponse.SplitRuleVersionId"/> is the version that produced
/// the shares, or null to say they are the expense's own. These check the dialog says what
/// that field says -- including the two cases the field alone does not settle, where the
/// rule cannot be reached from the category the expense is filed under.
/// </para>
/// </remarks>
public class DivisionSourceTest : ComponentTest
{
    private static readonly Guid Expense = Guid.NewGuid();
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Groceries = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private static readonly Guid CurrentVersion = Guid.NewGuid();
    private static readonly Guid OldVersion = Guid.NewGuid();

    /// <summary>When the superseded version opened, and when it closed, as instants.</summary>
    private static readonly DateTimeOffset Opened = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Closed = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A date safely inside the current version's window.</summary>
    private static readonly DateTimeOffset Spent = new(2026, 8, 20, 19, 42, 0, TimeSpan.Zero);

    private static readonly Guid MeId = Guid.NewGuid();
    private static readonly Guid LuId = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<IUserLogin> _login = new();

    public DivisionSourceTest()
    {
        _login.SetupGet(login => login.User)
            .Returns(new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"));

        Categories
            .Setup(client => client.GetCategoriesAsync(Flat, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(Groceries, Flat, "Groceries", Household, "Household 3-way")]);

        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleHistoryResponse(Household, Flat, "Household 3-way",
            [
                new SplitRuleVersionResponse(CurrentVersion, Closed, null, new EvenSplitRuleDto()),

                new SplitRuleVersionResponse(OldVersion, Opened, Closed,
                    new SharesSplitRuleDto { Shares = { [MeId] = 2, [LuId] = 1 } })
            ]));

        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_login.Object);
    }

    /// <summary>
    /// The rule is named, and so is the window the version stood for -- which is the whole
    /// point of the line: what divided this is not what the rule says today.
    /// </summary>
    [Fact]
    public async Task An_expense_a_superseded_version_divided_names_the_rule_and_its_window()
    {
        var dialog = await OpenAsync(Details(OldVersion));

        var strip = dialog.Find(".gs-provenance");

        Assert.Contains("Household 3-way", strip.QuerySelector(".gs-provenance-what")!.TextContent,
            StringComparison.Ordinal);

        // Read through the same clock the dialog renders with. These are instants, and the
        // clock shows them in the reader's zone -- so a UTC midnight is the previous day
        // west of Greenwich, and asserting the literal "1 Mar" would pass in London and fail
        // in Santo Domingo. See LocalClock in ComponentTest.
        var clock = Services.GetRequiredService<LocalClock>();

        var when = strip.QuerySelector(".gs-provenance-when")!.TextContent;

        Assert.Contains(clock.Local(Opened).ToString("d MMM"), when, StringComparison.Ordinal);
        Assert.Contains(clock.Local(Closed).ToString("d MMM yyyy"), when, StringComparison.Ordinal);

        // The warning that matters: the rule has moved on, so re-dividing this expense today
        // would not reproduce these figures from what the rule now says.
        Assert.Equal("Edited since", strip.QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// The ordinary case is one line: no box, no icon, no badge.
    /// </summary>
    /// <remarks>
    /// It used to draw the full strip with a CURRENT tag over every expense in the app.
    /// Almost no rule is ever edited, so almost every expense carried it -- a badge on the
    /// normal state, which is how a badge stops being read at all, and which left the one
    /// tag that means something ("edited since") competing with it. The line also says what
    /// the rule *does*, because that is what makes the figures underneath make sense; naming
    /// the rule alone never did.
    /// </remarks>
    [Fact]
    public async Task The_ordinary_case_is_one_line_and_says_how_the_rule_divides()
    {
        var dialog = await OpenAsync(Details(CurrentVersion, Spent));

        var line = dialog.Find(".gs-provenance-line").TextContent;

        Assert.Contains("Divided evenly by Household 3-way", line, StringComparison.Ordinal);

        Assert.Empty(dialog.FindAll(".gs-provenance"));
        Assert.DoesNotContain("Current", dialog.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version that began after the money was spent cannot have divided it, and the screen
    /// says so.
    /// </summary>
    /// <remarks>
    /// This is the shape a back catalogue arrives in: an import points every expense at
    /// whatever its rule said on the day of the import, so expenses from years earlier claim
    /// a division agreed last week. Both dates are already on screen's data, and until now
    /// nothing compared them -- the strip reported the window as though it were ordinary.
    /// </remarks>
    [Fact]
    public async Task A_version_that_began_after_the_money_was_spent_is_called_wrong()
    {
        // The current version opened on 12 August; this was spent on 4 August.
        var dialog = await OpenAsync(Details(CurrentVersion));

        var strip = dialog.Find(".gs-provenance.is-wrong");

        Assert.Equal("Looks wrong", strip.QuerySelector(".gs-tag")!.TextContent.Trim());

        Assert.Contains("only began", strip.QuerySelector(".gs-provenance-when")!.TextContent,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Who carries the odd cent, which is the question this screen is asked most.
    /// </summary>
    [Fact]
    public async Task The_odd_cent_is_accounted_for()
    {
        var expense = Details(CurrentVersion, Spent) with
        {
            Amount = 27.87m,
            Splits =
            [
                new TransactionSplitResponse(MeId, "Ana Benitez", 13.93m),
                new TransactionSplitResponse(LuId, "Lu Ferrer", 13.94m)
            ]
        };

        var dialog = await OpenAsync(expense);

        Assert.Contains("the odd cent goes to Lu Ferrer", dialog.Find(".gs-provenance-line").TextContent,
            StringComparison.Ordinal);
    }

    /// <summary>And nothing is said where the shares were never meant to match.</summary>
    [Fact]
    public async Task A_division_that_is_not_almost_equal_says_nothing_about_cents()
    {
        var expense = Details(CurrentVersion, Spent) with
        {
            Splits =
            [
                new TransactionSplitResponse(MeId, "Ana Benitez", 40m),
                new TransactionSplitResponse(LuId, "Lu Ferrer", 20m)
            ]
        };

        var dialog = await OpenAsync(expense);

        Assert.DoesNotContain("odd cent", dialog.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// No version recorded means the amounts are the expense's own, and the dialog says so
    /// rather than reading the figures and guessing.
    /// </summary>
    /// <remarks>
    /// The shares here are 30.00 and 30.00 -- exactly the case the old markup called
    /// "evenly", and exactly the case where saying so is a claim about who decided them that
    /// nothing on the response supports.
    /// </remarks>
    [Fact]
    public async Task An_expense_nothing_divided_says_the_amounts_are_its_own()
    {
        var dialog = await OpenAsync(Details(null));

        var strip = dialog.Find(".gs-provenance");

        Assert.Equal("Amounts set by hand", strip.QuerySelector(".gs-provenance-what")!.TextContent.Trim());
        Assert.Equal("By hand", strip.QuerySelector(".gs-tag")!.TextContent.Trim());

        // And the word it used to print over exactly these figures is gone.
        Assert.DoesNotContain("evenly", dialog.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A version whose rule is not the one the category names any more. The expense was
    /// divided by something; naming the category's current rule would name the wrong one.
    /// </summary>
    [Fact]
    public async Task A_version_the_category_no_longer_reaches_is_not_named_as_the_current_rule()
    {
        var dialog = await OpenAsync(Details(Guid.NewGuid()));

        var strip = dialog.Find(".gs-provenance");

        Assert.Equal("Divided by a split rule", strip.QuerySelector(".gs-provenance-what")!.TextContent.Trim());
        Assert.Equal("Another rule", strip.QuerySelector(".gs-tag")!.TextContent.Trim());
        Assert.DoesNotContain("Household 3-way", strip.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// A personal expense has no group, so no rule can have divided it and there is nothing
    /// to say. It also costs no round trip, which is why the reader is asked at all.
    /// </summary>
    [Fact]
    public async Task A_personal_expense_says_nothing_and_asks_nothing()
    {
        var dialog = await OpenAsync(Details(null) with { GroupId = null, GroupName = null });

        Assert.Empty(dialog.FindAll(".gs-provenance"));

        Categories.Verify(
            client => client.GetCategoriesAsync(It.IsAny<Guid?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An expense nothing divided needs no history either: the answer is on the response it
    /// already has.
    /// </summary>
    [Fact]
    public async Task An_expense_nothing_divided_asks_for_no_history()
    {
        await OpenAsync(Details(null));

        SplitRules.Verify(
            client => client.GetSplitRuleVersionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The way through to the rule's history, offered only where there is one to read.
    /// </summary>
    [Fact]
    public async Task A_rule_with_more_than_one_version_offers_its_history()
    {
        var dialog = await OpenAsync(Details(OldVersion));

        Assert.Contains(
            dialog.FindAll("button"),
            button => button.TextContent.Contains("See how Household 3-way has changed", StringComparison.Ordinal));
    }

    /// <param name="when">
    /// When it was spent. It defaults inside the superseded version's window, because that
    /// is the interesting case; a test about the current version has to move it after that
    /// version began, or the expense is claiming a division that did not exist yet -- which
    /// is a different thing the screen now says out loud.
    /// </param>
    private static TransactionDetailsResponse Details(Guid? versionId, DateTimeOffset? when = null) => new()
    {
        Id = Expense,
        Name = "Mercadona",
        Amount = 60m,
        DateTime = when ?? new DateTimeOffset(2026, 8, 4, 19, 42, 0, TimeSpan.Zero),
        GroupId = Flat,
        GroupName = "The flat",
        CategoryId = Groceries,
        Category = "Groceries",
        PaidByUserId = MeId,
        PaidByUserName = "Ana Benitez",
        SplitRuleVersionId = versionId,
        Splits =
        [
            new TransactionSplitResponse(MeId, "Ana Benitez", 30m),
            new TransactionSplitResponse(LuId, "Lu Ferrer", 30m)
        ]
    };

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(TransactionDetailsResponse details)
    {
        _transactions
            .Setup(client => client.GetTransactionAsync(Expense, It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<TransactionDetailsDialog>
        {
            { dialog => dialog.TransactionId, Expense }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<TransactionDetailsDialog>("Mercadona", parameters));

        return provider;
    }
}
