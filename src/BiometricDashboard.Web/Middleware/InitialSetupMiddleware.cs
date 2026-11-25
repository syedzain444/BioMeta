using BiometricDashboard.Core.Interfaces;

namespace BiometricDashboard.Web.Middleware;

public sealed class InitialSetupMiddleware(RequestDelegate next)
{
    private static readonly PathString[] AllowedPaths =
    [
        new("/Setup"),
        new("/Account/Login"),
        new("/Account/AccessDenied"),
        new("/css"),
        new("/js"),
        new("/lib"),
        new("/favicon.ico"),
    ];

    public async Task InvokeAsync(HttpContext context, IDeviceRepository devices)
    {
        var path = context.Request.Path;
        if (AllowedPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        try
        {
            if (!await devices.IsSetupCompleteAsync(context.RequestAborted))
            {
                context.Response.Redirect("/Setup");
                return;
            }
        }
        catch
        {
            context.Response.Redirect("/Setup");
            return;
        }

        await next(context);
    }
}
