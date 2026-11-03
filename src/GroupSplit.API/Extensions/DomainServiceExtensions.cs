using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;
using GroupSplit.API.Services.BackgroundJobs;
using GroupSplit.API.Services.PersonalData;
using GroupSplit.API.Services.Push;
using GroupSplit.Jobs;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            services.AddScoped<IAccountDataGuard, AccountDataGuard>();
            services.AddScoped<IPersonalDataExport, PersonalDataExport>();
            services.AddScoped<IPersonalDataReader, PersonalDataReader>();
            services.AddScoped<IPersonalDataArchiveWriter, PersonalDataArchiveWriter>();

            // The Keycloak half of deleting an account. Keycloak itself is the host's to
            // provide -- IKeycloakUserRemoval, from AddKeycloakAdminClient in Program.cs, or a
            // fake in a test host -- like any other system outside this one.
            services.TryAddSingleton(TimeProvider.System);
            services.AddMemoryCache();
            services.AddSingleton(new BackgroundJobRegistry()
                .Add<DeleteReceiptFile>(DeleteReceiptFile.Name)
                .Add<SendExpensePush>(SendExpensePush.Name)
                .Add<SendPushNotification>(SendPushNotification.Name));
            services.TryAddSingleton<IPushSender, FirebasePushSender>();
            services.AddScoped<PushNotificationQueue>();
            services.AddBackgroundJobs();
            services.AddScoped<ILoginDeletion, LoginDeletion>();
            services.AddJobs()
                .Handlers
                    .AddJobHandler<DeleteReceiptFile, DeleteReceiptFileHandler>()
                    .AddJobHandler<SendExpensePush, SendExpensePushHandler>()
                    .AddJobHandler<SendPushNotification, SendPushNotificationHandler>()
                    .AddJobHandler<SweepPendingLoginDeletions, SweepPendingLoginDeletionsHandler>()
                    .AddJobHandler<SweepStaffActivity, SweepStaffActivityHandler>()
                .JobsBuilder
                    .Scheduler.Add(new SweepPendingLoginDeletions(), SweepPendingLoginDeletionsHandler.Schedule)
                    .Add(new SweepStaffActivity(), JobSchedule.Every(TimeSpan.FromHours(1), DateTimeOffset.UtcNow.AddMinutes(1)));
            services.AddScoped<IGroupService, GroupService>();
            services.AddScoped<IGroupJoiner, GroupJoiner>();
            services.AddScoped<IGroupParticipants, GroupParticipants>();
            services.AddScoped<ISettlementService, SettlementService>();
            services.AddScoped<ISettlementRequestService, SettlementRequestService>();
            services.AddScoped<IInvitationService, InvitationService>();
            services.AddScoped<IGuestService, GuestService>();
            services.AddScoped<IJoinLinkService, JoinLinkService>();
            services.AddScoped<ITransactionService, TransactionService>();
            services.AddScoped<IExpenseProvenance, ExpenseProvenance>();
            services.AddSplitRuleServices();
            services.AddScoped<ICategoryService, CategoryService>();
            services.AddScoped<IMerchantService, MerchantService>();
            services.AddScoped<MerchantImageService>();
            services.AddScoped<ISplitRuleService, SplitRuleService>();
            services.AddScoped<ISplitRuleRevisions, SplitRuleRevisions>();
            services.AddScoped<IMemberSplitRules, MemberSplitRules>();
            services.AddScoped<IBillSplitRule, BillSplitRule>();
            services.AddScoped<IEvenSplitRule, EvenSplitRule>();
            services.AddScoped<IExpenseSplitter, ExpenseSplitter>();
            services.AddScoped<IReceiptService, ReceiptService>();
            services.AddScoped<ReceiptInboxService>();
            services.AddScoped<IReceiptFiles, ReceiptFiles>();
            services.AddScoped<IReceiptAttachmentService, ReceiptAttachmentService>();
            services.AddBankingServices();
            services.AddScoped<SupportReportClassifier>();
            services.AddScoped<SupportTicketService>();
            services.AddScoped<StaffTeamService>();
            services.AddScoped<StaffActivityService>();
            services.AddScoped<StaffActivityRetention>();
            services.AddScoped<StaffSavedViewService>();
            services.AddScoped<SupportUserService>();

            return services;
        }
    }

    extension(IEndpointRouteBuilder routes)
    {
        public IEndpointRouteBuilder MapDomainApi()
        {
            routes.MapGroupApi();
            routes.MapUserApi();
            routes.MapPushApi();
            routes.MapSettlementRequestsApi();
            routes.MapInvitationsApi();
            routes.MapTransaction();
            routes.MapReceiptDraftsApi();
            routes.MapReceiptsApi();
            routes.MapReceiptAttachmentsApi();
            routes.MapCategoriesApi();
            routes.MapSplitRulesApi();
            routes.MapMerchantsApi();
            routes.MapBankConnectionsApi();
            routes.MapInboxApi();
            routes.MapSupportReportsApi();
            routes.MapStaffApi();
            routes.MapMerchantImagesApi();
            routes.MapWebhooksApi();

            return routes;
        }
    }
}
