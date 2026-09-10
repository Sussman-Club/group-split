using GroupSplit.Data.PostgreSQL;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Keycloak;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Seeder.Test.Orchestration;

/// <summary>
/// The seeder's service graph, composed the way <c>Program.cs</c> composes it, has to
/// resolve. The seeders borrow services from the API project -- the split-rule handlers,
/// the expense splitter -- and each borrowing is a registration the seeder host has to
/// repeat, because it is a separate host with its own container. Forgetting one is
/// invisible to the compiler and to every other test here, and shows up as the seeder
/// resource dying on startup with an <c>AggregateException</c>, which is how the splitter's
/// registration was found to be missing.
/// </summary>
public class SeederCompositionTest
{
    /// <summary>
    /// The same registrations as <c>Program.cs</c>, in the same order, against a database
    /// that is never opened: nothing here connects, so the connection string only has to
    /// parse.
    /// </summary>
    private static IHost ComposeSeederHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });

        builder.Configuration["ConnectionStrings:db"] =
            "Host=localhost;Database=groupsplit;Username=seeder;Password=unused";

        // The seed files are opened only when a seeder runs, so the paths need not exist;
        // they only need to be there for the options to bind.
        foreach (var file in new[] { "Groups", "Users", "Categories", "Transactions", "Invitations" })
            builder.Configuration[$"Seeder:Paths:{file}"] = $"SeedData/{file.ToLowerInvariant()}.json";

        builder.AddPostgreSqlAppDbContext("db");
        builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));
        builder.AddKeycloakAdminClient();

        builder.Services.AddSeederRunner().AddSeeders();

        // Development turns on ValidateOnBuild and ValidateScopes, which is what makes a
        // missing registration fail here rather than on the first request for it.
        return builder.Build();
    }

    [Fact]
    public void The_seeder_host_builds_with_every_registration_it_needs()
    {
        using var host = ComposeSeederHost();

        Assert.NotNull(host.Services);
    }

    /// <summary>
    /// Validation on build checks each descriptor on its own; this walks the seeders the
    /// runner will actually ask for, in a scope, the way the runner does.
    /// </summary>
    [Fact]
    public void Every_registered_seeder_resolves_in_a_scope()
    {
        using var host = ComposeSeederHost();
        using var scope = host.Services.CreateScope();

        var seeders = scope.ServiceProvider.GetServices<ISeeder>().ToList();

        Assert.NotEmpty(seeders);
        Assert.Contains(seeders, seeder => seeder is Seeders.TransactionSeeder);
        Assert.Contains(seeders, seeder => seeder is Seeders.CategorySeeder);
        Assert.Contains(seeders, seeder => seeder is Seeders.MerchantSeeder);
        Assert.Contains(seeders, seeder => seeder is Seeders.GroupInvitationSeeder);
    }

    /// <summary>
    /// An invitation names a group and the member who sent it, so both have to exist first.
    /// </summary>
    /// <remarks>
    /// A layer runs its seeders at the same time in separate scopes, so "before" here is
    /// the only thing that makes the foreign keys resolvable -- an invitation written
    /// beside the groups would race the group it points at.
    /// </remarks>
    [Fact]
    public void The_invitations_are_seeded_after_the_groups_and_the_people_who_sent_them()
    {
        using var host = ComposeSeederHost();
        using var scope = host.Services.CreateScope();

        var layers = scope.ServiceProvider.GetServices<ISeeder>().TopologicallySort();

        var invitations = LayerOf<Seeders.GroupInvitationSeeder>(layers);

        Assert.True(invitations > LayerOf<Seeders.GroupSeeder>(layers));
        Assert.True(invitations > LayerOf<Seeders.UserSeeder>(layers));
    }

    /// <summary>
    /// The shops have to be written before anything that points at one.
    /// </summary>
    /// <remarks>
    /// Not a preference about ordering: the expenses and the bank rows both name merchants,
    /// they land in the same layer as each other, and a layer runs its seeders at the same
    /// time in separate scopes. If either of them could create the row, two of them would
    /// race for it and the unique index would turn that into a failed seeding run -- so
    /// neither does, and this is the edge that makes the row already be there.
    /// </remarks>
    [Fact]
    public void The_merchants_are_seeded_before_the_expenses_and_the_bank_rows_that_name_them()
    {
        using var host = ComposeSeederHost();
        using var scope = host.Services.CreateScope();

        var layers = scope.ServiceProvider.GetServices<ISeeder>().TopologicallySort();

        var merchants = LayerOf<Seeders.MerchantSeeder>(layers);

        Assert.True(merchants < LayerOf<Seeders.TransactionSeeder>(layers));
        Assert.True(merchants < LayerOf<Seeders.BankConnectionSeeder>(layers));
    }

    private static int LayerOf<TSeeder>(List<List<ISeeder>> layers) =>
        layers.FindIndex(layer => layer.Any(seeder => seeder is TSeeder));
}
