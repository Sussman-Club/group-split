using System.Reflection;
using System.Reflection.Emit;
using GroupSplit.App.Services;
using GroupSplit.App.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Web.Test.Native;

public sealed class NativeOptionsBuildTest
{
    [Theory]
    [InlineData("42", 42)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("invalid", null)]
    [InlineData(null, null)]
    public void Android_build_metadata_is_forwarded_to_shared_app_links(string? value, int? expected)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, text) in new[] {
            ("NativeApiUrl", "https://example.test/native/api/"),
            ("NativeAuthority", "https://example.test/idp/realms/group-split"),
            ("NativeWebUrl", "https://example.test/"),
            ("NativeAndroidBuild", value)
        })
            if (text is not null) assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, text]));
        var options = NativeOptions.FromAssembly(assembly, false);
        Assert.Equal(expected, options.AndroidBuild);
        var services = new ServiceCollection();
        services.AddNativeServices(options);
        using var provider = services.BuildServiceProvider();
        var links = provider.GetRequiredService<AppLinks>();
        Assert.True(links.IsNative);
        Assert.Equal(OperatingSystem.IsAndroid(), links.IsAndroidNative);
        Assert.Equal(expected, links.AndroidBuild);
        Assert.Equal(new Uri("https://example.test/"), links.PublicOrigin);
    }
}
