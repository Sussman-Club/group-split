using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Reading a patch rather than applying it.
/// </summary>
/// <remarks>
/// Needed in exactly one place, and it is the place that matters most. A patch is the one
/// verb that can change <em>part</em> of a model, so for a field the API would otherwise
/// recompute -- an expense's splits -- "absent from the patch" and "set to nothing by the
/// patch" are different instructions, and the applied model cannot tell them apart: both
/// leave a value the endpoint has to interpret. The operations can, so they are read
/// before they are applied.
/// <para>
/// This is the awkwardness the plan named as the price of keeping <c>PATCH</c>. A
/// <c>PUT</c> deletes this file, because a whole model always arrives and silence about a
/// field is not a question. It should be deleted when the verb changes.
/// </para>
/// </remarks>
public static class PatchDocumentExtensions
{
    extension<T>(JsonPatchDocument<T> patch) where T : class
    {
        /// <summary>
        /// Whether any operation addresses <paramref name="pointer"/> or something inside
        /// it.
        /// </summary>
        /// <param name="pointer">
        /// A JSON Pointer to the member, leading slash and all, as the wire spells it:
        /// <c>/splits</c>.
        /// </param>
        /// <remarks>
        /// Matches the member itself and anything under it, so <c>/splits</c>,
        /// <c>/splits/0</c> and <c>/splits/0/amount</c> all count -- an operation on one
        /// share is a statement about the division as surely as replacing the whole list
        /// is. A <c>move</c> or <c>copy</c> is checked at both ends, since taking a share
        /// out of the list changes it just as much as putting one in.
        /// </remarks>
        public bool Touches(string pointer)
        {
            return patch.Operations.Any(operation =>
                Addresses(operation.path, pointer) || Addresses(operation.from, pointer));
        }
    }

    /// <summary>
    /// Whether a pointer from an operation names the member or a descendant of it.
    /// </summary>
    /// <remarks>
    /// The segment boundary is the point: <c>/splitsomething</c> starts with the same
    /// letters as <c>/splits</c> and is a different member, so a prefix match alone would
    /// read an unrelated edit as a statement about the division. Compared without regard to
    /// case because the model is camel-cased on the wire and hand-written patches are not
    /// always careful about it, while the members it could collide with are not.
    /// </remarks>
    private static bool Addresses(string? path, string pointer)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (!path.StartsWith(pointer, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == pointer.Length || path[pointer.Length] == '/';
    }
}
