using System.Text.Json;
using GroupSplit.App.Shared.Models;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Contract;

/// <summary>
/// <see cref="RuleVersionDto"/> is the one polymorphic type that crosses the wire, and the
/// two ends do not agree on serializer settings: the API is a minimal API, so it uses
/// <see cref="JsonSerializerDefaults.Web"/>, while the generated client is handed
/// <c>GroupSplitSerializer.Transform(new JsonSerializerOptions())</c> — plain defaults with
/// a camel-case naming policy laid on top. That is case-sensitive where the API is not, and
/// does not read numbers from strings where the API does.
/// <para>
/// Nothing else in the suite covers the gap between those two. The endpoint tests speak raw
/// HTTP, which is the right tool for status codes and refusals but says nothing about
/// whether the client can read what the API wrote; and a rule whose subtype is lost in
/// transit does not fail loudly, it comes back as the wrong kind of split.
/// </para>
/// </summary>
public class RuleVersionContractTest
{
    /// <summary>What the API writes with and reads with.</summary>
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    /// <summary>What the generated client writes with and reads with.</summary>
    private static readonly JsonSerializerOptions Client =
        GroupSplitSerializer.Transform(new JsonSerializerOptions());

    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static TheoryData<string, RuleVersionDto> EveryRuleType() => new()
    {
        { "personal", new PersonalRuleVersionDto() },
        {
            "percent",
            new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal> { [Alice] = 40m, [Bob] = 60m }
            }
        },
        {
            "shares",
            new SharesRuleVersionDto
            {
                Shares = new Dictionary<Guid, int> { [Alice] = 2, [Bob] = 3 }
            }
        }
    };

    /// <summary>
    /// Compares by content. These records hold dictionaries, and a record's generated
    /// equality compares those by reference, so two identical splits are never "equal".
    /// </summary>
    private static void AssertSameSplit(RuleVersionDto expected, RuleVersionDto actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());

        switch (expected)
        {
            case PercentRuleVersionDto percent:
                Assert.Equal(percent.Percentages,
                    ((PercentRuleVersionDto)actual).Percentages);
                break;
            case SharesRuleVersionDto shares:
                Assert.Equal(shares.Shares, ((SharesRuleVersionDto)actual).Shares);
                break;
            case PersonalRuleVersionDto:
                break;
            default:
                Assert.Fail($"No comparison written for {expected.GetType().Name}.");
                break;
        }
    }

    /// <summary>
    /// The direction that matters most: the API answers, the client reads. A discriminator
    /// the client cannot resolve is where a rule quietly becomes the wrong kind of split.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRuleType))]
    public void What_the_api_writes_the_client_can_read(string _, RuleVersionDto version)
    {
        var json = JsonSerializer.Serialize(version, Api);

        var read = JsonSerializer.Deserialize<RuleVersionDto>(json, Client);

        Assert.NotNull(read);
        AssertSameSplit(version, read);
    }

    [Theory]
    [MemberData(nameof(EveryRuleType))]
    public void What_the_client_writes_the_api_can_read(string _, RuleVersionDto version)
    {
        var json = JsonSerializer.Serialize(version, Client);

        var read = JsonSerializer.Deserialize<RuleVersionDto>(json, Api);

        Assert.NotNull(read);
        AssertSameSplit(version, read);
    }

    /// <summary>
    /// The discriminator values are the wire contract. Renaming one is invisible in C# and
    /// breaks every client that has not been regenerated, so they are pinned literally
    /// rather than derived from the attributes that declare them.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRuleType))]
    public void The_discriminator_is_the_name_the_wire_expects(
        string discriminator, RuleVersionDto version)
    {
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(version, Api));

        Assert.True(written.RootElement.TryGetProperty("$type", out var type),
            "the subtype has to travel with the payload or it cannot be reconstructed");
        Assert.Equal(discriminator, type.GetString());
    }

    /// <summary>
    /// A rule version never travels alone — it is a property of the request that creates
    /// one, and polymorphism nested inside another object is resolved separately from
    /// polymorphism at the root.
    /// </summary>
    [Fact]
    public void A_rule_version_nested_in_a_create_request_survives_the_round_trip()
    {
        var request = new CreateRuleRequest
        {
            GroupId = Guid.NewGuid(),
            Category = "Groceries",
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal> { [Alice] = 100m }
            }
        };

        var read = JsonSerializer.Deserialize<CreateRuleRequest>(
            JsonSerializer.Serialize(request, Client), Api);

        Assert.NotNull(read);
        var percent = Assert.IsType<PercentRuleVersionDto>(read.Version);
        Assert.Equal(100m, percent.Percentages[Alice]);
    }

    /// <summary>The same nesting on the way back, in the response the details route returns.</summary>
    [Fact]
    public void A_rule_version_nested_in_a_details_response_survives_the_round_trip()
    {
        var response = new RuleDetailsResponse
        {
            RuleId = Guid.NewGuid(),
            RuleVersionId = Guid.NewGuid(),
            Category = "Rent",
            Version = new SharesRuleVersionDto
            {
                Shares = new Dictionary<Guid, int> { [Alice] = 1, [Bob] = 2 }
            }
        };

        var read = JsonSerializer.Deserialize<RuleDetailsResponse>(
            JsonSerializer.Serialize(response, Api), Client);

        Assert.NotNull(read);
        Assert.Equal("Rent", read.Category);
        var shares = Assert.IsType<SharesRuleVersionDto>(read.Version);
        Assert.Equal(1, shares.Shares[Alice]);
        Assert.Equal(2, shares.Shares[Bob]);
    }

    /// <summary>
    /// The split is keyed by user id, and a dictionary key is not a property — the naming
    /// policy the client adds does not touch it. Worth pinning, because a key policy set
    /// later would rewrite every id and the lookups would silently miss.
    /// </summary>
    [Fact]
    public void The_user_ids_keying_a_split_are_written_verbatim()
    {
        var version = new PercentRuleVersionDto
        {
            Percentages = new Dictionary<Guid, decimal> { [Alice] = 100m }
        };

        foreach (var options in new[] { Api, Client })
        {
            using var written = JsonDocument.Parse(JsonSerializer.Serialize(version, options));

            var percentages = written.RootElement.GetProperty("percentages");

            Assert.True(percentages.TryGetProperty(Alice.ToString(), out _),
                $"the user id should key the split unchanged, got {percentages}");
        }
    }

    /// <summary>
    /// Percentages carry cents. A round trip through <c>double</c> would not keep 33.33,
    /// and the drift would land in someone's share of a bill.
    /// </summary>
    [Fact]
    public void A_percentage_keeps_its_precision_across_the_wire()
    {
        // Declared as the base type deliberately -- see the discriminator test below.
        RuleVersionDto version = new PercentRuleVersionDto
        {
            Percentages = new Dictionary<Guid, decimal> { [Alice] = 33.33m, [Bob] = 66.67m }
        };

        var read = Assert.IsType<PercentRuleVersionDto>(
            JsonSerializer.Deserialize<RuleVersionDto>(
                JsonSerializer.Serialize(version, Api), Client));

        Assert.Equal(33.33m, read.Percentages[Alice]);
        Assert.Equal(66.67m, read.Percentages[Bob]);
    }

    /// <summary>
    /// A discriminator neither end knows fails rather than degrading to a base instance,
    /// which would be a rule with no split at all.
    /// </summary>
    [Fact]
    public void An_unknown_rule_type_is_rejected_rather_than_silently_dropped()
    {
        const string fromANewerApi = """{"$type":"weighted","weights":{}}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RuleVersionDto>(fromANewerApi, Client));
    }

    /// <summary>
    /// And a payload that lost its discriminator on the way is rejected too, rather than
    /// resolving to whichever subtype happens to be listed first.
    /// </summary>
    [Fact]
    public void A_rule_version_with_no_discriminator_is_rejected()
    {
        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Deserialize<RuleVersionDto>("""{"percentages":{}}""", Client));
    }

    /// <summary>
    /// The trap, and the reason every DTO that carries a rule version declares it as
    /// <see cref="RuleVersionDto"/>. System.Text.Json writes the discriminator only when
    /// the static type is the base: hand it a concrete subtype and the <c>$type</c> is
    /// simply absent, and the other end then fails to reconstruct it. Nothing in C# marks
    /// the difference, so it is pinned here.
    /// </summary>
    [Fact]
    public void Serializing_through_the_concrete_type_loses_the_discriminator()
    {
        var concrete = new PercentRuleVersionDto
        {
            Percentages = new Dictionary<Guid, decimal> { [Alice] = 100m }
        };

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(concrete, Api));
        Assert.False(written.RootElement.TryGetProperty("$type", out _));

        // Which is exactly what the reading end cannot cope with.
        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Deserialize<RuleVersionDto>(
                JsonSerializer.Serialize(concrete, Api), Client));

        // Through the base type it is there, and it round-trips.
        RuleVersionDto asBase = concrete;
        using var throughBase = JsonDocument.Parse(JsonSerializer.Serialize(asBase, Api));
        Assert.True(throughBase.RootElement.TryGetProperty("$type", out _));
    }
}
