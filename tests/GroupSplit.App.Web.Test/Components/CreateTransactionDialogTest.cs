using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Recording an expense, at the one place where the dialog says something the person could
/// not otherwise know: which rule the category they are about to pick divides by.
/// </summary>
/// <remarks>
/// Issue #245 asked for it in both transaction dialogs, and the two say it in their own
/// code -- this one off <see cref="CategoryResponse.DefaultSplitRuleName"/> directly, the
/// edit dialog off a row it builds -- so one test cannot stand for both.
/// </remarks>
public class CreateTransactionDialogTest : ComponentTest
{
    [Fact]
    public async Task A_new_scanned_expense_can_assign_receipt_lines_before_it_is_saved()
    {
        var itemized = Guid.NewGuid();
        var even = Guid.NewGuid();
        var evenVersion = Guid.NewGuid();
        _splitRules.Setup(service => service.ForGroupAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SplitRuleResponse(itemized, GroupId, "Receipt", true, DividesByBill: true),
                new SplitRuleResponse(even, GroupId, "Even", true, DividesEvenly: true)]);
        _splitRules.Setup(service => service.GetAsync(itemized, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleDetailsResponse { Id = itemized, GroupId = GroupId, Name = "Receipt",
                VersionId = Guid.NewGuid(), Definition = new ItemizedSplitRuleDto() });
        _splitRules.Setup(service => service.GetAsync(even, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleDetailsResponse { Id = even, GroupId = GroupId, Name = "Even",
                VersionId = evenVersion, Definition = new EvenSplitRuleDto() });
        _commands.Setup(service => service.PreviewAsync(It.IsAny<CreateTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitPreviewResponse([new TransactionSplitResponse(MeId, "Ana", 12m)], "Receipt"));
        var receipt = new SaveReceiptRequest { Subtotal = 12m, Total = 12m,
            Items = [new ReceiptItemInput { Name = "Lunch", UnitPrice = 12m, TotalPrice = 12m }] };
        var receipts = new Mock<IReceiptCommands>();
        receipts.Setup(service => service.TranscribeAsync(It.IsAny<Microsoft.AspNetCore.Components.Forms.IBrowserFile>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(new ReceiptDraftResponse("test", receipt));
        Services.AddSingleton(receipts.Object);
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<CreateTransactionDialog>("Add expense", new DialogParameters<CreateTransactionDialog>
            { { component => component.SelectedGroupId, GroupId } }));
        var picker = provider.FindComponent<ReceiptFilePicker>();
        var reading = provider.InvokeAsync(() => picker.Instance.FileChanged.InvokeAsync(new Microsoft.AspNetCore.Components.Forms.InputFileChangeEventArgs(
            [new BufferedBrowserFile("receipt.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1])])));
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<ReceiptDraftDialog>()));
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt"))
            .ClickAsync(new MouseEventArgs());
        await reading;
        await ChooseSplitAsync(provider, "Receipt items");
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<BillDivision>()));
        Assert.True(provider.FindAll(".mud-dialog-actions button").Single(button => button.TextContent.Trim() == "Add expense")
            .HasAttribute("disabled"));
        var division = provider.FindComponent<BillDivision>();
        division.WaitForAssertion(() => Assert.Single(division.FindComponents<MudSelect<Guid?>>()));
        await division.InvokeAsync(() => division.FindComponent<MudSelect<Guid?>>().Instance.ValueChanged.InvokeAsync(evenVersion));
        await provider.FindAll(".mud-dialog-actions button").Single(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());
        var answer = await reference!.GetReturnValueAsync<CreateTransactionDialog.Answer>();
        Assert.NotNull(answer);
        Assert.Equal(itemized, answer.Request.SplitRuleId);
        Assert.Equal(evenVersion, Assert.Single(answer.Request.Receipt!.Items).SplitRuleVersionId);
        Assert.Null(answer.Request.Receipt.Items[0].Id);
        receipts.Verify(service => service.SetRuleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actions_stay_locked_during_a_receipt_read_and_unlock_after_failure(bool throws)
    {
        var pending = new TaskCompletionSource<ReceiptDraftResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipts = new Mock<IReceiptCommands>();
        receipts.Setup(service => service.TranscribeAsync(It.IsAny<Microsoft.AspNetCore.Components.Forms.IBrowserFile>(),
            It.IsAny<CancellationToken>())).Returns(pending.Task);
        Services.AddSingleton(receipts.Object);
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(async () => await Services.GetRequiredService<IDialogService>()
            .ShowAsync<CreateTransactionDialog>("Add expense", new DialogParameters<CreateTransactionDialog>(),
                new DialogOptions { CloseButton = true, BackdropClick = true, CloseOnEscapeKey = true }));
        var picker = provider.FindComponent<ReceiptFilePicker>();
        var reading = provider.InvokeAsync(() => picker.Instance.FileChanged.InvokeAsync(new Microsoft.AspNetCore.Components.Forms.InputFileChangeEventArgs(
            [new BufferedBrowserFile("receipt.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1])])));
        provider.WaitForAssertion(() => Assert.True(provider.Find("fieldset").HasAttribute("inert")));
        Assert.True(provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?").Find("input").HasAttribute("disabled"));
        Assert.All(provider.FindAll(".mud-dialog-actions button"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.Empty(provider.FindAll("button[aria-label='Close']"));
        if (throws)
        {
            pending.SetException(new IOException("Test scan failure"));
            await reading;
        }
        else
        {
            pending.SetResult(null);
            await reading;
        }
        provider.WaitForAssertion(() => Assert.False(provider.Find("fieldset").HasAttribute("inert")));
        Assert.All(provider.FindAll(".mud-dialog-actions button"), button => Assert.False(button.HasAttribute("disabled")));
        Assert.NotEmpty(provider.FindAll("button[aria-label='Close']"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_reviewed_receipt_supplies_the_date_and_merchant_and_offers_a_group_and_category(bool knownMerchant, bool fixedGroup)
    {
        var receipt = new SaveReceiptRequest
        {
            Subtotal = 12m, Total = 12m, MerchantName = "Corner shop", PurchaseDate = new DateOnly(2026, 9, 12),
            Items = [new ReceiptItemInput { Name = "Milk", Quantity = 1m, UnitPrice = 12m, TotalPrice = 12m }]
        };
        var merchantId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var receipts = new Mock<IReceiptCommands>();
        receipts.Setup(service => service.TranscribeAsync(It.IsAny<Microsoft.AspNetCore.Components.Forms.IBrowserFile>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiptDraftResponse("test", receipt));
        receipts.Setup(service => service.SuggestFilingAsync(It.IsAny<SuggestReceiptFilingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilingDestinationSuggestion(GroupId, "Trip", false, categoryId, "Groceries", .95));
        Services.AddSingleton(receipts.Object);
        var merchants = new Mock<IMerchantCommands>();
        merchants.Setup(service => service.SearchAsync("Corner shop", It.IsAny<CancellationToken>()))
            .ReturnsAsync(knownMerchant ? [new MerchantResponse(merchantId, "Corner shop", null, DateTimeOffset.UtcNow, 0)] : []);
        merchants.Setup(service => service.CreateAsync("Corner shop", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MerchantResponse(merchantId, "Corner shop", null, DateTimeOffset.UtcNow, 0));
        Services.AddSingleton(merchants.Object);
        _categories.Setup(client => client.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(categoryId, GroupId, "Groceries", null, null)]);
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<CreateTransactionDialog>("Add expense", new DialogParameters<CreateTransactionDialog>
            {
                { component => component.SelectedGroupId, fixedGroup ? GroupId : null }
            }));
        var picker = provider.FindComponent<ReceiptFilePicker>();
        var reading = provider.InvokeAsync(() => picker.Instance.FileChanged.InvokeAsync(new Microsoft.AspNetCore.Components.Forms.InputFileChangeEventArgs(
            [new BufferedBrowserFile("receipt.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1, 2, 3])])));
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<ReceiptDraftDialog>()));
        await provider.FindAll("button").Single(button => button.TextContent.Contains("Use this receipt")).ClickAsync(new MouseEventArgs());
        await reading;
        Assert.Contains("Corner shop", provider.FindAll("button.gs-expense-chip").Single(button =>
            button.GetAttribute("aria-label")!.StartsWith("where:")).TextContent);
        merchants.Verify(service => service.CreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var suggestion = provider.FindAll("button").Single(button => button.TextContent.Contains("Use suggested"));
        Assert.Contains("Trip", suggestion.TextContent);
        await suggestion.ClickAsync(new MouseEventArgs());
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Add expense").ClickAsync(new MouseEventArgs());
        var answer = await reference!.GetReturnValueAsync<CreateTransactionDialog.Answer>();
        Assert.NotNull(answer);
        Assert.Equal(merchantId, answer.Request.MerchantId);
        Assert.Equal(GroupId, answer.Request.GroupId);
        Assert.Equal(categoryId, answer.Request.CategoryId);
        Assert.Equal(receipt.PurchaseDate, DateOnly.FromDateTime(Services.GetRequiredService<LocalClock>().Local(answer.Request.DateTime).DateTime));
        receipts.Verify(service => service.SuggestFilingAsync(It.Is<SuggestReceiptFilingRequest>(request => request.GroupId == (fixedGroup ? GroupId : null)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Task OpenSplitChoices(IRenderedComponent<MudDialogProvider> provider)
    {
        var opening = provider.FindComponent<SplitEditor>().Find("button").ClickAsync(new MouseEventArgs());
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<ExpenseChoiceDialog>()));
        return opening;
    }

    private static async Task CancelChoiceAsync(IRenderedComponent<MudDialogProvider> provider, Task opening)
    {
        await provider.FindAll("button").Last(button => button.TextContent.Trim() == "Cancel").ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task ChooseSplitAsync(IRenderedComponent<MudDialogProvider> provider, string label)
    {
        var opening = OpenSplitChoices(provider);
        await provider.FindAll(".gs-selection-option").Single(option => option.QuerySelector("strong")!.TextContent == label)
            .ClickAsync(new MouseEventArgs());
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Use selection").ClickAsync(new MouseEventArgs());
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid MeId = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ICategoriesClient> _categories = new();
    private readonly Mock<ISplitRuleCommands> _splitRules = new();
    private readonly Mock<IMerchantsClient> _merchants = new();
    private readonly Mock<ITransactionCommands> _commands = new();
    private readonly Mock<IUserLogin> _login = new();

    public CreateTransactionDialogTest()
    {
        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GroupResponse(GroupId, "Trip", 2)]);

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserInfo(MeId, "Ana", "Benitez", "ana@example.com")]);

        // Whoever is recording the expense is who the dialog says paid for it, so it needs
        // to know who that is.
        _login.SetupGet(login => login.User)
            .Returns(new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"));

        // Any other group -- the personal one a personal expense reads its categories
        // from -- has none, rather than the mock's null.
        _categories
            .Setup(c => c.GetCategoriesAsync(It.IsAny<Guid>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(It.IsAny<Guid>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsyncEnumerable.Empty<CategoryResponse>());

        _merchants
            .Setup(m => m.GetMerchantsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_categories.Object);
        Services.AddSingleton(_splitRules.Object);
        Services.AddSingleton(_merchants.Object);
        Services.AddSingleton(_login.Object);
        Services.AddSingleton<IMerchantCommands, MerchantCommands>();

        // Scoped, for the reason UpdateTransactionDialogTest gives: the real
        // TransactionCommands takes IDialogService, which MudBlazor registers scoped.
        Services.AddScoped(_ => _commands.Object);
    }

    /// <summary>
    /// Every category in the list names how it divides, and one that names no rule reads
    /// "even split" rather than nothing.
    /// </summary>
    /// <remarks>
    /// "Groceries" says nothing about how Groceries is divided, and somebody picking it is
    /// choosing both. The blank on a category with no rule was not a missing value either:
    /// no rule means an even split, which is the commonest arrangement in the app and was
    /// the one the dropdown said least about.
    /// </remarks>
    [Fact]
    public async Task The_category_select_says_how_each_category_divides()
    {
        var categories = new[]
        {
            new CategoryResponse(Guid.NewGuid(), GroupId, "Food", null, null),
            new CategoryResponse(Guid.NewGuid(), GroupId, "Rent", Guid.NewGuid(), "By room size")
        };

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(categories);

        _categories
            .Setup(c => c.GetCategoriesAsAsyncEnumerable(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(() => categories.ToAsyncEnumerable());

        // The options live in a popover, which needs its host rendered: without one the
        // select shows only what is chosen, and the list under test is never built.
        var popovers = Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("New expense", parameters));

        // The category is a chip now, and its select opens underneath when the chip is
        // pressed -- so the list under test does not exist until somebody asks for it.
        _ = provider.FindAll("button.gs-expense-chip")
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("category", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<ExpenseChoiceDialog>()));

        Assert.Contains("By room size", provider.Markup, StringComparison.Ordinal);
        Assert.Contains("even split", provider.Markup, StringComparison.Ordinal);
        await provider.FindAll("button").Last(button => button.TextContent.Trim() == "Cancel").ClickAsync(new MouseEventArgs());

    }

    [Fact]
    public async Task Opened_from_a_group_it_prefills_the_group_and_allows_changing_it()
    {
        var provider = await OpenAsync(GroupId);
        var decisions = provider.FindComponent<ExpenseDecisions>();
        Assert.True(decisions.Instance.ShowGroup);
        Assert.Equal(GroupId, decisions.Instance.GroupId);
        Assert.Equal("Trip", decisions.Instance.GroupName);
        Assert.Contains(Chips(provider),
            chip => chip.GetAttribute("aria-label")!.Contains("group", StringComparison.Ordinal));

        await provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(null));

        provider.WaitForAssertion(() =>
        {
            Assert.Null(decisions.Instance.GroupId);
            Assert.Equal("Personal", decisions.Instance.GroupName);
            Assert.Empty(provider.FindComponents<SplitEditor>());
        });
    }

    /// <summary>
    /// A personal expense can say what it was for: the personal group's own categories are
    /// offered, read from the group whose id is the caller's. And the personal group is not
    /// listed among the others, since "Personal (just you)" already stands for it.
    /// </summary>
    [Fact]
    public async Task A_personal_expense_is_offered_the_personal_groups_categories()
    {
        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new GroupResponse(MeId, "Personal", 1) { IsPersonal = true },
                new GroupResponse(GroupId, "Trip", 2)
            ]);
        _categories
            .Setup(c => c.GetCategoriesAsync(MeId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(Guid.NewGuid(), MeId, "Groceries", null, null)]);

        var provider = await OpenAsync(null);
        var decisions = provider.FindComponent<ExpenseDecisions>();

        provider.WaitForAssertion(() =>
            Assert.Equal(["Groceries"], decisions.Instance.Categories.Select(category => category.Name)));
        Assert.True(decisions.Instance.ShowCategory);
        Assert.Equal(["Trip"], decisions.Instance.Groups.Select(group => group.Name));
    }

    /// <summary>
    /// And opened from the expenses page, where there is no group yet, it is the one thing
    /// that has to be asked.
    /// </summary>
    [Fact]
    public async Task Opened_from_outside_a_group_the_group_is_the_first_chip()
    {
        var provider = await OpenAsync(null);

        Assert.Contains(Chips(provider),
            chip => chip.GetAttribute("aria-label")!.Contains("group", StringComparison.Ordinal));
    }

    /// <summary>
    /// There is no second step: the division is under the amount that decides it.
    /// </summary>
    /// <remarks>
    /// The split used to sit behind Next, which put the decision the app exists for one
    /// screen away from the figure it depends on -- and made the commonest expense in the
    /// app a five-gesture job.
    /// </remarks>
    [Fact]
    public async Task The_division_is_on_the_same_screen_as_the_amount()
    {
        var provider = await OpenAsync(GroupId);

        Assert.Single(provider.FindComponents<SplitEditor>());

        Assert.DoesNotContain(provider.FindAll("button"),
            button => button.TextContent.Trim() is "Next" or "Back");

        Assert.Contains(provider.FindAll("button"),
            button => button.TextContent.Trim() == "Add expense");
    }

    /// <summary>
    /// The note is a field, always on screen, and optional.
    /// </summary>
    /// <remarks>
    /// It is the one optional thing somebody types rather than picks, and it is what they
    /// reach for when an expense needs explaining. Behind a "more" disclosure it may as well
    /// not exist.
    /// </remarks>
    [Fact]
    public async Task A_note_can_be_written_without_going_looking_for_it()
    {
        var provider = await OpenAsync(GroupId);

        var note = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "Note");

        Assert.Equal("Optional", note.Instance.HelperText);
        Assert.False(note.Instance.Disabled);
    }

    /// <summary>
    /// The commonest thing anybody does with this app: type an amount and a name, press the
    /// button, and get the expense the dialog was showing.
    /// </summary>
    /// <remarks>
    /// Nothing pinned it. Every other test here reads what the dialog renders, and the
    /// dialog was just rebuilt from two steps into one -- the amount moved to the top, the
    /// group became a field of its own rather than something inferred from the category, the
    /// validate-and-close that used to sit behind Next moved to the only button left. A
    /// rewrite of the path between typing and saving deserves one test that walks it, or the
    /// dialog can come apart in the way that costs the most and look perfectly correct in
    /// every assertion above.
    /// <para>
    /// The date especially. It is applied on submit and nowhere else, so a Save that skipped
    /// <c>ApplyDate</c> would file every expense at whatever instant the dialog happened to
    /// be constructed, which is a defect nothing on screen would show.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "All for one" names a rule, and the rule's id is the whole of what the expense
    /// carries: the division is the API's to work out, so a dialog that sent amounts
    /// instead would be doing the arithmetic twice and would cost the expense its ability
    /// to be divided again when the amount is corrected.
    /// </summary>
    [Fact]
    public async Task Marking_an_expense_as_one_members_own_sends_that_members_rule()
    {
        var ruleId = Guid.NewGuid();

        _splitRules
            .Setup(rules => rules.ForGroupAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SplitRuleResponse(ruleId, GroupId, "All for Ana", BuiltIn: true, AllForUserId: MeId)]);

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", parameters));

        var amount = provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");

        await provider.InvokeAsync(() => amount.Instance.ValueChanged.InvokeAsync(42.50m));

        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");

        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("The gym"));

        await ChooseSplitAsync(provider, "One person");

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var request = await reference!.GetReturnValueAsync<CreateTransactionRequest>();

        Assert.NotNull(request);
        Assert.Equal(ruleId, request!.SplitRuleId);

        // And nothing stated alongside it. The API refuses both, because they are two
        // answers to one question.
        Assert.Null(request.Splits);
    }

    /// <summary>
    /// A provisioned rule for somebody who has left is historical data, not a choice for a
    /// new expense.
    /// </summary>
    [Fact]
    public async Task It_does_not_offer_all_for_a_member_who_left()
    {
        var departed = Guid.NewGuid();

        _splitRules
            .Setup(rules => rules.ForGroupAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SplitRuleResponse(
                Guid.NewGuid(), GroupId, "All for departed", BuiltIn: true, AllForUserId: departed)]);

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = await OpenAsync(GroupId);
        var editor = provider.FindComponent<SplitEditor>();

        Assert.Empty(editor.Instance.MemberRules);
        var opening = OpenSplitChoices(provider);
        Assert.True(provider.FindAll(".gs-selection-option").Single(option => option.TextContent.Contains("One person"))
            .HasAttribute("disabled"));
        await CancelChoiceAsync(provider, opening);
    }

    /// <summary>
    /// The dialog opens with the same three answers it will still have once everything it
    /// asked for has arrived.
    /// </summary>
    /// <remarks>
    /// The control is drawn from a count -- the thumb is positioned as a fraction of
    /// --gs-seg-n -- so an option appearing later does not slide in beside the others: it
    /// resizes all of them, under the pointer, a fraction of a second after the dialog
    /// opened. The group's rules are a round trip, and the dialog is on screen while it is
    /// out, so "draw it when they arrive" is "change shape while somebody is reading it".
    /// <para>
    /// Here the rules never arrive at all, which is the strongest version of the same
    /// question: the option is drawn and disabled, because a button that would send an id
    /// the dialog does not have is worse than a button that says not yet.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_five_ways_to_divide_are_there_before_the_rules_are()
    {
        _splitRules
            .Setup(rules => rules.ForGroupAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SplitRuleResponse>?)null);

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", parameters));

        var opening = OpenSplitChoices(provider);
        var options = provider.FindAll(".gs-selection-option");
        Assert.Equal(5, options.Count);
        foreach (var label in new[] { "Receipt items", "One person", "Evenly" })
            Assert.True(options.Single(option => option.QuerySelector("strong")!.TextContent == label).HasAttribute("disabled"));
        await CancelChoiceAsync(provider, opening);

    }

    [Fact]
    public async Task Typing_an_amount_and_a_name_records_that_expense_in_that_group()
    {
        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId }
        };

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", parameters));

        var amount = provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");

        await provider.InvokeAsync(() => amount.Instance.ValueChanged.InvokeAsync(42.50m));

        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");

        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Mercadona"));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var request = await reference!.GetReturnValueAsync<CreateTransactionRequest>();

        Assert.NotNull(request);

        Assert.Equal(42.50m, request!.Amount);
        Assert.Equal("Mercadona", request.Name);

        // The group the dialog was opened in, which is the one thing on this screen nobody is
        // asked about and the one the expense is meaningless without.
        Assert.Equal(GroupId, request.GroupId);

        // Today, in the reader's zone -- read off the same clock the dialog picks its default
        // from, because that clock is not necessarily on the UTC day.
        var clock = Services.GetRequiredService<LocalClock>();

        Assert.Equal(clock.Today, clock.Local(request.DateTime).Date);
    }

    /// <summary>
    /// Typed into the field itself rather than handed to it, because the parse is the
    /// defect: <c>12,50</c> was recorded as a $1,250 expense (#313).
    /// </summary>
    [Fact]
    public async Task An_amount_typed_with_a_comma_is_recorded_as_that_many_cents()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense",
                    new DialogParameters<CreateTransactionDialog> { { d => d.SelectedGroupId, GroupId } }));

        var amount = provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");

        await amount.Find("input").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "12,50" });

        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");

        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Cena"));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        var request = await reference!.GetReturnValueAsync<CreateTransactionRequest>();

        Assert.Equal(12.50m, request!.Amount);
    }

    /// <summary>
    /// And one that could be read two ways is not recorded at all -- not as 1234.56, not as
    /// 1.23456, and not as the zero a failed parse used to leave in the model.
    /// </summary>
    [Fact]
    public async Task An_amount_with_a_thousands_separator_cannot_be_added()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense",
                    new DialogParameters<CreateTransactionDialog> { { d => d.SelectedGroupId, GroupId } }));

        var amount = provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");

        await amount.Find("input").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "1.234,56" });

        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");

        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Cena"));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        Assert.False(reference!.Result.IsCompleted);
        Assert.Contains("no thousands separators", provider.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read that fails does not take the expense down with it.
    /// </summary>
    /// <remarks>
    /// The group, the amount and the name are what an expense needs; the categories are a
    /// label somebody may not want and the members only fill the payer picker, since the
    /// division is worked out by the API. All of it was asked for in one block whose
    /// failure ended the dialog -- first by closing it onto a title and two buttons with
    /// nothing between them, then by replacing it with an apology -- when recording the
    /// expense was still perfectly possible.
    /// </remarks>
    [Fact]
    public async Task A_group_that_will_not_say_who_is_in_it_can_still_be_spent_in()
    {
        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("no connection"));

        var provider = await OpenAsync(GroupId);

        // The group is the one fact this was handed rather than fetched, and nothing that
        // comes back from the API can unseat it.
        Assert.Contains("Trip", provider.Markup, StringComparison.Ordinal);

        Assert.Contains(provider.FindComponents<MudTextField<string>>(),
            field => field.Instance.Label == "What was it?");

        var add = provider.FindComponents<MudButton>()
            .Single(button => button.Markup.Contains("Add expense", StringComparison.Ordinal));

        Assert.False(add.Instance.Disabled);
    }

    [Fact]
    public async Task Opened_from_a_named_group_it_loads_the_other_group_choices()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, GroupId },
            { dialog => dialog.SelectedGroupName, "Trip" }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", parameters));

        Assert.Contains("Trip", provider.Markup, StringComparison.Ordinal);

        _groups.Verify(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The payer chip names whoever is recording the expense, not "somebody".
    /// </summary>
    /// <remarks>
    /// The API has always defaulted an absent payer to the caller, so this was never wrong
    /// in the ledger -- only on the screen, where the chip said "paid by somebody" and drew
    /// itself as a settled choice: no plus, no dashed border, nothing suggesting it wanted
    /// answering. The app was admitting to a gap it did not have, on the one field people
    /// are most likely to check before pressing Add.
    /// </remarks>
    [Fact]
    public async Task The_payer_is_whoever_is_recording_it()
    {
        var provider = await OpenAsync(GroupId);

        var payer = Chips(provider)
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("payer", StringComparison.Ordinal));

        Assert.Contains("Ana", payer.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("somebody", payer.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picking a place puts its name on the chip.
    /// </summary>
    /// <remarks>
    /// The chip counts as filled the moment an id is set, and it draws its value rather
    /// than its label once it is -- so without the name resolved beside the id, choosing a
    /// shop replaced "+ where" with an empty pill and told a screen reader "where: .". The
    /// edit dialog looked the name up from the start; this one was given the chip without
    /// the lookup.
    /// </remarks>
    [Fact]
    public async Task Picking_where_it_was_spent_says_where_that_was()
    {
        var lidl = Guid.NewGuid();

        _merchants
            .Setup(m => m.GetMerchantAsync(lidl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MerchantResponse(lidl, "Lidl", null, DateTimeOffset.UtcNow, 0));

        var provider = await OpenAsync(GroupId);

        // The search lives in the chip's picker, which does not exist until it is opened.
        _ = Chips(provider)
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("where", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs());
        provider.WaitForAssertion(() => Assert.Single(provider.FindComponents<ExpenseDetailsChoiceDialog>()));

        var picker = provider.FindComponent<MerchantPicker>();

        await provider.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(lidl));
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Use selection").ClickAsync(new MouseEventArgs());

        var where = Chips(provider)
            .Single(chip => chip.GetAttribute("aria-label")!.Contains("where", StringComparison.Ordinal));

        Assert.Contains("Lidl", where.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shares typed by hand that stop adding up when the amount moves are refused at Save,
    /// in the dialog, with everything else still in it (#323).
    /// </summary>
    /// <remarks>
    /// Save validated the form and nothing else, so the dialog closed, the page sent the
    /// request, the API refused it, and the name, the note and every pick were gone with the
    /// dialog. The remaining line was text and nothing more.
    /// </remarks>
    [Fact]
    public async Task Shares_that_no_longer_add_up_are_refused_without_closing_over_the_rest()
    {
        var daniel = Guid.NewGuid();

        _groups
            .Setup(g => g.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new UserInfo(MeId, "Ana", "Benitez", "ana@example.com"),
                new UserInfo(daniel, "Daniel", "Jones", "daniel@example.com")
            ]);

        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add an expense", new DialogParameters<CreateTransactionDialog>
                {
                    { dialog => dialog.SelectedGroupId, GroupId }
                }));

        var amount = provider.FindComponents<DecimalField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");
        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");

        await provider.InvokeAsync(() => amount.Instance.ValueChanged.InvokeAsync(100m));
        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Dinner"));

        // 70/30 by hand, and then the amount corrected underneath it.
        var editor = provider.FindComponent<SplitEditor>();

        await provider.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(
        [
            new SplitInput { UserId = MeId, Amount = 70m },
            new SplitInput { UserId = daniel, Amount = 30m }
        ]));

        await provider.InvokeAsync(() => amount.Instance.ValueChanged.InvokeAsync(120m));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        Assert.False(reference!.Result.IsCompleted);

        var remaining = provider.Find(".gs-split-remaining");
        Assert.Contains("Not saved", remaining.TextContent, StringComparison.Ordinal);
        Assert.Contains("$20.00 still to assign.", remaining.TextContent, StringComparison.Ordinal);

        // And the rest of the edit is where it was.
        Assert.Equal("Dinner", name.Instance.Value);
        Assert.Equal(120m, amount.Instance.Value);
    }

    /// <summary>
    /// A group picked while the last one's people are still on their way is the one whose
    /// people and categories the dialog offers (#323).
    /// </summary>
    /// <remarks>
    /// The three reads behind a pick wrote what they found as they found it, so an answer
    /// about the group somebody had already moved off landed over the group they moved to:
    /// the flat's members offered as the trip's payers and shares.
    /// </remarks>
    [Fact]
    public async Task A_group_picked_while_the_last_one_loads_keeps_its_own_people()
    {
        var flat = new GroupResponse(Guid.NewGuid(), "Flat", 2);
        var trip = new GroupResponse(Guid.NewGuid(), "Trip", 2);
        var flatmate = new UserInfo(Guid.NewGuid(), "Flat", "Mate", null);
        var traveller = new UserInfo(Guid.NewGuid(), "Fellow", "Traveller", null);
        var flatMembers = new TaskCompletionSource<ICollection<UserInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _groups
            .Setup(g => g.GetGroupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([flat, trip]);
        _groups
            .Setup(g => g.GetGroupMembersAsync(flat.Id, It.IsAny<CancellationToken>()))
            .Returns(flatMembers.Task);
        _groups
            .Setup(g => g.GetGroupMembersAsync(trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([traveller]);
        _categories
            .Setup(c => c.GetCategoriesAsync(flat.Id, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(Guid.NewGuid(), flat.Id, "Rent", null, null)]);
        _categories
            .Setup(c => c.GetCategoriesAsync(trip.Id, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CategoryResponse(Guid.NewGuid(), trip.Id, "Fuel", null, null)]);

        var provider = await OpenAsync(null);
        var decisions = provider.FindComponent<ExpenseDecisions>();

        // The flat first, whose people are slow; then the trip, before they arrive.
        var pickingFlat = provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(flat.Id));
        await provider.InvokeAsync(() => decisions.Instance.GroupIdChanged.InvokeAsync(trip.Id));

        flatMembers.SetResult([flatmate]);
        await pickingFlat;

        var editor = provider.FindComponent<SplitEditor>();
        Assert.Equal([traveller.Id], editor.Instance.Members.Select(member => member.Id));
        Assert.Equal(["Fuel"], decisions.Instance.Categories.Select(category => category.Name));
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Chips(
        IRenderedComponent<MudDialogProvider> provider) =>
        [.. provider.FindAll("button.gs-expense-chip")];

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(Guid? groupId)
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();

        var parameters = new DialogParameters<CreateTransactionDialog>
        {
            { dialog => dialog.SelectedGroupId, groupId }
        };

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add expense", parameters));

        return provider;
    }

    // ---- What it is in (#320) -----------------------------------------------------------

    /// <summary>Fills the two fields an expense cannot be saved without, and saves it.</summary>
    private static async Task<CreateTransactionRequest?> SaveAsync(
        IRenderedComponent<MudDialogProvider> provider, IDialogReference reference)
    {
        var amount = provider.FindComponents<MudNumericField<decimal>>()
            .Single(field => field.Instance.Label == "How much?");
        await provider.InvokeAsync(() => amount.Instance.ValueChanged.InvokeAsync(20m));

        var name = provider.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "What was it?");
        await provider.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync("Dinner in Lisbon"));

        await provider.FindAll("button").First(button => button.TextContent.Trim() == "Add expense")
            .ClickAsync(new MouseEventArgs());

        return await reference.GetReturnValueAsync<CreateTransactionRequest>();
    }

    /// <summary>
    /// A personal expense may be in any currency, and the dialog is how somebody says which:
    /// a dinner abroad written down in dollars is a different amount of money.
    /// </summary>
    [Fact]
    public async Task A_personal_expense_is_sent_in_the_currency_picked_for_it()
    {
        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add expense", new DialogParameters<CreateTransactionDialog>()));

        var currency = provider.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Currency");
        await provider.InvokeAsync(() => currency.Instance.ValueChanged.InvokeAsync("EUR"));

        // The amount field says it too, so the figure being typed reads as euros.
        provider.WaitForAssertion(() => Assert.Contains("€", provider.Find(".gs-amount").TextContent));

        var request = await SaveAsync(provider, reference!);

        Assert.Null(request!.GroupId);
        Assert.Equal("EUR", request.Currency);
    }

    /// <summary>
    /// In a group there is nothing to pick: its balances are in one currency, and the API
    /// takes the group's own when the request names none. Naming one would only invite the
    /// refusal for a currency the person never chose.
    /// </summary>
    [Fact]
    public async Task A_group_expense_offers_no_currency_and_sends_none()
    {
        _categories
            .Setup(c => c.GetCategoriesAsync(GroupId, It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var provider = Render<MudDialogProvider>();
        IDialogReference? reference = null;

        await provider.InvokeAsync(async () =>
            reference = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<CreateTransactionDialog>("Add expense", new DialogParameters<CreateTransactionDialog>
                {
                    { dialog => dialog.SelectedGroupId, GroupId },
                    { dialog => dialog.SelectedGroupName, "Trip" },
                    { dialog => dialog.SelectedGroupCurrency, "USD" }
                }));

        Assert.DoesNotContain(provider.FindComponents<MudSelect<string>>(),
            select => select.Instance.Label == "Currency");

        var request = await SaveAsync(provider, reference!);

        Assert.Equal(GroupId, request!.GroupId);
        Assert.Null(request.Currency);
    }
}
