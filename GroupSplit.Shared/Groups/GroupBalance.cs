using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.Shared;

public record GroupBalance
{
    public Guid UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public decimal AmountPaid { get; init; }
    public decimal AmountOwed { get; init; }
    public decimal Balance { get; init; }
}