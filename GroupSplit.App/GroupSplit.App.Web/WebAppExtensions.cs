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
            
            // group.RequireAuthorization(); // Uncomment to require authorization

            group.MapForwarder("{*path}","https+http://api", new ForwarderRequestConfig(), b =>
            {
                b.AddRequestTransform(async requestTransformContext =>
                {
                    if (requestTransformContext.Path.StartsWithSegments("/api", out var other))
                    {
                        requestTransformContext.Path = other;
                    }
                    
                    // Do requests transformations as needed here.
                    await ValueTask.CompletedTask;
                });
            });
            
            return group;
        }
    }
}