using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.Shared;

public record UserGroupBalanceResponse
{
    public IEnumerable<GroupNetBalance> NetBalances { get; init; } = [];
    public IEnumerable<DebtInfo> OwedToYou { get; set; } = [];
    public IEnumerable<DebtInfo> YouOwed { get; set; } = [];

    /// <summary>
    /// The whole minimised plan for the group: the fewest payments that leave everybody
    /// square, including the ones the caller is on neither end of.
    /// </summary>
    /// <remarks>
    /// The same arithmetic <see cref="OwedToYou"/> and <see cref="YouOwed"/> are two slices
    /// of. It has been computed on every read of this resource since the resource existed
    /// and was never sent, so no screen has ever shown it -- which is a strange thing for
    /// the product to know and not say. Four members with non-zero balances could need six
    /// transfers between them; this is the three that actually clear it.
    /// <para>
    /// The lines with the caller on one end are the ones they may record. The rest are here
    /// because a plan that hides a step does not add up: somebody checking the arithmetic
    /// has to be able to see the payment that makes the total work.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SettlementPayment> Plan { get; init; } = [];
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
    public Guid UserId { get; set; }
    public string UserName { get; set; } = "";
    public decimal Amount { get; set; }
}