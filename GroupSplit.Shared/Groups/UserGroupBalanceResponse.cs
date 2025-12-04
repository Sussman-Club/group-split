using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.Shared;

public record UserGroupBalanceResponse
{
    public IEnumerable<GroupNetBalance> NetBalances { get; init; } = [];
    public IEnumerable<DebtInfo> OwedToYou { get; set; } = [];
    public IEnumerable<DebtInfo> YouOwed { get; set; } = [];
}

public record GroupNetBalance
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public decimal AmountPaid { get; set; }
    public decimal AmountOwed { get; set; }
    public decimal Balance { get; set; }
}

public record DebtInfo
{
    public string UserName { get; set; } = "";
    public decimal Amount { get; set; }
}