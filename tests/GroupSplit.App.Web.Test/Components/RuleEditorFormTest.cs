using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The rule editor's two pre-fills: "split evenly", and what shares become when the rule
/// type is changed to percentages. Neither divides money -- the server does that -- but
/// both put numbers in front of somebody, and those numbers used to change on their own.
/// </summary>
/// <remarks>
/// Both handed the rounding remainder to <c>new Random()</c>, so the same click gave
/// different answers and there was nothing to say where the odd cent had gone. These pin
/// the rule that replaced it, which is the server's: the largest weight, and the lowest id
/// among equals. See issue #158.
/// <para>
/// Every determinism check repeats rather than comparing two runs. Two runs of the old code
/// agreed a third of the time with three members, so a test written that way would have
/// failed only sometimes; twenty leave it no room.
/// </para>
/// </remarks>
public class RuleEditorFormTest : ComponentTest
{
    private const int Repeats = 20;

    private static readonly Guid GroupId = Guid.NewGuid();

    // Deliberately not in id order, and named so the ordering under test is visibly not
    // the order they arrive in: the group answers Carol, Alice, Bob.
    private static readonly UserInfo Alice = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Alice", "A", null);
    private static readonly UserInfo Bob = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Bob", "B", null);
    private static readonly UserInfo Carol = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Carol", "C", null);

    /// <summary>Who the group answers with. Set before rendering to change the membership.</summary>
    private List<UserInfo> _group = [Carol, Alice, Bob];

    public RuleEditorFormTest()
    {
        var groups = new Mock<IGroupsClient>();
        groups
            .Setup(client => client.GetGroupMembersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _group);

        var rules = new Mock<ISplitRulesClient>();
        rules
            .Setup(client => client.GetSplitRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(groups.Object);
        Services.AddSingleton(rules.Object);

        // The real command over the mocked client, rather than a mocked command: what the
        // form reads through it is the same call it used to make itself, and the error
        // handling around it is the command's now.
        Services.AddSingleton<ISplitRuleCommands, SplitRuleCommands>();
    }

    private IRenderedComponent<RuleEditorForm> Render(SplitRuleDto version)
    {
        var model = new RuleEditorForm.RuleEditorModel { Category = "Food", Version = version };

        return Render<RuleEditorForm>(parameters => parameters
            .AddCascadingValue(Mock.Of<IMudDialogInstance>())
            .Add(form => form.GroupId, GroupId)
            .Add(form => form.Model, model));
    }

    /// <summary>
    /// A percent rule as the editor itself would have built one: every member present at
    /// zero. The fields bind straight into this dictionary, so a member missing from it is
    /// not a leaner starting point but a render that throws.
    /// </summary>
    private IRenderedComponent<RuleEditorForm> RenderEmptyPercentages() =>
        Render(new PercentSplitRuleDto { Percentages = _group.ToDictionary(member => member.Id, _ => 0m) });

    private static PercentSplitRuleDto Percentages(IRenderedComponent<RuleEditorForm> form) =>
        (PercentSplitRuleDto)form.Instance.Model.Version;

    private static Task SplitEvenlyAsync(IRenderedComponent<RuleEditorForm> form) =>
        form.FindAll("button").First(button => button.TextContent.Contains("Split Evenly")).ClickAsync(new());

    /// <summary>
    /// The acceptance criterion from the issue, as a test: the same members and the same
    /// click give the same numbers.
    /// </summary>
    [Fact]
    public async Task Splitting_evenly_gives_the_same_numbers_every_time()
    {
        var answers = new HashSet<string>();

        for (var attempt = 0; attempt < Repeats; attempt++)
        {
            var form = RenderEmptyPercentages();

            await SplitEvenlyAsync(form);

            answers.Add(Describe(Percentages(form).Percentages));
        }

        Assert.Single(answers);
    }

    /// <summary>
    /// Three members cannot have a third each, so one carries the odd cent. Where it goes
    /// is stated rather than incidental: the lowest id, which is what the server's rule
    /// comes to once every weight is equal.
    /// </summary>
    [Fact]
    public async Task The_odd_cent_goes_to_the_lowest_id_and_the_percentages_still_total_a_hundred()
    {
        var form = RenderEmptyPercentages();

        await SplitEvenlyAsync(form);

        var split = Percentages(form).Percentages;

        Assert.Equal(33.34m, split[Alice.Id]);
        Assert.Equal(33.33m, split[Bob.Id]);
        Assert.Equal(33.33m, split[Carol.Id]);
        Assert.Equal(100m, split.Values.Sum());
    }

    /// <summary>Members that do divide cleanly get no odd cent to argue about.</summary>
    [Fact]
    public async Task A_membership_that_divides_cleanly_is_split_flat()
    {
        _group = [Alice, Bob];

        var form = RenderEmptyPercentages();

        await SplitEvenlyAsync(form);

        var split = Percentages(form).Percentages;

        Assert.Equal(50m, split[Alice.Id]);
        Assert.Equal(50m, split[Bob.Id]);
    }

    // ---- shares turned into percentages ------------------------------------------------

    /// <summary>
    /// Changing the rule type from shares to percentages converts what was already typed,
    /// which is where the second <c>Random</c> was.
    /// </summary>
    private static async Task SwitchToPercentAsync(IRenderedComponent<RuleEditorForm> form)
    {
        var select = form.FindComponent<MudSelect<RuleType?>>();

        await form.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(RuleType.Percent));
    }

