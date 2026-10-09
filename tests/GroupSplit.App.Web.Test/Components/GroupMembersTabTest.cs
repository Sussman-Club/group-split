using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Extensions;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's People tab: its three sections, what it asks the server for, and what each
/// of a guest's buttons does.
/// </summary>
/// <remarks>
/// The page above it re-renders on every announcement about the group -- several per load
/// -- and a re-render sets the tab's parameters again. The tab used to read the members,
/// the invitations and the join link on every parameter set, which was three requests per
/// render for three answers it already had. They are read once per group, and again only
/// when a write says the membership moved.
/// <para>
/// The second card was "Invited", with a withdraw button that handed a position to a
/// member. It is "Guests" now: people the group records for, with a link only when somebody
/// sends one, and removed like a member once they are square.
/// </para>
/// </remarks>
public class GroupMembersTabTest : ComponentTest
{
    private static readonly GroupResponse Group = new(Guid.NewGuid(), "Weekend in Lisbon", 2);
    private static readonly UserInfo Me = new(Guid.NewGuid(), "Anabel", "Benítez", "anabel@test.com");
    private static readonly UserInfo Omar = new(Guid.NewGuid(), "Omar", "Sussman", "omar@test.com");
    private static readonly UserInfo FormerOmar = new(Guid.NewGuid(), "Former", "Omar", null,
        IsPastMember: true);

    /// <summary>A guest who was removed once they were square: past, and a guest.</summary>
    private static readonly UserInfo FormerGuest = new(Guid.NewGuid(), "Pilar", null, null,
        IsGuest: true, IsPastMember: true);

    /// <summary>
    /// A guest nobody has sent a link. The members listing carries them, because they can be
    /// given a share -- so this tab has to know they are not a member.
    /// </summary>
    /// <remarks>
    /// A name and no address: nobody has signed in as this person, and most guests never
    /// will.
    /// </remarks>
    private static readonly UserInfo Daniel = new(Guid.NewGuid(), "Daniel", null, null, IsGuest: true);

    /// <summary>A guest with a link out.</summary>
    private static readonly UserInfo Luz = new(Guid.NewGuid(), "Luz", null, null, IsGuest: true);

    private static readonly DateTimeOffset Added = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Sent = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IGroupsPageStateService> _state = new();
    private readonly Mock<IDialogService> _dialogs = new();

    /// <summary>How the next confirmation is answered.</summary>
    private DialogResult _confirmation = DialogResult.Ok(true);

    /// <summary>What every confirmation asked, in order.</summary>
    private readonly List<(string Title, string Question)> _asked = [];

    public GroupMembersTabTest()
    {
        _state.SetupGet(state => state.SelectedGroup).Returns(Group);

        _state
            .Setup(state => state.GetGroupMembersAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new[] { Me, Omar, Daniel, Luz }.ToAsyncEnumerable());

        _state
            .Setup(state => state.GetGroupGuestsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                GuestRow(Daniel, token: null),
                GuestRow(Luz, token: "token-luz")
            ]);

        _state
            .Setup(state => state.GetGroupPastMembersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([FormerOmar, FormerGuest]);

        _state
            .Setup(state => state.GetGroupJoinLinkAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupJoinLinkResponse?)null);

