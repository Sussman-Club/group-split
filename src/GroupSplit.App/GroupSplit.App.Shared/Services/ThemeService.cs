using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// The MudBlazor side of the Group Split design system. The CSS tokens in
/// wwwroot/app.css restate the same palette for the custom chrome; keep the
/// two in step when changing a colour here.
/// </summary>
public static class ThemeService
{
    private static readonly string[] DisplayFont =
        ["Bricolage Grotesque", "Manrope", "system-ui", "-apple-system", "Segoe UI", "sans-serif"];

    private static readonly string[] BodyFont =
        ["Manrope", "system-ui", "-apple-system", "Segoe UI", "Helvetica Neue", "Arial", "sans-serif"];

    public static IServiceCollection AddMudTheme(this IServiceCollection services)
    {
        var theme = new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = "#22817a",
                PrimaryDarken = "#1a655f",
                PrimaryLighten = "#3aa79e",
                PrimaryContrastText = "#ffffff",
                Secondary = "#6b6459",
                SecondaryContrastText = "#ffffff",
                Tertiary = "#cb6e3a",
                TertiaryContrastText = "#ffffff",
                Success = "#22817a",
                Warning = "#c58a2a",
                Error = "#b3402f",
                Info = "#2f6f9e",
                TextPrimary = "#23211d",
                TextSecondary = "#6b6459",
                TextDisabled = "rgba(35,33,29,0.38)",
                ActionDefault = "#6b6459",
                ActionDisabled = "rgba(35,33,29,0.26)",
                ActionDisabledBackground = "rgba(35,33,29,0.08)",
                Surface = "#fbf8f1",
                Background = "#f5f0e6",
                BackgroundGray = "#ece5d8",
                Divider = "#e3d9cc",
                DividerLight = "#ede5d9",
                LinesDefault = "#e3d9cc",
                LinesInputs = "#cfc3b3",
                TableLines = "#ece5d8",
                TableStriped = "rgba(34,129,122,0.03)",
                TableHover = "rgba(34,129,122,0.06)",
                DrawerBackground = "#fbf8f1",
                DrawerText = "#23211d",
                DrawerIcon = "#6b6459",
                AppbarBackground = "#fbf8f1",
                AppbarText = "#23211d",
                HoverOpacity = 0.06,
                RippleOpacity = 0.08
            },
            PaletteDark = new PaletteDark
            {
                Primary = "#3aa79e",
                PrimaryDarken = "#22817a",
                PrimaryLighten = "#5fc4bb",
                PrimaryContrastText = "#0f1a19",
                Secondary = "#a89f92",
                SecondaryContrastText = "#161512",
                Tertiary = "#e08a5a",
                TertiaryContrastText = "#1a120c",
                Success = "#3aa79e",
                Warning = "#d9a04a",
                Error = "#e06a57",
                Info = "#5d9bd0",
                TextPrimary = "#f3ede2",
                TextSecondary = "#a89f92",
                TextDisabled = "rgba(243,237,226,0.38)",
                ActionDefault = "#a89f92",
                ActionDisabled = "rgba(243,237,226,0.26)",
                ActionDisabledBackground = "rgba(243,237,226,0.10)",
                Surface = "#1f1d19",
                Background = "#151412",
                BackgroundGray = "#26231f",
                Divider = "#312d27",
                DividerLight = "#2a2723",
                LinesDefault = "#312d27",
                LinesInputs = "#4a4438",
                TableLines = "#2a2723",
                TableStriped = "rgba(58,167,158,0.04)",
                TableHover = "rgba(58,167,158,0.08)",
                DrawerBackground = "#1f1d19",
                DrawerText = "#f3ede2",
                DrawerIcon = "#a89f92",
                AppbarBackground = "#1f1d19",
                AppbarText = "#f3ede2",
                HoverOpacity = 0.08,
                RippleOpacity = 0.10
            },
            Typography = new Typography
            {
                Default = new DefaultTypography
                {
                    FontFamily = BodyFont,
                    FontSize = "0.9375rem",
                    FontWeight = "500",
                    LineHeight = "1.5",
                    LetterSpacing = "0"
                },
                H1 = new H1Typography { FontFamily = DisplayFont, FontSize = "3.5rem", FontWeight = "700", LineHeight = "1.05", LetterSpacing = "-0.02em" },
                H2 = new H2Typography { FontFamily = DisplayFont, FontSize = "2.75rem", FontWeight = "700", LineHeight = "1.1", LetterSpacing = "-0.02em" },
                H3 = new H3Typography { FontFamily = DisplayFont, FontSize = "2.125rem", FontWeight = "700", LineHeight = "1.15", LetterSpacing = "-0.015em" },
                H4 = new H4Typography { FontFamily = DisplayFont, FontSize = "1.75rem", FontWeight = "700", LineHeight = "1.2", LetterSpacing = "-0.01em" },
                H5 = new H5Typography { FontFamily = DisplayFont, FontSize = "1.375rem", FontWeight = "700", LineHeight = "1.25", LetterSpacing = "-0.01em" },
                H6 = new H6Typography { FontFamily = DisplayFont, FontSize = "1.125rem", FontWeight = "700", LineHeight = "1.3", LetterSpacing = "0" },
                Subtitle1 = new Subtitle1Typography { FontFamily = BodyFont, FontSize = "1rem", FontWeight = "700", LineHeight = "1.5", LetterSpacing = "0" },
                Subtitle2 = new Subtitle2Typography { FontFamily = BodyFont, FontSize = "0.875rem", FontWeight = "700", LineHeight = "1.5", LetterSpacing = "0" },
                Body1 = new Body1Typography { FontFamily = BodyFont, FontSize = "0.9375rem", FontWeight = "500", LineHeight = "1.55", LetterSpacing = "0" },
                Body2 = new Body2Typography { FontFamily = BodyFont, FontSize = "0.875rem", FontWeight = "500", LineHeight = "1.5", LetterSpacing = "0" },
                Button = new ButtonTypography { FontFamily = BodyFont, FontSize = "0.875rem", FontWeight = "700", LineHeight = "1.5", LetterSpacing = "0", TextTransform = "none" },
                Caption = new CaptionTypography { FontFamily = BodyFont, FontSize = "0.75rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "0.01em" },
                Overline = new OverlineTypography { FontFamily = BodyFont, FontSize = "0.6875rem", FontWeight = "800", LineHeight = "1.4", LetterSpacing = "0.14em", TextTransform = "uppercase" }
            },
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = "12px"
            }
        };

        services.AddSingleton(theme);
        return services;
    }
}