    private static RuleEditorForm.RuleEditorModel SharesModel() => new()
    {
        Category = "Food",
        Version = new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int> { [Alice.Id] = 1, [Bob.Id] = 1, [Carol.Id] = 1 }
        }
    };

    private IRenderedComponent<RuleEditorForm> RenderShares() =>
        Render<RuleEditorForm>(parameters => parameters
            .AddCascadingValue(Mock.Of<IMudDialogInstance>())
            .Add(form => form.GroupId, GroupId)
            .Add(form => form.Model, SharesModel()));

    [Fact]
    public async Task Shares_become_the_same_percentages_every_time()
    {
        var answers = new HashSet<string>();

        for (var attempt = 0; attempt < Repeats; attempt++)
        {
            var form = RenderShares();

            await SwitchToPercentAsync(form);

            answers.Add(Describe(Percentages(form).Percentages));
        }

        Assert.Single(answers);
    }

    /// <summary>
    /// The adjustment lands on the largest share, so it is the smallest part of the number
    /// it moves -- and never on a member holding none, who was left out of the split and
    /// would be put back into it by a hundredth of a percent.
    /// </summary>
    [Fact]
    public async Task The_rounding_lands_on_the_largest_share_and_never_on_a_member_with_none()
    {
        var model = new RuleEditorForm.RuleEditorModel
        {
            Category = "Food",
            // 6 of 7 and 1 of 7: 85.71 and 14.29 round to 100.00 exactly, so nudge it with
            // a third member holding nothing at all.
            Version = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [Alice.Id] = 1, [Bob.Id] = 2, [Carol.Id] = 0 }
            }
        };

        var form = Render<RuleEditorForm>(parameters => parameters
            .AddCascadingValue(Mock.Of<IMudDialogInstance>())
            .Add(component => component.GroupId, GroupId)
            .Add(component => component.Model, model));

        await SwitchToPercentAsync(form);

        var split = Percentages(form).Percentages;

        Assert.Equal(0m, split[Carol.Id]);
        Assert.Equal(100m, split.Values.Sum());

        // 33.33 and 66.67, not 33.34 and 66.66: the cent goes to the bigger share.
        Assert.Equal(33.33m, split[Alice.Id]);
        Assert.Equal(66.67m, split[Bob.Id]);
    }

    /// <summary>
    /// The form asks who carries the remainder rather than deciding it, so a host that wants
    /// a different answer registers one. This is the seam, exercised: a policy that always
    /// names Carol puts the odd cent on Carol, without the form changing.
    /// </summary>
    [Fact]
    public async Task The_form_takes_the_answer_from_the_policy_it_was_given()
    {
        Services.AddSingleton<IRemainderPolicy>(new AlwaysPolicy(Carol.Id));

        var form = RenderEmptyPercentages();

        await SplitEvenlyAsync(form);

        var split = Percentages(form).Percentages;

        Assert.Equal(33.34m, split[Carol.Id]);
        Assert.Equal(33.33m, split[Alice.Id]);
        Assert.Equal(100m, split.Values.Sum());
    }

    private sealed class AlwaysPolicy(Guid bearer) : IRemainderPolicy
    {
        public Guid CarriedBy(IReadOnlyCollection<UserInfo> members, Func<UserInfo, decimal> weight) => bearer;
    }

    /// <summary>Ordered by id, so the comparison is about the numbers and not the dictionary.</summary>
    private static string Describe(IDictionary<Guid, decimal> percentages) =>
        string.Join(", ", percentages.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));
}
