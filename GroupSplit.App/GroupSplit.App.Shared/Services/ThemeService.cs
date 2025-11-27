using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.App.Shared.Services;

public static class ThemeService
{
    public static IServiceCollection AddMudTheme(this IServiceCollection services)
    {
        var theme = new MudTheme()
        {
            PaletteLight = new PaletteLight
            {
                Primary = "#22817a", // Bright natural green
                Secondary = "#F5F0E6", // Soft neutral
                Tertiary = "#CB6E3A", // Warm clay accent
                Success = "#22817a", // Green for success states
                TextPrimary = "#2A2A2A", // Deep Charcoal
                TextSecondary = "#2A2A2A", // Deep Charcoal
                Surface = "#FAF7F0", // Neutral surface
                Background = "#F5F0E6", // Light optimistic background
                BackgroundGray = "#E0D5CB", // Soft neutral
                Divider = "#E0D5CB", // Soft neutral for borders
                DrawerBackground = "#FAF7F0", // Neutral drawer background
                AppbarBackground = "#22817a", // Green app bar
                AppbarText = "#FFFFFF" // White text on green
            },
            PaletteDark = new PaletteDark
            {
                Primary = "#22817a", // Bright natural green
                Secondary = "#F5F0E6", // Soft neutral
                Tertiary = "#CB6E3A", // Warm clay accent
                Success = "#22817a", // Green for success states
                TextPrimary = "#F5F0E6", // Neutral for dark mode text
                Surface = "#2A2A2A", // Deep Charcoal for surfaces
                Background = "#1A1A1A", // Very dark background
                BackgroundGray = "#3A3A3A", // Lighter charcoal
                Divider = "#E0D5CB" // Soft neutral for borders
            }
        };

        services.AddSingleton<MudTheme>(theme);
        // Placeholder for future theme-related services
        return services;
    }
}
