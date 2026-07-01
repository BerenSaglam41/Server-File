using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace OpsApi.Infrastructure;

// Authenticated kullanıcı ops rolü olmadan /ops/* erişmeye çalışırsa
// 403 yerine 404 döner — URL varlığı gizlenir (security through indistinguishability)
public sealed class OpsForbidToNotFoundHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext ctx,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult result)
    {
        if (result.Forbidden && ctx.Request.Path.StartsWithSegments("/ops"))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"error\":\"not_found\"}");
            return;
        }

        // Diğer durumlar (Challenge/Success) varsayılan akışa devredilir
        if (result.Challenged)
        {
            await ctx.ChallengeAsync();
            return;
        }

        await next(ctx);
    }
}
