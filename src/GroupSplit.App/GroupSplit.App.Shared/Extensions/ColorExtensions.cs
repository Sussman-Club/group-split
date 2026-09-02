using MudBlazor.Utilities;

namespace GroupSplit.App.Shared.Extensions;

public static class ColorExtensions
{
    extension(MudColor color)
    {
        public StyleHelper ColorStyle => color.Style("color");
        public StyleHelper BackgroundColorStyle => color.Style("background-color");

        private StyleHelper Style(string name)
        {
            return new StyleHelper($"{name}: {color};");
        }
    }
}