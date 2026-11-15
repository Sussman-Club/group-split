using GroupSplit.Shared;
using ModelContextProtocol.AspNetCore;

public static class McpServiceExtensions
{
    public static IServiceCollection AddGroupSplitApiClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
#pragma warning disable EXTEXP0001 // Never retry token exchange or financial writes automatically.
        services.AddHttpClient("exchange").RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ApiTokenExchangeHandler>();
        var apiClient = services.AddHttpClient("api", client => client.BaseAddress =
                new Uri(configuration["Mcp:ApiBaseAddress"] ?? "https+http://api/"))
            .RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<ApiTokenExchangeHandler>();
        apiClient.AddTypedClient<GroupsClient>();
        apiClient.AddTypedClient<UsersClient>();
        apiClient.AddTypedClient<CategoriesClient>();
        apiClient.AddTypedClient<InvitationsClient>();
        apiClient.AddTypedClient<TransactionsClient>();
        apiClient.AddTypedClient<SplitRulesClient>();
        apiClient.AddTypedClient<BankConnectionsClient>();
        apiClient.AddTypedClient<InboxClient>();
        apiClient.AddTypedClient<ReceiptsClient>();
        apiClient.AddTypedClient<MerchantsClient>();
#pragma warning restore EXTEXP0001
        return services;
    }

    public static IServiceCollection AddGroupSplitMcpTools(this IServiceCollection services)
    {
        services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithTools<GroupTools>()
            .WithTools<AccountTools>()
            .WithTools<CategoryTools>()
            .WithTools<InvitationTools>()
            .WithTools<TransactionTools>()
            .WithTools<SplitRuleTools>()
            .WithTools<BankConnectionTools>()
            .WithTools<BankTransactionTools>()
            .WithTools<ReceiptTools>()
            .WithTools<SettlementTools>()
            .WithTools<MerchantTools>()
            .WithTools<GroupInvitationTools>();
        return services;
    }
}
