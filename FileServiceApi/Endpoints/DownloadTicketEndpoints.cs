using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using FileServiceApi.Data;
using FileServiceApi.Models;
using FileServiceApi.Services;

namespace FileServiceApi.Endpoints;

// Ticket yaşam döngüsü (oluşturma + tüketme) burada, dosya kataloğuyla aynı
// serviste yaşıyor — hedef konum file-service düzeltme talebindeki
// `/internal/download-tickets` ve `/internal/download-tickets/{ticket}/consume`
// ile birebir. Bu aşamada çağıran hâlâ YonetimApi/FlotaApi (servis token'ıyla,
// mevcut AppCaller güven sınırı) — Gateway'in doğrudan tüketmesi ve
// X-Accel-Redirect ile byte'ın nginx'ten servis edilmesi bilinçli olarak
// ayrı bir aşamaya bırakıldı (bkz. PROJECT_STATUS.md).
public static class DownloadTicketEndpoints
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    public static void MapDownloadTicketEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/download-tickets").RequireAuthorization();

        group.MapPost("", CreateTicketAsync);
        group.MapGet("/{ticket}/consume", ConsumeTicketAsync);
    }

    // ─── OLUŞTURMA ───────────────────────────────────────────────────────────
    private static async Task<IResult> CreateTicketAsync(
        HttpRequest request, Guid fileId,
        AppDbContext db, AuditService audit)
    {
        var appCode = FileEndpoints.ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();
        var clientIp = request.Headers["X-Client-IP"].FirstOrDefault();
        var userAgent = request.Headers["User-Agent"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(fileId, "unknown", actor, "ticket_create", "denied", "unauthenticated", correlationId, clientIp, userAgent);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);
        if (policy is null || !policy.CanRead)
        {
            await audit.WriteAsync(fileId, appCode, actor, "ticket_create", "denied", "policy_denied", correlationId, clientIp, userAgent);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var fileObject = await db.Objects.FindAsync(fileId);
        if (fileObject is null || fileObject.Status != "active")
        {
            await audit.WriteAsync(fileId, appCode, actor, "ticket_create", "not_found", "object_unavailable", correlationId, clientIp, userAgent);
            return Results.NotFound();
        }

        if (!policy.AllowedDomains.Contains(fileObject.Domain))
        {
            await audit.WriteAsync(fileId, appCode, actor, "ticket_create", "denied", "policy_denied", correlationId, clientIp, userAgent);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var rawBytes = RandomNumberGenerator.GetBytes(32); // 256-bit
        var rawTicket = Convert.ToBase64String(rawBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ticketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawTicket))).ToLowerInvariant();
        var expiresAt = DateTime.UtcNow.Add(TicketLifetime);

        db.DownloadTickets.Add(new DownloadTicket
        {
            TicketHash = ticketHash,
            FileId = fileId,
            AppCode = appCode,
            Actor = actor,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await audit.WriteAsync(fileId, appCode, actor, "ticket_create", "success", null, correlationId, clientIp, userAgent);

        return Results.Ok(new { ticket = rawTicket, expiresInSeconds = (int)TicketLifetime.TotalSeconds });
    }

    // ─── TÜKETME (STREAM) ────────────────────────────────────────────────────
    private static async Task<IResult> ConsumeTicketAsync(
        string ticket, HttpRequest request, HttpResponse response,
        AppDbContext db, AuditService audit, IConfiguration config)
    {
        var appCode = FileEndpoints.ExtractAppCode(request.HttpContext.User) ?? "unknown";
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var clientIp = request.Headers["X-Client-IP"].FirstOrDefault();
        var userAgent = request.Headers["User-Agent"].FirstOrDefault();
        var ticketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();

        // Atomik tek-kullanım: raw SQL UPDATE...RETURNING, EF change-tracking'in
        // araya girmesini önlemek için ExecuteSqlInterpolatedAsync yerine doğrudan
        // ADO.NET komutu kullanılıyor (EF Core'un DownloadTickets DbSet'i sadece
        // Create tarafında kullanılıyor).
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        Guid fileId; string ticketAppCode; string? actor;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE files.download_tickets SET used_at = now() " +
                "WHERE ticket_hash = @hash AND used_at IS NULL AND expires_at > now() " +
                "RETURNING file_id, app_code, actor";
            var p = cmd.CreateParameter();
            p.ParameterName = "@hash";
            p.Value = ticketHash;
            cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await reader.DisposeAsync();
                await WriteConsumeDenialAuditAsync(connection, audit, ticketHash, correlationId, clientIp, userAgent);
                return Results.NotFound();
            }
            fileId = reader.GetFieldValue<Guid>(0);
            ticketAppCode = reader.GetString(1);
            actor = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var fileObject = await db.Objects.FindAsync(fileId);
        if (fileObject is null || fileObject.Status != "active")
        {
            await audit.WriteAsync(fileId, ticketAppCode, actor, "ticket_consume", "not_found", "object_unavailable", correlationId, clientIp, userAgent);
            return Results.NotFound();
        }

        await audit.WriteAsync(fileId, ticketAppCode, actor, "ticket_consume", "success", null, correlationId, clientIp, userAgent);

        return await FileEndpoints.StreamContentAsync(request, response, fileObject, audit, config, appCode, actor, correlationId, clientIp, userAgent);
    }

    private static async Task WriteConsumeDenialAuditAsync(
        System.Data.Common.DbConnection connection, AuditService audit, string ticketHash,
        string? correlationId, string? clientIp, string? userAgent)
    {
        // UPDATE 0 satır etkiledi — teşhis amaçlı ayrı, salt-okunur bir SELECT'le
        // ret nedenini (yok / süresi dolmuş / zaten tüketilmiş) buluyoruz.
        Guid? diagFileId = null;
        var diagAppCode = "unknown";
        var reasonCode = "ticket_not_found";

        await using (var diagCmd = connection.CreateCommand())
        {
            diagCmd.CommandText = "SELECT file_id, app_code, used_at, expires_at FROM files.download_tickets WHERE ticket_hash = @hash";
            var p = diagCmd.CreateParameter();
            p.ParameterName = "@hash";
            p.Value = ticketHash;
            diagCmd.Parameters.Add(p);

            await using var diagReader = await diagCmd.ExecuteReaderAsync();
            if (await diagReader.ReadAsync())
            {
                diagFileId = diagReader.GetFieldValue<Guid>(0);
                diagAppCode = diagReader.GetString(1);
                var usedAt = diagReader.IsDBNull(2) ? (DateTime?)null : diagReader.GetFieldValue<DateTime>(2);
                var expiresAt = diagReader.GetFieldValue<DateTime>(3);
                reasonCode = usedAt is not null ? "ticket_already_used"
                    : expiresAt <= DateTime.UtcNow ? "ticket_expired"
                    : "ticket_race_lost";
            }
        }

        await audit.WriteAsync(diagFileId, diagAppCode, null, "ticket_consume", "denied", reasonCode, correlationId, clientIp, userAgent);
    }
}
