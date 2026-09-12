using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson.Operations;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class UpdateTransactionDialogTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid FoodCategoryId = Guid.NewGuid();
    private static readonly Guid RentCategoryId = Guid.NewGuid();
    private static readonly Guid MeId = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<IMerchantsClient> _merchants = new();
    private readonly Mock<ITransactionCommands> _commands = new();
    private readonly Mock<IUserLogin> _login = new();

    public UpdateTransactionDialogTest()
    {
        _login.SetupGet(l => l.User).Returns(new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"));

        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "Trip", 2)]);

        _groups
            .Setup(g => g.GetGroupsAsAsyncEnumerable(It.IsAny<CancellationToken>()))
            .Returns(() => new[] { new GroupResponse(GroupId, "Trip", 2) }.ToAsyncEnumerable());

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"),
                new UserInfo(Guid.NewGuid(), "Daniel", "Jones", "daniel@example.com")
            ]);

        _groups
            .Setup(g => g.GetGroupMembersAsAsyncEnumerable(GroupId, It.IsAny<CancellationToken>()))
            .Returns(() => new[]
            {
                new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"),
                new UserInfo(Guid.NewGuid(), "Daniel", "Jones", "daniel@example.com")
            }.ToAsyncEnumerable());

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null)]);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(() => new[] { new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null) }.ToAsyncEnumerable());

        _merchants
            .Setup(m => m.GetMerchantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_merchants.Object);
        Services.AddSingleton(_login.Object);

        // The merchant picker reaches the API through the real command over a mocked
        // client, the way GroupSplitsTabTest does.
        Services.AddSingleton<IMerchantCommands, MerchantCommands>();

        // The transaction commands are mocked rather than real, and not for convenience:
        // TransactionCommands takes IDialogService, which MudBlazor registers as scoped, so
        // a singleton registration of it cannot be constructed at all -- the dialog then
        // fails to render and the test reads as an empty page rather than as a container
        // error. What this test wants from it is one method anyway.
        Services.AddScoped(_ => _commands.Object);
    }

    /// <summary>
    /// Through step one to the split step, which is where Save lives. The dialog is two
    /// steps now, matching the create dialog: everything that makes the expense, then how
    /// it divides.
    /// </summary>
    private static async Task ToSplitStepAsync(IRenderedComponent<MudDialogProvider> provider) =>
        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Next")
            .ClickAsync(new MouseEventArgs());

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference DialogRef)> OpenAsync(
        TransactionResponse original)
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<UpdateTransactionDialog>
        {
            { dialog => dialog.Original, original }
        };

        IDialogReference? dialogRef = null;
        await provider.InvokeAsync(async () =>
        {
            dialogRef = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<UpdateTransactionDialog>("Edit Transaction", parameters);
        });

        return (provider, dialogRef!);
    }

    [Fact]
    public async Task When_opened_with_null_category_id_it_hydrates_category_from_transaction_details()
    {
        var transactionId = Guid.NewGuid();

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = transactionId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = GroupId,
                PaidByUserId = MeId,
                CategoryId = FoodCategoryId,
                Category = "Food",
                Splits =
                [
                    new TransactionSplitResponse(MeId, "Ana Benitez", 50m),
                    new TransactionSplitResponse(Guid.NewGuid(), "Daniel Jones", 50m)
                ]
            });

        var original = new TransactionResponse
        {
            Id = transactionId,
            Name = "Dinner",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = GroupId,
            PaidByUserId = MeId,
            CategoryId = null, // Empty from ledger
            Category = "Food"
        };

        var (provider, dialogRef) = await OpenAsync(original);

        // Original.CategoryId should be hydrated from details
        Assert.Equal(FoodCategoryId, original.CategoryId);

        // Submit to verify the patch does not inadvertently wipe the category
        await ToSplitStepAsync(provider);

        var submitButton = provider.FindAll("button").First(b => b.TextContent.Trim() == "Save");
        await submitButton.ClickAsync(new MouseEventArgs());

        var result = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();
        Assert.NotNull(result);

        // No fields changed, so patch operations shouldn't remove categoryId.
        //
        // Compared without regard to case, which is not fussiness: the paths come out
        // PascalCase, so the exact-match version of this assertion could never have found
        // the operation it was written to forbid and passed whether or not the bug was
        // there.
        Assert.DoesNotContain(result.Operations, op => Touches(op, "categoryId"));
    }

    /// <summary>
    /// The place an expense names is editable everywhere else -- the API takes it, the CLI
    /// sets and clears it -- and was unreachable from the app, because the read that fed
    /// this dialog carried the merchant's name and not its id.
    /// </summary>
    [Fact]
    public async Task The_place_it_was_spent_is_loaded_and_sent_when_it_changes()
    {
        var transactionId = Guid.NewGuid();
        var lidl = Guid.NewGuid();
        var corner = Guid.NewGuid();

        _merchants
            .Setup(m => m.GetMerchantAsync(lidl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MerchantResponse(lidl, "Lidl", null, DateTimeOffset.UtcNow, 3));

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId, merchantId: lidl));

        var original = Row(transactionId);

        var (provider, dialogRef) = await OpenAsync(original);

        // Read off the detail response, which is the only thing that carries the id: the
        // listing row the edit was opened from does not.
        provider.WaitForAssertion(() =>
            Assert.Contains("Lidl", provider.Markup, StringComparison.Ordinal));

        var picker = provider.FindComponent<MerchantPicker>();
        await provider.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(corner));

        await ToSplitStepAsync(provider);
        await provider.FindAll("button").First(b => b.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        var operation = Assert.Single(patch!.Operations, op => Touches(op, "merchantId"));

        Assert.Equal(corner, Assert.IsType<Guid>(operation.value));
    }

    /// <summary>
    /// The split step asks the endpoint made for an edit, not the one the create dialog
    /// uses.
    /// </summary>
    /// <remarks>
    /// The two answer differently and the difference is the whole point: an edit is divided
    /// again by the version of the rule the expense was written under, so previewing it as
    /// though it were a new expense would show today's rule and the save would then produce
    /// other numbers.
    /// </remarks>
    [Fact]
    public async Task The_split_step_previews_the_edit_rather_than_a_new_expense()
    {
        var transactionId = Guid.NewGuid();

        // Divided by a rule, which is the only expense the notice below can be about: it
        // says the rule has moved on since, and an expense with no rule version behind it
        // has no rule to have moved on. The dialog opens on the automatic division for
        // exactly those, and that branch is where the notice lives.
        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId, splitRuleVersionId: Guid.NewGuid()));

        _commands
            .Setup(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitPreviewResponse(
                [new TransactionSplitResponse(MeId, "Ana Benitez", 100m)],
                "Rent",
                DateTimeOffset.UtcNow));

        var (provider, _) = await OpenAsync(Row(transactionId));

        await ToSplitStepAsync(provider);

        _commands.Verify(c => c.PreviewUpdateAsync(transactionId,
            It.Is<UpdateTransactionRequest>(request => request.Amount == 100m),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);

        _commands.Verify(c => c.PreviewAsync(It.IsAny<CreateTransactionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // And the answer reaches the split editor, including the part only an edit can say.
        provider.WaitForAssertion(() =>
            Assert.Contains("The rule has changed since this was recorded", provider.Markup,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The split control opens on the shares the expense actually has when no rule put them
    /// there.
    /// </summary>
    /// <remarks>
    /// It opened on "Automatically" for every expense, because the model it reads starts
    /// with no shares stated and that is what the control takes as automatic. So an expense
    /// somebody had split 70/30 was presented as divided by a rule, with "Divided evenly
    /// between everyone in the group" written over amounts that are not even and came from
    /// nobody's rule. Which it is, is a question only the API can answer -- the amounts do
    /// not say who chose them -- and the answer is the rule version on the detail read.
    /// </remarks>
    [Fact]
    public async Task The_split_control_opens_on_the_shares_when_no_rule_divided_them()
    {
        var transactionId = Guid.NewGuid();

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        var (provider, _) = await OpenAsync(Row(transactionId));

        await ToSplitStepAsync(provider);

        // The editor is holding the expense's own shares, which is what "By hand" means and
        // what the fields are filled with.
        var editor = provider.FindComponent<SplitEditor>();

        Assert.NotNull(editor.Instance.Value);
        var share = Assert.Single(editor.Instance.Value!, split => split.UserId == MeId);
        Assert.Equal(100m, share.Amount);

        // And no claim about where they came from, which is the sentence that was false.
        Assert.DoesNotContain("Divided evenly between everyone in the group", provider.Markup,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Choosing "Automatically" sends the one operation that asks for the division again.
    /// </summary>
    /// <remarks>
    /// The control could not change anything: the patch named the shares only when there
    /// were shares to name, so choosing "Automatically" sent nothing at all, the endpoint
    /// read the silence as "keep them" -- which is what it has meant since 47c6904 -- and
    /// the expense was never re-divided.
    /// <para>
    /// Explicit null and not silence, and only when somebody chose it. Sending it on every
    /// edit is the bulk re-division of 2026-09-08 with a dialog in front of it, so the test
    /// above is half of this one: an edit nobody made a choice in says nothing here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Choosing_to_divide_it_automatically_asks_for_that_in_the_patch_and_the_preview()
    {
        var transactionId = Guid.NewGuid();

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        _commands
            .Setup(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitPreviewResponse(
                [new TransactionSplitResponse(MeId, "Ana Benitez", 50m)], "Food"));

        var (provider, dialogRef) = await OpenAsync(Row(transactionId));

        await ToSplitStepAsync(provider);

        var editor = provider.FindComponent<SplitEditor>();
        await provider.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(null));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        var operation = Assert.Single(patch!.Operations, op => Touches(op, "splits"));
        Assert.Null(operation.value);

        // And the preview was asked the same question, or the numbers on screen would be
        // the shares being replaced rather than the ones about to be stored.
        _commands.Verify(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The category select names the rule each category divides by, beside the category.
    /// </summary>
    /// <remarks>
    /// Issue #245. "Groceries" says nothing about how Groceries is divided, and the person
    /// picking it is choosing both -- so the choice was being made half blind, and the half
    /// nobody could see is the half that moves money.
    /// <para>
    /// A category naming no rule reads "even split" rather than nothing. The blank was not
    /// a gap in the data: a category with no rule divides evenly, which is an answer, and
    /// leaving it out made the commonest arrangement in the app the one the dropdown said
    /// least about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_category_select_says_how_each_category_divides()
    {
        var transactionId = Guid.NewGuid();

        WithCategories(
            new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null),
            new CategoryResponse(RentCategoryId, GroupId, "Rent", Guid.NewGuid(), "By room size"));

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        // The options live in a popover, which needs its host rendered: without one the
        // select shows only what is already chosen, and the list this test is about is
        // never built at all.
        var popovers = Render<MudPopoverProvider>();

        var (provider, _) = await OpenAsync(Row(transactionId));

        var select = provider.FindComponents<MudSelect<Guid?>>()
            .Single(component => component.Instance.Label == "Category");

        await provider.InvokeAsync(() => select.Instance.OpenMenu());

        popovers.WaitForAssertion(() =>
        {
            // The rule a category names...
            Assert.Contains("By room size", popovers.Markup, StringComparison.Ordinal);

            // ...and the answer for one that names none, which is an answer and not a gap.
            Assert.Contains("even split", popovers.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Picking a different category asks for the expense to be divided again -- in the
    /// patch, and in the preview that claims to show what the patch will do.
    /// </summary>
    /// <remarks>
    /// The other half of issue #245, and the half a label alone cannot deliver: a picker
    /// that changed the name and nothing else let somebody move an expense from Rent to
    /// Groceries, read Groceries' rule on the row, and keep Rent's division for ever.
    /// <para>
    /// An explicit null and not silence. Silence means "keep the shares it has" -- that is
    /// what 47c6904 made it mean, after a pass that read it the other way moved 1,394.72
    /// onto one member -- so the operation below is emitted because somebody chose a
    /// category, and an edit that touches no category still sends nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Picking_a_different_category_asks_for_the_division_again()
    {
        var transactionId = Guid.NewGuid();

        WithCategories(
            new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null),
            new CategoryResponse(RentCategoryId, GroupId, "Rent", Guid.NewGuid(), "By room size"));

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        _commands
            .Setup(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitPreviewResponse(
                [new TransactionSplitResponse(MeId, "Ana Benitez", 60m)], "By room size"));

        var (provider, dialogRef) = await OpenAsync(Row(transactionId));

        await PickCategoryAsync(provider, RentCategoryId);

        await ToSplitStepAsync(provider);

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        // The new category, and the instruction that makes picking it mean anything.
        Assert.Equal(RentCategoryId, Assert.IsType<Guid>(
            Assert.Single(patch!.Operations, op => Touches(op, "categoryId")).value));

        Assert.Null(Assert.Single(patch.Operations, op => Touches(op, "splits")).value);

        // And the numbers on screen are the ones about to be stored, rather than the
        // shares being replaced.
        _commands.Verify(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
            true, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// And the override still wins: shares typed on the split step after a category was
    /// picked are what gets stored.
    /// </summary>
    /// <remarks>
    /// The third acceptance criterion of issue #245, and the reason re-dividing on a pick
    /// is safe to do at all. The pick is a default rather than a verdict -- it is made on
    /// step one and step two is still ahead, pre-filled with what the expense holds -- so
    /// the last word belongs to whoever spoke last.
    /// </remarks>
    [Fact]
    public async Task Shares_stated_after_a_category_was_picked_beat_the_category()
    {
        var transactionId = Guid.NewGuid();
        var daniel = Guid.NewGuid();

        WithCategories(
            new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null),
            new CategoryResponse(RentCategoryId, GroupId, "Rent", Guid.NewGuid(), "By room size"));

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        _commands
            .Setup(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitPreviewResponse(
                [new TransactionSplitResponse(MeId, "Ana Benitez", 100m)], "By room size"));

        var (provider, dialogRef) = await OpenAsync(Row(transactionId));

        await PickCategoryAsync(provider, RentCategoryId);

        await ToSplitStepAsync(provider);

        // "By hand", with the amounts somebody typed.
        var editor = provider.FindComponent<SplitEditor>();

        await provider.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(
        [
            new SplitInput { UserId = MeId, Amount = 70m },
            new SplitInput { UserId = daniel, Amount = 30m }
        ]));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        var splits = Assert.IsAssignableFrom<IReadOnlyList<SplitInput>>(
            Assert.Single(patch!.Operations, op => Touches(op, "splits")).value);

        Assert.Equal(70m, Assert.Single(splits, split => split.UserId == MeId).Amount);
        Assert.Equal(30m, Assert.Single(splits, split => split.UserId == daniel).Amount);

        // The pick is still in the patch: overriding the division does not un-file it.
        Assert.Equal(RentCategoryId, Assert.IsType<Guid>(
            Assert.Single(patch.Operations, op => Touches(op, "categoryId")).value));

        // And the preview was asked for the stated shares rather than the category's rule.
        _commands.Verify(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
            false, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// Picking a category, changing your mind, and picking the one it was already under
    /// leaves the expense exactly as an untouched dialog would.
    /// </summary>
    /// <remarks>
    /// The ask latched: the first pick set it and no pick ever cleared it, so an expense
    /// with shares somebody had typed by hand came back from a there-and-back through the
    /// picker as a patch of exactly <c>replace /Splits = null</c> -- the one operation that
    /// discards them -- for an edit that changed nothing. The <c>Clearable</c> X followed by
    /// re-picking is the same path.
    /// <para>
    /// Every other field here is sent only when it ended up different. This one has to be
    /// too, and more so than the others, because it is the only one that moves money. What
    /// it may still send is the shares the expense already holds, which is what an untouched
    /// dialog sends for a hand-split expense and what the API reads as no change at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Picking_a_category_and_changing_your_mind_back_asks_for_no_division()
    {
        var transactionId = Guid.NewGuid();

        WithCategories(
            new CategoryResponse(FoodCategoryId, GroupId, "Food", null, null),
            new CategoryResponse(RentCategoryId, GroupId, "Rent", Guid.NewGuid(), "By room size"));

        // Shares with no rule behind them, which is what the dialog opens on "By hand" for
        // and what the latch threw away.
        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        _commands
            .Setup(c => c.PreviewUpdateAsync(transactionId, It.IsAny<UpdateTransactionRequest>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitPreviewResponse(
                [new TransactionSplitResponse(MeId, "Ana Benitez", 100m)], "By room size"));

        var (provider, dialogRef) = await OpenAsync(Row(transactionId));

        await PickCategoryAsync(provider, RentCategoryId);
        await PickCategoryAsync(provider, FoodCategoryId);

        await ToSplitStepAsync(provider);

        // And the editor is back on the expense's own shares rather than left on
        // "Automatically" over a division the save is not going to make.
        var editor = provider.FindComponent<SplitEditor>();

        Assert.NotNull(editor.Instance.Value);
        Assert.Equal(100m, Assert.Single(editor.Instance.Value!, split => split.UserId == MeId).Amount);

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        // No ask to divide it again -- that is the null the latch was sending -- and the
        // only thing it may say about the shares is the ones the expense already holds.
        foreach (var operation in patch!.Operations.Where(op => Touches(op, "splits")))
        {
            var stated = Assert.IsAssignableFrom<IReadOnlyList<SplitInput>>(operation.value);

            Assert.Equal(100m, Assert.Single(stated, split => split.UserId == MeId).Amount);
        }

        // And nothing about the category either, since it ended up where it started.
        Assert.DoesNotContain(patch.Operations, op => Touches(op, "categoryId"));
    }

    /// <summary>What the group's categories listing answers, for a test that needs more than one.</summary>
    private void WithCategories(params CategoryResponse[] categories)
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(categories);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(() => categories.ToAsyncEnumerable());
    }

    /// <summary>
    /// Chooses a category the way the select does, so <c>@bind-Value:after</c> runs and the
    /// dialog reacts rather than the field merely being assigned.
    /// </summary>
    private static async Task PickCategoryAsync(
        IRenderedComponent<MudDialogProvider> provider, Guid categoryId)
    {
        var select = provider.FindComponents<MudSelect<Guid?>>()
            .Single(component => component.Instance.Label == "Category");

        await provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(categoryId));
    }

    /// <summary>
    /// Whether a patch operation addresses one member, whatever case the serializer wrote
    /// the path in.
    /// </summary>
    private static bool Touches(Operation<UpdateTransactionRequest> operation, string member) =>
        string.Equals(operation.path, $"/{member}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Correcting an expense to "these amounts are the expense's own" leaves the split
    /// control holding them, so the next Save states them.
    /// </summary>
    /// <remarks>
    /// Only the other direction was handled. Recording a version cleared the stated shares,
    /// correctly; recording "by hand" did nothing, so the control went on claiming the
    /// division was automatic -- the exact false claim the strip above it exists to end, and
    /// now made false <em>by</em> a correction the person had just made. The consequence is
    /// not cosmetic: with no shares in the patch the expense keeps whatever the server
    /// decides, which is what "by hand" was being recorded to prevent.
    /// </remarks>
    [Fact]
    public async Task Recording_that_the_amounts_are_its_own_leaves_the_control_holding_them()
    {
        var transactionId = Guid.NewGuid();
        var version = Guid.NewGuid();

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId, splitRuleVersionId: version));

        // A rule divided it, so the reader has a rule and a history to resolve it against.
        // Through this class's own categories client, which it registers over the shared
        // one -- the reader resolves whatever the container last had, so setting up the
        // shared mock here would configure a client nothing asks.
        WithCategories(new CategoryResponse(FoodCategoryId, GroupId, "Food", Household, "Household"));

        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleHistoryResponse(Household, GroupId, "Household",
                [new SplitRuleVersionResponse(version, DateTimeOffset.UtcNow.AddMonths(-2), null,
                    new EvenSplitRuleDto())]));

        _commands
            .Setup(c => c.DivisionSourceAsync(transactionId, null, It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (provider, dialogRef) = await OpenAsync(Row(transactionId));

        await ToSplitStepAsync(provider);

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Correct")
            .ClickAsync(new MouseEventArgs());

        var choice = provider.FindComponent<MudRadioGroup<Guid?>>();

        await provider.InvokeAsync(() => choice.Instance.ValueChanged.InvokeAsync(null));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Record")
            .ClickAsync(new MouseEventArgs());

        _commands.Verify(c => c.DivisionSourceAsync(transactionId, null, It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        // The amounts the expense is holding, stated -- not silence, which would leave the
        // server free to divide it again.
        var operation = Assert.Single(patch!.Operations, op => Touches(op, "splits"));

        Assert.NotNull(operation.value);
    }

    /// <summary>
    /// And the other direction: recording that a version divided it gives up the shares the
    /// expense was holding, so the save states none.
    /// </summary>
    /// <remarks>
    /// The two are one rule read both ways, and only one of them was pinned. Stated shares
    /// and a rule's division are the two answers to the same question: an edit that sends
    /// both says the rule produced these amounts and then pins them against it, which is the
    /// shape <c>UpdateTransactionRequest</c> reads as "divide it exactly this way".
    /// </remarks>
    [Fact]
    public async Task Recording_that_a_version_divided_it_gives_up_the_shares_it_was_holding()
    {
        var transactionId = Guid.NewGuid();
        var version = Guid.NewGuid();

        // No version recorded, so the dialog opens holding the stored shares.
        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(transactionId));

        // Through this class's own categories client, which it registers over the shared
        // one -- the reader resolves whatever the container last had, so setting up the
        // shared mock here would configure a client nothing asks.
        WithCategories(new CategoryResponse(FoodCategoryId, GroupId, "Food", Household, "Household"));

        SplitRules
            .Setup(client => client.GetSplitRuleVersionsAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleHistoryResponse(Household, GroupId, "Household",
                [new SplitRuleVersionResponse(version, DateTimeOffset.UtcNow.AddMonths(-2), null,
                    new EvenSplitRuleDto())]));

        _commands
            .Setup(c => c.DivisionSourceAsync(transactionId, version, It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (provider, dialogRef) = await OpenAsync(Row(transactionId));

        await ToSplitStepAsync(provider);

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Correct")
            .ClickAsync(new MouseEventArgs());

        var choice = provider.FindComponent<MudRadioGroup<Guid?>>();

        await provider.InvokeAsync(() => choice.Instance.ValueChanged.InvokeAsync(version));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Record")
            .ClickAsync(new MouseEventArgs());

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        var patch = await dialogRef.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>();

        Assert.NotNull(patch);

        // Nothing about the shares at all: the expense is a rule's now, and the rule says
        // what they are.
        Assert.DoesNotContain(patch!.Operations, op => Touches(op, "splits"));
    }

    private static readonly Guid Household = Guid.NewGuid();

    private static TransactionDetailsResponse Details(
        Guid id, Guid? merchantId = null, Guid? splitRuleVersionId = null) => new()
    {
        SplitRuleVersionId = splitRuleVersionId,
        Id = id,
        Name = "Dinner",
        Amount = 100m,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = GroupId,
        PaidByUserId = MeId,
        CategoryId = FoodCategoryId,
        Category = "Food",
        MerchantId = merchantId,
        Splits = [new TransactionSplitResponse(MeId, "Ana Benitez", 100m)]
    };

    private static TransactionResponse Row(Guid id) => new()
    {
        Id = id,
        Name = "Dinner",
        Amount = 100m,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = GroupId,
        PaidByUserId = MeId,
        CategoryId = FoodCategoryId,
        Category = "Food"
    };

    [Fact]
    public async Task When_category_was_deleted_from_group_it_preserves_category_in_dropdown_list()
    {
        var transactionId = Guid.NewGuid();
        var deletedCatId = Guid.NewGuid();

        // CategoriesClient returns empty (category deleted from group)
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncEnumerable.Empty<CategoryResponse>());

        _transactions
            .Setup(t => t.GetTransactionAsync(transactionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionDetailsResponse
            {
                Id = transactionId,
                Name = "Dinner",
                Amount = 100m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = GroupId,
                PaidByUserId = MeId,
                CategoryId = deletedCatId,
                Category = "Archived Food",
                Splits = []
            });

        var original = new TransactionResponse
        {
            Id = transactionId,
            Name = "Dinner",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = GroupId,
            PaidByUserId = MeId,
            CategoryId = deletedCatId,
            Category = "Archived Food"
        };

        var (provider, dialogRef) = await OpenAsync(original);

        // The dialog markup should still render the preserved category name
        Assert.Contains("Archived Food", provider.Markup);
    }
}
