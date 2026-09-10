using Bunit;
using GroupSplit.App.Shared.Components;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// One 40px slot in a row's first column, carrying two facts.
/// </summary>
/// <remarks>
/// The decision worth pinning is which fact keeps the tile, and it has been reversed once,
/// so both answers are recorded here rather than only the current one.
/// <para>
/// It used to be the payer: the avatar took the tile and the shop's logo rode its corner,
/// on the argument that a splitting app is about who is owed and that swapping answers the
/// wrong question. That argument assumed swapping loses the payer. It does not -- the payer
/// moves to the corner, and stays in the "Daniel Rivero paid" line beside it as well. What
/// actually settled it was measurement: on the grid rows the corner badge came out at 17px,
/// under the 18px floor the note on it set, so no logo down that column was legible at all.
/// A face at 20px still reads, because initials survive sizes a pictogram does not.
/// </para>
/// <para>
/// So the place takes the tile now. Two things follow that are easy to undo by accident and
/// are therefore asserted below: the corner holds a <em>circle</em>, which is correct
/// exactly because the thing in it is a person, and the inbox's arrangement -- logo on the
/// tile, no payer at all -- is no longer the odd one out but the same arrangement with one
/// fact missing.
/// </para>
/// </remarks>
public class MerchantMarkTest : ComponentTest
{
    [Fact]
    public void A_row_with_a_payer_and_a_shop_leads_with_the_shop()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.PaidByUserName, "Omar Silva")
            .Add(component => component.MerchantName, "Lidl")
            .Add(component => component.MerchantLogoUrl, "https://logos/lidl.png"));

        var place = mark.Find("img.gs-mark-place");
        Assert.Equal("https://logos/lidl.png", place.GetAttribute("src"));

        // Decorative: the expense is named for the shop beside it, and a screen reader
        // being told "Lidl" twice per row is worse than being told it once.
        Assert.Equal("", place.GetAttribute("alt"));
        Assert.Equal("Lidl", place.GetAttribute("title"));

        // The payer did not go away, it moved to the corner -- which is the whole answer
        // to the objection the old arrangement was built on.
        Assert.Equal("OS", mark.Find(".gs-mark-who").TextContent.Trim());
        Assert.Equal("Omar Silva", mark.Find(".gs-mark-who").GetAttribute("title"));
    }

    /// <summary>
    /// The payer's colour is the payer's, wherever they are drawn.
    /// </summary>
    /// <remarks>
    /// The badge is not an <c>Avatar</c> -- at a fifth of the area it carries initials and
    /// a tone and none of the rest -- so the tone has to be applied by hand, and a badge
    /// that skipped it would make one person teal in the corner of one row and clay in the
    /// tile of the next.
    /// </remarks>
    [Fact]
    public void The_payer_on_the_corner_keeps_the_tone_their_avatar_would_have()
    {
        var badge = Render<MerchantMark>(parameters => parameters
                .Add(component => component.PaidByUserName, "Omar Silva")
                .Add(component => component.MerchantLogoUrl, "https://logos/lidl.png"))
            .Find(".gs-mark-who");

        var avatar = Render<Avatar>(parameters => parameters
                .Add(component => component.Name, "Omar Silva"))
            .Find(".gs-avatar");

        var tones = new[] { "clay", "ink" };

        Assert.Equal(
            tones.Where(tone => avatar.ClassList.Contains(tone)),
            tones.Where(tone => badge.ClassList.Contains(tone)));
    }

    [Fact]
    public void A_payer_with_no_shop_is_the_avatar_and_nothing_else()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.PaidByUserName, "Omar Silva"));

        Assert.Equal("OS", mark.Find(".gs-avatar").TextContent.Trim());
        Assert.Empty(mark.FindAll("img"));

        // No corner on this one: there is nothing behind the payer to badge them onto, and
        // a lone badge would be a presence dot on nothing.
        Assert.Empty(mark.FindAll(".gs-mark-who"));
    }

    /// <summary>
    /// The mark is one size, because the column is one size.
    /// </summary>
    /// <remarks>
    /// It took a <c>Size</c> the two grids set to "sm", which drew a 28px avatar beside the
    /// 40px tile a settlement gets -- so the first column had two left edges and two right
    /// edges, and the corner badge shrank to 17px along with it. Asserted because the
    /// parameter is exactly the kind of thing that gets handed back.
    /// </remarks>
    [Fact]
    public void The_mark_is_always_the_size_of_the_column()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.PaidByUserName, "Omar Silva"));

        var avatar = mark.Find(".gs-avatar");

        Assert.Contains("mark", avatar.ClassList);
        Assert.DoesNotContain("sm", avatar.ClassList);
    }

    [Fact]
    public void With_nobody_having_paid_yet_the_logo_takes_the_tile_alone()
    {
        // The inbox: an imported row has a shop but no payer, because filing it is the
        // decision nobody has made yet. Same tile as a filed row, minus the corner.
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.MerchantName, "Lidl")
            .Add(component => component.MerchantLogoUrl, "https://logos/lidl.png"));

        Assert.Empty(mark.FindAll(".gs-avatar"));
        Assert.Empty(mark.FindAll(".gs-mark-who"));
        Assert.Equal("https://logos/lidl.png", mark.Find("img.gs-mark-place").GetAttribute("src"));
    }

    [Fact]
    public void With_neither_a_payer_nor_a_logo_the_shops_initials_stand_in()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.MerchantName, "Pharmacy 88"));

        Assert.Empty(mark.FindAll("img"));

        var avatar = mark.Find(".gs-avatar");
        Assert.Equal("P8", avatar.TextContent.Trim());

        // A tile, not a circle: it stands for a place, and the column's one rule is that a
        // square is a place and a circle is a person.
        Assert.Contains("tile", avatar.ClassList);
    }

    [Fact]
    public void An_empty_logo_is_no_logo_rather_than_an_image_pointing_at_this_page()
    {
        // What an API that sends "" instead of null would do to a row: an img with an empty
        // src resolves to the document itself and renders as a broken image.
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.PaidByUserName, "Omar Silva")
            .Add(component => component.MerchantName, "Lidl")
            .Add(component => component.MerchantLogoUrl, "   "));

        Assert.Empty(mark.FindAll("img"));
    }
}
