using GroupSplit.App.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GroupSplit.App.Web;

public static class RenderModeConfig
{
    public static IComponentRenderMode Current { get; private set; } = null!;

    public static bool IsDevelopment { get; private set; }

    public static void Initialize(bool isDevelopment)
    {
        IsDevelopment = isDevelopment;
        Current = isDevelopment
            ? RenderMode.InteractiveServer
            : RenderMode.InteractiveWebAssembly;
    }

    public static IRazorComponentsBuilder AddRenderModeComponents(this IRazorComponentsBuilder builder)
    {
        builder.AddInteractiveServerComponents();

        if (!IsDevelopment) builder.AddInteractiveWebAssemblyComponents();

        return builder;
    }

    public static RazorComponentsEndpointConventionBuilder MapRenderMode(
        this RazorComponentsEndpointConventionBuilder builder,
        WebApplication app)
    {
        builder.AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(_Imports).Assembly);

        if (!IsDevelopment)
        {
            builder.AddInteractiveWebAssemblyRenderMode()
                .AddAdditionalAssemblies(typeof(Client._Imports).Assembly);

            app.UseExceptionHandler("/Error", true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        return builder;
    }
}