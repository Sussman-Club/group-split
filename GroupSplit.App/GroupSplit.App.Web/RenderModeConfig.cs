using GroupSplit.App.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GroupSplit.App.Web;

public enum RenderModePreference
{
    ServerFirst,
    WebAssemblyFirst
}

public static class RenderModeConfig
{
    public static IComponentRenderMode Current { get; private set; } = null!;
    private static RenderModePreference Preference { get; set; }

    public static void Initialize(RenderModePreference preference)
    {
        Preference = preference;
        Current = preference switch
        {
            RenderModePreference.ServerFirst => RenderMode.InteractiveServer,
            RenderModePreference.WebAssemblyFirst => RenderMode.InteractiveWebAssembly,
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null)
        };
    }

    public static IRazorComponentsBuilder AddRenderModeComponents(this IRazorComponentsBuilder builder)
    {
        switch (Preference)
        {
            case RenderModePreference.ServerFirst:
                builder.AddInteractiveServerComponents();
                break;
            case RenderModePreference.WebAssemblyFirst:
                builder.AddInteractiveWebAssemblyComponents();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return builder;
    }

    public static RazorComponentsEndpointConventionBuilder MapRenderMode(
        this RazorComponentsEndpointConventionBuilder builder,
        WebApplication app)
    {
        switch (Preference)
        {
            case RenderModePreference.ServerFirst:
                builder
                    .AddInteractiveServerRenderMode()
                    .AddAdditionalAssemblies(typeof(_Imports).Assembly);
                break;
            case RenderModePreference.WebAssemblyFirst:
                builder
                    .AddInteractiveWebAssemblyRenderMode()
                    .AddAdditionalAssemblies(typeof(Client._Imports).Assembly);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return builder;
    }
}