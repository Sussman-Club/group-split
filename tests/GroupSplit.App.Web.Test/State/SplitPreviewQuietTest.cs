using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What a division preview says when the server cannot give one.
/// </summary>
/// <remarks>
/// The edit dialog previews on every change. With the shares set by hand, changing the total
/// before the shares makes the two disagree for a moment, and the server answers that with
/// <c>SPLITS_DO_NOT_SUM_TO_AMOUNT</c>. That used to put an error snackbar up while somebody
/// was still typing, over a dialog whose own total line was already saying the same thing.
/// </remarks>
public class SplitPreviewQuietTest
{
    private readonly Mock<ITransactionsClient> _client = new();
    private readonly Mock<ISnackbar> _snackbar = new();

    private TransactionCommands Commands() =>
        new(_client.Object,
            new ApiErrorPresenter(new Mock<NavigationManager>().Object, _snackbar.Object),
            _snackbar.Object, Mock.Of<IDialogService>(), Mock.Of<IReceiptAttachmentCommands>(),
            Mock.Of<IReceiptCommands>(), new DataChangeNotifier());

    private void PreviewRefusedWith(string code) =>
        _client
            .Setup(client => client.PreviewTransactionSplitsAsync(It.IsAny<CreateTransactionRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(422, code));

    private int Snackbars() => _snackbar.Invocations.Count(i => i.Method.Name == nameof(ISnackbar.Add));

    [Fact]
    public async Task Shares_that_do_not_yet_add_up_say_nothing()
    {
        PreviewRefusedWith(ErrorCodes.SplitsDoNotSumToAmount);

        Assert.Null(await Commands().PreviewAsync(new CreateTransactionRequest()));
        Assert.Equal(0, Snackbars());
    }

    [Fact]
    public async Task Any_other_refusal_is_still_said()
    {
        PreviewRefusedWith(ErrorCodes.TransactionPayerNotInGroup);

        Assert.Null(await Commands().PreviewAsync(new CreateTransactionRequest()));
        Assert.Equal(1, Snackbars());
    }
}
