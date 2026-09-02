namespace GroupSplit.Seeder.Abstractions;

/// <summary>
///     Provides seed data for a particular DTO type, allowing seeders to consume
///     records from files, services, or any other underlying source.
/// </summary>
/// <typeparam name="TDto">The DTO model used to represent each seed entry.</typeparam>
public interface ISeedDataSource<TDto>
{
    /// <summary>
    ///     Reads the seed data as an asynchronous stream.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>An async sequence of <typeparamref name="TDto" /> items.</returns>
    IAsyncEnumerable<TDto> ReadAsync(CancellationToken ct = default);
}