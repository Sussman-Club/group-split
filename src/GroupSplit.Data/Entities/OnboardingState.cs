namespace GroupSplit.Data.Entities;

/// <summary>Per-account progress through the first-use guides.</summary>
public class OnboardingState
{
    public Guid? GroupId { get; set; }
    public bool ChecklistDismissed { get; set; }
    public bool ExpenseDismissed { get; set; }
    public bool BankDismissed { get; set; }
}
