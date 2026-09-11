using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Writing the divisions a rule stood for before it was ever recorded here.
/// </summary>
/// <remarks>
/// Every other path that opens a version stamps the clock, which is right for an edit
/// somebody is making now and no use at all for a past that happened in a spreadsheet. A
/// rule migrated out of one arrives holding a single version, and every expense migrated
/// with it points at that version whatever month it was spent in -- so the ratios that
/// changed twenty times over 42 months are recorded as having never changed.
/// <para>
/// This is deliberately the narrowest thing that fixes that: it is accepted only for a rule
/// that has stood for one division since it was made, and the last entry has to be that
/// division, so the row the expenses already point at is reused rather than replaced. A rule
/// that has genuinely changed since is refused by name.
/// </para>
/// </remarks>
public class SplitRuleHistoryTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo()
    {
        var self = GetService<ICurrentUser>().User.Id;

        var group = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, self, other.Id);
    }

    private static SharesSplitRuleDto Shares(Guid a, int weightA, Guid b, int weightB) =>
        new() { Shares = new Dictionary<Guid, int> { [a] = weightA, [b] = weightB } };

    private static DateTimeOffset On(int year, int month) =>
        new(year, month, 1, 0, 0, 0, TimeSpan.Zero);

    private Task<List<SplitRuleVersion>> VersionsOf(Guid ruleId) =>
        DbContext.Set<SplitRuleVersion>()
            .Where(version => version.SplitRuleId == ruleId)
            .OrderBy(version => version.StartedAt)
            .ToListAsync(Ct);

    private Task<SplitRule> ARule(Guid groupId, SplitRuleDto definition) =>
        Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Groceries",
            Definition = definition
        }, Ct);

    // ---- What it writes -------------------------------------------------------------------

    /// <summary>
    /// Every entry but the last becomes a closed version, and the windows meet: each one
    /// ends exactly where the next begins, so there is no month the rule said two things or
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Each_entry_becomes_a_version_whose_window_ends_where_the_next_begins()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        await Rules.SetHistory(rule.Id,
        [
            new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
            new SplitRuleVersionInput(On(2024, 1), Shares(self, 2, other, 1)),
            new SplitRuleVersionInput(On(2025, 6), Shares(self, 1, other, 1))
        ], Ct);

        var versions = await VersionsOf(rule.Id);

        Assert.Equal(3, versions.Count);
        Assert.Equal(On(2023, 3), versions[0].StartedAt);
        Assert.Equal(On(2024, 1), versions[0].SupersededAt);
        Assert.Equal(On(2024, 1), versions[1].StartedAt);
        Assert.Equal(On(2025, 6), versions[1].SupersededAt);
        Assert.Equal(On(2025, 6), versions[2].StartedAt);
    }

    /// <summary>
    /// Exactly one version is open when it is done, which is the invariant the partial
    /// unique index in the schema exists to hold.
    /// </summary>
    [Fact]
    public async Task Exactly_one_version_is_left_open()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        await Rules.SetHistory(rule.Id,
        [
            new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
            new SplitRuleVersionInput(On(2024, 1), Shares(self, 1, other, 1))
        ], Ct);

        var open = (await VersionsOf(rule.Id)).Where(version => version.SupersededAt is null);

        Assert.Single(open);
    }

    /// <summary>
    /// The row the rule was already on is the one that stays open, rather than being deleted
    /// and written back.
    /// </summary>
    /// <remarks>
    /// The whole reason the last entry has to match. Transactions point at that row, and the
    /// pointer is the only account of which division produced their amounts; replacing it
    /// would leave every one of them pointing at nothing. All that moves on it is the date
    /// it opened.
    /// </remarks>
    [Fact]
    public async Task The_version_expenses_already_point_at_is_reused_rather_than_replaced()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var before = Assert.Single(await VersionsOf(rule.Id));

        await Rules.SetHistory(rule.Id,
        [
            new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
            new SplitRuleVersionInput(On(2024, 1), Shares(self, 1, other, 1))
        ], Ct);

        var stillOpen = Assert.Single(
            await VersionsOf(rule.Id), version => version.SupersededAt is null);

        Assert.Equal(before.Id, stillOpen.Id);
        Assert.Equal(On(2024, 1), stillOpen.StartedAt);
    }

    /// <summary>
    /// A history of one entry is a rule that has said one thing since a date. It writes no
    /// new rows and backdates the one that is there.
    /// </summary>
    [Fact]
    public async Task A_history_of_one_entry_only_backdates_the_version_that_is_there()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        await Rules.SetHistory(rule.Id,
            [new SplitRuleVersionInput(On(2023, 3), Shares(self, 1, other, 1))], Ct);

        var version = Assert.Single(await VersionsOf(rule.Id));

        Assert.Equal(On(2023, 3), version.StartedAt);
        Assert.Null(version.SupersededAt);
    }

    /// <summary>
    /// Kinds may change across a history, because a group's arrangement does: "whoever paid"
    /// for a year, then a ratio once somebody's salary changed.
    /// </summary>
    [Fact]
    public async Task A_history_may_change_kind_between_entries()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 2, other, 1));

        var history = await Rules.SetHistory(rule.Id,
        [
            new SplitRuleVersionInput(On(2023, 3), new PayerSplitRuleDto()),
            new SplitRuleVersionInput(On(2024, 1), new EvenSplitRuleDto()),
            new SplitRuleVersionInput(On(2025, 6), Shares(self, 2, other, 1))
        ], Ct);

        // Newest first, which is the order the history reads in.
        Assert.Equal(3, history.Versions.Count);
        Assert.IsType<SharesSplitRuleDto>(history.Versions[0].Definition);
        Assert.IsType<EvenSplitRuleDto>(history.Versions[1].Definition);
        Assert.IsType<PayerSplitRuleDto>(history.Versions[2].Definition);
    }

    // ---- What it refuses ------------------------------------------------------------------

    /// <summary>
    /// A rule that has already changed has windows of its own and expenses pointing into
    /// them. What a written history should do to those is a larger question than this
    /// answers, so it says so rather than guessing.
    /// </summary>
    [Fact]
    public async Task A_rule_that_has_already_changed_is_refused_by_name()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 2, other, 1));

        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Groceries",
            Definition = Shares(self, 1, other, 1)
        }, Ct);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Rules.SetHistory(rule.Id,
            [
                new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
                new SplitRuleVersionInput(On(2024, 1), Shares(self, 1, other, 1))
            ], Ct));

        Assert.Equal(ErrorCodes.SplitRuleAlreadyHasHistory, refusal.Code);
        Assert.Equal(2, refusal.Extensions["versionCount"]);

        // And nothing was written: the rule is on the two versions its own edit gave it.
        Assert.Equal(2, (await VersionsOf(rule.Id)).Count);
    }

    /// <summary>
    /// The last entry has to be what the rule says now, because that entry <em>is</em> the
    /// row the rule is already on. A history ending anywhere else is a correction to the
    /// rule dressed up as a history, and the ordinary update is where a correction goes.
    /// </summary>
    [Fact]
    public async Task A_history_that_does_not_end_where_the_rule_stands_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Rules.SetHistory(rule.Id,
            [
                new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
                new SplitRuleVersionInput(On(2024, 1), Shares(self, 2, other, 1))
            ], Ct));

        Assert.Equal(ErrorCodes.SplitRuleHistoryEndsElsewhere, refusal.Code);
        Assert.Single(await VersionsOf(rule.Id));
    }

    /// <summary>
    /// The same division under a different kind is a different division, which is the
    /// handler's judgement and not a comparison of two DTOs.
    /// </summary>
    [Fact]
    public async Task A_last_entry_of_another_kind_is_refused_even_when_it_would_divide_alike()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Rules.SetHistory(rule.Id,
                [new SplitRuleVersionInput(On(2023, 3), new EvenSplitRuleDto())], Ct));

        Assert.Equal(ErrorCodes.SplitRuleHistoryEndsElsewhere, refusal.Code);
    }

    [Fact]
    public async Task A_history_with_no_entries_at_all_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Rules.SetHistory(rule.Id, [], Ct));

        Assert.Equal(ErrorCodes.SplitRuleHistoryInvalid, refusal.Code);
    }

    /// <summary>
    /// Each entry's window ends where the next one starts, so dates that do not strictly
    /// increase describe a version that was never what the rule said.
    /// </summary>
    [Theory]
    [InlineData(2023, 3)]
    [InlineData(2022, 12)]
    public async Task Dates_that_do_not_move_forward_are_refused(int year, int month)
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Rules.SetHistory(rule.Id,
            [
                new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
                new SplitRuleVersionInput(On(year, month), Shares(self, 1, other, 1))
            ], Ct));

        Assert.Equal(ErrorCodes.SplitRuleHistoryInvalid, refusal.Code);
        Assert.Equal(1, refusal.Extensions["index"]);
    }

    /// <summary>
    /// A history is an account of what a rule has already stood for, so an entry that starts
    /// after now is refused rather than forward-dating the version it leaves open.
    /// </summary>
    /// <remarks>
    /// Two readers disagree the moment it does. A new expense is divided by the version
    /// nothing has superseded, whatever its date, so it would be billed under a window that
    /// has not opened; a reattach looks for the window containing the date and would point
    /// the very same expense at the entry before it. The rule's own history would then say
    /// one thing and today's spending another.
    /// </remarks>
    [Fact]
    public async Task An_entry_that_starts_after_now_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Rules.SetHistory(rule.Id,
            [
                new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, other, 2)),
                new SplitRuleVersionInput(
                    DateTimeOffset.UtcNow.AddDays(1), Shares(self, 1, other, 1))
            ], Ct));

        Assert.Equal(ErrorCodes.SplitRuleHistoryInvalid, refusal.Code);
        Assert.Equal(1, refusal.Extensions["index"]);

        // And nothing was written: the rule still stands on the single version it was made
        // with, so a history refused halfway cannot leave one behind.
        Assert.Single(await VersionsOf(rule.Id));
    }

    /// <summary>
    /// Every entry goes through the same validation a create does, including the one thing
    /// no handler can know: whether the people it names are in the group.
    /// </summary>
    [Fact]
    public async Task An_entry_naming_somebody_outside_the_group_is_refused()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));
        var stranger = await CreateNewUser();

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Rules.SetHistory(rule.Id,
            [
                new SplitRuleVersionInput(On(2023, 3), Shares(self, 3, stranger.Id, 2)),
                new SplitRuleVersionInput(On(2024, 1), Shares(self, 1, other, 1))
            ], Ct));

        Assert.Equal(ErrorCodes.RuleUsersNotInGroup, refusal.Code);
        Assert.Single(await VersionsOf(rule.Id));
    }

    /// <summary>
    /// An incoherent definition is refused for the same reasons a create's is, and the whole
    /// history is refused with it.
    /// </summary>
    [Fact]
    public async Task An_incoherent_entry_is_refused_and_nothing_is_written()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var rule = await ARule(groupId, Shares(self, 1, other, 1));

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Rules.SetHistory(rule.Id,
            [
                new SplitRuleVersionInput(On(2023, 3), new PercentSplitRuleDto
                {
                    Percentages = new Dictionary<Guid, decimal> { [self] = 60m, [other] = 60m }
                }),
                new SplitRuleVersionInput(On(2024, 1), Shares(self, 1, other, 1))
            ], Ct));

        Assert.Equal(ErrorCodes.SplitRuleInvalid, refusal.Code);
        Assert.Single(await VersionsOf(rule.Id));
    }

    /// <summary>
    /// A rule in somebody else's group is not there to be rewritten, and answers as a
    /// missing one does.
    /// </summary>
    [Fact]
    public async Task A_rule_in_a_group_the_caller_is_not_in_is_not_found()
    {
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Rules.SetHistory(Guid.NewGuid(),
                [new SplitRuleVersionInput(On(2023, 3), new EvenSplitRuleDto())], Ct));

        Assert.Equal(ErrorCodes.SplitRuleNotFound, refusal.Code);
    }
}

