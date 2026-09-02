namespace GroupSplit.Data.Entities;

[Flags]
public enum RuleFlags
{
    None = 0,
    NonEditable = 1 << 0,
    NonDeletable = 1 << 1,
    NoUserTransactions = 1 << 2
}