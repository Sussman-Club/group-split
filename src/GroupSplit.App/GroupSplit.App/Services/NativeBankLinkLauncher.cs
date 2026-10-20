using GroupSplit.App.Shared.Services.Banking;

namespace GroupSplit.App.Services;

internal sealed class NativeBankLinkLauncher : IBankLinkLauncher
{
    public bool RequiresExternalBrowser => true;
    public Task<string?> OpenAsync(string linkToken, Guid? connectionId = null) => Task.FromResult<string?>(null);
    public Task<string?> ResumeAsync(string linkToken) => Task.FromResult<string?>(null);
    public Task<PendingBankLinkSession?> PendingAsync() => Task.FromResult<PendingBankLinkSession?>(null);
}
