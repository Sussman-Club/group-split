using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Extensions;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The "guest" tag beside a name, the box that puts a space between the two, and the label
/// every person picker uses.
/// </summary>
/// <remarks>
/// The space cannot come from the markup. Razor drops the whitespace-only line before an
/// <c>@if</c> block, so a tag written on its own line under a name arrives against the last
/// letter of it -- "Omar HaddadGUEST". Everywhere else in the app the pair sits in a flex
/// box with a gap, which is what <c>.gs-row .title</c> does; these two places were plain
/// boxes and so had nothing to hold them apart.
/// <para>
/// A gap is a stylesheet's business and cannot be asserted here. What can, and what the gap
/// rests on, is the structure: the name in an element of its own with the tag as its
/// sibling. That is also what decides which of the two gets clipped when a name is long --
/// and before this the tag lost, so a wide enough name took the mark off the row entirely
/// and left the share reading as a member's.
/// </para>
/// <para>
/// The tag said "invited" while guests were pending invitees. The word changed with the
/// idea: a guest is not somebody the group is waiting on, and the mark is what they are.
/// </para>
/// </remarks>
public class GuestTagTest : ComponentTest
{
    private static readonly Guid Expense = Guid.NewGuid();

    private static readonly Guid MeId = Guid.NewGuid();

    private static readonly Guid OmarId = Guid.NewGuid();

    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<IUserLogin> _login = new();

    public GuestTagTest()
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
    public async Task A_guest_who_paid_is_named_apart_from_the_tag()
    {
        var dialog = await OpenAsync(Details(paidByGuest: true));

        var value = dialog.Find(".gs-kv-tagged");

        // The name in its own element, so the box can space the two rather than the markup.
        Assert.Equal("Omar Haddad", value.QuerySelector("span:not(.gs-tag)")!.TextContent.Trim());
        Assert.Equal("guest", value.QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// A share held by a guest, the same way.
    /// </summary>
    [Fact]
    public async Task A_guests_share_is_named_apart_from_the_tag()
    {
        var dialog = await OpenAsync(Details(paidByGuest: false));

        var who = dialog.FindAll(".gs-split-who")
            .Single(element => element.QuerySelector(".gs-tag") is not null);

        Assert.Equal("Omar Haddad", who.QuerySelector("span:not(.gs-tag)")!.TextContent.Trim());
        Assert.Equal("guest", who.QuerySelector(".gs-tag")!.TextContent.Trim());
    }

    /// <summary>
    /// A member's row carries no tag at all, so the mark means what it says.
    /// </summary>
    [Fact]
    public async Task A_share_against_a_member_carries_no_tag()
    {
        var dialog = await OpenAsync(Details(paidByGuest: false));

        var mine = dialog.FindAll(".gs-split-who")
            .Single(element => element.TextContent.Contains("You", StringComparison.Ordinal));

        Assert.Null(mine.QuerySelector(".gs-tag"));
    }

    /// <summary>
    /// The label every picker uses: a guest is marked as one, a member is just their name.
    /// </summary>
    [Fact]
    public void A_guest_is_labelled_as_a_guest()
    {
        Assert.Equal("Omar Haddad (guest)", new UserInfo(OmarId, "Omar", "Haddad", null, IsGuest: true).Label());
        Assert.Equal("Omar Haddad", new UserInfo(OmarId, "Omar", "Haddad", null).Label());
    }

    /// <summary>
    /// A removed guest carries both flags, and the past one wins.
    /// </summary>
    /// <remarks>
    /// Checked the other way round, a guest who was removed read as one still in the group
    /// -- offered by every screen that looks at the mark, on an old expense, as somebody the
    /// members could still record for. And not "(left the group)" either: they never joined.
    /// </remarks>
    [Fact]
    public void A_removed_guest_is_labelled_as_removed_rather_than_as_a_guest_or_a_leaver()
    {
        var removed = new UserInfo(OmarId, "Omar", "Haddad", null, IsGuest: true, IsPastMember: true);

        Assert.Equal("Omar Haddad (removed guest)", removed.Label());
    }

    /// <summary>A member who left is still marked as having left.</summary>
    [Fact]
    public void A_member_who_left_is_labelled_as_having_left()
    {
        var left = new UserInfo(OmarId, "Omar", "Haddad", "omar@example.com", IsPastMember: true);

        Assert.Equal("Omar Haddad (left the group)", left.Label());
    }

    /// <summary>An expense the reader shares with one of the group's guests.</summary>
    private static TransactionDetailsResponse Details(bool paidByGuest) => new()
    {
        Id = Expense,
        Name = "Dinner",
        Amount = 60m,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = Guid.NewGuid(),
        GroupName = "The flat",
        PaidByUserId = paidByGuest ? OmarId : MeId,
        PaidByUserName = paidByGuest ? "Omar Haddad" : "Ana Benitez",
        PaidByIsGuest = paidByGuest,
        Splits =
        [
            new TransactionSplitResponse(MeId, "Ana Benitez", 30m),
            new TransactionSplitResponse(OmarId, "Omar Haddad", 30m, IsGuest: true)
        ]
    };

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(TransactionDetailsResponse details)
    {
        _transactions
            .Setup(client => client.GetTransactionAsync(Expense, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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
