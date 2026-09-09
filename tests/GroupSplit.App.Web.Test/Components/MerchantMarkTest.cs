using Bunit;
using GroupSplit.App.Shared.Components;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// One 40px slot in a row's first column, carrying two facts.
/// </summary>
/// <remarks>
/// The decision worth pinning is which fact keeps the square. A splitting app is about who
/// is owed, so the payer's avatar stays and the shop's logo rides its corner; swapping the
/// avatar out for the logo looks better and answers the wrong question. It is easy to
/// "simplify" this component into doing exactly that, so it is written down here.
/// </remarks>
public class MerchantMarkTest : ComponentTest
{
    [Fact]
    public void A_row_with_a_payer_and_a_shop_keeps_the_payer_and_badges_the_shop()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.PaidByUserName, "Omar Silva")
            .Add(component => component.MerchantName, "Lidl")
            .Add(component => component.MerchantLogoUrl, "https://logos/lidl.png"));

        Assert.Equal("OS", mark.Find(".gs-avatar").TextContent.Trim());

        var badge = mark.Find("img.gs-mark-badge");
        Assert.Equal("https://logos/lidl.png", badge.GetAttribute("src"));

        // Decorative: the expense is named for the shop beside it, and a screen reader
        // being told "Lidl" twice per row is worse than being told it once.
        Assert.Equal("", badge.GetAttribute("alt"));
        Assert.Equal("Lidl", badge.GetAttribute("title"));
    }

    [Fact]
    public void A_payer_with_no_shop_is_the_avatar_and_nothing_else()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.PaidByUserName, "Omar Silva"));

        Assert.Equal("OS", mark.Find(".gs-avatar").TextContent.Trim());
        Assert.Empty(mark.FindAll("img"));
    }

    [Fact]
    public void With_nobody_having_paid_yet_the_logo_takes_the_square_itself()
    {
        // The inbox: an imported row has a shop but no payer, because filing it is the
        // decision nobody has made yet.
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.MerchantName, "Lidl")
            .Add(component => component.MerchantLogoUrl, "https://logos/lidl.png"));

        Assert.Empty(mark.FindAll(".gs-avatar"));
        Assert.Equal("https://logos/lidl.png", mark.Find("img.gs-mark-solo").GetAttribute("src"));
    }

    [Fact]
    public void With_neither_a_payer_nor_a_logo_the_shops_initials_stand_in()
    {
        var mark = Render<MerchantMark>(parameters => parameters
            .Add(component => component.MerchantName, "Pharmacy 88"));

        Assert.Empty(mark.FindAll("img"));
        Assert.Equal("P8", mark.Find(".gs-avatar").TextContent.Trim());
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