        // A real MudBlazor reference, already finished with the answer -- the state a
        // component is actually handed. See ConfirmationResultTest for why a stand-in is
        // the wrong tool here.
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<ConfirmationDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ConfirmationDialog>>(), It.IsAny<DialogOptions>()))
            .Callback((string title, DialogParameters parameters, DialogOptions _) =>
                _asked.Add((title, (string)parameters[nameof(ConfirmationDialog.ContentText)]!)))
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);

                reference.Dismiss(_confirmation);

                return reference;
            });

        // The clipboard says yes. The link is built from the address the app is on, which
        // under bUnit is http://localhost/.
        JSInterop.Setup<bool>("gs.copy", _ => true).SetResult(true);

        Services.AddSingleton(_state.Object);
        Services.AddSingleton(_dialogs.Object);
    }

    private static GroupGuestResponse GuestRow(UserInfo guest, string? token) =>
        new(guest.Id, Group.Id, Group.Name, guest.FirstName!, "Anabel", Added,
            HasLink: token is not null, Token: token,
            LinkIssuedByUserName: token is null ? null : "Anabel",
            LinkIssuedAt: token is null ? null : Sent,
            LinkExpiresAt: token is null ? null : Sent.AddDays(30));

    /// <summary>A guest whose link went out and ran out, unclaimed.</summary>
    private static GroupGuestResponse ExpiredRow(UserInfo guest) =>
        new(guest.Id, Group.Id, Group.Name, guest.FirstName!, "Anabel", Added,
            HasLink: false, Token: null,
            LinkIssuedByUserName: "Anabel",
            LinkIssuedAt: Sent,
            LinkExpiresAt: Sent.AddDays(30),
            LinkExpired: true);

    private IRenderedComponent<GroupMembersTab> Render() =>
        base.Render<GroupMembersTab>(parameters => parameters
            .Add(tab => tab.GroupId, Group.Id)
            .Add(tab => tab.UserInfo, Me));

    private int Reads(string method) => _state.Invocations.Count(i => i.Method.Name == method);

    /// <summary>A card, by the heading it carries.</summary>
    private static IElement Card(IRenderedComponent<GroupMembersTab> tab, string heading) =>
        tab.FindAll(".gs-card").Single(card => card.QuerySelector(".gs-card-title h2")?.TextContent.Trim() == heading);

    /// <summary>A guest's row in the Guests card.</summary>
    private static IElement Row(IRenderedComponent<GroupMembersTab> tab, string name) =>
        Card(tab, "Guests").QuerySelectorAll(".gs-row")
            .Single(row => row.QuerySelector(".title")!.TextContent.Trim() == name);

    private static IElement Button(IElement row, string text) =>
        row.QuerySelectorAll("button").Single(button => button.TextContent.Trim() == text);

    private void SaidSuccess(string message) =>
        Snackbar.Verify(snackbar => snackbar.Add(message, Severity.Success, It.IsAny<Action<SnackbarOptions>>(),
            It.IsAny<string>()), Times.Once);

    private void Copied(string url) =>
        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "gs.copy" && Equals(invocation.Arguments[0], url));

    // ---- The three sections ----------------------------------------------------------------

    [Fact]
    public void Members_guests_and_past_people_each_have_a_section()
    {
        var tab = Render();

        Assert.Contains("Omar Sussman", Card(tab, "Members").InnerHtml);
        Assert.Contains("Daniel", Card(tab, "Guests").InnerHtml);
        Assert.Contains("Luz", Card(tab, "Guests").InnerHtml);
        Assert.Contains("Former Omar", Card(tab, "Past members and guests").InnerHtml);
        Assert.Contains("Pilar", Card(tab, "Past members and guests").InnerHtml);
    }

    /// <summary>
    /// The members count is the people who joined, and each guest is listed once.
    /// </summary>
    /// <remarks>
    /// The listing behind this tab answers the wider question -- everybody the group may
    /// record money against -- so it contains the guests as well as the two members.
    /// Counting them as members would say four; showing them in both cards would say Daniel
    /// twice, once as though he had joined.
    /// </remarks>
    [Fact]
    public void Guests_are_counted_as_guests_and_not_as_members()
    {
        var tab = Render();

        var members = Card(tab, "Members");
        var guests = Card(tab, "Guests");

        Assert.Equal("2", members.QuerySelector(".gs-card-title .gs-tag")!.TextContent.Trim());
        Assert.Equal("2", guests.QuerySelector(".gs-card-title .gs-tag")!.TextContent.Trim());

        Assert.DoesNotContain("Daniel", members.InnerHtml);
        Assert.DoesNotContain("Luz", members.InnerHtml);
    }

    /// <summary>
    /// Who does the recording is the one thing about guests nobody could guess, so it is
    /// said once at the top of their card.
    /// </summary>
    [Fact]
    public void The_guests_card_says_who_records_for_them()
    {
        var tab = Render();

        Assert.Contains(ParticipantCopy.GuestsNote, Card(tab, "Guests").TextContent);
    }

    /// <summary>
    /// The line under a guest says when and by whom they were added, and whether a link is
    /// out -- the fact that decides which buttons the row offers.
    /// </summary>
    [Fact]
    public void Each_guest_says_who_added_them_and_whether_a_link_is_out()
    {
        var tab = Render();

        var added = Added.ToLocalTime().ToString("d MMM yyyy");
        var sent = Sent.ToLocalTime().ToString("d MMM yyyy");
        var expires = Sent.AddDays(30).ToLocalTime().ToString("d MMM yyyy");

        Assert.Equal($"Guest · added {added} by Anabel · no link",
            Row(tab, "Daniel").QuerySelector(".meta")!.TextContent.Trim());
        Assert.Equal($"Guest · added {added} by Anabel · link sent {sent}, expires {expires}",
            Row(tab, "Luz").QuerySelector(".meta")!.TextContent.Trim());
    }

    /// <summary>
    /// A link that ran out says so, with the day, and the row offers "Send link" again --
    /// not "Copy link", which would read as though the old URL still worked.
    /// </summary>
    [Fact]
    public void A_guest_whose_link_expired_says_so_and_is_offered_a_new_one()
    {
        _state
            .Setup(state => state.GetGroupGuestsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([GuestRow(Daniel, token: null), ExpiredRow(Luz)]);

        var tab = Render();

        var added = Added.ToLocalTime().ToString("d MMM yyyy");
        var expired = Sent.AddDays(30).ToLocalTime().ToString("d MMM yyyy");
        var row = Row(tab, "Luz");

        Assert.Equal($"Guest · added {added} by Anabel · link expired {expired}",
            row.QuerySelector(".meta")!.TextContent.Trim());

        Assert.NotNull(row.QuerySelector("button[aria-label='Send Luz a link']"));
        Assert.Null(row.QuerySelector("button[aria-label='Copy the link for Luz']"));
    }

    /// <summary>
    /// A removed guest is told apart from a member who went: they never joined, so "past
    /// member" would claim a membership the ledger never had.
    /// </summary>
    [Fact]
    public void Past_guests_and_past_members_are_labelled_apart()
    {
        var tab = Render();

        var past = Card(tab, "Past members and guests").QuerySelectorAll(".gs-row").ToList();

        Assert.StartsWith("Past member",
            past.Single(row => row.TextContent.Contains("Former Omar")).QuerySelector(".meta")!.TextContent.Trim());
        Assert.StartsWith("Past guest",
            past.Single(row => row.TextContent.Contains("Pilar")).QuerySelector(".meta")!.TextContent.Trim());
    }

    /// <summary>
    /// Nothing is "invited", nobody is "waiting", and there is no withdrawing: those were the
    /// old card's words, and each one said something about guests that is no longer true.
    /// </summary>
    [Fact]
    public void Nothing_on_the_tab_is_an_invitation_any_more()
    {
        var tab = Render();

        Assert.DoesNotContain("Invited", tab.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting", tab.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Withdraw", tab.Markup, StringComparison.Ordinal);
    }

    // ---- What it reads ---------------------------------------------------------------------

    /// <summary>
    /// The defect: every render of the page above set the parameters again, and every
    /// parameter set was a fresh read of everything.
    /// </summary>
    [Fact]
    public void Setting_the_parameters_again_does_not_ask_the_server_again()
    {
        var tab = Render();

        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupMembersAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupGuestsAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupJoinLinkAsync)));

        // The page renders again -- a balance landed, say -- and the cascading user is a
        // reference, so the tab cannot tell its parameters did not move.
        tab.Render(parameters => parameters
            .Add(t => t.GroupId, Group.Id)
            .Add(t => t.UserInfo, new UserInfo(Me.Id, Me.FirstName, Me.LastName, Me.Email)));

        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupMembersAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupGuestsAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupJoinLinkAsync)));
    }

    /// <summary>A write that moves the membership is the one thing that re-reads it.</summary>
    [Fact]
    public async Task A_change_to_the_groups_re_reads_the_lists()
    {
        var tab = Render();

        await tab.InvokeAsync(() => Changes.NotifyGroupsChangedAsync());

        Assert.Equal(2, Reads(nameof(IGroupsPageStateService.GetGroupMembersAsync)));
        Assert.Equal(2, Reads(nameof(IGroupsPageStateService.GetGroupGuestsAsync)));
        Assert.Equal(2, Reads(nameof(IGroupsPageStateService.GetGroupJoinLinkAsync)));
    }

    /// <summary>A members read that fails the way a dropped connection does.</summary>
    private static async IAsyncEnumerable<UserInfo> Unreachable()
    {
        await Task.Yield();

        throw new HttpRequestException("The server could not be reached.");

#pragma warning disable CS0162 // An iterator needs a yield to be one.
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// A reload that fails is said as a load failure, in the card, with what was on screen
    /// left there -- and the announcement that started it completes (#323).
    /// </summary>
    /// <remarks>
    /// The members were read with nothing around the call, so the failure went up through the
    /// announcement into whatever write had made it, and under server rendering a failure in
    /// a parameter set took the circuit down.
    /// </remarks>
    [Fact]
    public async Task A_failed_reload_is_shown_as_a_load_failure()
    {
        var tab = Render();

        _state
            .Setup(state => state.GetGroupMembersAsync(It.IsAny<CancellationToken>()))
            .Returns(Unreachable);

        await tab.InvokeAsync(() => Changes.NotifyGroupsChangedAsync());

        Assert.Contains("Could not load who is in this group, so this may be out of date.",
            Card(tab, "Members").TextContent, StringComparison.Ordinal);
        Assert.Contains("Omar Sussman", Card(tab, "Members").InnerHtml, StringComparison.Ordinal);

        Snackbar.Verify(snackbar => snackbar.Add(
            It.Is<string>(message => message.StartsWith("Could not load who is in this group.", StringComparison.Ordinal)),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);

        // And "Try again" reads them again, and the notice goes once they arrive.
        _state
            .Setup(state => state.GetGroupMembersAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new[] { Me, Omar, Daniel, Luz }.ToAsyncEnumerable());

        await Card(tab, "Members").QuerySelectorAll("button")
            .Single(button => button.TextContent.Trim() == "Try again")
            .ClickAsync(new());

        Assert.DoesNotContain("Could not load who is in this group", tab.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the issue was raised about: adding a guest goes through, the refresh it
    /// announces fails, and the person is told the guest was added -- not that adding failed,
    /// which is what sent them to add the guest a second time (#323).
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_after_adding_a_guest_does_not_report_the_add_as_failed()
    {
        var groups = new Mock<IGroupsClient>();
        groups
            .Setup(client => client.AddGroupGuestsAsync(Group.Id, It.IsAny<AddGuestsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([GuestRow(Daniel, token: null)]);

        var commands = new GroupSplit.App.Shared.Services.Commands.GroupCommands(
            groups.Object, Mock.Of<IInvitationsClient>(),
            Services.GetRequiredService<GroupSplit.App.Shared.Services.Errors.ApiErrorPresenter>(),
            Snackbar.Object, Changes);

        var tab = Render();

        _state
            .Setup(state => state.GetGroupMembersAsync(It.IsAny<CancellationToken>()))
            .Returns(Unreachable);

        var added = await tab.InvokeAsync(() =>
            commands.AddGuestsAsync(Group.Id, Group.Name, new AddGuestsRequest { Names = ["Daniel"] }));

        Assert.True(added);
        SaidSuccess($"Daniel added to {Group.Name} as a guest.");
        Snackbar.Verify(snackbar => snackbar.Add(
            It.Is<string>(message => message.StartsWith("Could not add them.", StringComparison.Ordinal)),
            It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);
    }

    // ---- Which group a confirmed action is for ----------------------------------------------

    /// <summary>
    /// A confirmation that stays open until the test answers it, so the page can move on
    /// underneath it first.
    /// </summary>
    private Func<DialogResult, Task> HoldConfirmationsOpen()
    {
        DialogReference? open = null;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<ConfirmationDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ConfirmationDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(() => open = new DialogReference(Guid.NewGuid(), _dialogs.Object));

        return result =>
        {
            open!.Dismiss(result);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// "Remove member" opened on one group and confirmed after the page moved to another
    /// removes them from the group it was opened on (#323).
    /// </summary>
    /// <remarks>
    /// The write read the selected group when the button was pressed, so the member was
    /// removed from whichever group was on screen by then.
    /// </remarks>
    [Fact]
    public async Task Removing_a_member_acts_on_the_group_the_question_was_asked_in()
    {
        var answer = HoldConfirmationsOpen();
        var elsewhere = Guid.NewGuid();

        var tab = Render();

        var removing = tab.Find("button[aria-label='Remove Omar Sussman']").ClickAsync(new());

        tab.Render(parameters => parameters
            .Add(t => t.GroupId, elsewhere)
            .Add(t => t.UserInfo, Me));

        await tab.InvokeAsync(() => answer(DialogResult.Ok(true)));
        await removing;

        _state.Verify(state => state.RemoveGroupMemberAsync(Group.Id, Omar.Id, "Omar Sussman",
            It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(state => state.RemoveGroupMemberAsync(elsewhere, It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The same for leaving: it is the group the question named that is left.</summary>
    [Fact]
    public async Task Leaving_acts_on_the_group_the_question_was_asked_in()
    {
        var answer = HoldConfirmationsOpen();
        var elsewhere = Guid.NewGuid();

        var tab = Render();

        var leaving = tab.FindAll("button").Single(button => button.TextContent.Trim() == "Leave group")
            .ClickAsync(new());

        tab.Render(parameters => parameters
            .Add(t => t.GroupId, elsewhere)
            .Add(t => t.UserInfo, Me));

        await tab.InvokeAsync(() => answer(DialogResult.Ok(true)));
        await leaving;

        _state.Verify(state => state.LeaveGroupAsync(Group.Id, It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(state => state.LeaveGroupAsync(elsewhere, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Adding ----------------------------------------------------------------------------

    /// <summary>
    /// "Add people" opens the guests dialog, and what it hands back is added -- with no link.
    /// </summary>
    [Fact]
    public async Task Adding_people_adds_them_as_guests()
    {
        var request = new AddGuestsRequest { Names = ["Nuria"] };

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<AddGuestsDialog>(
                "Add people", It.IsAny<DialogParameters<AddGuestsDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);

                reference.Dismiss(DialogResult.Ok(request));

                return reference;
            });

        var tab = Render();

        await tab.FindAll("button").Single(button => button.TextContent.Trim() == "Add people").ClickAsync(new());

        _state.Verify(state => state.AddGuestsAsync(Group.Id,
            It.Is<AddGuestsRequest>(sent => !sent.IssueLinks && sent.Names.Single() == "Nuria"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- A guest's link --------------------------------------------------------------------

    /// <summary>
    /// A guest with no link gets "Send link", which issues one and copies it in the same
    /// press, and says what the link does.
    /// </summary>
    [Fact]
    public async Task Send_link_issues_a_link_and_copies_it()
    {
        _state
            .Setup(state => state.IssueGuestLinkAsync(Group.Id, Daniel.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GuestRow(Daniel, token: "token-daniel"));

        var tab = Render();

        // Only the guest without a link is offered it.
        Assert.DoesNotContain(Row(tab, "Luz").QuerySelectorAll("button"),
            button => button.TextContent.Trim() == "Send link");

        await Button(Row(tab, "Daniel"), "Send link").ClickAsync(new());

        _state.Verify(state => state.IssueGuestLinkAsync(Group.Id, Daniel.Id, false, It.IsAny<CancellationToken>()), Times.Once);
        Copied("http://localhost/claim/token-daniel");
        SaidSuccess("Link for Daniel copied. Whoever opens it can join as Daniel.");
    }

    /// <summary>
    /// A guest whose link is out gets "Copy link", which asks the server for the link that is
    /// out now -- without replacing it -- rather than trusting the listing, which another
    /// member may have made stale by replacing the link since.
    /// </summary>
    [Fact]
    public async Task Copy_link_copies_the_current_link_even_when_the_listing_is_stale()
    {
        _state
            .Setup(state => state.IssueGuestLinkAsync(Group.Id, Luz.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GuestRow(Luz, token: "token-luz-replaced-elsewhere"));

        var tab = Render();

        await Button(Row(tab, "Luz"), "Copy link").ClickAsync(new());

        _state.Verify(state => state.IssueGuestLinkAsync(Group.Id, Luz.Id, false, It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(state => state.IssueGuestLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()), Times.Never);
        Copied("http://localhost/claim/token-luz-replaced-elsewhere");
        SaidSuccess("Link for Luz copied. Whoever opens it can join as Luz.");
    }

    /// <summary>Opens the menu on a guest's row and presses one of its items.</summary>
    private async Task ChooseFromMenuAsync(IRenderedComponent<GroupMembersTab> tab,
        IRenderedComponent<MudPopoverProvider> popovers, string name, string item)
    {
        await Row(tab, name).QuerySelector(".mud-menu button")!.ClickAsync(new MouseEventArgs());

        popovers.WaitForAssertion(() => Assert.Contains(item, popovers.Markup, StringComparison.Ordinal));

        await popovers.FindAll(".mud-menu-item")
            .Single(element => element.TextContent.Trim() == item)
            .ClickAsync(new MouseEventArgs());
    }

    /// <summary>
    /// "New link" asks first -- the link already sent stops working -- then replaces it and
    /// copies the new one.
    /// </summary>
    [Fact]
    public async Task New_link_asks_first_then_replaces_the_link_and_copies_it()
    {
        _state
            .Setup(state => state.IssueGuestLinkAsync(Group.Id, Luz.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GuestRow(Luz, token: "token-luz-2"));

        var popovers = Render<MudPopoverProvider>();
        var tab = Render();

        await ChooseFromMenuAsync(tab, popovers, "Luz", "New link");

        var (_, question) = Assert.Single(_asked);
        Assert.Contains("stops working", question, StringComparison.Ordinal);

        tab.WaitForAssertion(() =>
            _state.Verify(state => state.IssueGuestLinkAsync(Group.Id, Luz.Id, true, It.IsAny<CancellationToken>()), Times.Once));
        Copied("http://localhost/claim/token-luz-2");
    }

    [Fact]
    public async Task Cancelling_a_new_link_keeps_the_old_one()
    {
        _confirmation = DialogResult.Cancel();

        var popovers = Render<MudPopoverProvider>();
        var tab = Render();

        await ChooseFromMenuAsync(tab, popovers, "Luz", "New link");

        Assert.Single(_asked);
        _state.Verify(state => state.IssueGuestLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>"Stop link" puts the link out and leaves the guest where they are.</summary>
    [Fact]
    public async Task Stop_link_revokes_the_link()
    {
        var popovers = Render<MudPopoverProvider>();
        var tab = Render();

        await ChooseFromMenuAsync(tab, popovers, "Luz", "Stop link");

        tab.WaitForAssertion(() =>
            _state.Verify(state => state.RevokeGuestLinkAsync(Group.Id, Luz.Id, "Luz", It.IsAny<CancellationToken>()), Times.Once));
        _state.Verify(state => state.RemoveGroupMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Removing a guest ------------------------------------------------------------------

    /// <summary>
    /// Removing a guest asks first, saying the rule -- square first -- and that their history
    /// stays, then goes through the same call as removing a member.
    /// </summary>
    [Fact]
    public async Task Removing_a_guest_asks_first_then_removes_them()
    {
        var tab = Render();

        await Row(tab, "Daniel").QuerySelector("button[aria-label='Remove Daniel']")!.ClickAsync(new());

        var (title, question) = Assert.Single(_asked);
        Assert.Equal("Remove guest", title);
        Assert.Equal("Remove Daniel? They have to be square with the group first. Their history stays.", question);

        _state.Verify(state => state.RemoveGroupMemberAsync(Group.Id, Daniel.Id, "Daniel", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Cancelling_the_removal_removes_nobody()
    {
        _confirmation = DialogResult.Cancel();

        var tab = Render();

        await Row(tab, "Daniel").QuerySelector("button[aria-label='Remove Daniel']")!.ClickAsync(new());

        _state.Verify(state => state.RemoveGroupMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
