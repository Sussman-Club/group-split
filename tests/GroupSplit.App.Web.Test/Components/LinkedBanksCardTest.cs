using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The linked-banks card: what it says about a connection, and which way out it offers.
/// </summary>
/// <remarks>
/// Two of the states here are ones nothing has broken in -- an account the bank has and
/// this connection does not, and a sign-in about to lapse. Both were invisible until the
/// card learned to say them, which was the whole complaint in issue 233: the person was
/// told their bank was healthy and last checked five minutes ago while their money was
/// being dropped on the floor. So what the card renders for each is the fix, and this is
/// where it stays fixed.
/// </remarks>
public class LinkedBanksCardTest : ComponentTest
{
    private readonly Mock<IInboxStateService> _inbox = new();
    private readonly Mock<IBankCommands> _bank = new();
    private readonly Mock<IBankLinkLauncher> _link = new();

    public LinkedBanksCardTest()
    {
        Services.AddSingleton(_inbox.Object);
        Services.AddSingleton(_bank.Object);
        Services.AddSingleton(_link.Object);
    }

    [Fact]
    public async Task A_healthy_connection_says_when_it_was_last_checked_and_offers_a_sync()
    {
        var popovers = Render<MudPopoverProvider>();
        var card = Render(Connection());

        Assert.Contains("last checked", Text(card));
        Assert.DoesNotContain("Needs attention", Text(card));
        Assert.Contains("Actions for First Platypus Bank", Text(card));
        Assert.Single(card.FindAll(".gs-bank-row .mud-menu"));
        Assert.Single(card.FindAll(".gs-bank-row button"));

        await OpenMenuAsync(card, popovers);

        Assert.Contains("Sync now", popovers.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state issue 233 was about. It has to name the problem before somebody has to
    /// open the actions menu to repair it.
    /// </summary>
    [Fact]
    public void An_account_the_bank_has_and_this_does_not_is_said_out_loud()
    {
        var card = Render(Connection(accountsNotShared: true));

        var text = Text(card);

        Assert.Contains("Needs attention", text);
        Assert.Contains("account needs sharing", text);
    }

    [Fact]
    public void A_sign_in_about_to_lapse_says_so_before_it_becomes_an_outage()
    {
        var text = Text(Render(Connection(signInExpiring: true)));

        Assert.Contains("Needs attention", text);
        Assert.Contains("sign in expires soon", text);
    }

    /// <summary>
    /// Neither of those has stopped the connection working, so syncing stays on offer.
    /// Taking it away would make a warning about the future look like a breakage now.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_connection_that_only_needs_attention_soon_can_still_be_synced(
        bool accountsNotShared, bool signInExpiring)
    {
        var popovers = Render<MudPopoverProvider>();
        var card = Render(Connection(accountsNotShared: accountsNotShared, signInExpiring: signInExpiring));

        Assert.Contains("Needs attention", Text(card));
        Assert.Single(card.FindAll(".gs-bank-row .mud-menu"));

        await OpenMenuAsync(card, popovers);

        Assert.Contains("Sync now", popovers.Markup, StringComparison.Ordinal);
        Assert.Contains("Sign in again", popovers.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection that has actually stopped is the other way round: syncing it would only
    /// be told to sign in again, so the card offers the one thing that works.
    /// </summary>
    [Fact]
    public async Task A_connection_the_bank_has_stopped_offers_only_the_sign_in()
    {
        var popovers = Render<MudPopoverProvider>();
        var card = Render(Connection(status: BankConnectionState.LoginRequired));
        var text = Text(card);

        Assert.Contains("sign in required", text);

        await OpenMenuAsync(card, popovers);

        Assert.DoesNotContain("Sync now", popovers.Markup, StringComparison.Ordinal);
        Assert.Contains("Sign in again", popovers.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state is not only a changed password any more: a locked account, an agreement
    /// waiting at the bank, no account shared all land here (issue 319). The compact status
    /// says the bank needs the person without claiming which one it was.
    /// </summary>
    [Fact]
    public void A_connection_the_bank_has_stopped_says_it_needs_the_person_and_where_to_go()
    {
        var text = Text(Render(Connection(status: BankConnectionState.LoginRequired)));

        Assert.Contains("Needs attention", text);
        Assert.Contains("sign in required", text);
    }

    [Fact]
    public void An_account_whose_access_was_withdrawn_is_named_and_the_rest_carries_on()
    {
        var text = Text(Render(Connection(accounts:
        [
            new LinkedAccountResponse(Guid.NewGuid(), "Everyday", "1234", "depository", "checking", "USD"),
            new LinkedAccountResponse(Guid.NewGuid(), "Savings", "5678", "depository", "savings", "USD",
                AccessRevoked: true)
        ])));

        Assert.Contains("Savings access withdrawn", text);
    }

    [Fact]
    public async Task Signing_in_again_in_the_actions_menu_repairs_its_own_connection()
    {
        var connection = Connection(accountsNotShared: true);

        _bank.Setup(bank => bank.CreateLinkTokenAsync(connection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LinkTokenResponse("link-token", DateTimeOffset.UtcNow.AddMinutes(30)));

        _link.Setup(link => link.OpenAsync("link-token", connection.Id)).ReturnsAsync((string?)null);

        var popovers = Render<MudPopoverProvider>();
        var card = Render(connection);

        await OpenMenuAsync(card, popovers);
        await popovers.FindAll(".mud-menu-item")
            .Single(item => item.TextContent.Trim() == "Sign in again")
            .ClickAsync(new MouseEventArgs());

        // The connection travels with the token. An OAuth bank takes the browser away, and
        // the page it comes back to has only what was written down here to tell it whether
        // this was a repair or a new bank.
        _link.Verify(link => link.OpenAsync("link-token", connection.Id), Times.Once);
        _bank.Verify(bank => bank.SyncAsync(connection.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Linking_a_new_bank_names_no_connection()
    {
        _bank.Setup(bank => bank.CreateLinkTokenAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LinkTokenResponse("link-token", DateTimeOffset.UtcNow.AddMinutes(30)));

        _link.Setup(link => link.OpenAsync("link-token", null)).ReturnsAsync("public-token");

        var card = Render(Connection());

        await card.Find("button:contains('Link a bank')").ClickAsync(new());

        _link.Verify(link => link.OpenAsync("link-token", null), Times.Once);
        _bank.Verify(bank => bank.LinkAsync("public-token", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_deployment_with_no_provider_says_bank_sync_is_off_rather_than_showing_nothing()
    {
        _inbox.SetupGet(inbox => inbox.Connections).Returns(new BankConnectionsResponse(false, []));

        Assert.Contains("not switched on", Text(base.Render<LinkedBanksCard>()));
    }

    private IRenderedComponent<LinkedBanksCard> Render(BankConnectionResponse connection)
    {
        _inbox.SetupGet(inbox => inbox.Connections)
            .Returns(new BankConnectionsResponse(true, [connection]));

        return base.Render<LinkedBanksCard>();
    }

    private static async Task OpenMenuAsync(IRenderedComponent<LinkedBanksCard> card,
        IRenderedComponent<MudPopoverProvider> popovers)
    {
        await card.Find(".gs-bank-row .mud-menu button").ClickAsync(new MouseEventArgs());
        popovers.WaitForAssertion(() => Assert.Contains("mud-menu-item", popovers.Markup, StringComparison.Ordinal));
    }

    private static string Text(IRenderedComponent<LinkedBanksCard> card) => card.Markup;

    private static BankConnectionResponse Connection(
        BankConnectionState status = BankConnectionState.Active,
        bool accountsNotShared = false,
        bool signInExpiring = false,
        IReadOnlyList<LinkedAccountResponse>? accounts = null) =>
        new(Guid.NewGuid(),
            "plaid",
            "First Platypus Bank",
            status,
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddHours(-1),
            accounts ??
            [
                new LinkedAccountResponse(Guid.NewGuid(), "Everyday", "1234", "depository", "checking", "USD")
            ],
            accountsNotShared,
            signInExpiring);
}
