namespace GroupSplit.App.Shared.Extensions;

public static class StyleHelperExtensions
{
    extension(StyleHelper)
    {
        public static StyleHelper New(string key, string value)
        {
            return new StyleHelper($"{key}: {value};");
        }


        public static StyleHelper NewColor(string value)
        {
            return StyleHelper.New("color", value);
        }

        public static StyleHelper NewBackgroundColor(string value)
        {
            return StyleHelper.New("background-color", value);
        }
    }
}