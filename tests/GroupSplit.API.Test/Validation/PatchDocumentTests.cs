using GroupSplit.API.Extensions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson.Operations;

namespace GroupSplit.API.Test.Validation;

/// <summary>
/// Reading a patch instead of applying it, which the update route does for exactly one
/// member.
/// </summary>
/// <remarks>
/// An expense's splits are recomputed unless the patch stated them, and the applied model
/// cannot say which happened: it carries the expense's current shares either way. So the
/// operations are read first, and everything about whether an edit rescales somebody's
/// share or keeps it rests on this answering correctly.
/// </remarks>
public class PatchDocumentTests
{
    private static JsonPatchDocument<UpdateTransactionRequest> Patch(
        params Operation<UpdateTransactionRequest>[] operations)
    {
        var patch = new JsonPatchDocument<UpdateTransactionRequest>();

        foreach (var operation in operations)
            patch.Operations.Add(operation);

        return patch;
    }

    private static Operation<UpdateTransactionRequest> At(string path, string op = "replace") =>
        new(op, path, from: null, value: null);

    [Fact]
    public void A_patch_that_says_nothing_touches_nothing()
    {
        Assert.False(Patch().Touches("/splits"));
    }

    [Fact]
    public void A_patch_of_another_member_does_not_touch_it()
    {
        Assert.False(Patch(At("/amount"), At("/name"), At("/categoryId")).Touches("/splits"));
    }

    [Fact]
    public void Replacing_the_whole_list_touches_it()
    {
        Assert.True(Patch(At("/splits")).Touches("/splits"));
    }

    /// <summary>
    /// One share is a statement about the division as surely as all of them are: the rest
    /// of the list has to stay as it is for that edit to mean anything.
    /// </summary>
    [Theory]
    [InlineData("/splits/0")]
    [InlineData("/splits/0/amount")]
    [InlineData("/splits/-")]
    public void Addressing_one_share_touches_it(string path)
    {
        Assert.True(Patch(At(path)).Touches("/splits"));
    }

    /// <summary>
    /// The reason this is not a <c>StartsWith</c>. A member whose name merely begins with
    /// the same letters is a different member, and reading an edit to it as a statement
    /// about the division would keep shares the caller expected to be recomputed.
    /// </summary>
    [Theory]
    [InlineData("/splitsomething")]
    [InlineData("/splitsTotal")]
    public void A_member_whose_name_merely_starts_the_same_does_not_touch_it(string path)
    {
        Assert.False(Patch(At(path)).Touches("/splits"));
    }

    [Fact]
    public void One_operation_among_several_is_enough()
    {
        Assert.True(Patch(At("/name"), At("/splits/1/amount"), At("/amount")).Touches("/splits"));
    }

    /// <summary>
    /// Taking a share out of the list changes it as much as putting one in, and a move
    /// says where it came from in <c>from</c> rather than in <c>path</c>.
    /// </summary>
    [Fact]
    public void A_move_out_of_the_list_touches_it()
    {
        var move = new Operation<UpdateTransactionRequest>(
            "move", "/description", from: "/splits/0/amount", value: null);

        Assert.True(Patch(move).Touches("/splits"));
    }

    /// <summary>
    /// The model is camel-cased on the wire, and a hand-written patch is not always
    /// careful about it. Nothing this could collide with differs only by case.
    /// </summary>
    [Fact]
    public void The_comparison_ignores_case()
    {
        Assert.True(Patch(At("/Splits/0/Amount")).Touches("/splits"));
    }
}
