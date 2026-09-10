using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's Members tab, and what it asks the server for.
/// </summary>
/// <remarks>
/// The page above it re-renders on every announcement about the group -- several per load
/// -- and a re-render sets the tab's parameters again. The tab used to read the members,
/// the invitations and the join link on every parameter set, which was three requests per
/// render for three answers it already had. They are read once per group, and again only
/// when a write says the membership moved.
/// </remarks>
public class GroupMembersTabTest : ComponentTest
{
    private static readonly GroupResponse Group = new(Guid.NewGuid(), "Weekend in Lisbon", 2);
    private static readonly UserInfo Me = new(Guid.NewGuid(), "Anabel", "Benítez", "anabel@test.com");
    private static readonly UserInfo Omar = new(Guid.NewGuid(), "Omar", "Sussman", "omar@test.com");

    /// <summary>
    /// Somebody the group has named and is waiting on. The members listing carries them,
    /// because they can be given a share -- so this tab has to know they are not a member.
    /// </summary>
    /// <remarks>
    /// A name and no address: nobody has signed in as this person, which is the case
    /// invitations exist for.
    /// </remarks>
    private static readonly UserInfo Daniel =
        new(Guid.NewGuid(), "Daniel", null, null, IsPendingInvitee: true);

    private readonly Mock<IGroupsPageStateService> _state = new();

    public GroupMembersTabTest()
    {
        _state.SetupGet(state => state.SelectedGroup).Returns(Group);

        _state
            .Setup(state => state.GetGroupMembersAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new[] { Me, Omar, Daniel }.ToAsyncEnumerable());

        _state
            .Setup(state => state.GetGroupInvitationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GroupInvitationResponse(Guid.NewGuid(), Group.Id, Group.Name, "Daniel",
                    "demo-token", "Anabel", DateTimeOffset.UtcNow, Daniel.Id)
            ]);

        _state
            .Setup(state => state.GetGroupJoinLinkAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GroupJoinLinkResponse?)null);

        Services.AddSingleton(_state.Object);
        Services.AddSingleton(Mock.Of<IDialogService>());
    }

    private IRenderedComponent<GroupMembersTab> Render() =>
        base.Render<GroupMembersTab>(parameters => parameters
            .Add(tab => tab.GroupId, Group.Id)
            .Add(tab => tab.UserInfo, Me));

    private int Reads(string method) => _state.Invocations.Count(i => i.Method.Name == method);

    [Fact]
    public void The_members_and_the_invitations_are_on_the_page()
    {
        var tab = Render();

        Assert.Contains("Omar Sussman", tab.Markup);
        Assert.Contains("Daniel", tab.Markup);
        Assert.Contains("1 waiting", tab.Markup);
    }

    /// <summary>
    /// The members count is the people who joined, and the invitee is listed once.
    /// </summary>
    /// <remarks>
    /// The listing behind this tab answers the wider question -- everybody the group may
    /// record money against -- so it contains the person being waited on as well as the two
    /// members. Counting them as a member would say three; showing them in both cards would
    /// say Daniel twice, once as though he were in.
    /// </remarks>
    [Fact]
    public void Somebody_still_to_answer_is_counted_as_invited_and_not_as_a_member()
    {
        var tab = Render();

        var members = tab.FindAll(".gs-card")[0];

        Assert.Equal("2", members.QuerySelector(".gs-card-title .gs-tag.neutral")!.TextContent.Trim());

        // And they are in the other card, the one that is about the invitation, rather than
        // in both.
        Assert.DoesNotContain("Daniel", members.InnerHtml);
        Assert.Contains("Daniel", tab.Markup);
    }

    /// <summary>
    /// The defect: every render of the page above set the parameters again, and every
    /// parameter set was a fresh read of everything.
    /// </summary>
    [Fact]
    public void Setting_the_parameters_again_does_not_ask_the_server_again()
    {
        var tab = Render();

        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupMembersAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupInvitationsAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupJoinLinkAsync)));

        // The page renders again -- a balance landed, say -- and the cascading user is a
        // reference, so the tab cannot tell its parameters did not move.
        tab.Render(parameters => parameters
            .Add(t => t.GroupId, Group.Id)
            .Add(t => t.UserInfo, new UserInfo(Me.Id, Me.FirstName, Me.LastName, Me.Email)));

        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupMembersAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupInvitationsAsync)));
        Assert.Equal(1, Reads(nameof(IGroupsPageStateService.GetGroupJoinLinkAsync)));
    }

    /// <summary>A write that moves the membership is the one thing that re-reads it.</summary>
    [Fact]
    public async Task A_change_to_the_groups_re_reads_the_lists()
    {
        var tab = Render();

        await tab.InvokeAsync(() => Changes.NotifyGroupsChangedAsync());

        Assert.Equal(2, Reads(nameof(IGroupsPageStateService.GetGroupMembersAsync)));
        Assert.Equal(2, Reads(nameof(IGroupsPageStateService.GetGroupInvitationsAsync)));
        Assert.Equal(2, Reads(nameof(IGroupsPageStateService.GetGroupJoinLinkAsync)));
    }
}
