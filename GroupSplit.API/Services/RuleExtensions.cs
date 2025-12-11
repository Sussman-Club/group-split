using GroupSplit.Data.Entities;

namespace GroupSplit.API.Services;

public static class RuleExtensions
{
    extension(Rule rule)
    {
        public bool IsEditable => (rule.Flags & RuleFlags.NonEditable) == 0;

        public bool IsDeletable => (rule.Flags & RuleFlags.NonDeletable) == 0;

        public bool AllowsUserTransactions => (rule.Flags & RuleFlags.NoUserTransactions) == 0;

        public bool IsSystem => rule is { IsEditable: true, IsDeletable: true };
    }
}