using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Editing a split rule from the group's Splits tab or from its history: the dialog around
/// <see cref="RuleEditorForm"/>, which reads the rule, hands the form a copy, and saves the
/// whole of it back.
/// </summary>
/// <remarks>
/// The form has its own tests. These are about the dialog's half: that it opens on the rule
/// as it stands, that what it sends is the rule as edited, and that a save the server refuses
/// leaves the dialog open with the edit still in it. A dialog that closed on a refusal would
/// take a division somebody had just retyped with it.
/// </remarks>
public class EditRuleDialogTest : ComponentTest
{
    private static readonly Guid Flat = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private static readonly UserInfo Ana = new(Guid.NewGuid(), "Ana", "Benitez", null);
    private static readonly UserInfo Lu = new(Guid.NewGuid(), "Lu", "Ferrer", null);

    private readonly Mock<IGroupsClient> _groups = new();

    private readonly List<(string Message, Severity Severity)> _said = [];

    /// <summary>The rule as the server answers it, kept so a test can see whether an edit reached it.</summary>
    private readonly SplitRuleDetailsResponse _stored = ARule("Household 3-way",
        new SharesSplitRuleDto { Shares = { [Ana.Id] = 3, [Lu.Id] = 1 } });

    public EditRuleDialogTest()
    {
        _groups
            .Setup(client => client.GetGroupMembersAsync(Flat, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Ana, Lu]);

        SplitRules
            .Setup(client => client.GetSplitRuleAsync(Household, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored);

        SplitRules
            .Setup(client => client.UpdateSplitRuleAsync(Household, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, UpdateSplitRuleRequest request, CancellationToken _) =>
                ARule(request.Name, request.Definition));

        Snackbar
            .Setup(s => s.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions> _, string _) =>
                _said.Add((message, severity)))
            .Returns((Snackbar?)null);

        Services.AddSingleton(_groups.Object);

        // The real commands over the mocked client, so a refusal travels the way it does in
        // the app: through the presenter, into a null the dialog has to handle.
        Services.AddSingleton<ISplitRuleCommands, SplitRuleCommands>();
    }

