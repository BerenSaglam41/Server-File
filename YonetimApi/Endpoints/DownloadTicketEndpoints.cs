using System.Text.Json;
using YonetimApi.Services;

namespace YonetimApi.Endpoints;

// Ticket oluşturma burada — normal cookie auth + RBAC (CanReadAsync) + fileId-
// ownership kontrolü sonrası FileServiceApi'ye servis token'ıyla proxy.
//
// Ticket TÜKETME artık burada değil — client, ticket'ı doğrudan Gateway'in
// `/files/download/{ticket}` yoluna götürür; Gateway (mTLS, CN=gateway) ile
// FileServiceApi'yi çağırır, dönen X-Accel-Redirect ile byte'ı kendi salt-okunur
// mount'undan servis eder. YonetimApi/FileServiceApi artık bu byte'ı hiç
// görmüyor (bkz. nginx/nginx.conf, FileServiceApi/Endpoints/DownloadTicketEndpoints.cs).
public static class DownloadTicketEndpoints
{
    public static void MapDownloadTicketEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/personnel");

        group.MapPost("/{personnelId}/files/{fileId}/download-ticket",
            (string personnelId, Guid fileId, HttpRequest req, IDomainAuditService a, IPermissionService p, IHttpClientFactory f, ITokenService t) =>
                CreateTicketAsync(personnelId, fileId, req, a, p, f, t))
            .RequireAuthorization();
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
        if (await PersonnelEndpoints.FileBelongsToPersonnelAsync(client, tokenService, personnelId, fileId, actor, correlationId) is null)
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
            downloadUrl = $"/files/download/{rawTicket}"
        });
    }
}
