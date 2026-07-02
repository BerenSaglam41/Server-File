using System.Security.Cryptography;
using System.Text;
using Npgsql;
using YonetimApi.Services;

namespace YonetimApi.Endpoints;

// Opak, kısa ömürlü, tek kullanımlık indirme ticket'ı.
// Amaç: uzun ömürlü oturum cookie'sine ek olarak, tek bir dosyaya, tek seferliğine
// bağlı, hash olarak saklanan, en az 256-bit entropili bir bilet üretmek — imzalı/
// tek kullanımlık indirme linki (S3 presigned URL benzeri) deseni.
//
// FileServiceApi'nin dışa hiç açılmaması kuralı korunuyor: ticket YonetimApi'nin
// kendi proxy zincirinden geçiyor, FileServiceApi'ye hâlâ sadece mTLS+servis
// token'ı ile ulaşılıyor.
public static class DownloadTicketEndpoints
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    public static void MapDownloadTicketEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/personnel");

        // Ticket oluşturma — normal cookie auth + RBAC gerektirir.
        group.MapPost("/{personnelId}/files/{fileId}/download-ticket",
            (string personnelId, Guid fileId, HttpRequest req, NpgsqlDataSource db, IDomainAuditService a, IPermissionService p, IHttpClientFactory f, ITokenService t) =>
                CreateTicketAsync(personnelId, fileId, req, db, a, p, f, t))
            .RequireAuthorization();

        // Ticket tüketme — kimlik doğrulaması yok, ticket'ın kendisi yetkidir.
        group.MapGet("/download/{ticket}",
            (string ticket, HttpContext ctx, NpgsqlDataSource db, IDomainAuditService a, IHttpClientFactory f, ITokenService t) =>
                ConsumeTicketAsync(ticket, ctx, db, a, f, t))
            .AllowAnonymous();
    }

    // ─── TICKET OLUŞTURMA ────────────────────────────────────────────────────
    private static async Task<IResult> CreateTicketAsync(
        string personnelId, Guid fileId, HttpRequest request,
        NpgsqlDataSource db, IDomainAuditService audit, IPermissionService perm,
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

        // 256-bit (32 byte) rastgele opak ticket. Base64Url ile URL-safe.
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawTicket = Convert.ToBase64String(rawBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ticketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawTicket))).ToLowerInvariant();
        var expiresAt = DateTime.UtcNow.Add(TicketLifetime);

        await using var cmd = db.CreateCommand(
            "INSERT INTO yonetim.download_tickets (ticket_hash, personnel_id, file_id, relation_type, actor, expires_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddWithValue(ticketHash);
        cmd.Parameters.AddWithValue(personnelId);
        cmd.Parameters.AddWithValue(fileId);
        cmd.Parameters.AddWithValue("unknown"); // relation_type burada bilinmiyor, fileId bazlı erişimde önemli değil
        cmd.Parameters.AddWithValue(actor);
        cmd.Parameters.AddWithValue(expiresAt);
        await cmd.ExecuteNonQueryAsync();

        await audit.WriteAsync(personnelId, actor, "PersonnelDownloadTicketCreated", "success", null, correlationId);

        return Results.Ok(new
        {
            ticket = rawTicket,
            expiresInSeconds = (int)TicketLifetime.TotalSeconds,
            downloadUrl = $"/api/personnel/download/{rawTicket}"
        });
    }

    // ─── TICKET TÜKETME (STREAM) ─────────────────────────────────────────────
    private static async Task ConsumeTicketAsync(
        string ticket, HttpContext httpContext,
        NpgsqlDataSource db, IDomainAuditService audit,
        IHttpClientFactory httpClientFactory, ITokenService tokenService)
    {
        var response = httpContext.Response;
        var ticketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();

        // Atomik tek-kullanım: sadece used_at NULL iken güncelle, aynı anda iki
        // istek gelirse yalnızca biri satırı "kullanılmış" olarak işaretleyebilir.
        // ÖNEMLİ: UPDATE komutu ve okuyucusu kendi using bloğunda TAM olarak
        // kapatılmadan ikinci bir komut (diagnostic SELECT) açılmamalı — aksi
        // halde eşzamanlı isteklerde Npgsql "a command is already in progress"
        // hatası fırlatıyor (bu, canlı yük testiyle bulunup düzeltildi).
        var correlationId = Guid.NewGuid().ToString();
        string? personnelId = null;
        Guid fileId = Guid.Empty;
        string? actor = null;

        await using (var cmd = db.CreateCommand(
            "UPDATE yonetim.download_tickets SET used_at = now() " +
            "WHERE ticket_hash = $1 AND used_at IS NULL AND expires_at > now() " +
            "RETURNING personnel_id, file_id, actor"))
        {
            cmd.Parameters.AddWithValue(ticketHash);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                personnelId = reader.GetString(0);
                fileId = reader.GetFieldValue<Guid>(1);
                actor = reader.GetString(2);
            }
        } // cmd + reader burada tamamen kapanır, bağlantı pool'a temiz döner

        if (personnelId is null)
        {
            // Atomik UPDATE 0 satır etkiledi — neden olduğunu (hiç yok / süresi
            // dolmuş / zaten tüketilmiş) sadece audit amacıyla, TAMAMEN AYRI ve
            // yukarıdaki komut kapandıktan sonra açılan bir SELECT'le öğreniyoruz.
            // Bu SELECT tek-kullanım garantisinin bir parçası değil, yalnızca
            // teşhis/audit içindir.
            var diagPersonnelId = "UNKNOWN";
            var reasonCode = "ticket_not_found";

            await using (var diagCmd = db.CreateCommand(
                "SELECT personnel_id, used_at, expires_at FROM yonetim.download_tickets WHERE ticket_hash = $1"))
            {
                diagCmd.Parameters.AddWithValue(ticketHash);
                await using var diagReader = await diagCmd.ExecuteReaderAsync();
                if (await diagReader.ReadAsync())
                {
                    diagPersonnelId = diagReader.GetString(0);
                    var usedAt = diagReader.IsDBNull(1) ? (DateTime?)null : diagReader.GetFieldValue<DateTime>(1);
                    var expiresAt = diagReader.GetFieldValue<DateTime>(2);
                    reasonCode = usedAt is not null ? "ticket_already_used"
                        : expiresAt <= DateTime.UtcNow ? "ticket_expired"
                        : "ticket_race_lost";
                }
            }

            await audit.WriteAsync(diagPersonnelId, "anonymous", "PersonnelDownloadTicketConsumed", "denied", reasonCode, correlationId);
            response.StatusCode = 404;
            await response.WriteAsJsonAsync(new { error = "not_found" });
            return;
        }

        var resolvedActor = actor ?? "anonymous";
        var client = httpClientFactory.CreateClient("FileService");
        var serviceToken = await tokenService.GetServiceTokenAsync();

        var contentReq = new HttpRequestMessage(HttpMethod.Get, $"internal/files/{fileId}/content");
        contentReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        contentReq.Headers.Add("X-Actor-User-Id", resolvedActor);
        contentReq.Headers.Add("X-Correlation-Id", correlationId);
        // Not: ticket tek kullanımlıktır — bu, Range header'ı olsa bile TEK bir HTTP
        // isteğiyle sınırlıdır. Aynı ticket'la ikinci bir Range isteği (örn. video/PDF
        // parça parça okuma) 404 döner. V1'de "lease" (birden fazla Range isteğine izin
        // veren süreli oturum) yok — bilinçli karar, bkz. proof/download-ticket-sistemi.md.
        if (httpContext.Request.Headers.TryGetValue("Range", out var range))
            contentReq.Headers.TryAddWithoutValidation("Range", range.ToString());

        var resp = await client.SendAsync(contentReq, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode = (int)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            await audit.WriteAsync(personnelId, resolvedActor, "PersonnelDownloadTicketConsumed", "error", null, correlationId);
            return;
        }

        await audit.WriteAsync(personnelId, resolvedActor, "PersonnelDownloadTicketConsumed", "success", null, correlationId);

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
