namespace GroupSplit.Shared;

public record SettleRequest
{
    public Guid UserId { get; set; }

    public decimal Amount { get; set; }
}