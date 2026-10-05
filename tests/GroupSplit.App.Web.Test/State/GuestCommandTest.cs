using System.Text.Json;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What the app says when a guest is added, sent a link, removed, settled between, or when
/// their link is claimed or turned down.
/// </summary>
/// <remarks>
/// The sentences are the only place a person finds out what one of these writes did, and
/// several of them say something the press alone does not: that turning a link down left
/// the guest where they were, that a claim cancelled repayments, that a refusal to remove
/// somebody is about a specific amount owed a specific way.
/// <para>
/// These construct the command service rather than rendering a page. The message is the
/// unit -- which severity, and whether the number in it is the one the API sent -- and a
/// page would only put a component's own rendering between the test and that.
/// </para>
/// </remarks>
public class GuestCommandTest
{
    private static readonly Guid Flat = Guid.NewGuid();

    private static readonly Guid Omar = Guid.NewGuid();

    private static readonly Guid Luz = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<IInvitationsClient> _invitations = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly GroupCommands _commands;

    private readonly List<(string Message, Severity Severity)> _said = [];

    public GuestCommandTest()
    {
        _snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        var errors = new ApiErrorPresenter(
            new TestNavigationManager(), _snackbar.Object);

        _commands = new GroupCommands(_groups.Object, _invitations.Object, errors, _snackbar.Object, _changes);
    }

    private static GroupGuestResponse Guest(Guid id, string name, string? token = null) =>
        new(id, Flat, "The flat", name, "Anabel", DateTimeOffset.UtcNow,
            HasLink: token is not null, Token: token,
            LinkIssuedByUserName: token is null ? null : "Anabel",
            LinkIssuedAt: token is null ? null : DateTimeOffset.UtcNow);

    // ---- Adding ------------------------------------------------------------------------------

