using System.Globalization;

namespace GroupSplit.App.Shared.Extensions;

public static class MoneyExtensions
{
    // Amounts carry no currency of their own, so "C" would follow whichever
    // culture happens to render: the server's during prerender, then the
    // browser's language once the WebAssembly runtime takes over. A British
    // browser would flip every figure from $ to £ on hydration. Pinned until
    // the data model carries a currency.
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string ToMoney(this decimal amount) => amount.ToString("C", Culture);
}
