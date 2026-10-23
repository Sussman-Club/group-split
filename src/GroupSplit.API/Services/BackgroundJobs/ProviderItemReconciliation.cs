namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Safe identifiers retained when provider access cannot be revoked automatically.</summary>
public sealed record ProviderItemReconciliation(string Provider, string Resource, Guid ConnectionId);
