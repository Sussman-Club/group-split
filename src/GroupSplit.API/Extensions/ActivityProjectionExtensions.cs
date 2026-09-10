using Microsoft.EntityFrameworkCore;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Turning transactions into the rows a history is read as -- a group's ledger, and the
/// cross-group feed on the home page.
/// </summary>
/// <remarks>
/// One file because the two projections are the same row with a different extra column on
/// it, and they were written twice before this: the group's Activity tab and the home
/// page's list had drifted on what a settlement is called and on whether the reader's own
/// share appeared at all.
/// <para>
/// The kind is read off the type rather than a column. EF knows which leaf each row is, and
/// a <c>Kind</c> beside the discriminator would be a second answer to the same question.
/// </para>
/// </remarks>
public static class ActivityProjectionExtensions
{
    extension(IQueryable<Transaction> activity)
    {
        /// <summary>
        /// One row per thing that happened in a group, with what it cost the person reading.
        /// </summary>
        /// <param name="readerId">
        /// Whose share to put on the row. Passed in rather than read from the current user
        /// so the projection has no ambient dependency and stays translatable.
        /// </param>
        public IQueryable<GroupActivityResponse> SelectActivityDto(Guid readerId)
        {
            return from transaction in activity
                   select new GroupActivityResponse
                   {
                       Id = transaction.Id,
                       Kind = transaction is Transfer ? ActivityKind.Transfer : ActivityKind.Expense,
                       Name = transaction.Name,
                       Description = transaction.Description,
                       Amount = transaction.Amount,
                       DateTime = transaction.DateTime,
                       PaidByUserId = transaction.UserId,
                       // People.Display, written out longhand because this is built in
                       // SQL: somebody invited and not yet signed in has no name, only the
                       // address the group typed in.
                       PaidByUserName = transaction.User.FirstName == null && transaction.User.LastName == null
                           ? transaction.User.Email ?? ""
                           : transaction.User.FirstName +
                             (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                       PaidByIsPendingInvitee = transaction.Group != null &&
                                                transaction.Group.Invitations.Any(invitation =>
                                                    invitation.ParticipantUserId == transaction.UserId),
                       // A transfer has exactly one split, to whoever was paid, and that is
                       // what makes it a transfer. On an expense the shares say who carried
                       // it, and there is no single other party to name.
                       PaidToUserId = transaction is Transfer
                           ? transaction.Splits.Select(split => (Guid?)split.UserId).FirstOrDefault()
                           : null,
                       PaidToUserName = transaction is Transfer
                           ? transaction.Splits.Select(split =>
                               split.User.FirstName +
                               (split.User.LastName != null ? " " + split.User.LastName : "")).FirstOrDefault()
                           : null,
                       CategoryId = transaction is Expense
                           ? ((Expense)transaction).CategoryId
                           : null,
                       Category = transaction is Expense && ((Expense)transaction).Category != null
                           ? ((Expense)transaction).Category!.Name
                           : null,
                       // Through the merchant rather than off the row: the logo is a fact
                       // about the place, stored once, and this join is how an expense gets
                       // to show it at all.
                       MerchantName = transaction.Merchant != null ? transaction.Merchant.Name : null,
                       MerchantLogoUrl = transaction.Merchant != null ? transaction.Merchant.LogoUrl : null,
                       // Null on a transfer, and null rather than zero on an expense the
                       // reader has no split of: both mean "this did not cost you", and a
                       // 0.00 in the column would be claiming their part of it was nothing.
                       Share = transaction is Transfer
                           ? null
                           : transaction.Splits
                               .Where(split => split.UserId == readerId)
                               .Select(split => (decimal?)split.Amount)
                               .FirstOrDefault()
                   };
        }

        /// <summary>
        /// Narrows a ledger the way its filter chips and its search box do.
        /// </summary>
        public IQueryable<Transaction> ApplyFilter(ActivityFilter? filter)
        {
            if (filter is null)
                return activity;

            // Hoisted out of the expression and normalised here, as the expense filter does
            // it: Npgsql writes a DateTimeOffset to a timestamptz column only at offset
            // zero, and lowering the search once keeps the comparison off the database's
            // collation. ToLower().Contains rather than EF.Functions.ILike for the same
            // reason as there -- ILike translates on Npgsql and not on the in-memory
            // provider the tests use.
            var after = filter.From?.ToUniversalTime();
            var before = filter.To?.ToUniversalTime();
            var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim().ToLowerInvariant();

            var narrowed = filter.Kind switch
            {
                ActivityKind.Expense => activity.Where(transaction => transaction is Expense),
                ActivityKind.Transfer => activity.Where(transaction => transaction is Transfer),
                _ => activity
            };

            // Both parties, not just the payer: "Omar paid you" is a row somebody looks for
            // by the name of whoever was paid as readily as by the name of whoever paid. The
            // names are nullable once an account has been anonymised.
            return from transaction in narrowed
                   where (after == null || transaction.DateTime >= after) &&
                         (before == null || transaction.DateTime <= before) &&
                         (search == null ||
                          transaction.Name.ToLower().Contains(search) ||
                          (transaction.Description != null &&
                           transaction.Description.ToLower().Contains(search)) ||
                          (transaction.User.FirstName != null &&
                           transaction.User.FirstName.ToLower().Contains(search)) ||
                          (transaction.User.LastName != null &&
                           transaction.User.LastName.ToLower().Contains(search)) ||
                          transaction.Splits.Any(split =>
                              (split.User.FirstName != null &&
                               split.User.FirstName.ToLower().Contains(search)) ||
                              (split.User.LastName != null &&
                               split.User.LastName.ToLower().Contains(search))) ||
                          (transaction is Expense && ((Expense)transaction).Category != null &&
                           ((Expense)transaction).Category!.Name.ToLower().Contains(search)) ||
                          // The shop, once a row shows one. Searching a ledger for "lidl"
                          // and being told nothing happened, while four rows on screen
                          // carry the Lidl logo, is the list disagreeing with itself.
                          (transaction.Merchant != null &&
                           transaction.Merchant.Name.ToLower().Contains(search)))
                   select transaction;
        }

        /// <summary>
        /// A group's ledger rows: the entry, what it cost the reader, and where the reader
        /// stood immediately after it.
        /// </summary>
        /// <param name="wholeLedger">
        /// The group's entries before any filtering. The running balance is read from this
        /// rather than from the narrowed query on purpose -- a balance as at a date is what
        /// it is whether or not the rows above it are currently on screen, and narrowing the
        /// list to settlements must not rewrite history.
        /// </param>
        public IQueryable<GroupLedgerEntryResponse> SelectLedgerDto(Guid readerId,
            IQueryable<Transaction> wholeLedger)
        {
            return from transaction in activity
                   select new GroupLedgerEntryResponse
                   {
                       Id = transaction.Id,
                       Kind = transaction is Transfer ? ActivityKind.Transfer : ActivityKind.Expense,
                       Name = transaction.Name,
                       Description = transaction.Description,
                       Amount = transaction.Amount,
                       DateTime = transaction.DateTime,
                       PaidByUserId = transaction.UserId,
                       // People.Display, written out longhand because this is built in
                       // SQL: somebody invited and not yet signed in has no name, only the
                       // address the group typed in.
                       PaidByUserName = transaction.User.FirstName == null && transaction.User.LastName == null
                           ? transaction.User.Email ?? ""
                           : transaction.User.FirstName +
                             (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                       PaidByIsPendingInvitee = transaction.Group != null &&
                                                transaction.Group.Invitations.Any(invitation =>
                                                    invitation.ParticipantUserId == transaction.UserId),
                       PaidToUserId = transaction is Transfer
                           ? transaction.Splits.Select(split => (Guid?)split.UserId).FirstOrDefault()
                           : null,
                       PaidToUserName = transaction is Transfer
                           ? transaction.Splits.Select(split =>
                               split.User.FirstName +
                               (split.User.LastName != null ? " " + split.User.LastName : "")).FirstOrDefault()
                           : null,
                       CategoryId = transaction is Expense
                           ? ((Expense)transaction).CategoryId
                           : null,
                       Category = transaction is Expense && ((Expense)transaction).Category != null
                           ? ((Expense)transaction).Category!.Name
                           : null,
                       // Through the merchant rather than off the row: the logo is a fact
                       // about the place, stored once, and this join is how an expense gets
                       // to show it at all.
                       MerchantName = transaction.Merchant != null ? transaction.Merchant.Name : null,
                       MerchantLogoUrl = transaction.Merchant != null ? transaction.Merchant.LogoUrl : null,
                       Share = transaction is Transfer
                           ? null
                           : transaction.Splits
                               .Where(split => split.UserId == readerId)
                               .Select(split => (decimal?)split.Amount)
                               .FirstOrDefault(),
                       // What the reader has paid out minus what has been put on them, over
                       // everything up to and including this entry. Transfers are in both
                       // sums, which is exactly why the merged ledger can carry this column
                       // and neither of the two tabs it replaces could.
                       RunningBalance =
                           wholeLedger
                               .Where(prior => prior.DateTime < transaction.DateTime ||
                                               (prior.DateTime == transaction.DateTime &&
                                                prior.Id <= transaction.Id))
                               .Where(prior => prior.UserId == readerId)
                               .Sum(prior => prior.Amount)
                           - wholeLedger
                               .Where(prior => prior.DateTime < transaction.DateTime ||
                                               (prior.DateTime == transaction.DateTime &&
                                                prior.Id <= transaction.Id))
                               .SelectMany(prior => prior.Splits)
                               .Where(split => split.UserId == readerId)
                               .Sum(split => split.Amount)
                   };
        }

        /// <summary>
        /// The same rows, plus which group each happened in -- for the feed that spans them.
        /// </summary>
        public IQueryable<UserActivityResponse> SelectUserActivityDto(Guid readerId)
        {
            return from transaction in activity
                   select new UserActivityResponse
                   {
                       Id = transaction.Id,
                       Kind = transaction is Transfer ? ActivityKind.Transfer : ActivityKind.Expense,
                       Name = transaction.Name,
                       Description = transaction.Description,
                       Amount = transaction.Amount,
                       DateTime = transaction.DateTime,
                       PaidByUserId = transaction.UserId,
                       // People.Display, written out longhand because this is built in
                       // SQL: somebody invited and not yet signed in has no name, only the
                       // address the group typed in.
                       PaidByUserName = transaction.User.FirstName == null && transaction.User.LastName == null
                           ? transaction.User.Email ?? ""
                           : transaction.User.FirstName +
                             (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                       PaidByIsPendingInvitee = transaction.Group != null &&
                                                transaction.Group.Invitations.Any(invitation =>
                                                    invitation.ParticipantUserId == transaction.UserId),
                       PaidToUserId = transaction is Transfer
                           ? transaction.Splits.Select(split => (Guid?)split.UserId).FirstOrDefault()
                           : null,
                       PaidToUserName = transaction is Transfer
                           ? transaction.Splits.Select(split =>
                               split.User.FirstName +
                               (split.User.LastName != null ? " " + split.User.LastName : "")).FirstOrDefault()
                           : null,
                       CategoryId = transaction is Expense
                           ? ((Expense)transaction).CategoryId
                           : null,
                       Category = transaction is Expense && ((Expense)transaction).Category != null
                           ? ((Expense)transaction).Category!.Name
                           : null,
                       // Through the merchant rather than off the row: the logo is a fact
                       // about the place, stored once, and this join is how an expense gets
                       // to show it at all.
                       MerchantName = transaction.Merchant != null ? transaction.Merchant.Name : null,
                       MerchantLogoUrl = transaction.Merchant != null ? transaction.Merchant.LogoUrl : null,
                       Share = transaction is Transfer
                           ? null
                           : transaction.Splits
                               .Where(split => split.UserId == readerId)
                               .Select(split => (decimal?)split.Amount)
                               .FirstOrDefault(),
                       // Null is not a missing value here: an expense with no group is what
                       // personal means, and a client renders it as "Personal".
                       GroupId = transaction.GroupId,
                       GroupName = transaction.Group != null ? transaction.Group.Name : null
                   };
        }
    }
}