    /// <summary>
    /// Adding somebody sends nobody anything, and the message does not suggest it did.
    /// </summary>
    [Fact]
    public async Task Adding_a_guest_says_they_are_a_guest_and_mints_no_link()
    {
        _groups
            .Setup(c => c.AddGroupGuestsAsync(Flat, It.IsAny<AddGuestsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Guest(Omar, "Omar")]);

        Assert.True(await _commands.AddGuestsAsync(Flat, "The flat", new AddGuestsRequest { Names = ["Omar"] }));

        _groups.Verify(c => c.AddGroupGuestsAsync(Flat,
            It.Is<AddGuestsRequest>(request => !request.IssueLinks && request.Names.Single() == "Omar"),
            It.IsAny<CancellationToken>()), Times.Once);

        var said = Assert.Single(_said);
        Assert.Equal("Omar added to The flat as a guest.", said.Message);
        Assert.DoesNotContain("link", said.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adding_several_guests_counts_them()
    {
        _groups
            .Setup(c => c.AddGroupGuestsAsync(Flat, It.IsAny<AddGuestsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Guest(Omar, "Omar"), Guest(Luz, "Luz")]);

        Assert.True(await _commands.AddGuestsAsync(Flat, "The flat", new AddGuestsRequest { Names = ["Omar", "Luz"] }));

        Assert.Contains(_said, said => said.Message == "2 guests added to The flat.");
    }

    // ---- Links -------------------------------------------------------------------------------

    /// <summary>
    /// Issuing a link is quiet: the component that copies it is the one that knows whether
    /// the clipboard took it, and says so. Two messages for one press would be one too many.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issuing_a_link_asks_for_a_replacement_only_when_told_to_and_says_nothing(bool replace)
    {
        _groups
            .Setup(c => c.IssueGuestLinkAsync(Flat, Omar, It.IsAny<IssueGuestLinkRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guest(Omar, "Omar", "token-omar"));

        var issued = await _commands.IssueGuestLinkAsync(Flat, Omar, replace);

        Assert.Equal("token-omar", issued?.Token);
        _groups.Verify(c => c.IssueGuestLinkAsync(Flat, Omar,
            It.Is<IssueGuestLinkRequest>(request => request.Replace == replace), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(_said);
    }

    /// <summary>
    /// Stopping a link says the guest is still there, because "stop link" beside "remove"
    /// could be read as the gentler way of taking somebody out.
    /// </summary>
    [Fact]
    public async Task Stopping_a_link_says_the_guest_stays()
    {
        Assert.True(await _commands.RevokeGuestLinkAsync(Flat, Omar, "Omar"));

        _groups.Verify(c => c.RevokeGuestLinkAsync(Flat, Omar, It.IsAny<CancellationToken>()), Times.Once);

        var said = Assert.Single(_said);
        Assert.Contains("no longer works", said.Message, StringComparison.Ordinal);
        Assert.Contains("still a guest", said.Message, StringComparison.Ordinal);
    }

    // ---- Removing ----------------------------------------------------------------------------

    /// <summary>
    /// A guest who is not square is refused, and the refusal names them, the amount and the
    /// direction -- the one thing the person who pressed Remove needs to go and fix it.
    /// </summary>
    [Theory]
    [InlineData(12.5, "Omar is still owed $12.50")]
    [InlineData(-7.25, "Omar still owes $7.25")]
    public async Task Removing_somebody_who_is_not_square_says_who_owes_what(double balance, string expected)
    {
        _groups
            .Setup(c => c.RemoveGroupMemberAsync(Flat, Omar, It.IsAny<CancellationToken>()))
            .ThrowsAsync(NotSettled((decimal)balance));

        Assert.False(await _commands.RemoveMemberAsync(Flat, Omar, "Omar"));

        var said = Assert.Single(_said);
        Assert.Equal(Severity.Error, said.Severity);
        Assert.Contains(expected, said.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a balance on the refusal, the table's own sentence -- which has to read for
    /// members, guests and leaving alike -- is what is shown.
    /// </summary>
    [Fact]
    public async Task A_refusal_without_a_balance_falls_back_to_the_general_message()
    {
        _groups
            .Setup(c => c.RemoveGroupMemberAsync(Flat, Omar, It.IsAny<CancellationToken>()))
            .ThrowsAsync(NotSettled(balance: null));

        Assert.False(await _commands.RemoveMemberAsync(Flat, Omar, "Omar"));

        var said = Assert.Single(_said);
        Assert.Contains(ErrorMessages.For(ErrorCodes.GroupMemberNotSettled), said.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_somebody_who_is_square_says_so()
    {
        Assert.True(await _commands.RemoveMemberAsync(Flat, Omar, "Omar"));

        Assert.Contains(_said, said => said.Message == "Omar removed from the group." && said.Severity == Severity.Success);
    }

    // ---- Repayments between two other people ----------------------------------------------------

    /// <summary>
    /// Both ends named, because the caller is on neither of them.
    /// </summary>
    [Fact]
    public async Task Recording_a_repayment_between_two_guests_names_both()
    {
        var request = new RecordRepaymentRequest { FromUserId = Omar, ToUserId = Luz, Amount = 20m };

        Assert.True(await _commands.RecordRepaymentAsync(Flat, request, "Omar", "Luz"));

        _groups.Verify(c => c.RecordRepaymentAsync(Flat, request, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_said, said => said.Message == "Recorded: Omar paid Luz $20.00.");
    }

    // ---- Answering a link ----------------------------------------------------------------------

    /// <summary>
    /// Turning a link down only stops the link, and the message says so -- the guest stays,
    /// with everything recorded for them, and nothing is handed to anybody.
    /// </summary>
    [Fact]
    public async Task Declining_says_the_group_still_has_the_guest()
    {
        _invitations
            .Setup(c => c.DeclineInvitationAsync("token-omar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationClosedResponse(Guid.NewGuid(), Flat, "The flat", "Omar",
                InvitationOutcome.Declined, SharesMoved: 0, AmountOwed: 0m, PaymentsMoved: 0, AmountPaid: 0m,
                RulesAffected: 0, RulesEmptied: 0, AbsorbedByUserId: null, AbsorbedByUserName: null));

        Assert.True(await _commands.DeclineInvitationAsync("token-omar", "The flat"));

        var said = Assert.Single(_said);
        Assert.Equal("The flat still has Omar as a guest. The link no longer works.", said.Message);
    }

    /// <summary>
    /// Claiming says which rules now name the claimer.
    /// </summary>
    /// <remarks>
    /// The shares and the payments are the past: amounts already recorded, now theirs. A
    /// rule place is the future, and it was the silent half of a claim until it was counted
    /// -- somebody joins, and a category they have never heard of starts giving them a
    /// share of every expense filed under it.
    /// </remarks>
    [Fact]
    public async Task Claiming_says_which_rules_name_you_now()
    {
        Claimed(rulesTaken: 1);

        Assert.NotNull(await _commands.ClaimInvitationAsync("token-omar"));

        Assert.Contains(_said, said => said.Message.Contains("One split rule", StringComparison.Ordinal));
    }

    /// <summary>
    /// A claim that inherited no rule place does not invent one.
    /// </summary>
    [Fact]
    public async Task Claiming_says_nothing_about_rules_when_none_name_you()
    {
        Claimed(rulesTaken: 0);

        Assert.NotNull(await _commands.ClaimInvitationAsync("token-omar"));

        Assert.DoesNotContain(_said, said => said.Message.Contains("split rule", StringComparison.Ordinal));
    }

    /// <summary>
    /// Repayments between the guest and the claimer go in the claim -- they would read as
    /// the claimer paying themselves -- and the joining message says how many.
    /// </summary>
    [Fact]
    public async Task Claiming_says_how_many_repayments_cancelled_out()
    {
        Claimed(rulesTaken: 0, settlementsCancelled: 2);

        Assert.NotNull(await _commands.ClaimInvitationAsync("token-omar"));

        var joined = Assert.Single(_said, said => said.Severity == Severity.Success);
        Assert.Equal(
            "You have joined The flat as Omar. 2 repayments between you and Omar cancelled out.",
            joined.Message);
    }

    [Fact]
    public async Task A_claim_that_cancelled_nothing_does_not_mention_repayments()
    {
        Claimed(rulesTaken: 0);

        Assert.NotNull(await _commands.ClaimInvitationAsync("token-omar"));

        Assert.DoesNotContain(_said, said => said.Message.Contains("repayment", StringComparison.Ordinal));
    }

    private void Claimed(int rulesTaken, int settlementsCancelled = 0) =>
        _invitations
            .Setup(c => c.ClaimInvitationAsync("token-omar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationClaimedResponse(
                Flat, "The flat", 3, "Omar",
                SharesTaken: 2, AmountOwed: 40m, PaymentsTaken: 0, AmountPaid: 0m,
                RulesTaken: rulesTaken, SettlementsCancelled: settlementsCancelled,
                AmountCancelled: settlementsCancelled * 10m));

    /// <summary>
    /// GROUP_MEMBER_NOT_SETTLED as the generated client would raise it: written by the API's
    /// serializer, read back by the client's, with the balance as an extension member.
    /// </summary>
    private static ApiException<ProblemDetails> NotSettled(decimal? balance)
    {
        var members = new Dictionary<string, object?>
        {
            ["title"] = "Not settled",
            ["status"] = 409,
            ["code"] = ErrorCodes.GroupMemberNotSettled
        };

        if (balance is { } value) members["balance"] = value;

        var json = JsonSerializer.Serialize(members, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var problem = JsonSerializer.Deserialize<ProblemDetails>(json, GroupSplitSerializer.Options)!;

        return new ApiException<ProblemDetails>("refused", 409, json,
            new Dictionary<string, IEnumerable<string>>(), problem, null!);
    }

    /// <summary>A navigation manager that goes nowhere, for a presenter that never navigates here.</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/claim/abc");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
