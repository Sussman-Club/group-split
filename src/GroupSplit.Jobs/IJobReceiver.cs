namespace GroupSplit.Jobs;

public interface IJobReceiver
{
    IAsyncEnumerable<IJobDelivery> ReceiveAsync(CancellationToken cancellationToken);
}
