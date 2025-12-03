using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.Shared;

public record GroupBalance
{
    public IEnumerable<GroupNetBalance> NetBalances { get; init; } = [];
    public IEnumerable<DebtInfo> OwedToYou { get; set; } = [];
    public IEnumerable<DebtInfo> YouOwed { get; set; } = [];
}

public record GroupNetBalance
{
    public Guid UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public decimal AmountPaid { get; init; }
    public decimal AmountOwed { get; init; }
    public decimal Balance { get; init; }
}

public record DebtInfo
{
    public string UserName { get; set; } = "";
    public decimal Amount { get; set; }
}