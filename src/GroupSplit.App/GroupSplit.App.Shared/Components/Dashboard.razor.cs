using GroupSplit.App.Shared.Localization;
namespace GroupSplit.App.Shared.Components;

public partial class Dashboard
{
    /// <summary>
    /// In a code-behind file rather than the component's <c>@code</c> block because of the
    /// relational patterns: inside a Razor code block the parser can read a <c>&lt;</c> at
    /// the start of an expression as the start of a tag, and this failed to compile on
    /// Windows while passing on the CI runner. Plain C# has no such ambiguity.
    /// </summary>
    private string Greeting => Clock.Now.Hour switch
    {
        < 5 => UiText.Get("Still up"),
        < 12 => UiText.Get("Good morning"),
        < 18 => UiText.Get("Good afternoon"),
        _ => UiText.Get("Good evening")
    };
}
