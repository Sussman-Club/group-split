using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;

namespace GroupSplit.AppHost.Test.Configuration;

public sealed class OpenAIConfigurationTest(AppHostFixture appHost)
{
    [Fact]
    public async Task Receipt_transcription_reference_uses_the_configured_endpoint()
    {
        var connection = await appHost.Application.GetConnectionStringAsync(
            "receipt-transcription", TestContext.Current.CancellationToken);

        Assert.NotNull(connection);
        Assert.Contains(
            "Endpoint=https://azure-openai.test/openai/v1/",
            connection,
            StringComparison.Ordinal);
        Assert.Contains("Model=test-receipt-model", connection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filing_suggestion_reference_is_the_decision_deployment_on_the_same_resource()
    {
        var connection = await appHost.Application.GetConnectionStringAsync(
            "filing-suggestions", TestContext.Current.CancellationToken);

        Assert.NotNull(connection);
        Assert.Contains(
            "Endpoint=https://azure-openai.test/openai/v1/",
            connection,
            StringComparison.Ordinal);
        Assert.Contains("Key=test-azure-openai-key", connection, StringComparison.Ordinal);
        Assert.Contains("Model=test-decision-model", connection, StringComparison.Ordinal);
    }
}
