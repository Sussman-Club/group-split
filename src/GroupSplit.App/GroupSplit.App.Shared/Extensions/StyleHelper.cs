namespace GroupSplit.App.Shared.Extensions;

public class StyleHelper(string style)
{
    public override string ToString()
    {
        return style;
    }

    public StyleHelper Add(string newStyle)
    {
        return new StyleHelper(style + newStyle);
    }

    public static implicit operator string(StyleHelper styleHelper)
    {
        return styleHelper.ToString();
    }
}