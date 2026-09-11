using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Editing an expense's division over the wire, which is the one place keeping
/// <c>PATCH</c> costs something.
/// </summary>
/// <remarks>
/// A patch can change part of a model, and the shares are the part that has to stay
/// consistent with the rest of it: a patch that changes the amount and says nothing about
/// the shares would otherwise leave shares that no longer sum to it, and every balance in
/// the group is that sum.
/// <para>
/// So silence about the shares is an instruction, not an omission -- and the endpoint can
/// only tell silence from a statement by reading the operations before applying them.
/// These go over real HTTP because that reading is the behaviour under test, and calling
/// the service directly skips the patch document entirely.
/// </para>
/// <para>
/// What silence instructs is <em>keep these</em> for any division that is somebody's own,
/// and only a move between groups clears them. The tests below are the record of that: an
/// amount changed on its own is refused on a hand-split expense, and a name, a note, a date
/// and a merchant changed together move nothing whatever divided it.
/// </para>
/// <para>
/// The one thing silence no longer keeps is a division nobody chose. Shares a rule produced
/// -- or an even division under no rule, which the app made just as surely -- are worked out
/// again when the edit moves something they were worked out <em>from</em>: the amount, the
/// payer, the category or the group. Nothing is re-derived on the strength of an absent
/// operation alone, which is the instruction that caused the incident; it takes an input
/// having moved, and the shares being reproducible from the expense's own division.
/// </para>
/// <para>
/// It meant the opposite until 47c6904, and the reversal has a body count behind it: the
/// day before, a merchant-linking pass over 733 expenses re-divided all of them from their
/// categories' rules and moved 1,394.72 onto one member, silently, because every one of
/// those patches said nothing about the shares. Anything proposing to make silence mean
/// "divide it again" is proposing that again, and the header above is what the design was
/// before it happened.
/// </para>
/// </remarks>
public class TransactionSplitPatchTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static StringContent PatchBody(params (string Op, string Path, object? Value)[] operations) =>
        new(JsonSerializer.Serialize(
                operations.Select(operation => new
                {
                    op = operation.Op,
                    path = operation.Path,
                    value = operation.Value
                }), Json),
            Encoding.UTF8,
            "application/json-patch+json");

    /// <summary>A group of two, which is what every fixture below is built on.</summary>
    private async Task<(Guid GroupId, Guid Me, Guid Other)> AGroupOfTwo()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        using var otherClient = _host.ClientForAnotherUser();
        var other = (await otherClient.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var groupResponse = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Trip" }, Json, Ct);
        groupResponse.EnsureSuccessStatusCode();
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        // Two calls, because joining is now something the invitee agrees to: the group asks,
        // and they accept. There is no route left that puts somebody in a group without it.
        var invited = await Client.PostAsJsonAsync($"/groups/{groupId}/invitations",
            new InviteToGroupRequest { Names = ["Other"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();

        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;

        var accepted = await otherClient.PostAsync($"/invitations/claims/{pending[0].Token}", null, Ct);
        accepted.EnsureSuccessStatusCode();

        return (groupId, me.Id, other.Id);
    }

    /// <summary>
    /// A group of two with an expense of 100 split evenly, which is the shape every test
    /// below edits.
    /// </summary>
    private async Task<(Guid TransactionId, Guid Me, Guid Other)> AnEvenlySplitExpense()
    {
        var (groupId, me, other) = await AGroupOfTwo();

        var transaction = await AnExpense(groupId, me, 100m, categoryId: null);

        return (transaction, me, other);
    }

    private async Task<Guid> AnExpense(Guid groupId, Guid payerId, decimal amount, Guid? categoryId)
    {
        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            PaidByUserId = payerId
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!.Id;
    }

    private static SharesSplitRuleDto Shares(Guid a, int weightA, Guid b, int weightB) =>
        new() { Shares = new Dictionary<Guid, int> { [a] = weightA, [b] = weightB } };

    /// <summary>A category whose default rule divides the way <paramref name="definition"/> says.</summary>
    private async Task<(Guid CategoryId, Guid RuleId)> ACategory(
        Guid groupId, string name, SplitRuleDto definition)
    {
        var created = await Client.PostAsJsonAsync("/split-rules", new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = name,
            Definition = definition
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var rule = (await created.Content.ReadFromJsonAsync<SplitRuleDetailsResponse>(Json, Ct))!;

        var filed = await Client.PostAsJsonAsync("/categories", new CreateCategoryRequest
        {
            GroupId = groupId,
            Name = name,
            DefaultSplitRuleId = rule.Id
        }, Json, Ct);
        filed.EnsureSuccessStatusCode();

        var category = (await filed.Content.ReadFromJsonAsync<CategoryResponse>(Json, Ct))!;

        return (category.Id, rule.Id);
    }

    private async Task Rewrite(Guid ruleId, string name, SplitRuleDto definition)
    {
        var edited = await Client.PutAsJsonAsync($"/split-rules/{ruleId}",
            new UpdateSplitRuleRequest { Name = name, Definition = definition }, Json, Ct);

        edited.EnsureSuccessStatusCode();
    }

    private async Task<TransactionDetailsResponse> Details(Guid transactionId) =>
        (await Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{transactionId}", Json, Ct))!;

    private static decimal ShareOf(TransactionDetailsResponse details, Guid userId) =>
        details.Splits.Single(split => split.UserId == userId).Amount;

    private async Task<ProblemDetails> Refused(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            await response.Content.ReadAsStringAsync(Ct), Json);

        Assert.NotNull(problem);
        return problem;
    }

    /// <summary>
    /// The shares travel with their user ids, because the edit dialog sends them back and
    /// a name is not something the API can be addressed by.
    /// </summary>
    [Fact]
    public async Task The_details_of_an_expense_name_who_owes_each_share()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var details = await Details(transactionId);

        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(50m, ShareOf(details, me));
        Assert.Equal(50m, ShareOf(details, other));
        Assert.All(details.Splits, split => Assert.NotEqual(Guid.Empty, split.UserId));
    }

    /// <summary>
    /// Patching the amount alone on an expense nobody hand-split divides it again at the new
    /// amount.
    /// </summary>
    /// <remarks>
    /// It was refused with <c>SPLITS_DO_NOT_SUM_TO_AMOUNT</c> until the API could tell the
    /// two kinds of division apart. The shares here are the ones an even division produced --
    /// the fixture has no category, which is "evenly, under no rule", and a division the app
    /// made follows the amount it was made from. Nothing about silence has loosened: the
    /// shares are still carried into the patch, and they are still kept whenever they are
    /// somebody's own.
    /// <para>
    /// <see cref="Patching_the_amount_alone_on_a_hand_split_expense_is_still_refused"/> is
    /// that other half, and it is where the refusal lives now.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Patching_the_amount_alone_divides_a_rule_divided_expense_again()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/amount", 250m)), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(250m, details.Amount);
        Assert.Equal(125m, ShareOf(details, me));
        Assert.Equal(125m, ShareOf(details, other));
    }

    /// <summary>
    /// The same patch on an expense whose shares somebody typed is refused, because those
    /// amounts are that person's record and no rule is behind them to work them out again.
    /// </summary>
    /// <remarks>
    /// The protective half of the pair above, and the one that has to keep working: the
    /// alternative is an expense whose stated division is silently restated the next time
    /// anybody corrects a receipt total.
    /// </remarks>
    [Fact]
    public async Task Patching_the_amount_alone_on_a_hand_split_expense_is_still_refused()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var byHand = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 70m },
                new { userId = other, amount = 30m }
            })), Ct);
        byHand.EnsureSuccessStatusCode();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/amount", 250m)), Ct);

        var problem = await Refused(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, problem.Code);

        // And nothing moved.
        var details = await Details(transactionId);
        Assert.Equal(100m, details.Amount);
        Assert.Equal(70m, ShareOf(details, me));
    }

    /// <summary>
    /// The incident, reconstructed: a name, a note, a date and a merchant changed in one
    /// patch, and not a penny moves.
    /// </summary>
    /// <remarks>
    /// <see cref="Patching_only_the_name_leaves_the_shares_alone"/> pins one field of the
    /// four. This pins all of them at once, and the merchant in particular, because the pass
    /// that re-divided 733 expenses and moved 1,394.72 onto one member was a merchant-linking
    /// pass. None of these four says anything about who owed what, so none of them may reach
    /// the division -- whatever the division turns out to have come from.
    /// </remarks>
    [Fact]
    public async Task Patching_only_metadata_moves_no_money()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var byHand = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 64m },
                new { userId = other, amount = 36m }
            })), Ct);
        byHand.EnsureSuccessStatusCode();

        var shop = await Client.PostAsJsonAsync("/merchants",
            new CreateMerchantRequest { Name = "Hotel Cristal" }, Json, Ct);
        shop.EnsureSuccessStatusCode();
        var merchantId = (await shop.Content.ReadFromJsonAsync<MerchantResponse>(Json, Ct))!.Id;

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/name", "Hotel, two nights"),
                ("replace", "/description", "with breakfast"),
                ("replace", "/dateTime", DateTimeOffset.UtcNow.AddDays(-3)),
                ("replace", "/merchantId", merchantId)), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal("Hotel, two nights", details.Name);
        Assert.Equal(merchantId, details.MerchantId);
        Assert.Equal(64m, ShareOf(details, me));
        Assert.Equal(36m, ShareOf(details, other));
    }

    /// <summary>
    /// Patching the payer updates who paid while preserving the division.
    /// </summary>
    [Fact]
    public async Task Patching_the_payer_alone_preserves_the_shares()
    {
        var (transactionId, _, other) = await AnEvenlySplitExpense();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/paidByUserId", other)), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(other, details.PaidByUserId);
        Assert.Equal(100m, details.Splits.Sum(split => split.Amount));
    }

    [Fact]
    public async Task Patching_the_shares_keeps_exactly_what_was_sent()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 90m },
                new { userId = other, amount = 10m }
            })), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(90m, ShareOf(details, me));
        Assert.Equal(10m, ShareOf(details, other));
    }

    /// <summary>
    /// Both at once, which is the ordinary way somebody changes what an expense cost and
    /// who carries it: the shares are checked against the amount the same patch set, not
    /// the one it replaced.
    /// </summary>
    [Fact]
    public async Task Patching_the_amount_and_the_shares_together_checks_them_against_each_other()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/amount", 60m),
                ("replace", "/splits", new[]
                {
                    new { userId = me, amount = 20m },
                    new { userId = other, amount = 40m }
                })), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(60m, details.Amount);
        Assert.Equal(20m, ShareOf(details, me));
        Assert.Equal(40m, ShareOf(details, other));
    }

    /// <summary>
    /// Shares stated against an amount they do not add up to are refused rather than
    /// adjusted, and the refusal says by how much.
    /// </summary>
    [Fact]
    public async Task Shares_that_do_not_add_up_to_the_patched_amount_are_refused()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/amount", 60m),
                ("replace", "/splits", new[]
                {
                    new { userId = me, amount = 20m },
                    new { userId = other, amount = 20m }
                })), Ct);

        var problem = await Refused(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, problem.Code);

        // And nothing moved: the expense is as it was.
        var details = await Details(transactionId);
        Assert.Equal(100m, details.Amount);
        Assert.Equal(50m, ShareOf(details, me));
    }

    /// <summary>
    /// One share addressed by index is a statement about the division too -- the rest of
    /// the list has to stay put for that edit to mean anything, so it is not recomputed.
    /// </summary>
    [Fact]
    public async Task Patching_a_single_share_by_index_is_taken_as_stating_them_all()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var before = await Details(transactionId);
        var firstIsMine = before.Splits[0].UserId == me;

        // Move ten from the first share to the second, so the pair still sums to 100.
        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/splits/0/amount", 40m),
                ("replace", "/splits/1/amount", 60m)), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(100m, details.Splits.Sum(split => split.Amount));
        Assert.Equal(firstIsMine ? 40m : 60m, ShareOf(details, me));
        Assert.Equal(firstIsMine ? 60m : 40m, ShareOf(details, other));
    }

    /// <summary>
    /// The same edit left half-done: one share changed and the other not, so the pair no
    /// longer sums. Refused, because the alternative is a group whose balances quietly
    /// stop adding up to zero.
    /// </summary>
    [Fact]
    public async Task Patching_one_share_and_leaving_the_rest_short_is_refused()
    {
        var (transactionId, _, _) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits/0/amount", 40m)), Ct);

        var problem = await Refused(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, problem.Code);
    }

    /// <summary>
    /// A patch of something unrelated leaves existing custom shares intact without
    /// resetting/re-splitting by category.
    /// </summary>
    [Fact]
    public async Task Patching_only_the_name_leaves_the_shares_alone()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        // Stating custom splits (70 / 30)
        var customSplitsResponse = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 70m },
                new { userId = other, amount = 30m }
            })), Ct);
        customSplitsResponse.EnsureSuccessStatusCode();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/name", "Hotel, two nights")), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal("Hotel, two nights", details.Name);
        Assert.Equal(70m, ShareOf(details, me));
        Assert.Equal(30m, ShareOf(details, other));
    }

    /// <summary>
    /// Sending <c>/splits</c> as null outright is the way to ask for the division again.
    /// </summary>
    /// <remarks>
    /// The other half of what silence means. Keeping the shares is the default because a
    /// metadata edit must not move money, and this is the operation somebody sends when
    /// moving it is precisely what they meant -- the app's "Automatically", chosen on an
    /// expense whose shares were typed. Without it the choice would be unreachable, which
    /// is what made the toggle in the edit dialog unable to change anything.
    /// <para>
    /// An instruction and not an absence: <c>Touches("/splits")</c> sees this operation, so
    /// nothing here loosens what a patch that says nothing about the shares does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Patching_the_shares_to_null_outright_divides_it_again()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var byHand = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 70m },
                new { userId = other, amount = 30m }
            })), Ct);
        byHand.EnsureSuccessStatusCode();

        var redivided = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", null)), Ct);
        redivided.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(50m, ShareOf(details, me));
        Assert.Equal(50m, ShareOf(details, other));
    }

    /// <summary>
    /// The two operations together are the way past the refusal a changed amount earns on
    /// its own.
    /// </summary>
    /// <remarks>
    /// <see cref="Patching_the_amount_alone_on_a_hand_split_expense_is_still_refused"/> is
    /// the other half of this: the stored shares no longer sum to the new total, and the
    /// endpoint will not adjust them behind anybody's back. So there are exactly two
    /// answers to "the amount changed on an expense somebody split by hand" -- state the
    /// shares, or ask for them again -- and this is the second, which is what the CLI's
    /// <c>--redivide</c> and the edit dialog's "Automatically" both send.
    /// <para>
    /// Checked against the amount the same patch set, not the one it replaced: the
    /// division is worked out after the new total is applied, or 250 would be divided as
    /// though it were still 100.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Patching_the_amount_and_asking_for_the_division_again_is_accepted()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var byHand = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 70m },
                new { userId = other, amount = 30m }
            })), Ct);
        byHand.EnsureSuccessStatusCode();

        var refused = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/amount", 250m)), Ct);

        Assert.Equal(
            ErrorCodes.SplitsDoNotSumToAmount,
            (await Refused(refused, HttpStatusCode.UnprocessableEntity)).Code);

        var accepted = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/amount", 250m),
                ("replace", "/splits", null)), Ct);
        accepted.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(250m, details.Amount);
        Assert.Equal(125m, ShareOf(details, me));
        Assert.Equal(125m, ShareOf(details, other));
    }

    /// <summary>
    /// And the preview of that same instruction agrees, which is what
    /// <c>?redivide=true</c> is for.
    /// </summary>
    /// <remarks>
    /// The preview takes the whole expense in a body rather than the patch, so it cannot
    /// see the operation above: a body with no shares is what a save that keeps them looks
    /// like. The flag is the one thing the body is not allowed to carry -- a second way to
    /// say "divide it again" inside the save contract is the instruction that re-divided
    /// 733 expenses -- so it rides on the query string, and its absence answers as silence
    /// does.
    /// </remarks>
    [Fact]
    public async Task Previewing_with_redivide_asked_shows_the_division_a_null_patch_would_store()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var byHand = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 70m },
                new { userId = other, amount = 30m }
            })), Ct);
        byHand.EnsureSuccessStatusCode();

        var edited = (await Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{transactionId}", Json, Ct))!;

        var body = new UpdateTransactionRequest
        {
            Name = edited.Name,
            Amount = edited.Amount,
            DateTime = edited.DateTime,
            GroupId = edited.GroupId,
            PaidByUserId = edited.PaidByUserId
        };

        var kept = await Client.PostAsJsonAsync(
            $"/transactions/{transactionId}/preview", body, Json, Ct);
        kept.EnsureSuccessStatusCode();

        var asStored = (await kept.Content.ReadFromJsonAsync<SplitPreviewResponse>(Json, Ct))!;
        Assert.Equal(70m, asStored.Splits.Single(split => split.UserId == me).Amount);

        var again = await Client.PostAsJsonAsync(
            $"/transactions/{transactionId}/preview?redivide=true", body, Json, Ct);
        again.EnsureSuccessStatusCode();

        var divided = (await again.Content.ReadFromJsonAsync<SplitPreviewResponse>(Json, Ct))!;
        Assert.Equal(50m, divided.Splits.Single(split => split.UserId == me).Amount);
        Assert.Equal(50m, divided.Splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// Asking for the division again divides by the version the expense was written under,
    /// not by the rule as it reads today.
    /// </summary>
    /// <remarks>
    /// The half of this operation the CLI documentation had backwards. "Divide it again" is
    /// a question about one expense, and the rule it was filed under at the time is the only
    /// honest answer: nobody correcting an expense is asking to be re-billed under a ratio
    /// the group agreed afterwards. What does reach a different rule is filing it under a
    /// different category -- the version it holds then belongs to a rule it is no longer
    /// filed under, which is
    /// <c>Filing_an_expense_under_another_category_divides_it_by_that_categorys_rule</c>.
    /// <para>
    /// The two tests above send the same operation against an expense under no category at
    /// all, where the version is null on both readings and they cannot be told apart. This
    /// is the fixture that tells them apart: 60 / 30 is the old rule applied afresh, 45 / 45
    /// would be today's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Asking_for_the_division_again_uses_the_version_the_expense_was_written_under()
    {
        var (groupId, me, other) = await AGroupOfTwo();
        var (categoryId, ruleId) = await ACategory(groupId, "Rent", Shares(me, 2, other, 1));

        var transactionId = await AnExpense(groupId, me, 90m, categoryId);

        Assert.Equal(60m, ShareOf(await Details(transactionId), me));

        // The flat agrees to split the rent evenly from here on. The expense above was
        // recorded before that and goes on pointing at the version that divided it.
        await Rewrite(ruleId, "Rent", Shares(me, 1, other, 1));

        var again = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", null)), Ct);
        again.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(60m, ShareOf(details, me));
        Assert.Equal(30m, ShareOf(details, other));
    }

    /// <summary>
    /// A preview divides again for the same reasons a save does, on an expense whose rule
    /// weighs by the people it names.
    /// </summary>
    /// <remarks>
    /// Over HTTP because that is what makes it a real test of the question. The preview
    /// loads the expense's version to name the rule in its answer, and loads it without the
    /// participants a proportional rule divides by; asked whether the stored shares are that
    /// rule's, a reading that preferred the loaded navigation would be handed a rule
    /// appearing to name nobody, fail to divide by it, and answer "not the rule's" -- so the
    /// preview would carry the stale shares forward while the save re-divided. In a service
    /// test the two readings agree, because the version is already tracked with its
    /// participants from an earlier call in the same scope; a request is the scope where it
    /// is not.
    /// <para>
    /// The amount is left alone deliberately. Carried-forward shares that no longer sum
    /// would be refused outright, and a refusal is not the failure this is about: the
    /// failure is a preview that answers confidently with the division the save is about to
    /// replace.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Previewing_a_move_between_categories_divides_by_the_new_one_on_a_weighted_rule()
    {
        var (groupId, me, other) = await AGroupOfTwo();
        var (rent, _) = await ACategory(groupId, "Rent", Shares(me, 2, other, 1));
        var (food, _) = await ACategory(groupId, "Food", Shares(me, 1, other, 1));

        var transactionId = await AnExpense(groupId, me, 90m, rent);

        var stored = await Details(transactionId);
        Assert.Equal(60m, ShareOf(stored, me));

        var body = new UpdateTransactionRequest
        {
            Name = stored.Name,
            Amount = stored.Amount,
            DateTime = stored.DateTime,
            GroupId = stored.GroupId,
            PaidByUserId = stored.PaidByUserId,
            CategoryId = food
        };

        var preview = await Client.PostAsJsonAsync(
            $"/transactions/{transactionId}/preview", body, Json, Ct);
        preview.EnsureSuccessStatusCode();

        var divided = (await preview.Content.ReadFromJsonAsync<SplitPreviewResponse>(Json, Ct))!;

        Assert.Equal(45m, divided.Splits.Single(split => split.UserId == me).Amount);
        Assert.Equal(45m, divided.Splits.Single(split => split.UserId == other).Amount);
        Assert.Equal("Food", divided.RuleName);

        // And the save it previewed agrees, which is the whole claim the preview makes.
        var saved = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/categoryId", food)), Ct);
        saved.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(45m, ShareOf(details, me));
        Assert.Equal(45m, ShareOf(details, other));
    }

    [Fact]
    public async Task A_share_for_somebody_outside_the_group_is_refused()
    {
        var (transactionId, me, _) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 50m },
                new { userId = Guid.NewGuid(), amount = 50m }
            })), Ct);

        var problem = await Refused(response, HttpStatusCode.Conflict);

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, problem.Code);
    }

    /// <summary>
    /// An expense can be created with its division stated outright, which is what the
    /// dialog sends when somebody sets the shares themselves.
    /// </summary>
    [Fact]
    public async Task An_expense_can_be_created_with_its_shares_stated()
    {
        var (_, me, other) = await AnEvenlySplitExpense();

        var groups = await Client.GetFromJsonAsync<List<GroupResponse>>("/groups", Json, Ct);
        var groupId = groups!.Single(group => group.Name == "Trip").Id;

        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Cake",
            Amount = 30m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = me,
            Splits = [new SplitInput { UserId = me, Amount = 30m }]
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var transaction = (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;
        var details = await Details(transaction.Id);

        var share = Assert.Single(details.Splits);
        Assert.Equal(me, share.UserId);
        Assert.Equal(30m, share.Amount);
        Assert.DoesNotContain(details.Splits, split => split.UserId == other);
    }

    /// <summary>
    /// A refusal is problem details like every other, so a client reads one shape whatever
    /// went wrong -- and the shortfall rides along for a dialog to show.
    /// </summary>
    [Fact]
    public async Task The_refusal_is_problem_details_carrying_the_shortfall()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 30m },
                new { userId = other, amount = 20m }
            })), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(50m, body.RootElement.GetProperty("splitTotal").GetDecimal());
        Assert.Equal(100m, body.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(50m, body.RootElement.GetProperty("difference").GetDecimal());
    }

    [Fact]
    public async Task Patching_the_group_to_personal_clears_the_splits()
    {
        var (transactionId, me, _) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/groupId", null)), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Null(details.GroupId);
        var share = Assert.Single(details.Splits);
        Assert.Equal(me, share.UserId);
        Assert.Equal(100m, share.Amount);
    }

    [Fact]
    public async Task Patching_the_group_to_another_group_clears_the_splits_and_recomputes_for_the_new_group()
    {
        var (transactionId, me, _) = await AnEvenlySplitExpense();

        using var other2Client = _host.ClientForAnotherUser();
        var other2 = (await other2Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var group2Response = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Group 2" }, Json, Ct);
        group2Response.EnsureSuccessStatusCode();
        var group2Id = (await group2Response.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        var invited = await Client.PostAsJsonAsync($"/groups/{group2Id}/invitations",
            new InviteToGroupRequest { Names = ["Other two"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();
        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;
        var accepted = await other2Client.PostAsync($"/invitations/claims/{pending[0].Token}", null, Ct);
        accepted.EnsureSuccessStatusCode();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/groupId", group2Id)), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transactionId);
        Assert.Equal(group2Id, details.GroupId);
        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(50m, ShareOf(details, me));
        Assert.Equal(50m, ShareOf(details, other2.Id));
    }

    [Fact]
    public async Task Patching_the_group_to_another_group_with_explicit_splits_applies_them()
    {
        var (transactionId, me, _) = await AnEvenlySplitExpense();

        using var other2Client = _host.ClientForAnotherUser();
        var other2 = (await other2Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var group2Response = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Group 2" }, Json, Ct);
        group2Response.EnsureSuccessStatusCode();
        var group2Id = (await group2Response.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        var invited = await Client.PostAsJsonAsync($"/groups/{group2Id}/invitations",
            new InviteToGroupRequest { Names = ["Other two"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();
        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;
        var accepted = await other2Client.PostAsync($"/invitations/claims/{pending[0].Token}", null, Ct);
        accepted.EnsureSuccessStatusCode();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/groupId", group2Id),
                ("replace", "/splits", new[]
                {
                    new { userId = me, amount = 80m },
                    new { userId = other2.Id, amount = 20m }
                })
            ), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transactionId);
        Assert.Equal(group2Id, details.GroupId);
        Assert.Equal(80m, ShareOf(details, me));
        Assert.Equal(20m, ShareOf(details, other2.Id));
    }

    [Fact]
    public async Task Patching_a_personal_expense_to_a_group_divides_among_members()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        using var otherClient = _host.ClientForAnotherUser();
        var other = (await otherClient.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var groupResponse = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Shared Group" }, Json, Ct);
        groupResponse.EnsureSuccessStatusCode();
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        var invited = await Client.PostAsJsonAsync($"/groups/{groupId}/invitations",
            new InviteToGroupRequest { Names = ["Other"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();
        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;
        var accepted = await otherClient.PostAsync($"/invitations/claims/{pending[0].Token}", null, Ct);
        accepted.EnsureSuccessStatusCode();

        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Coffee",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = me.Id
        }, Json, Ct);
        created.EnsureSuccessStatusCode();
        var transaction = (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;

        var response = await Client.PatchAsync($"/transactions/{transaction.Id}",
            PatchBody(("replace", "/groupId", groupId)), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transaction.Id);
        Assert.Equal(groupId, details.GroupId);
        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(20m, ShareOf(details, me.Id));
        Assert.Equal(20m, ShareOf(details, other.Id));
    }

    [Fact]
    public async Task Patch_returns_transaction_details_response_with_splits()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/name", "New Name")), Ct);
        response.EnsureSuccessStatusCode();

        var details = await response.Content.ReadFromJsonAsync<TransactionDetailsResponse>(Json, Ct);
        Assert.NotNull(details);
        Assert.Equal("New Name", details.Name);
        Assert.NotNull(details.Splits);
        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(50m, ShareOf(details, me));
        Assert.Equal(50m, ShareOf(details, other));
    }
}
