namespace GroupSplit.Shared;

/// <summary>
/// How long an expense's name may be, in one place so the requests, the dialogs and the
/// inbox cannot disagree about it.
/// </summary>
public static class TransactionNames
{
    /// <summary>The longest name a request accepts. The column itself holds 128.</summary>
    public const int MaxLength = 124;
}
