using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GroupSplit.API.Test;

public class SupportReportClassifierTest
{
    [Theory]
    [InlineData("Bug", 0.1)]
    [InlineData("invented", 0.99)]
    [InlineData("Bug", double.NaN)]
    public async Task Unreliable_answers_use_defaults(string choice, double confidence)
    {
        var decisions = new Mock<IDecisionClient>();
        decisions.Setup(x => x.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionAnswer(choice, confidence));
        var result = await Create(decisions.Object).ClassifyAsync("Feedback", TestContext.Current.CancellationToken);
        Assert.Equal((SupportTicketType.Support, SupportTicketPriority.Normal, true), result);
    }

    [Fact]
    public async Task Unavailable_classifier_does_not_prevent_reporting()
    {
        var decisions = new Mock<IDecisionClient>();
        decisions.Setup(x => x.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unavailable"));
        var result = await Create(decisions.Object).ClassifyAsync("Feedback", TestContext.Current.CancellationToken);
        Assert.Equal((SupportTicketType.Support, SupportTicketPriority.Normal, true), result);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var decisions = new Mock<IDecisionClient>();
        decisions.Setup(x => x.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(decisions.Object).ClassifyAsync("Feedback", cancellation.Token));
    }

    [Theory]
    [InlineData(0.4, 0.95)]
    [InlineData(0.95, 0.4)]
    [InlineData(0.95, 0.95)]
    public async Task Review_is_required_when_either_answer_is_uncertain(double typeConfidence, double priorityConfidence)
    {
        var decisions = new Mock<IDecisionClient>();
        decisions.SetupSequence(x => x.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionAnswer("Bug", typeConfidence))
            .ReturnsAsync(new DecisionAnswer("High", priorityConfidence));
        var result = await Create(decisions.Object).ClassifyAsync("Feedback", TestContext.Current.CancellationToken);
        Assert.Equal(typeConfidence < 0.6 || priorityConfidence < 0.6, result.NeedsReview);
    }
    private static SupportReportClassifier Create(IDecisionClient decisions) => new(decisions,
        Options.Create(new FilingSuggestionOptions
        {
            Endpoint = new Uri("https://classifier.example/"),
            Key = "test",
            Deployment = "test"
        }), NullLogger<SupportReportClassifier>.Instance);
}