/// <summary>
/// The same thing over real HTTP, because the body is a bare JSON array and the polymorphic
/// definitions inside it are the part a service test never exercises.
/// </summary>
public class SplitRuleHistoryEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<(Guid GroupId, Guid Me)> AGroup()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var created = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Flat" }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!;

        return (group.Id, me.Id);
    }

    [Fact]
    public async Task A_history_sent_as_a_json_array_is_written_and_read_back()
    {
        var (groupId, me) = await AGroup();

        var created = await Client.PostAsJsonAsync("/split-rules", new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Groceries",
            Definition = new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [me] = 1 } }
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var rule = (await created.Content.ReadFromJsonAsync<SplitRuleDetailsResponse>(Json, Ct))!;

        var written = await Client.PutAsJsonAsync($"/split-rules/{rule.Id}/versions",
            new SplitRuleVersionInput[]
            {
                new(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero), new PayerSplitRuleDto()),
                new(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [me] = 1 } })
            }, Json, Ct);
        written.EnsureSuccessStatusCode();

        var history = (await Client.GetFromJsonAsync<SplitRuleHistoryResponse>(
            $"/split-rules/{rule.Id}/versions", Json, Ct))!;

        Assert.Equal(2, history.Versions.Count);
        Assert.Equal(rule.VersionId, history.Versions[0].Id);
        Assert.Null(history.Versions[0].SupersededAt);
        Assert.IsType<PayerSplitRuleDto>(history.Versions[1].Definition);
    }

    [Fact]
    public async Task A_history_that_does_not_end_where_the_rule_stands_answers_409()
    {
        var (groupId, me) = await AGroup();

        var created = await Client.PostAsJsonAsync("/split-rules", new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Groceries",
            Definition = new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [me] = 1 } }
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var rule = (await created.Content.ReadFromJsonAsync<SplitRuleDetailsResponse>(Json, Ct))!;

        var response = await Client.PutAsJsonAsync($"/split-rules/{rule.Id}/versions",
            new SplitRuleVersionInput[]
            {
                new(new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero), new PayerSplitRuleDto())
            }, Json, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            await response.Content.ReadAsStringAsync(Ct), Json);

        Assert.Equal(ErrorCodes.SplitRuleHistoryEndsElsewhere, problem!.Code);
    }
}
