using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Everything the domain is made of, and every route it is reachable through, declared
/// once.
/// </summary>
/// <remarks>
/// There used to be three copies of this list -- <c>Program.cs</c> and the two test hosts
/// -- and adding a service meant remembering all three. Forgetting one fails at run time
/// rather than at compile time, and the failure it produces ("No service for type ... has
/// been registered", sixty-six tests red) says nothing about the cause.
/// <para>
/// A test host differs from production where it means to: the database provider and the
/// HTTP context. Not in which services exist.
/// </para>
/// </remarks>
public static class DomainServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDomainServices()
        {
            services.AddCurrentUser();
            services.AddScoped<IDebtCalculationService, DebtCalculationService>();
            services.AddScoped<IAccountService, AccountService>();
            services.AddScoped<IGroupService, GroupService>();
            services.AddScoped<IGroupJoiner, GroupJoiner>();
            services.AddScoped<IGroupParticipants, GroupParticipants>();
            services.AddScoped<ISettlementService, SettlementService>();
            services.AddScoped<IInvitationService, InvitationService>();
            services.AddScoped<IJoinLinkService, JoinLinkService>();
            services.AddScoped<ITransactionService, TransactionService>();
            services.AddScoped<IExpenseProvenance, ExpenseProvenance>();
            services.AddSplitRuleServices();
            services.AddScoped<ICategoryService, CategoryService>();
            services.AddScoped<IMerchantService, MerchantService>();
            services.AddScoped<ISplitRuleService, SplitRuleService>();
            services.AddScoped<ISplitRuleRevisions, SplitRuleRevisions>();
            services.AddScoped<IExpenseSplitter, ExpenseSplitter>();
            services.AddScoped<IReceiptService, ReceiptService>();
            services.AddBankingServices();

            return services;
        }
    }

    extension(IEndpointRouteBuilder routes)
    {
        public IEndpointRouteBuilder MapDomainApi()
        {
            routes.MapGroupApi();
            routes.MapUserApi();
            routes.MapInvitationsApi();
            routes.MapTransaction();
            routes.MapReceiptsApi();
            routes.MapCategoriesApi();
            routes.MapSplitRulesApi();
            routes.MapMerchantsApi();
            routes.MapBankConnectionsApi();
            routes.MapInboxApi();
            routes.MapWebhooksApi();

            return routes;
        }
    }
}
