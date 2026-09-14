using GroupSplit.Data.Entities;

namespace GroupSplit.Data.Extensions;

public static class TransactionExtensions
{
    extension(Transaction transaction)
    {
        /// <summary>
        /// Who paid, whether the transaction was loaded from the table or built in memory.
        /// </summary>
        /// <remarks>
        /// The navigation first, because a draft is not always keyed. The update preview
        /// builds a detached expense carrying the payer as an object and never sets the
        /// foreign key, so reading <see cref="Transaction.UserId"/> alone answers
        /// <see cref="Guid.Empty"/> there -- a payer nobody is, which divides as a stranger
        /// and rounds the remainder onto nobody.
        /// <para>
        /// A property rather than a method because it is a fact the transaction already has,
        /// not a question being asked of it: reading it costs two field accesses and cannot
        /// fail. Written once here because every caller that divides needs it and each of
        /// them had the pair inline, which is one place for the two halves to be got round
        /// the wrong way.
        /// </para>
        /// </remarks>
        public Guid Payer => transaction.User?.Id ?? transaction.UserId;
    }
}
