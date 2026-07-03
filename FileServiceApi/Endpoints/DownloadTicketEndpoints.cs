using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using FileServiceApi.Data;
using FileServiceApi.Models;
using FileServiceApi.Services;

namespace FileServiceApi.Endpoints;

// Ticket yaşam döngüsü (oluşturma + tüketme) burada, dosya kataloğuyla aynı
// serviste yaşıyor — hedef konum file-service düzeltme talebindeki
// `/internal/download-tickets` ve `/internal/download-tickets/{ticket}/consume`
// ile birebir.
//
// Oluşturma (POST) hâlâ normal AppCaller güven sınırında — mTLS + JWT service
// token gerektirir (YonetimApi/FlotaApi).
//
// Tüketme (GET .../consume) artık SADECE Gateway tarafından, SADECE mTLS ile
// (CN=gateway, JWT gerekmez) çağrılır ve byte'ı kendisi okumak yerine
// X-Accel-Redirect header'ı döner — nginx'in internal location'ı Files-01'in
// salt-okunur mount'undan doğrudan servis eder. Bu istisna bilinçli: ticket
// zaten oluşturulurken YonetimApi'nin RBAC kontrolünden geçmiş, kısa ömürlü,
// süre+sayı sınırlı (lease) — consume anında ek bir JWT/app_code kontrolüne
// ihtiyaç yok, mTLS kimliği (CN=gateway) + ticket'ın kendi geçerliliği yeterli
// güvence. Diğer tüm /internal/* endpoint'ler hem mTLS hem JWT istemeye devam ediyor.
public static class DownloadTicketEndpoints
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);
    // Lease modeli: ilk tüketimden sonra ticket hemen ölmüyor — LeaseDuration kadar
    // daha, MaxUsesPerTicket'a kadar ek isteğe (özellikle Range — video/büyük PDF
    // seeking) izin veriyor. S3 presigned URL / Google Signed URL'lerin de yaptığı
    // gibi süre + sayı bazlı sınırlı bir model; sonsuz/kalıcı bir link değil.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private const int MaxUsesPerTicket = 20;
    private const string GatewayCn = "gateway";

    public static void MapDownloadTicketEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/download-tickets");

        group.MapPost("", CreateTicketAsync).RequireAuthorization();

        // AllowAnonymous: JWT bearer şeması burada aranmaz, kimlik doğrulaması
        // tamamen mTLS istemci sertifikası (Kestrel seviyesinde zaten CA'ya karşı
        // doğrulanmış) üzerinden, handler içinde CN kontrolüyle yapılır.
        group.MapGet("/{ticket}/consume", ConsumeTicketAsync).AllowAnonymous();
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

    // ─── TÜKETME (X-Accel-Redirect) ──────────────────────────────────────────
    private static async Task<IResult> ConsumeTicketAsync(
        string ticket, HttpRequest request, HttpResponse response,
        AppDbContext db, AuditService audit)
    {
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var clientIp = request.Headers["X-Client-IP"].FirstOrDefault();
        var userAgent = request.Headers["User-Agent"].FirstOrDefault();

        // mTLS-only kimlik doğrulama: Kestrel bu bağlantıyı zaten CA'ya karşı
        // doğruladı (RequireCertificate + CustomRootTrust) — burada sadece CN'in
        // gerçekten "gateway" olduğunu kontrol ediyoruz. JWT aranmıyor.
        var clientCert = request.HttpContext.Connection.ClientCertificate;
        var cn = clientCert?.GetNameInfo(X509NameType.SimpleName, false);
        if (!string.Equals(cn, GatewayCn, StringComparison.OrdinalIgnoreCase))
        {
            await audit.WriteAsync(null, cn ?? "unknown", null, "ticket_consume", "denied", "gateway_cn_required", correlationId, clientIp, userAgent);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }
        var appCode = GatewayCn;
        var ticketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();

        // Atomik tek-kullanım: raw SQL UPDATE...RETURNING, EF change-tracking'in
        // araya girmesini önlemek için ExecuteSqlInterpolatedAsync yerine doğrudan
        // ADO.NET komutu kullanılıyor (EF Core'un DownloadTickets DbSet'i sadece
        // Create tarafında kullanılıyor).
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        Guid fileId; string ticketAppCode; string? actor; int useCount;
        await using (var cmd = connection.CreateCommand())
        {
            // Lease mantığı tek atomik UPDATE'te: ilk kullanım (used_at IS NULL) hâlâ
            // orijinal kısa ömür (expires_at) penceresinde olmalı; sonraki kullanımlar
            // (used_at zaten set) lease penceresinde (used_at + LeaseDuration) olmalı.
            // Her iki durumda da use_count sınırının altında kalınmalı. Eşzamanlı "ilk
            // kullanım" denemeleri Postgres'in satır kilidiyle doğal olarak sıraya girer;
            // kaybeden istek used_at'i zaten set görüp otomatik olarak lease koluna düşer.
            cmd.CommandText =
                "UPDATE files.download_tickets SET used_at = COALESCE(used_at, now()), use_count = use_count + 1 " +
                "WHERE ticket_hash = @hash AND use_count < @maxUses AND (" +
                "  (used_at IS NULL AND expires_at > now())" +
                "  OR (used_at IS NOT NULL AND now() < used_at + (@leaseSeconds * INTERVAL '1 second'))" +
                ") RETURNING file_id, app_code, actor, use_count";
            var pHash = cmd.CreateParameter();
            pHash.ParameterName = "@hash";
            pHash.Value = ticketHash;
            cmd.Parameters.Add(pHash);
            var pMaxUses = cmd.CreateParameter();
            pMaxUses.ParameterName = "@maxUses";
            pMaxUses.Value = MaxUsesPerTicket;
            cmd.Parameters.Add(pMaxUses);
            var pLeaseSeconds = cmd.CreateParameter();
            pLeaseSeconds.ParameterName = "@leaseSeconds";
            pLeaseSeconds.Value = LeaseDuration.TotalSeconds;
            cmd.Parameters.Add(pLeaseSeconds);

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
            useCount = reader.GetInt32(3);
        }

        var fileObject = await db.Objects.FindAsync(fileId);
        if (fileObject is null || fileObject.Status != "active")
        {
            await audit.WriteAsync(fileId, ticketAppCode, actor, "ticket_consume", "not_found", "object_unavailable", correlationId, clientIp, userAgent);
            return Results.NotFound();
        }

        // İlk kullanımda reasonCode boş bırakılır (mevcut davranış); lease
        // kapsamındaki tekrar kullanımlar "lease_use_N" ile ayırt edilebilir hale gelir.
        var leaseReasonCode = useCount > 1 ? $"lease_use_{useCount}" : null;
        await audit.WriteAsync(fileId, ticketAppCode, actor, "ticket_consume", "success", leaseReasonCode, correlationId, clientIp, userAgent);

        // Byte'ı burada okumuyoruz — nginx'in internal location'ına X-Accel-Redirect
        // ile yönlendiriyoruz, o Files-01'in salt-okunur mount'undan servis ediyor.
        var etag = $"\"sha256:{fileObject.Sha256}\"";
        var ifNoneMatch = request.Headers["If-None-Match"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "success", "not_modified", correlationId, clientIp, userAgent);
            return Results.StatusCode(304);
        }

        var rawName = string.IsNullOrEmpty(fileObject.OriginalFileName)
            ? $"file.{fileObject.Extension}"
            : fileObject.OriginalFileName;
        var asciiFallback = new string(rawName.Select(c => (c < 128 && c != '"' && c != '\\') ? c : '_').ToArray());
        var encodedName = Uri.EscapeDataString(rawName);
        // Bu endpoint SADECE ticket tabanlı "İndir" akışı için var (bkz. FileCard.tsx handleDownload) —
        // önizleme/inline görüntüleme amacı yok. FileEndpoints.StreamContentAsync'teki resimler için
        // "inline" istisnası (o, /content endpoint'inin önizleme amacı için doğru) buraya kopyalanmıştı,
        // bu yüzden fotoğraf indirmeleri tarayıcıda açılıyor, indirilmiyordu — kaldırıldı.
        var disposition = "attachment";

        response.Headers["ETag"] = etag;
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Content-Disposition"] = $"{disposition}; filename=\"{asciiFallback}\"; filename*=UTF-8''{encodedName}";
        response.Headers["Content-Type"] = fileObject.ContentType;
        response.Headers["X-Accel-Redirect"] = $"/protected-download/{fileObject.RelativePath}";

        await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "success", null, correlationId, clientIp, userAgent);

        return Results.StatusCode(200);
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
            diagCmd.CommandText = "SELECT file_id, app_code, used_at, expires_at, use_count FROM files.download_tickets WHERE ticket_hash = @hash";
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
                var useCount = diagReader.GetInt32(4);

                reasonCode = useCount >= MaxUsesPerTicket ? "ticket_max_uses_reached"
                    : usedAt is null && expiresAt <= DateTime.UtcNow ? "ticket_expired"
                    : usedAt is not null && DateTime.UtcNow >= usedAt.Value.Add(LeaseDuration) ? "ticket_lease_expired"
                    : "ticket_race_lost";
            }
        }

        await audit.WriteAsync(diagFileId, diagAppCode, null, "ticket_consume", "denied", reasonCode, correlationId, clientIp, userAgent);
    }
}
