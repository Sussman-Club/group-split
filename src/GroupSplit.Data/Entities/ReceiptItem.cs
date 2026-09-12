namespace GroupSplit.Data.Entities;

/// <summary>
/// One line on a bill: what it was, how many, what it cost, which purchase it belongs to,
/// and who had it.
/// </summary>
/// <remarks>
/// <see cref="TotalPrice"/> is stored rather than multiplied out of <see cref="UnitPrice"/>
/// and <see cref="Quantity"/>, because the paper does not always agree with the arithmetic
/// -- a two-for-one, a line discount, a price rounded at the till -- and the bill is the
/// record. The division reads this one and nothing else, so a line that does not multiply
/// out is transcribed faithfully instead of being corrected into something nobody was
/// charged.
/// </remarks>
public class ReceiptItem : Entity
{
    public virtual Receipt Receipt { get; set; } = null!;

    /// <summary>
    /// Public, because which bill a line belongs to is the line's own data -- the service
    /// that divides a receipt reads it outside this assembly.
    /// </summary>
    public Guid ReceiptId { get; set; }

    /// <summary>
    /// The expense this line's money is part of, or null while nobody has said.
    /// </summary>
    /// <remarks>
    /// What makes one charge able to be two purchases. A warehouse run is the flat's
    /// groceries and a jacket that is yours alone on one piece of paper; each distinct
    /// expense the lines name is one part of the bill, with its own group, its own category
    /// and its own place in the ledger. A restaurant bill names one expense on every line,
    /// which is the same shape with one part.
    /// <para>
    /// Null is the ordinary state of a bill somebody is still working through and a refusal
    /// once it comes to filing: a line nobody has placed is money belonging to no purchase,
    /// and there is no honest guess to make about which one.
    /// </para>
    /// <para>
    /// Distinct from <see cref="Claims"/>, which answers a different question about the same
    /// line. This one says <em>which purchase this is</em>; the claims say <em>who owed it</em>
    /// within that purchase. They ran together while a bill could only be one expense, and the
    /// jacket is what pulled them apart: claiming it for yourself divides it correctly and
    /// still files it under Groceries in the flat's ledger.
    /// </para>
    /// </remarks>
    public virtual Expense? Expense { get; set; }

    public Guid? ExpenseId { get; set; }

    /// <summary>
    /// Where this line sits on the paper, from zero. What makes "line 4" mean anything.
    /// </summary>
    /// <remarks>
    /// A bill is a piece of paper and its order is part of what it says -- the groceries are
    /// the first fourteen lines and the clothes are the last two, which is why a split can be
    /// written as a range at all. Without a column for it the order is whatever the database
    /// hands back, and on Postgres that changes the moment a row is updated: somebody could
    /// read the numbers off one listing, claim a line, and put the wrong lines in the wrong
    /// part with nothing refusing it.
    /// <para>
    /// Stored rather than derived from insertion order, because there is nothing to derive it
    /// from: the ids are client-generated and random, so they sort into no order the paper
    /// has.
    /// </para>
    /// </remarks>
    public int Position { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// <see cref="Name"/> folded for matching: lower-cased and trimmed. What says two lines
    /// are the same product.
    /// </summary>
    /// <remarks>
    /// A bill prints "2 @ 59.99" for two of a thing, and that is one line -- which is fine
    /// until the two are headed different ways. <see cref="ExpenseId"/> and
    /// <see cref="Division"/> are facts about a line, so one line cannot be one jacket of
    /// yours and one of Ana's, nor a pack of paper towels for the flat beside an identical
    /// one for your office. Two lines can. Splitting the quantity is therefore how that is
    /// said, and this is what lets the two be shown as what they are rather than as an
    /// accidental duplicate: a reader collapses on it.
    /// <para>
    /// Not derived by splitting <see cref="Quantity"/> automatically, because a quantity is
    /// not always a count. Three-quarters of a kilo of salmon is a measure and has no units
    /// to separate; a twelve-pack does, and turning it into twelve rows nobody asked for
    /// would be worse than the problem.
    /// </para>
    /// <para>
    /// Stored rather than folded in the query, the same way <see cref="Merchant.NormalizedName"/>
    /// is and for the same reason: an index can be on it. Which is also what would let a
    /// later feature recognise a thing across bills -- "you bought this last month" -- without
    /// anything here having to change.
    /// </para>
    /// </remarks>
    public required string NormalizedName { get; set; }

    /// <summary>What one of them cost.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// How many. Decimal rather than an integer count, because bills are written in kilos
    /// and litres as readily as in units.
    /// </summary>
    public decimal Quantity { get; set; } = 1;

    /// <summary>
    /// What the line came to, and the only figure the division uses. See the remarks on the
    /// type for why it is not derived.
    /// </summary>
    public required decimal TotalPrice { get; set; }

    /// <summary>
    /// Whether the bill's tax was charged on this line. True unless somebody says otherwise.
    /// </summary>
    /// <remarks>
    /// A flag and not a rate, because a flag is what the paper gives you: a receipt prints
    /// one tax total at the bottom and a letter beside the lines it was charged on. A rate
    /// per line would be a more general model of something no till hands over, and it would
    /// make <see cref="Receipt.Tax"/> derived rather than transcribed.
    /// <para>
    /// It matters most on exactly the bill that made a receipt worth splitting: where
    /// groceries are exempt and general goods are not, apportioning the tax across every
    /// line taxes the bananas and lets the jacket off. So the tax is weighed over the lines
    /// it was actually charged on -- and the tip, which is a fact about the bill rather than
    /// about the goods, is weighed over all of them.
    /// </para>
    /// <para>
    /// Defaulted to true so that a bill nobody flags divides exactly as it did before this
    /// existed, and so that the common case -- a restaurant, where everything is taxable --
    /// needs no thought. Where the price already includes the tax, as it does under VAT,
    /// <see cref="Receipt.Tax"/> is zero and this decides nothing.
    /// </para>
    /// </remarks>
    public bool IsTaxable { get; set; } = true;

    /// <summary>
    /// Who had it, and in what proportion.
    /// </summary>
    /// <remarks>
    /// The only thing that says how a line divides, and deliberately the only thing. A line
    /// carried a marker for "the table's, rather than anybody's" for a while; it is gone,
    /// because dividing something between everybody is not what an itemised bill is for. An
    /// itemised division says everybody owes what they had -- a category that wants an even
    /// split has an even rule to name, and naming both was two ways to say one thing with a
    /// silent disagreement between them.
    /// <para>
    /// Empty means nobody has claimed the line yet: an ordinary state while a bill is being
    /// worked through, and a refusal once it comes to dividing its part.
    /// </para>
    /// </remarks>
    public virtual ICollection<ReceiptItemClaim> Claims { get; } = [];
}
