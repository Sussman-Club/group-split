using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

public sealed record PushDeviceRequest(
    [property: Required, StringLength(4096, MinimumLength = 1)] string Token,
    bool ExpensesEnabled,
    bool SettlementsEnabled = false,
    bool InvitationsEnabled = false, bool BankSyncIssuesEnabled = false);
