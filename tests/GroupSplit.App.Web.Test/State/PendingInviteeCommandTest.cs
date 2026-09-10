using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What the app says when an invitation is answered or taken back.
/// </summary>
/// <remarks>
/// Every one of these writes moves a position from a stand-in onto somebody real, or off a
/// stand-in onto a member, and the sentences are the only place a person finds out. Two of
/// them are worth a test on their own account: the rule warning, because it is the one
/// thing here that changes what the group will do <em>next</em> rather than reporting what
/// it did; and the claim, because a claimer inherits a rule place silently otherwise.
/// <para>
/// These construct the command service rather than rendering a page. The message is the
/// unit -- which severity, and whether the number in it is the one the API sent -- and a
/// page would only put a component's own rendering between the test and that.
/// </para>
/// </remarks>
public class PendingInviteeCommandTest
{
    private static readonly Guid Flat = Guid.NewGuid();

    private static readonly Guid Invitation = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<IInvitationsClient> _invitations = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly GroupCommands _commands;

    private readonly List<(string Message, Severity Severity)> _said = [];

    public PendingInviteeCommandTest()
    {
        _snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        var errors = new ApiErrorPresenter(
            Mock.Of<IAuthService>(), new TestNavigationManager(), _snackbar.Object);

        _commands = new GroupCommands(_groups.Object, _invitations.Object, errors, _snackbar.Object, _changes);
    }

    /// <summary>
    /// What the API says it did with the position, with the two rule counts as given.
    /// </summary>
    private static InvitationClosedResponse Closed(int rulesAffected, int rulesEmptied) =>
        new(Invitation, Flat, "The flat", "Omar", InvitationOutcome.Withdrawn,
            SharesMoved: 1, AmountOwed: 20m, PaymentsMoved: 0, AmountPaid: 0m,
            RulesAffected: rulesAffected, RulesEmptied: rulesEmptied,
            AbsorbedByUserId: Guid.NewGuid(), AbsorbedByUserName: "Anabel");

    /// <summary>
    /// A rule left naming nobody is said out loud, as a warning.
    /// </summary>
    /// <remarks>
    /// The severity is half the point. A rule with nobody in it has changed what it means --
    /// a shares or percentage rule refuses the next expense filed under it, an even one
    /// starts dividing between everybody -- so this is not a receipt in the same tone as
    /// the two lines above it, and whoever caused it is the last person who can still fix
    /// it cheaply.
    /// </remarks>
    [Fact]
    public async Task Withdrawing_warns_when_it_has_left_a_rule_naming_nobody()
    {
        _groups
            .Setup(c => c.WithdrawGroupInvitationAsync(Flat, Invitation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Closed(rulesAffected: 1, rulesEmptied: 1));

        Assert.True(await _commands.WithdrawInvitationAsync(Flat, Invitation, "Omar"));

        var warning = Assert.Single(_said, said => said.Severity == Severity.Warning);

        Assert.Contains("names nobody", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// More than one reads as more than one, rather than as "1 split rule(s)".
    /// </summary>
    [Fact]
    public async Task Withdrawing_counts_the_rules_it_emptied()
    {
        _groups
            .Setup(c => c.WithdrawGroupInvitationAsync(Flat, Invitation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Closed(rulesAffected: 3, rulesEmptied: 2));

        Assert.True(await _commands.WithdrawInvitationAsync(Flat, Invitation, "Omar"));

        var warning = Assert.Single(_said, said => said.Severity == Severity.Warning);

        Assert.Contains("2 split rules", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rules that still name somebody are nobody's problem, and are not mentioned.
    /// </summary>
    [Fact]
    public async Task Withdrawing_says_nothing_about_a_rule_that_still_names_somebody()
    {
        _groups
            .Setup(c => c.WithdrawGroupInvitationAsync(Flat, Invitation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Closed(rulesAffected: 1, rulesEmptied: 0));

        Assert.True(await _commands.WithdrawInvitationAsync(Flat, Invitation, "Omar"));

        Assert.DoesNotContain(_said, said => said.Severity == Severity.Warning);
    }

    /// <summary>
    /// Declining warns about it too, and not only withdrawing.
    /// </summary>
    /// <remarks>
    /// The two are the same event from opposite ends -- an invitation closing, its position
    /// absorbed by a member -- and a rule can be left naming nobody either way. Declining
    /// is the end where the person hearing it is not in the group, which is a reason to
    /// word it carefully and not a reason to leave it out: they are the only one present.
    /// </remarks>
    [Fact]
    public async Task Declining_warns_when_it_has_left_a_rule_naming_nobody()
    {
        _invitations
            .Setup(c => c.DeclineInvitationAsync("token-omar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Closed(rulesAffected: 1, rulesEmptied: 1));

        Assert.True(await _commands.DeclineInvitationAsync("token-omar", "The flat"));

        var warning = Assert.Single(_said, said => said.Severity == Severity.Warning);

        Assert.Contains("names nobody", warning.Message, StringComparison.Ordinal);
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
        _invitations
            .Setup(c => c.ClaimInvitationAsync("token-omar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationClaimedResponse(
                Flat, "The flat", 3, "Omar",
                SharesTaken: 2, AmountOwed: 40m, PaymentsTaken: 0, AmountPaid: 0m,
                RulesTaken: 1));

        Assert.NotNull(await _commands.ClaimInvitationAsync("token-omar"));

        Assert.Contains(_said, said => said.Message.Contains("One split rule", StringComparison.Ordinal));
    }

    /// <summary>
    /// A claim that inherited no rule place does not invent one.
    /// </summary>
    [Fact]
    public async Task Claiming_says_nothing_about_rules_when_none_name_you()
    {
        _invitations
            .Setup(c => c.ClaimInvitationAsync("token-omar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationClaimedResponse(
                Flat, "The flat", 3, "Omar",
                SharesTaken: 2, AmountOwed: 40m, PaymentsTaken: 0, AmountPaid: 0m,
                RulesTaken: 0));

        Assert.NotNull(await _commands.ClaimInvitationAsync("token-omar"));

        Assert.DoesNotContain(_said, said => said.Message.Contains("split rule", StringComparison.Ordinal));
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
