namespace GroupSplit.App;

using Microsoft.Extensions.DependencyInjection;

public partial class App : Application
{
    private readonly IServiceProvider services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        this.services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Resolve the page after InitializeComponent has loaded application resources.
        return new Window(services.GetRequiredService<MainPage>()) { Title = "Group Split" };
    }

    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        services.GetRequiredService<Services.NativeLinkRouter>().Open(uri);
        base.OnAppLinkRequestReceived(uri);
    }
}
