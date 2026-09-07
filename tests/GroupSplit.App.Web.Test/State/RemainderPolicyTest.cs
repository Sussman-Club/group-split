using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// Who carries the cent a division could not split. The rule editor asks this rather than
/// deciding it, so the rule itself is pinned here and the form's tests are about the form.
/// </summary>
/// <remarks>
/// The answer has to be the same every time it is asked. What it decides is not one payment
/// but a stored rule, which then divides every expense in its category -- so an answer that
/// moved would not spread the cent around, it would pick one member to overpay it forever
/// and pick them unrepeatably. See issue #158.
/// </remarks>
public class RemainderPolicyTest
{
    private static readonly UserInfo Alice = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Alice", null, null);
    private static readonly UserInfo Bob = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Bob", null, null);
    private static readonly UserInfo Carol = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Carol", null, null);

    private readonly IRemainderPolicy _policy = new LargestShareRemainderPolicy();

    /// <summary>Arriving in a different order must not be a different answer.</summary>
    private Guid CarriedBy(IReadOnlyDictionary<Guid, decimal> weights, params UserInfo[] members) =>
        _policy.CarriedBy(members, member => weights[member.Id]);

    [Fact]
    public void The_largest_share_carries_it()
    {
        var weights = new Dictionary<Guid, decimal> { [Alice.Id] = 1, [Bob.Id] = 5, [Carol.Id] = 2 };

        Assert.Equal(Bob.Id, CarriedBy(weights, Alice, Bob, Carol));
        Assert.Equal(Bob.Id, CarriedBy(weights, Carol, Alice, Bob));
    }

    /// <summary>
    /// An even split is every weight the same, so this is the tiebreak doing all the work.
    /// The lowest id, and not whichever member the group happened to be listed in.
    /// </summary>
    [Fact]
    public void Equal_shares_go_to_the_lowest_id_whatever_order_they_arrive_in()
    {
        var weights = new Dictionary<Guid, decimal> { [Alice.Id] = 1, [Bob.Id] = 1, [Carol.Id] = 1 };

        Assert.Equal(Alice.Id, CarriedBy(weights, Carol, Bob, Alice));
        Assert.Equal(Alice.Id, CarriedBy(weights, Alice, Bob, Carol));
        Assert.Equal(Alice.Id, CarriedBy(weights, Bob, Carol, Alice));
    }

    /// <summary>
    /// A member holding no share was left out of the split, and a hundredth of a percent
    /// would put them back into it.
    /// </summary>
    [Fact]
    public void A_member_holding_nothing_never_carries_it()
    {
        var weights = new Dictionary<Guid, decimal> { [Alice.Id] = 0, [Bob.Id] = 0, [Carol.Id] = 3 };

        Assert.Equal(Carol.Id, CarriedBy(weights, Alice, Bob, Carol));
    }

    [Fact]
    public void One_member_carries_it_alone()
    {
        var weights = new Dictionary<Guid, decimal> { [Alice.Id] = 1 };

        Assert.Equal(Alice.Id, CarriedBy(weights, Alice));
    }

    /// <summary>
    /// The property the whole thing exists for, said plainly: asking again is asking the
    /// same question.
    /// </summary>
    [Fact]
    public void Asking_repeatedly_gives_one_answer()
    {
        var weights = new Dictionary<Guid, decimal> { [Alice.Id] = 1, [Bob.Id] = 1, [Carol.Id] = 1 };

        var answers = Enumerable
            .Range(0, 50)
            .Select(_ => CarriedBy(weights, Carol, Bob, Alice))
            .Distinct();

        Assert.Single(answers);
    }
}
