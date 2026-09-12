using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// An itemised bill, as the client states it: the whole receipt in one go.
/// </summary>
/// <remarks>
/// Whole rather than line by line, because a bill is transcribed in one sitting -- scanned,
/// or typed at the table -- and its figures only make sense together. Sending it replaces
/// whatever was there, which is also how a correction is made: re-send the bill as it should
/// have read.
/// <para>
/// The claims come with the items rather than in a second call, so that the common case is
/// one request. They can be left off and set afterwards through
/// <see cref="SetReceiptItemClaimsRequest"/>, which is what a client that lets people tap
/// their own lines will do.
/// </para>
/// </remarks>
public record SaveReceiptRequest
{
    /// <summary>
    /// What the items came to, before tax and tip. Must equal the lines' total, which is
    /// checked rather than assumed: the two disagreeing is the ordinary sign of a
    /// transcription slip.
    /// </summary>
    [Required(ErrorMessage = "Subtotal is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Subtotal must be a number with no more than 2 decimal places.")]
    public decimal Subtotal { get; init; }

    [MaxDecimalPlaces(2, ErrorMessage = "Tax must be a number with no more than 2 decimal places.")]
    public decimal Tax { get; init; }

    [MaxDecimalPlaces(2, ErrorMessage = "Tip must be a number with no more than 2 decimal places.")]
    public decimal Tip { get; init; }

    /// <summary>
    /// What was paid. Must equal the subtotal plus tax plus tip, and -- once the receipt is
    /// on an expense -- the expense's own amount.
    /// </summary>
    [Required(ErrorMessage = "Total is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Total must be a number with no more than 2 decimal places.")]
    public decimal Total { get; init; }

    /// <summary>The lines on the bill. At least one: a receipt with no items divides nothing.</summary>
    [MinLength(1, ErrorMessage = "A receipt needs at least one item.")]
    public IReadOnlyList<ReceiptItemInput> Items { get; init; } = [];
}

/// <summary>One line on a bill.</summary>
/// <remarks>
/// <see cref="TotalPrice"/> is sent rather than multiplied out of the unit price and the
/// quantity, because the paper does not always agree with the arithmetic -- a two-for-one,
/// a line discount, a price rounded at the till -- and the bill is the record. It is the only
/// figure the division reads.
/// </remarks>
public record ReceiptItemInput
{
    /// <summary>
    /// The line's own id, to keep claims attached to it across an edit. Null for a new line,
    /// which is given one.
    /// </summary>
    /// <remarks>
    /// Sending the id back is what separates "the wine cost 24.00, not 22.00" from "there was
    /// no wine, there was a 24.00 something-else": the first keeps the three people who
    /// claimed it, the second should not. A client re-sending a bill it has just read gets
    /// the first for free by echoing what it was given.
    /// </remarks>
    public Guid? Id { get; init; }

    [Required(ErrorMessage = "An item needs a name.")]
    [StringLength(128, ErrorMessage = "An item name must be less than 128 characters.")]
    public string Name { get; init; } = null!;

    [MaxDecimalPlaces(2, ErrorMessage = "A unit price must be a number with no more than 2 decimal places.")]
    public decimal UnitPrice { get; init; }

    /// <summary>How many, or how much: bills are written in kilos and litres as well as in units.</summary>
    [MaxDecimalPlaces(3, ErrorMessage = "A quantity must be a number with no more than 3 decimal places.")]
    public decimal Quantity { get; init; } = 1;

    [Required(ErrorMessage = "An item needs a total price.")]
    [MaxDecimalPlaces(2, ErrorMessage = "A total price must be a number with no more than 2 decimal places.")]
    public decimal TotalPrice { get; init; }

    /// <summary>
    /// Whether the bill's tax was charged on this line. True unless you say otherwise.
    /// </summary>
    /// <remarks>
    /// A flag rather than a rate, because a flag is what the paper gives you: one tax total
    /// at the bottom and a letter beside the lines it was charged on. It matters on the bill
    /// that made splitting worth doing -- where groceries are exempt and general goods are
    /// not, taxing every line taxes the bananas and lets the jacket off. Where the price
    /// already includes the tax, as under VAT, the receipt's tax is zero and this decides
    /// nothing.
    /// </remarks>
    public bool IsTaxable { get; init; } = true;

    /// <summary>
    /// How this line divides. Defaults to <see cref="ReceiptItemSplit.Claimed"/>, which is
    /// what <see cref="Claims"/> is for.
    /// </summary>
    /// <remarks>
    /// The default is the cautious one on purpose: a line that says nothing about how it
    /// divides, and names nobody, stops the bill being divided rather than quietly landing on
    /// everybody.
    /// </remarks>
    [EnumDataType(typeof(ReceiptItemSplit), ErrorMessage = "That is not a way a line can divide.")]
    public ReceiptItemSplit Split { get; init; } = ReceiptItemSplit.Claimed;

    /// <summary>
    /// Who had it, or empty to leave the line unclaimed for now. Unclaimed lines are
    /// perfectly ordinary while a bill is being worked through, and are refused when it comes
    /// to dividing it -- unless the line says it divides some other way, which is what
    /// <see cref="Split"/> is for.
    /// </summary>
    public IReadOnlyList<ReceiptClaimInput> Claims { get; init; } = [];
}

/// <summary>
/// How one line of a bill is divided, on the wire.
/// </summary>
/// <remarks>
/// Named apart from the entity's <c>ReceiptItemDivision</c> rather than shared with it, the
/// way <see cref="InboxStatus"/> is named apart from the row's own status: the DTOs and the
/// entities are separate assemblies on purpose, and the API has both namespaces in scope at
/// once.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ReceiptItemSplit>))]
public enum ReceiptItemSplit
{
    /// <summary>
    /// Between whoever claimed it, in proportion to their weights. The default, and the only
    /// one that needs <see cref="ReceiptItemInput.Claims"/> to say anything.
    /// </summary>
    Claimed = 0,

    /// <summary>
    /// Evenly between everybody, naming none of them. How "these two are mine and the rest is
    /// shared" is said, and the one division that cannot be written as a list of claimants --
    /// naming everybody means something different the moment somebody joins or leaves.
    /// </summary>
    Evenly = 1
}

/// <summary>One person's part of one line.</summary>
public record ReceiptClaimInput
{
    [Required]
    public Guid UserId { get; init; }

    /// <summary>
    /// Their part of the line, in proportion to the other claims on it. One apiece -- the
    /// default -- is an even share between whoever claimed it, which is what "we shared the
    /// wine" means. Two against one says somebody had twice as much.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "A claim's weight must be at least 1.")]
    public int Weight { get; init; } = 1;
}

/// <summary>
/// Who had one line, replacing whoever was on it.
/// </summary>
/// <remarks>
/// The call behind tapping your own name on a line. Replaces rather than adds, so that
/// un-claiming is the same operation as claiming and a client never has to work out which of
/// the two it is doing: send the list as it should now read, including empty.
/// </remarks>
public record SetReceiptItemClaimsRequest
{
    /// <summary>
    /// How the line divides from now on. <see cref="ReceiptItemSplit.Claimed"/> reads
    /// <see cref="Claims"/>; <see cref="ReceiptItemSplit.Evenly"/> names nobody and ignores
    /// it.
    /// </summary>
    [EnumDataType(typeof(ReceiptItemSplit), ErrorMessage = "That is not a way a line can divide.")]
    public ReceiptItemSplit Split { get; init; } = ReceiptItemSplit.Claimed;

    public IReadOnlyList<ReceiptClaimInput> Claims { get; init; } = [];
}
