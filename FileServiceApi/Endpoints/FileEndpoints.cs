using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using FileServiceApi.Data;
using FileServiceApi.Services;

namespace FileServiceApi.Endpoints;

public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        // Tüm /internal/files endpoint'leri geçerli JWT Bearer gerektirir.
        // app_code JWT'nin app_code claim'inden okunur (X-App-Code header artık kullanılmıyor).
        var group = app.MapGroup("/internal/files").RequireAuthorization();

        group.MapGet("/resolve", ResolveAsync);
        group.MapGet("/list", ListFilesAsync);
        group.MapGet("/{fileId}/content", GetContentAsync);
        group.MapGet("/{fileId}", GetMetadataAsync);
        group.MapPost("", CreateFileAsync);
        group.MapPost("/{fileId}/archive", ArchiveFileAsync);
    }

    private static string? ExtractAppCode(ClaimsPrincipal user) =>
        user.FindFirst("app_code")?.Value ?? user.FindFirst("azp")?.Value;

    // ─── RESOLVE ─────────────────────────────────────────────────────────────
    private static async Task<IResult> ResolveAsync(
        HttpRequest request,
        string domain,
        string entityType,
        string entityId,
        string relationType,
        AppDbContext db,
        AuditService audit)
    {
        var appCode = ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(null, "unknown", actor, "read", "denied", "unauthenticated", correlationId);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);

        if (policy is null)
        {
            await audit.WriteAsync(null, appCode, actor, "read", "denied", "app_code_unknown", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        if (!policy.CanRead || !policy.AllowedDomains.Contains(domain) || !policy.AllowedFileTypes.Contains(relationType))
        {
            await audit.WriteAsync(null, appCode, actor, "read", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var reference = await db.References
            .Where(r => r.EntityType == entityType
                     && r.EntityId == entityId
                     && r.RelationType == relationType
                     && r.IsPrimary
                     && r.Status == "active")
            .FirstOrDefaultAsync();

        if (reference is null)
        {
            await audit.WriteAsync(null, appCode, actor, "read", "not_found", "reference_not_found", correlationId);
            return Results.NotFound();
        }

        var fileObject = await db.Objects
            .Where(o => o.FileId == reference.FileId && o.Domain == domain)
            .FirstOrDefaultAsync();

        if (fileObject is null || fileObject.Status != "active")
        {
            await audit.WriteAsync(null, appCode, actor, "read", "not_found", "object_unavailable", correlationId);
            return Results.NotFound();
        }

        await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "success", null, correlationId);

        return Results.Ok(new
        {
            fileId = fileObject.FileId,
            domain = fileObject.Domain,
            relationType = reference.RelationType,
            contentType = fileObject.ContentType,
            extension = fileObject.Extension,
            sizeBytes = fileObject.SizeBytes,
            sha256 = fileObject.Sha256,
            classification = fileObject.Classification,
            status = fileObject.Status,
            etag = $"\"sha256:{fileObject.Sha256}\""
        });
    }

    // ─── METADATA ONLY ───────────────────────────────────────────────────────
    private static async Task<IResult> GetMetadataAsync(
        HttpRequest request,
        Guid fileId,
        AppDbContext db,
        AuditService audit)
    {
        var appCode = ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(null, "unknown", actor, "read", "denied", "unauthenticated", correlationId);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);

        if (policy is null || !policy.CanRead)
        {
            await audit.WriteAsync(null, appCode, actor, "read", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var fileObject = await db.Objects.FindAsync(fileId);

        if (fileObject is null || fileObject.Status != "active")
        {
            await audit.WriteAsync(null, appCode, actor, "read", "not_found", "object_unavailable", correlationId);
            return Results.NotFound();
        }

        if (!policy.AllowedDomains.Contains(fileObject.Domain))
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "success", null, correlationId);

        return Results.Ok(new
        {
            fileId = fileObject.FileId,
            domain = fileObject.Domain,
            contentType = fileObject.ContentType,
            extension = fileObject.Extension,
            originalFileName = fileObject.OriginalFileName,
            sizeBytes = fileObject.SizeBytes,
            sha256 = fileObject.Sha256,
            classification = fileObject.Classification,
            status = fileObject.Status,
            createdAt = fileObject.CreatedAt,
            etag = $"\"sha256:{fileObject.Sha256}\""
        });
    }

    // ─── CONTENT (stream) ────────────────────────────────────────────────────
    private static async Task<IResult> GetContentAsync(
        HttpRequest request,
        HttpResponse response,
        Guid fileId,
        AppDbContext db,
        AuditService audit,
        IConfiguration config)
    {
        var appCode = ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(null, "unknown", actor, "read", "denied", "unauthenticated", correlationId);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);

        if (policy is null || !policy.CanRead)
        {
            await audit.WriteAsync(null, appCode, actor, "read", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var fileObject = await db.Objects.FindAsync(fileId);

        if (fileObject is null || fileObject.Status != "active")
        {
            await audit.WriteAsync(null, appCode, actor, "read", "not_found", "object_unavailable", correlationId);
            return Results.NotFound();
        }

        if (!policy.AllowedDomains.Contains(fileObject.Domain))
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var etag = $"\"sha256:{fileObject.Sha256}\"";

        var ifNoneMatch = request.Headers["If-None-Match"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "success", "not_modified", correlationId);
            return Results.StatusCode(304);
        }

        var readPath = config["FileStorage:ReadPath"]!;
        var fullPath = Path.Combine(readPath, fileObject.RelativePath);

        var normalizedFull = Path.GetFullPath(fullPath);
        var normalizedRoot = Path.GetFullPath(readPath);
        if (!normalizedFull.StartsWith(normalizedRoot + Path.DirectorySeparatorChar))
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "error", "path_traversal_blocked", correlationId);
            return Results.StatusCode(500);
        }

        if (!File.Exists(fullPath))
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "error", "binary_missing", correlationId);
            return Results.Json(new { error = "storage_unavailable" }, statusCode: 503);
        }

        // Hash bütünlük kontrolü: disk ile katalog arasındaki SHA256 uyumsuzluğunu yakalar
        try
        {
            using var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(hashStream);
            var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            if (actualHash != fileObject.Sha256)
            {
                await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "error", "hash_mismatch", correlationId);
                return Results.Json(new { error = "hash_mismatch" }, statusCode: 409);
            }
        }
        catch (IOException)
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "error", "storage_read_failed", correlationId);
            return Results.Json(new { error = "storage_unavailable" }, statusCode: 503);
        }

        response.Headers["ETag"] = etag;

        var imageExtensions = new[] { "jpg", "jpeg", "png", "webp" };
        if (imageExtensions.Contains(fileObject.Extension))
        {
            response.Headers["Content-Disposition"] = "inline";
        }
        else
        {
            var safeName = string.IsNullOrEmpty(fileObject.OriginalFileName)
                ? $"file.{fileObject.Extension}"
                : fileObject.OriginalFileName;
            response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"";
        }

        await audit.WriteAsync(fileObject.FileId, appCode, actor, "read", "success", null, correlationId);

        var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);
        return Results.Stream(fileStream, fileObject.ContentType, enableRangeProcessing: true);
    }

    // ─── CREATE ──────────────────────────────────────────────────────────────
    private static async Task<IResult> CreateFileAsync(
        HttpRequest request,
        AppDbContext db,
        AuditService audit,
        IConfiguration config)
    {
        var appCode = ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(null, "unknown", actor, "create", "denied", "unauthenticated", correlationId);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);

        if (policy is null || !policy.CanCreate)
        {
            await audit.WriteAsync(null, appCode, actor, "create", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data bekleniyor" });

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        var domain = form["domain"].ToString();
        var entityType = form["entityType"].ToString();
        var entityId = form["entityId"].ToString();
        var relationType = form["relationType"].ToString();
        var classification = form["classification"].ToString();
        var originalFileName = form["originalFileName"].ToString();

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "dosya bulunamadi" });

        if (!policy.AllowedDomains.Contains(domain) || !policy.AllowedFileTypes.Contains(relationType))
        {
            await audit.WriteAsync(null, appCode, actor, "create", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        if (file.Length > policy.MaxFileSizeBytes)
        {
            await audit.WriteAsync(null, appCode, actor, "create", "denied", "file_too_large", correlationId);
            return Results.Json(new { error = "file_too_large" }, statusCode: 413);
        }

        var extension = Path.GetExtension(originalFileName).TrimStart('.').ToLowerInvariant();
        var allowedExtensions = AllowedExtensionsForRelationType(relationType);

        if (!allowedExtensions.Contains(extension))
        {
            await audit.WriteAsync(null, appCode, actor, "create", "denied", "unsupported_media_type", correlationId);
            return Results.Json(new { error = "unsupported_media_type" }, statusCode: 415);
        }

        if (!IsValidContentType(file.ContentType, extension))
        {
            await audit.WriteAsync(null, appCode, actor, "create", "denied", "content_type_mismatch", correlationId);
            return Results.Json(new { error = "unsupported_media_type" }, statusCode: 415);
        }

        using (var magicStream = file.OpenReadStream())
        {
            if (!await IsValidMagicBytesAsync(magicStream, extension))
            {
                await audit.WriteAsync(null, appCode, actor, "create", "denied", "magic_byte_mismatch", correlationId);
                return Results.Json(new { error = "unsupported_media_type" }, statusCode: 415);
            }
        }

        // Kardinalite: single-primary tipler için eski aktif primary arşivlenir.
        // multi-primary tipler için eski kayıtlara dokunulmaz; hepsi aktif kalır.
        var relTypeConfig = await db.RelationTypeConfigs.FindAsync(relationType);
        bool isSinglePrimary = relTypeConfig?.Cardinality != "multi"; // bilinmeyen → single (güvenli varsayılan)

        FileServiceApi.Models.FileObject? prevObj = null;
        FileServiceApi.Models.FileReference? prevRef = null;

        if (isSinglePrimary)
        {
            prevRef = await db.References
                .Where(r => r.AppCode == appCode
                         && r.EntityType == entityType
                         && r.EntityId == entityId
                         && r.RelationType == relationType
                         && r.IsPrimary
                         && r.Status == "active")
                .FirstOrDefaultAsync();

            if (prevRef is not null)
            {
                prevObj = await db.Objects.FindAsync(prevRef.FileId);
                if (prevObj is not null)
                {
                    prevObj.Status    = "archived";
                    prevObj.UpdatedAt = DateTime.UtcNow;
                }
                prevRef.Status = "revoked";
            }
        }

        var fileId = Guid.NewGuid();
        var fileIdString = fileId.ToString();
        var shard1 = fileIdString.Substring(0, 2);
        var shard2 = fileIdString.Substring(2, 2);
        var relativePath = $"{domain}/{shard1}/{shard2}/{fileIdString}.{extension}";

        var stagingPath = config["FileStorage:StagingPath"]!;
        var exportPath  = config["FileStorage:ExportPath"]!;

        var stagingFull = Path.Combine(stagingPath, relativePath);
        var exportFull  = Path.Combine(exportPath,  relativePath);

        string sha256Hash;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagingFull)!);

            // 1. Staging'e yaz
            using (var stagingStream = new FileStream(stagingFull, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var uploadStream = file.OpenReadStream())
            {
                await uploadStream.CopyToAsync(stagingStream);
            }

            // 2. Staging'deki dosyadan SHA256 hesapla (disk write bütünlüğü de doğrulanır)
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var hashStream = new FileStream(stagingFull, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hashBytes = await sha256.ComputeHashAsync(hashStream);
                sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // 3. Atomic promote: staging → export (aynı FS → rename)
            Directory.CreateDirectory(Path.GetDirectoryName(exportFull)!);
            File.Move(stagingFull, exportFull, overwrite: false);
        }
        catch (IOException)
        {
            if (File.Exists(stagingFull)) File.Delete(stagingFull);
            await audit.WriteAsync(null, appCode, actor, "create", "error", "storage_write_failed", correlationId);
            return Results.Json(new { error = "storage_unavailable" }, statusCode: 503);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            if (File.Exists(stagingFull)) File.Delete(stagingFull);
            await audit.WriteAsync(null, appCode, actor, "create", "error", "storage_write_failed", correlationId);
            return Results.Json(new { error = "storage_unavailable" }, statusCode: 503);
        }

        // 4. DB kayıtları — başarısız olursa export dosyasını geri al
        var fileObject = new FileServiceApi.Models.FileObject
        {
            FileId = fileId,
            Domain = domain,
            RelativePath = relativePath,
            ContentType = file.ContentType,
            Extension = extension,
            OriginalFileName = originalFileName,
            SizeBytes = file.Length,
            Sha256 = sha256Hash,
            Classification = string.IsNullOrEmpty(classification) ? "internal" : classification,
            Status = "active",
            CreatedByApp = appCode,
            CreatedByUser = actor,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var fileReference = new FileServiceApi.Models.FileReference
        {
            FileId = fileId,
            AppCode = appCode,
            EntityType = entityType,
            EntityId = entityId,
            RelationType = relationType,
            IsPrimary = true,
            Status = "active",
            CreatedAt = DateTime.UtcNow
        };

        db.Objects.Add(fileObject);
        db.References.Add(fileReference);
        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            // Rollback: export'tan da sil; katalog tutarsız kalmasın
            if (File.Exists(exportFull)) File.Delete(exportFull);
            await audit.WriteAsync(null, appCode, actor, "create", "error", "db_insert_failed", correlationId);
            return Results.Json(new { error = "internal_error" }, statusCode: 500);
        }

        await audit.WriteAsync(fileId, appCode, actor, "create", "success", null, correlationId);

        return Results.Ok(new
        {
            fileId = fileObject.FileId,
            domain = fileObject.Domain,
            relationType = fileReference.RelationType,
            contentType = fileObject.ContentType,
            extension = fileObject.Extension,
            sizeBytes = fileObject.SizeBytes,
            sha256 = fileObject.Sha256,
            classification = fileObject.Classification,
            status = fileObject.Status
        });
    }

    // ─── LIST ────────────────────────────────────────────────────────────────
    private static async Task<IResult> ListFilesAsync(
        HttpRequest request,
        string domain,
        string entityType,
        string entityId,
        AppDbContext db,
        AuditService audit)
    {
        var appCode = ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(null, "unknown", actor, "read", "denied", "unauthenticated", correlationId);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);

        if (policy is null || !policy.CanRead || !policy.AllowedDomains.Contains(domain))
        {
            await audit.WriteAsync(null, appCode, actor, "read", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var references = await db.References
            .Where(r => r.EntityType == entityType && r.EntityId == entityId && r.IsPrimary && r.Status == "active")
            .ToListAsync();

        var fileIds = references.Select(r => r.FileId).ToList();

        var objects = await db.Objects
            .Where(o => fileIds.Contains(o.FileId) && o.Domain == domain && o.Status == "active")
            .ToListAsync();

        var items = objects.Select(o =>
        {
            var rel = references.First(r => r.FileId == o.FileId);
            return new
            {
                fileId       = o.FileId,
                domain       = o.Domain,
                relationType = rel.RelationType,
                contentType  = o.ContentType,
                originalFileName = o.OriginalFileName,
                extension    = o.Extension,
                sizeBytes    = o.SizeBytes,
                sha256       = o.Sha256,
                classification = o.Classification,
                status       = o.Status,
                createdAt    = o.CreatedAt,
                etag         = $"\"sha256:{o.Sha256}\""
            };
        }).ToList();

        return Results.Ok(items);
    }

    // ─── MAGIC-BYTE KONTROLÜ ─────────────────────────────────────────────────
    private static string[] AllowedExtensionsForRelationType(string relationType) =>
        relationType switch
        {
            "cv" => ["pdf"],
            "photo" => ["jpg", "jpeg", "png", "webp"],
            "official_document" => ["pdf", "jpg", "jpeg", "png", "webp"],
            "document" => ["pdf", "jpg", "jpeg", "png", "webp"],
            "attachment" => ["pdf", "jpg", "jpeg", "png", "webp"],
            "report" => ["pdf"],
            _ => []
        };

    private static bool IsValidContentType(string? contentType, string extension)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return true;

        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return extension switch
        {
            "pdf" => mediaType == "application/pdf",
            "jpg" or "jpeg" => mediaType == "image/jpeg",
            "png" => mediaType == "image/png",
            "webp" => mediaType == "image/webp",
            _ => false
        };
    }

    private static async Task<bool> IsValidMagicBytesAsync(Stream stream, string extension)
    {
        var buffer = new byte[12];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length);
        if (read < 4) return false;

        return extension switch
        {
            "pdf"        => buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46,
            "jpg" or "jpeg" => buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF,
            "png"        => read >= 8
                            && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47
                            && buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A,
            "webp"       => read >= 12
                            && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
                            && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50,
            _            => false
        };
    }

    // ─── ARCHIVE ─────────────────────────────────────────────────────────────
    private static async Task<IResult> ArchiveFileAsync(
        HttpRequest request,
        Guid fileId,
        AppDbContext db,
        AuditService audit)
    {
        var appCode = ExtractAppCode(request.HttpContext.User);
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault();
        var actor = request.Headers["X-Actor-User-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(appCode))
        {
            await audit.WriteAsync(null, "unknown", actor, "archive", "denied", "unauthenticated", correlationId);
            return Results.Unauthorized();
        }

        var policy = await db.AppPolicies.FindAsync(appCode);

        if (policy is null || !policy.CanArchive)
        {
            await audit.WriteAsync(null, appCode, actor, "archive", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        var fileObject = await db.Objects.FindAsync(fileId);

        if (fileObject is null)
        {
            await audit.WriteAsync(null, appCode, actor, "archive", "not_found", "object_not_found", correlationId);
            return Results.NotFound();
        }

        if (!policy.AllowedDomains.Contains(fileObject.Domain))
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "archive", "denied", "policy_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        // active dışındaki tüm durumlarda (archived/revoked/deleted) idempotent 200
        if (fileObject.Status != "active")
            return Results.Ok(new { fileId = fileObject.FileId, status = fileObject.Status, message = "already_archived" });

        fileObject.Status = "archived";
        fileObject.UpdatedAt = DateTime.UtcNow;

        var reference = await db.References
            .Where(r => r.FileId == fileId && r.IsPrimary && r.Status == "active")
            .FirstOrDefaultAsync();
        if (reference is not null)
            reference.Status = "revoked";

        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            await audit.WriteAsync(fileObject.FileId, appCode, actor, "archive", "error", "db_update_failed", correlationId);
            return Results.Json(new { error = "internal_error" }, statusCode: 500);
        }

        await audit.WriteAsync(fileObject.FileId, appCode, actor, "archive", "success", null, correlationId);

        return Results.Ok(new { fileId = fileObject.FileId, status = fileObject.Status });
    }
}
