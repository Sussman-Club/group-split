using System.Net.Http.Headers;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

#pragma warning disable ASP0018

namespace GroupSplit.App.Web;

public static class WebAppExtensions
{
    extension(IEndpointRouteBuilder app)
    {
        public RouteGroupBuilder MapApiForwarder()
        {
            var group = app.MapGroup("/api");
            
            group.RequireAuthorization();

            group.MapForwarder("{*path}","https+http://api", new ForwarderRequestConfig(), b =>
            {
                b.AddRequestTransform(async requestTransformContext =>
                {
                    if (requestTransformContext.Path.StartsWithSegments("/api", out var other))
                    {
                        requestTransformContext.Path = other;
                    }

                    var tokenRefreshService = requestTransformContext.HttpContext.RequestServices.GetRequiredService<TokenRefreshService>();
                    var accessToken = await tokenRefreshService.GetValidAccessTokenAsync(requestTransformContext.HttpContext, requestTransformContext.CancellationToken);

                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        await requestTransformContext.HttpContext.SignOutAsync();
                        return;
                    }

                    requestTransformContext.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                });
            });
            
            return group;
        }
    }
}