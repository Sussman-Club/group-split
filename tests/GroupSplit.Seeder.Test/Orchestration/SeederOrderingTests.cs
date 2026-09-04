using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Orchestration;

namespace GroupSplit.Seeder.Test.Orchestration;

/// <summary>
/// <see cref="SeederOrderingExtensions.TopologicallySort"/> decides what the seeder runs
/// and in what order. It is Kahn's algorithm over the <see cref="DependsOnAttribute"/>
/// graph, it needs no host, and it had no tests — a wrong answer here either seeds against
/// data that is not there yet or, in the cycle case, silently drops seeders from the run.
/// </summary>
public class SeederOrderingTests
{
    // The graph is read off the attributes, so the shapes under test have to be real
    // types rather than values. Each does nothing when run: only the ordering matters.
    private class Independent : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private class AlsoIndependent : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(Independent))]
    private class NeedsIndependent : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(NeedsIndependent))]
    private class NeedsTheOneThatNeedsIndependent : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(Independent))]
    [DependsOn(typeof(AlsoIndependent))]
    private class NeedsBoth : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(Independent))]
    private class NeedsSomethingUnregistered : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(SecondOfCycle))]
    private class FirstOfCycle : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(FirstOfCycle))]
    private class SecondOfCycle : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [DependsOn(typeof(SelfDependent))]
    private class SelfDependent : ISeeder
    {
        public Task SeedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static IReadOnlyList<IReadOnlyList<Type>> LayerTypes(List<List<ISeeder>> layers) =>
        [.. layers.Select(layer => (IReadOnlyList<Type>)[.. layer.Select(seeder => seeder.GetType())])];

    [Fact]
    public void No_seeders_produce_no_layers()
    {
        Assert.Empty(Array.Empty<ISeeder>().TopologicallySort());
    }

    [Fact]
    public void A_seeder_that_depends_on_nothing_is_the_only_layer()
    {
        var layers = new ISeeder[] { new Independent() }.TopologicallySort();

        var only = Assert.Single(layers);
        Assert.Equal(typeof(Independent), Assert.Single(only).GetType());
    }

    /// <summary>
    /// Seeders with nothing between them land in the same layer, which is what lets the
    /// runner do them together rather than one after another.
    /// </summary>
    [Fact]
    public void Independent_seeders_share_a_layer()
    {
        var layers = new ISeeder[] { new Independent(), new AlsoIndependent() }
            .TopologicallySort();

        var only = Assert.Single(layers);
        Assert.Equal(2, only.Count);
    }

    [Fact]
    public void A_dependency_is_put_in_an_earlier_layer_than_its_dependent()
    {
        var layers = LayerTypes(new ISeeder[] { new NeedsIndependent(), new Independent() }
            .TopologicallySort());

        Assert.Equal(2, layers.Count);
        Assert.Equal([typeof(Independent)], layers[0]);
        Assert.Equal([typeof(NeedsIndependent)], layers[1]);
    }

    /// <summary>
    /// Order of registration must not decide order of execution — the attributes do.
    /// </summary>
    [Fact]
    public void The_order_seeders_are_registered_in_does_not_change_the_layers()
    {
        var oneWay = LayerTypes(new ISeeder[] { new Independent(), new NeedsIndependent() }
            .TopologicallySort());
        var theOther = LayerTypes(new ISeeder[] { new NeedsIndependent(), new Independent() }
            .TopologicallySort());

        Assert.Equal(oneWay, theOther);
    }

    [Fact]
    public void A_chain_of_dependencies_becomes_one_layer_each()
    {
        var layers = LayerTypes(new ISeeder[]
        {
            new NeedsTheOneThatNeedsIndependent(),
            new NeedsIndependent(),
            new Independent()
        }.TopologicallySort());

        Assert.Equal([
            [typeof(Independent)],
            [typeof(NeedsIndependent)],
            [typeof(NeedsTheOneThatNeedsIndependent)]
        ], layers);
    }

    /// <summary>
    /// A seeder waits for every dependency, not just the first one read off the type.
    /// </summary>
    [Fact]
    public void A_seeder_with_two_dependencies_waits_for_both()
    {
        var layers = LayerTypes(new ISeeder[]
        {
            new NeedsBoth(), new Independent(), new AlsoIndependent()
        }.TopologicallySort());

        Assert.Equal(2, layers.Count);
        Assert.Equal(2, layers[0].Count);
        Assert.Contains(typeof(Independent), layers[0]);
        Assert.Contains(typeof(AlsoIndependent), layers[0]);
        Assert.Equal([typeof(NeedsBoth)], layers[1]);
    }

    [Fact]
    public void Every_registered_seeder_comes_back_exactly_once()
    {
        ISeeder[] seeders =
        [
            new NeedsBoth(), new Independent(), new AlsoIndependent(),
            new NeedsIndependent(), new NeedsTheOneThatNeedsIndependent()
        ];

        var sorted = seeders.TopologicallySort().SelectMany(layer => layer).ToList();

        Assert.Equal(seeders.Length, sorted.Count);
        Assert.Equal(
            seeders.Select(seeder => seeder.GetType()).OrderBy(type => type.Name),
            sorted.Select(seeder => seeder.GetType()).OrderBy(type => type.Name));
    }

    /// <summary>
    /// Depending on something nobody registered is a wiring mistake, and it is named
    /// rather than quietly ignored — the dependent would otherwise run against data that
    /// was never seeded.
    /// </summary>
    [Fact]
    public void Depending_on_an_unregistered_seeder_names_both_ends()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ISeeder[] { new NeedsSomethingUnregistered() }.TopologicallySort());

        Assert.Contains(nameof(NeedsSomethingUnregistered), exception.Message);
        Assert.Contains(nameof(Independent), exception.Message);
    }

    /// <summary>
    /// The failure that matters most: with a cycle no node ever reaches indegree zero, so
    /// the layers come back missing those seeders. Without this check the run would look
    /// like it succeeded having skipped them.
    /// </summary>
    [Fact]
    public void A_cycle_is_reported_rather_than_dropping_the_seeders_in_it()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ISeeder[] { new FirstOfCycle(), new SecondOfCycle() }.TopologicallySort());

        Assert.Contains("Cyclic", exception.Message);
        Assert.Contains(nameof(FirstOfCycle), exception.Message);
        Assert.Contains(nameof(SecondOfCycle), exception.Message);
    }

    [Fact]
    public void A_seeder_that_depends_on_itself_is_a_cycle_too()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ISeeder[] { new SelfDependent() }.TopologicallySort());

        Assert.Contains("Cyclic", exception.Message);
        Assert.Contains(nameof(SelfDependent), exception.Message);
    }

    /// <summary>
    /// A cycle off to one side still fails the whole sort, rather than returning the part
    /// that happened to be sortable.
    /// </summary>
    [Fact]
    public void A_cycle_fails_the_sort_even_when_other_seeders_are_fine()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ISeeder[] { new Independent(), new FirstOfCycle(), new SecondOfCycle() }
                .TopologicallySort());
    }
}