    private static SplitRuleDetailsResponse ARule(string name, SplitRuleDto definition) => new()
    {
        Id = Household,
        GroupId = Flat,
        Name = name,
        VersionId = Guid.NewGuid(),
        ChangedAt = DateTimeOffset.UtcNow,
        Definition = definition
    };

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenAsync(
        IReadOnlyList<string>? usedBy)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<EditRuleDialog>("Edit split rule", new DialogParameters<EditRuleDialog>
                {
                    { d => d.GroupId, Flat },
                    { d => d.SplitRuleId, Household },
                    { d => d.UsedBy, usedBy }
                }));

        return (provider, dialog);
    }

    private Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> OpenAsync() =>
        OpenAsync(["Groceries"]);

    private static Task ClickAsync(IRenderedComponent<MudDialogProvider> provider, string label) =>
        provider.FindAll("button").Single(button => button.TextContent.Trim() == label)
            .ClickAsync(new MouseEventArgs());

    private static Task SharesAsync(IRenderedComponent<MudDialogProvider> provider, UserInfo member, int shares)
    {
        var field = provider.FindComponents<MudNumericField<int>>()
            .Single(candidate => candidate.Instance.Label == $"{member.FirstName} {member.LastName}");

        return provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(shares));
    }

    private static int SharesShown(IRenderedComponent<MudDialogProvider> provider, UserInfo member) =>
        provider.FindComponents<MudNumericField<int>>()
            .Single(candidate => candidate.Instance.Label == $"{member.FirstName} {member.LastName}")
            .Instance.Value;

    [Fact]
    public async Task It_opens_on_the_rule_as_it_stands()
    {
        var (provider, _) = await OpenAsync();

        Assert.Equal("Household 3-way", provider.Find("input").GetAttribute("value"));
        Assert.Equal(3, SharesShown(provider, Ana));
        Assert.Equal(1, SharesShown(provider, Lu));

        // A rule keeps the shape it was written with; the API refuses a change of kind.
        var kind = provider.FindComponent<MudSelect<RuleType?>>();

        Assert.Equal(RuleType.Shares, kind.Instance.Value);
        Assert.True(kind.Instance.Disabled);
    }

    /// <summary>
    /// A PUT, so the name and the division go together: the API opens a new version when the
    /// division moved and none when only the name did, and it can only tell which from the
    /// whole of it.
    /// </summary>
    [Fact]
    public async Task Saving_sends_the_whole_rule_as_edited_and_hands_back_what_was_saved()
    {
        var (provider, dialog) = await OpenAsync();

        await provider.Find("input").InputAsync(new ChangeEventArgs { Value = "Household" });
        await SharesAsync(provider, Ana, 2);

        await ClickAsync(provider, "Save");

        var saved = await dialog.GetReturnValueAsync<SplitRuleDetailsResponse>();

        Assert.Equal("Household", saved!.Name);

        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            Household,
            It.Is<UpdateSplitRuleRequest>(request =>
                request.Name == "Household"
                && request.Definition is SharesSplitRuleDto
                && ((SharesSplitRuleDto)request.Definition).Shares[Ana.Id] == 2
                && ((SharesSplitRuleDto)request.Definition).Shares[Lu.Id] == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The form binds straight into the division's dictionary, so editing the instance the
    /// screen behind holds would change that screen as somebody typed, and leave it changed
    /// after Cancel.
    /// </summary>
    [Fact]
    public async Task Cancelling_leaves_the_rule_it_was_handed_as_it_was()
    {
        var (provider, dialog) = await OpenAsync();

        await SharesAsync(provider, Ana, 7);
        await ClickAsync(provider, "Cancel");

        var result = await dialog.Result;

        Assert.True(result!.Canceled);
        Assert.Equal(3, ((SharesSplitRuleDto)_stored.Definition).Shares[Ana.Id]);

        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The error path. The refusal is said once, by the presenter, and the dialog stays where
    /// it was with Save live again: the edit is still in it, and the person can change it and
    /// try again rather than retype it.
    /// </summary>
    [Fact]
    public async Task A_refused_save_says_why_and_keeps_the_dialog_open_to_try_again()
    {
        SplitRules
            .Setup(client => client.UpdateSplitRuleAsync(Household, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.SplitRuleNameTaken));

        var (provider, dialog) = await OpenAsync();

        await provider.Find("input").InputAsync(new ChangeEventArgs { Value = "Whoever paid" });
        await ClickAsync(provider, "Save");

        Assert.False(dialog.Result.IsCompleted);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not save the split.", message, StringComparison.Ordinal);
        Assert.Contains(ErrorMessages.For(ErrorCodes.SplitRuleNameTaken), message, StringComparison.Ordinal);

        Assert.Equal("Whoever paid", provider.Find("input").GetAttribute("value"));

        var save = provider.FindAll("button").Single(button => button.TextContent.Trim() == "Save");
        Assert.False(save.HasAttribute("disabled"));

        // And the second attempt, with another name, goes through.
        SplitRules
            .Setup(client => client.UpdateSplitRuleAsync(Household, It.IsAny<UpdateSplitRuleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, UpdateSplitRuleRequest request, CancellationToken _) =>
                ARule(request.Name, request.Definition));

        await provider.Find("input").InputAsync(new ChangeEventArgs { Value = "Household" });
        await ClickAsync(provider, "Save");

        Assert.Equal("Household", (await dialog.GetReturnValueAsync<SplitRuleDetailsResponse>())!.Name);
    }

    /// <summary>
    /// Shares adding up to nothing divide nothing, and the API would refuse them. The form
    /// says so where the numbers are, and nothing is sent.
    /// </summary>
    [Fact]
    public async Task A_division_the_form_will_not_take_is_not_sent()
    {
        var (provider, dialog) = await OpenAsync();

        await SharesAsync(provider, Ana, 0);
        await SharesAsync(provider, Lu, 0);
        await ClickAsync(provider, "Save");

        Assert.Contains("At least one member must have shares", provider.Markup, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);

        SplitRules.Verify(client => client.UpdateSplitRuleAsync(
            It.IsAny<Guid>(), It.IsAny<UpdateSplitRuleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A rule that could not be read is not opened as an empty form: the dialog closes, and
    /// the presenter has said why.
    /// </summary>
    [Fact]
    public async Task A_rule_that_could_not_be_read_closes_the_dialog_and_says_why()
    {
        SplitRules
            .Setup(client => client.GetSplitRuleAsync(Household, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(404, ErrorCodes.SplitRuleNotFound));

        var (_, dialog) = await OpenAsync();

        var result = await dialog.Result;

        Assert.True(result!.Canceled);

        var (message, severity) = Assert.Single(_said);

        Assert.Equal(Severity.Error, severity);
        Assert.Contains("Could not load the split.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Null is "nobody counted", which is not the same as one category or none. The dialog is
    /// a modal over whatever notice the screen behind was showing, so it says it again.
    /// </summary>
    [Fact]
    public async Task Not_knowing_which_categories_use_the_rule_is_said_before_the_save()
    {
        var (provider, _) = await OpenAsync(usedBy: null);

        Assert.Contains("Could not check which categories divide by this rule", provider.Markup,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_three_categories_divide_by_names_all_three()
    {
        var (provider, _) = await OpenAsync(["Groceries", "Utilities", "Cleaning"]);

        var warning = provider.Find(".gs-notice.is-warn strong");

        Assert.Equal("Groceries, Utilities and Cleaning divide by this rule", warning.TextContent.Trim());
    }

    /// <summary>One category using the rule is a local decision, and gets no warning.</summary>
    [Fact]
    public async Task A_rule_only_one_category_divides_by_raises_no_warning()
    {
        var (provider, _) = await OpenAsync(["Groceries"]);

        Assert.Empty(provider.FindAll(".gs-notice.is-warn"));
    }
}
