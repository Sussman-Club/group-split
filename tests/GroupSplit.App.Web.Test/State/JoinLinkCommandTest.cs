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
/// What the app says and does around a join link.
/// </summary>
/// <remarks>
/// The commands are where a write becomes a sentence somebody reads, and two of these
/// sentences are the whole feature working or not. Making a new link silently puts the old
/// one out, which is a thing a person can be caught out by and so has to be told. And
/// following a link to a group you are already in is not a failure -- it is the ordinary
/// result of a URL that lives in a chat thread, and dressing it as an error would tell
/// somebody something went wrong when nothing did.
/// </remarks>
public class JoinLinkCommandTest
{
    private static readonly Guid Trip = Guid.NewGuid();

    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<IInvitationsClient> _invitations = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly GroupCommands _commands;

    private readonly List<(string Message, Severity Severity)> _said = [];

    public JoinLinkCommandTest()
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

    private static GroupJoinLinkResponse ALink(string token) =>
        new(Guid.NewGuid(), Trip, "Weekend in Lisbon", token, "Daniel Rivero",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

    /// <summary>A refusal shaped the way the generated client hands one over.</summary>
    private static ApiException<ProblemDetails> Refusal(int status, string code)
    {
        var members = new Dictionary<string, object?>
        {
            ["type"] = "https://groupsplit.app/errors/x",
            ["title"] = "A title for developers",
            ["status"] = status,
            ["detail"] = "A detail for developers",
            ["code"] = code,
            ["traceId"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        };

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            JsonSerializer.Serialize(members, Api), GroupSplitSerializer.Options)!;

        return new ApiException<ProblemDetails>("refused", status, JsonSerializer.Serialize(problem, Api),
            new Dictionary<string, IEnumerable<string>>(), problem, null!);
    }

    [Fact]
    public async Task Making_a_link_says_the_old_one_has_stopped_working()
    {
        _groups
            .Setup(c => c.CreateGroupJoinLinkAsync(Trip, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ALink("abc"));

        var announced = false;
        _changes.GroupsChanged += () => { announced = true; return Task.CompletedTask; };

        var made = await _commands.CreateJoinLinkAsync(Trip, "Weekend in Lisbon");

        Assert.Equal("abc", made?.Token);
        Assert.True(announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Contains("Weekend in Lisbon", message, StringComparison.Ordinal);

        // The part a person cannot see for themselves: pressing this quietly killed the URL
        // they may already have pasted somewhere.
        Assert.Contains("stopped working", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_a_link_off_says_so_and_tells_the_pages()
    {
        _groups
            .Setup(c => c.RevokeGroupJoinLinksAsync(Trip, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var announced = false;
        _changes.GroupsChanged += () => { announced = true; return Task.CompletedTask; };

        Assert.True(await _commands.RevokeJoinLinkAsync(Trip, "Weekend in Lisbon"));
        Assert.True(announced);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Contains("no longer works", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Joining_by_link_says_which_group_was_joined()
    {
        _invitations
            .Setup(c => c.AcceptJoinLinkAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JoinedGroupResponse(Trip, "Weekend in Lisbon", 4, false));

        var joined = await _commands.JoinByLinkAsync("abc");

        Assert.Equal(Trip, joined?.GroupId);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Success, severity);
        Assert.Equal("You have joined Weekend in Lisbon.", message);
    }

    [Fact]
    public async Task Following_a_link_to_a_group_you_are_in_is_not_an_error()
    {
        _invitations
            .Setup(c => c.AcceptJoinLinkAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JoinedGroupResponse(Trip, "Weekend in Lisbon", 4, true));

        var joined = await _commands.JoinByLinkAsync("abc");

        // Still answered with the group, because the page's next move is to open it -- the
        // person was trying to get somewhere and they are allowed to be there.
        Assert.Equal(Trip, joined?.GroupId);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Info, severity);
        Assert.Equal("You are already in Weekend in Lisbon.", message);
    }

    [Fact]
    public async Task A_withdrawn_link_is_told_apart_from_one_that_expired()
    {
        _invitations
            .Setup(c => c.AcceptJoinLinkAsync("gone", It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusal(409, ErrorCodes.GroupJoinLinkRevoked));

        _invitations
            .Setup(c => c.AcceptJoinLinkAsync("stale", It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusal(409, ErrorCodes.GroupJoinLinkExpired));

        Assert.Null(await _commands.JoinByLinkAsync("gone"));
        Assert.Null(await _commands.JoinByLinkAsync("stale"));

        Assert.Equal(2, _said.Count);
        Assert.All(_said, said => Assert.Equal(Severity.Error, said.Severity));

        // Two different things to have happened to the link, and the person holding it is
        // told which -- the reason the API keeps the two codes apart at all.
        Assert.Contains("withdrawn", _said[0].Message, StringComparison.Ordinal);
        Assert.Contains("expired", _said[1].Message, StringComparison.Ordinal);
        Assert.NotEqual(_said[0].Message, _said[1].Message);
    }

    /// <summary>A navigation manager that goes nowhere, for a presenter that never navigates here.</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/join/abc");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
