using MyNotes.Services;

namespace MyNotes.Controllers;

public static class StaticEndpointExtensions
{
    public static IEndpointRouteBuilder MapStaticAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/Master/MasterDataSystemVersion.txt", MasterVersion);
        endpoints.MapGet("/MasterDataSystemVersion.txt", MasterVersion);

        MapAndroidAssetProxy(endpoints, "/asset/Android/{**assetPath}");
        MapAndroidAssetProxy(endpoints, "/Android/{**assetPath}");

        return endpoints;
    }

    private static IResult MasterVersion(MasterDataService master) =>
        Results.Text(master.Version, "text/plain");

    private static void MapAndroidAssetProxy(IEndpointRouteBuilder endpoints, string pattern)
    {
        endpoints.MapMethods(
            pattern,
            [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, StaticAssetService assets, string assetPath) =>
                assets.ProxyAndroidAssetAsync(
                    context,
                    assetPath,
                    HttpMethods.IsHead(context.Request.Method)));
    }
}
