namespace GroupSplit.Shared;

/// <summary>First-use guidance state for the signed-in account.</summary>
public record OnboardingInfo(Guid? GroupId, bool ChecklistDismissed, bool ExpenseDismissed, bool BankDismissed);
