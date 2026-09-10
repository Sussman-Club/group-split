using System.Text.Json;
using Bunit;
using GroupSplit.App.Shared.Services.Banking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The half of the OAuth return that reads what the tab wrote down before it left.
/// </summary>
/// <remarks>
/// Session storage is the only place a link in flight can live across the trip to a bank's
/// own sign-in page, and it is also storage the person owns and can edit. So what comes
/// back is not trusted to be a session: the browser checks its shape, and this checks that
/// what gets past the browser and fails to become one is still answered "nothing to pick
/// up". The alternative is <c>/bank/oauth</c> throwing on arrival, which is the one thing
/// that page must not do -- it is where somebody lands mid-way through linking their bank.
/// </remarks>
public class PlaidLinkLauncherTest : BunitContext
{
    [Fact]
    public async Task A_session_that_will_not_deserialise_reads_as_nothing_to_pick_up()
    {
        JSInterop.Setup<PendingBankLinkSession?>("gs.plaid.pending")
            .SetException(new JsonException("connectionId was not a Guid."));

        Assert.Null(await Launcher().PendingAsync());
    }

    /// <summary>The browser having no storage at all, which is private browsing.</summary>
    [Fact]
    public async Task No_session_reads_as_nothing_to_pick_up()
    {
        JSInterop.Setup<PendingBankLinkSession?>("gs.plaid.pending").SetResult(null);

        Assert.Null(await Launcher().PendingAsync());
    }

    [Fact]
    public async Task A_session_that_is_there_is_handed_back_whole()
    {
        var connectionId = Guid.NewGuid();

        JSInterop.Setup<PendingBankLinkSession?>("gs.plaid.pending")
            .SetResult(new PendingBankLinkSession("link-token", connectionId));

        var pending = await Launcher().PendingAsync();

        Assert.NotNull(pending);
        Assert.Equal("link-token", pending.Token);
        Assert.Equal(connectionId, pending.ConnectionId);
    }

    private PlaidLinkLauncher Launcher() => new(Services.GetRequiredService<IJSRuntime>());
}
