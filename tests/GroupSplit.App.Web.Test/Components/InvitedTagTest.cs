using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The "invited" tag beside a name, and the box that puts a space between the two.
/// </summary>
/// <remarks>
/// The space cannot come from the markup. Razor drops the whitespace-only line before an
/// <c>@if</c> block, so a tag written on its own line under a name arrives against the last
/// letter of it -- "Omar HaddadINVITED". Everywhere else in the app the pair sits in a flex
/// box with a gap, which is what <c>.gs-row .title</c> does; these two places were plain
/// boxes and so had nothing to hold them apart.
/// <para>
/// A gap is a stylesheet's business and cannot be asserted here. What can, and what the gap
/// rests on, is the structure: the name in an element of its own with the tag as its
/// sibling. That is also what decides which of the two gets clipped when a name is long --
/// and before this the tag lost, so a wide enough name took the "invited" mark off the row
/// entirely and left the share reading as a member's.
/// </para>
/// </remarks>
public class InvitedTagTest : ComponentTest
{
    private static readonly Guid Expense = Guid.NewGuid();

    private static readonly Guid MeId = Guid.NewGuid();

    private static readonly Guid OmarId = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<IUserLogin> _login = new();

    public InvitedTagTest()
    {
        _login.SetupGet(login => login.User)
            .Returns(new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"));

        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_login.Object);
    }

    /// <summary>
    /// The payer's name and the tag are separate elements in the box that spaces them.
    /// </summary>
    [Fact]
    public async Task The_payer_who_has_not_joined_is_named_apart_from_the_tag()
    {
        var dialog = await OpenAsync(Details(paidByInvitee: true));

        var value = dialog.Find(".gs-kv-tagged");

        // The name in its own element, so the box can space the two rather than the markup.
        Assert.Equal("Omar Haddad", value.QuerySelector("span:not(.gs-tag)")!.TextContent.Trim());
        Assert.Equal("invited", value.QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// A split against somebody still to join, the same way.
    /// </summary>
    [Fact]
    public async Task A_share_against_somebody_still_to_join_is_named_apart_from_the_tag()
    {
        var dialog = await OpenAsync(Details(paidByInvitee: false));

        var who = dialog.FindAll(".gs-split-who")
            .Single(element => element.QuerySelector(".gs-tag") is not null);

        Assert.Equal("Omar Haddad", who.QuerySelector("span:not(.gs-tag)")!.TextContent.Trim());
        Assert.Equal("invited", who.QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// A member's row carries no tag at all, so the mark means what it says.
    /// </summary>
    [Fact]
    public async Task A_share_against_a_member_carries_no_tag()
    {
        var dialog = await OpenAsync(Details(paidByInvitee: false));

        var mine = dialog.FindAll(".gs-split-who")
            .Single(element => element.TextContent.Contains("You", StringComparison.Ordinal));

        Assert.Null(mine.QuerySelector(".gs-tag"));
    }

    /// <summary>An expense the reader shares with somebody who has not joined yet.</summary>
    private static TransactionDetailsResponse Details(bool paidByInvitee) => new()
    {
        Id = Expense,
        Name = "Dinner",
        Amount = 60m,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = Guid.NewGuid(),
        GroupName = "The flat",
        PaidByUserId = paidByInvitee ? OmarId : MeId,
        PaidByUserName = paidByInvitee ? "Omar Haddad" : "Ana Benitez",
        PaidByIsPendingInvitee = paidByInvitee,
        Splits =
        [
            new TransactionSplitResponse(MeId, "Ana Benitez", 30m),
            new TransactionSplitResponse(OmarId, "Omar Haddad", 30m, IsPendingInvitee: true)
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
                .ShowAsync<TransactionDetailsDialog>("Dinner", parameters));

        return provider;
    }
}
