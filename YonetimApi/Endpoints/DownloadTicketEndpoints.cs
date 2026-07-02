using System.Text.Json;
using YonetimApi.Services;

namespace YonetimApi.Endpoints;

// İnce proxy: ticket'ın kendisi (oluşturma + tüketme) artık FileServiceApi'de
// yaşıyor (bkz. FileServiceApi/Endpoints/DownloadTicketEndpoints.cs,
// /internal/download-tickets ve /internal/download-tickets/{ticket}/consume).
// YonetimApi burada sadece iki şeyi yapar:
//   1. Normal cookie auth + RBAC (CanReadAsync) + fileId-ownership kontrolü.
//   2. FileServiceApi'ye servis token'ıyla proxy.
// FileServiceApi'nin dışa hiç açılmaması kuralı korunuyor — Gateway/istemci
// FileServiceApi'yi hâlâ hiç görmüyor, her şey bu proxy üzerinden geçiyor.
public static class DownloadTicketEndpoints
{
    public static void MapDownloadTicketEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/personnel");

        // Ticket oluşturma — normal cookie auth + RBAC gerektirir.
        group.MapPost("/{personnelId}/files/{fileId}/download-ticket",
            (string personnelId, Guid fileId, HttpRequest req, IDomainAuditService a, IPermissionService p, IHttpClientFactory f, ITokenService t) =>
                CreateTicketAsync(personnelId, fileId, req, a, p, f, t))
            .RequireAuthorization();

        // Ticket tüketme — kimlik doğrulaması yok, ticket'ın kendisi yetkidir.
        group.MapGet("/download/{ticket}",
            (string ticket, HttpContext ctx, IDomainAuditService a, IHttpClientFactory f, ITokenService t) =>
                ConsumeTicketAsync(ticket, ctx, a, f, t))
            .AllowAnonymous();
    }

    // ─── TICKET OLUŞTURMA ────────────────────────────────────────────────────
    private static async Task<IResult> CreateTicketAsync(
        string personnelId, Guid fileId, HttpRequest request,
        IDomainAuditService audit, IPermissionService perm,
        IHttpClientFactory httpClientFactory, ITokenService tokenService)
    {
        var actor = request.HttpContext.User.FindFirst("preferred_username")?.Value
                    ?? request.HttpContext.User.FindFirst("sub")?.Value
                    ?? "anonymous";
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();

        if (!await perm.CanReadAsync(request.HttpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelDownloadTicketCreated", "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "access_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");

        // fileId gerçekten bu personnelId'ye ait mi — PersonnelEndpoints ile aynı paylaşılan kontrol.
        if (!await PersonnelEndpoints.FileBelongsToPersonnelAsync(client, tokenService, personnelId, fileId, actor, correlationId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelDownloadTicketCreated", "denied", "file_scope_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "file_scope_denied" }, statusCode: 403);
        }

        var serviceToken = await tokenService.GetServiceTokenAsync();
        var ticketReq = new HttpRequestMessage(HttpMethod.Post, $"internal/download-tickets?fileId={fileId}");
        ticketReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        ticketReq.Headers.Add("X-Actor-User-Id", actor);
        ticketReq.Headers.Add("X-Correlation-Id", correlationId);

        var resp = await client.SendAsync(ticketReq);
        if (!resp.IsSuccessStatusCode)
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelDownloadTicketCreated", "error", null, correlationId);
            return Results.Json(new { error = "upstream_error" }, statusCode: (int)resp.StatusCode);
        }

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var rawTicket = body.GetProperty("ticket").GetString()!;
        var expiresInSeconds = body.GetProperty("expiresInSeconds").GetInt32();

        await audit.WriteAsync(personnelId, actor, "PersonnelDownloadTicketCreated", "success", null, correlationId);

        return Results.Ok(new
        {
            ticket = rawTicket,
            expiresInSeconds,
            downloadUrl = $"/api/personnel/download/{rawTicket}"
        });
    }

    // ─── TICKET TÜKETME (STREAM) ─────────────────────────────────────────────
    private static async Task ConsumeTicketAsync(
        string ticket, HttpContext httpContext,
        IDomainAuditService audit, IHttpClientFactory httpClientFactory, ITokenService tokenService)
    {
        var response = httpContext.Response;
        var correlationId = Guid.NewGuid().ToString();

        var client = httpClientFactory.CreateClient("FileService");
        var serviceToken = await tokenService.GetServiceTokenAsync();

        var consumeReq = new HttpRequestMessage(HttpMethod.Get, $"internal/download-tickets/{Uri.EscapeDataString(ticket)}/consume");
        consumeReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        consumeReq.Headers.Add("X-Correlation-Id", correlationId);
        // Not: ticket tek kullanımlıktır — bu, Range header'ı olsa bile TEK bir HTTP
        // isteğiyle sınırlıdır. Aynı ticket'la ikinci bir Range isteği (örn. video/PDF
        // parça parça okuma) 404 döner. V1'de "lease" (birden fazla Range isteğine izin
        // veren süreli oturum) yok — bilinçli karar, bkz. proof/download-ticket-sistemi.md.
        if (httpContext.Request.Headers.TryGetValue("Range", out var range))
            consumeReq.Headers.TryAddWithoutValidation("Range", range.ToString());
        if (httpContext.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
            consumeReq.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch.ToString());

        var resp = await client.SendAsync(consumeReq, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode = (int)resp.StatusCode;

        // Audit burada domain seviyesinde: FileServiceApi zaten kendi
        // files.audit_events'ine teknik ticket_consume kaydını yazıyor
        // (iki katmanlı audit — bkz. file-service-api-contract.md).
        var domainResult = resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotModified
            ? "success" : resp.StatusCode == System.Net.HttpStatusCode.NotFound ? "denied" : "error";
        await audit.WriteAsync("UNKNOWN", "anonymous", "PersonnelDownloadTicketConsumed", domainResult, null, correlationId);

        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotModified)
            return;

        if (resp.Content.Headers.ContentType is not null)
            response.ContentType = resp.Content.Headers.ContentType.ToString();
        if (resp.Content.Headers.ContentLength.HasValue)
            response.ContentLength = resp.Content.Headers.ContentLength.Value;
        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var cd))
            response.Headers["Content-Disposition"] = cd.ToArray();
        if (resp.Headers.TryGetValues("ETag", out var etag))
            response.Headers["ETag"] = etag.ToArray();
        if (resp.Content.Headers.TryGetValues("Content-Range", out var cr))
            response.Headers["Content-Range"] = cr.ToArray();
        response.Headers["Accept-Ranges"] = "bytes";

        await resp.Content.CopyToAsync(response.Body);
    }
}
