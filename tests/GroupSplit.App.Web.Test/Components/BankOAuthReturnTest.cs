using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The page a bank's own sign-in returns to.
/// </summary>
/// <remarks>
/// Only OAuth institutions come here. The rest run in a popup and finish on the page that
/// opened them, so everything below is about the case where that page is gone: the browser
/// was taken to the bank and came back to a fresh load with nothing in memory. What it has
/// instead is what the tab wrote down before opening, and getting that wrong means somebody
/// signs in at their bank and lands on a page that cannot finish what they started.
/// </remarks>
public class BankOAuthReturnTest : ComponentTest
{
    private readonly Mock<IBankLinkLauncher> _link = new();
    private readonly Mock<IBankCommands> _bank = new();

    private const string Token = "link-sandbox-af1a0311";

    public BankOAuthReturnTest()
    {
        Services.AddSingleton(_link.Object);
        Services.AddSingleton(_bank.Object);
    }

    /// <summary>
    /// A fresh link that went through an OAuth bank finishes as an exchange, exactly as it
    /// would have on the page that started it.
    /// </summary>
    [Fact]
    public void A_returning_fresh_link_is_exchanged_and_lands_on_the_account_page()
    {
        Pending(new PendingBankLinkSession(Token, ConnectionId: null));
        _link.Setup(l => l.ResumeAsync(Token)).ReturnsAsync("public-token");

        Render<BankOAuthReturn>();

        _bank.Verify(b => b.LinkAsync("public-token", It.IsAny<CancellationToken>()), Times.Once);
        Assert.EndsWith("/account", Nav.Uri);
    }

    /// <summary>
    /// Update mode ends in a refresh instead: it hands back no usable public token, and an
    /// account shared during it is only heard of by asking for the list again.
    /// </summary>
    [Fact]
    public void A_returning_repair_refreshes_the_connection_it_was_repairing()
    {
        var connectionId = Guid.NewGuid();

        Pending(new PendingBankLinkSession(Token, connectionId));
        _link.Setup(l => l.ResumeAsync(Token)).ReturnsAsync("public-token");

        Render<BankOAuthReturn>();

        _bank.Verify(b => b.RefreshAsync(connectionId, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _bank.Verify(b => b.LinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Link is resumed with the token the session started with, never opened afresh —
    /// a new session would send the person back to their bank to sign in a second time.
    /// </summary>
    [Fact]
    public void The_session_is_resumed_rather_than_started_again()
    {
        Pending(new PendingBankLinkSession(Token, ConnectionId: null));
        _link.Setup(l => l.ResumeAsync(Token)).ReturnsAsync("public-token");

        Render<BankOAuthReturn>();

        _link.Verify(l => l.ResumeAsync(Token), Times.Once);
        _link.Verify(l => l.OpenAsync(It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);
    }

    /// <summary>
    /// Backing out at the bank is an ordinary way to leave, not a failure. Nothing is
    /// stored and nothing is said; they are simply back where they started.
    /// </summary>
    [Fact]
    public void Backing_out_at_the_bank_stores_nothing_and_still_leads_back()
    {
        Pending(new PendingBankLinkSession(Token, ConnectionId: null));
        _link.Setup(l => l.ResumeAsync(Token)).ReturnsAsync((string?)null);

        Render<BankOAuthReturn>();

        _bank.Verify(b => b.LinkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.EndsWith("/account", Nav.Uri);
    }

    /// <summary>
    /// Opening the address directly, or in a tab that never started a link. It is a real
    /// address on a real deployment, so somebody will — and it must say what it is rather
    /// than sit on a spinner for ever.
    /// </summary>
    [Fact]
    public void Arriving_with_nothing_in_hand_says_so_instead_of_waiting()
    {
        Pending(null);

        var page = Render<BankOAuthReturn>();

        Assert.Contains("no bank to finish linking", page.Markup);
        Assert.DoesNotContain("Finishing with your bank", page.Markup);

        _link.Verify(l => l.ResumeAsync(It.IsAny<string>()), Times.Never);
        Assert.DoesNotContain("/account", Nav.Uri);
    }

    private void Pending(PendingBankLinkSession? session) =>
        _link.Setup(l => l.PendingAsync()).ReturnsAsync(session);

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();
}
