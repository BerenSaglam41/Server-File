using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Npgsql;
using YonetimApi.Services;

namespace YonetimApi.Endpoints;

public static class PersonnelEndpoints
{
    public static void MapPersonnelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/personnel").RequireAuthorization();

        // Personel arama (isim / ID)
        group.MapGet("",
            (HttpRequest req, NpgsqlDataSource db, IDomainAuditService a, IPermissionService p) =>
                SearchPersonnelAsync(req, db, a, p));

        // FileId bazlı içerik ve arşivleme (multi-primary tipler için)
        group.MapGet("/{personnelId}/files/{fileId}/content",
            (string personnelId, Guid fileId, HttpContext ctx, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetFileContentByIdAsync(personnelId, fileId, ctx, f, t, a, p));

        group.MapPost("/{personnelId}/files/{fileId}/archive",
            (string personnelId, Guid fileId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyArchiveFileByIdAsync(personnelId, fileId, req, f, t, a, p));

        // CV
        group.MapGet("/{personnelId}/cv",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetMetadataAsync(personnelId, "cv", req, f, t, a, p));

        group.MapGet("/{personnelId}/cv/content",
            (string personnelId, HttpContext ctx, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetContentAsync(personnelId, "cv", ctx, f, t, a, p));

        group.MapPost("/{personnelId}/cv",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyUploadAsync(personnelId, "cv", req, f, t, a, p));

        group.MapPost("/{personnelId}/cv/archive",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyArchiveAsync(personnelId, "cv", req, f, t, a, p));

        // Dosya listesi
        group.MapGet("/{personnelId}/files",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ListPersonnelFilesAsync(personnelId, req, f, t, a, p));

        // Fotoğraf
        group.MapGet("/{personnelId}/photo",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetMetadataAsync(personnelId, "photo", req, f, t, a, p));

        group.MapGet("/{personnelId}/photo/content",
            (string personnelId, HttpContext ctx, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetContentAsync(personnelId, "photo", ctx, f, t, a, p));

        group.MapPost("/{personnelId}/photo",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyUploadAsync(personnelId, "photo", req, f, t, a, p));

        group.MapPost("/{personnelId}/photo/archive",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyArchiveAsync(personnelId, "photo", req, f, t, a, p));

        // Resmi evrak (single-primary)
        group.MapGet("/{personnelId}/official-document",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetMetadataAsync(personnelId, "official_document", req, f, t, a, p));

        group.MapGet("/{personnelId}/official-document/content",
            (string personnelId, HttpContext ctx, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyGetContentAsync(personnelId, "official_document", ctx, f, t, a, p));

        group.MapPost("/{personnelId}/official-document",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyUploadAsync(personnelId, "official_document", req, f, t, a, p));

        group.MapPost("/{personnelId}/official-document/archive",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyArchiveAsync(personnelId, "official_document", req, f, t, a, p));

        // Genel belge (multi-primary — listelemek için /files kullan)
        group.MapPost("/{personnelId}/document",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyUploadAsync(personnelId, "document", req, f, t, a, p));

        // Ek dosya (multi-primary)
        group.MapPost("/{personnelId}/attachment",
            (string personnelId, HttpRequest req, IHttpClientFactory f, ITokenService t, IDomainAuditService a, IPermissionService p) =>
                ProxyUploadAsync(personnelId, "attachment", req, f, t, a, p));
    }

    // ─── METADATA ─────────────────────────────────────────────────────────────
    private static async Task<IResult> ProxyGetMetadataAsync(
        string personnelId, string relationType,
        HttpRequest request, IHttpClientFactory httpClientFactory,
        ITokenService tokenService, IDomainAuditService audit, IPermissionService perm)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanReadAsync(request.HttpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Viewed"), "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "access_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");
        var req = await BuildResolveRequestAsync(personnelId, relationType, actor, correlationId, tokenService);
        var response = await client.SendAsync(req);
        var body = await response.Content.ReadAsStringAsync();

        var result = response.StatusCode == System.Net.HttpStatusCode.OK ? "success"
                   : response.StatusCode == System.Net.HttpStatusCode.NotFound ? "not_found" : "error";
        await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Viewed"), result, null, correlationId);

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => Results.Unauthorized(),
            System.Net.HttpStatusCode.Forbidden    => Results.Json(new { error = "forbidden" }, statusCode: 403),
            System.Net.HttpStatusCode.NotFound     => Results.NotFound(new { error = $"{relationType}_not_found" }),
            System.Net.HttpStatusCode.OK           => Results.Content(body, "application/json"),
            _                                      => Results.Content(body, "application/json", Encoding.UTF8, (int)response.StatusCode)
        };
    }

    // ─── CONTENT (stream proxy) ───────────────────────────────────────────────
    private static async Task ProxyGetContentAsync(
        string personnelId, string relationType,
        HttpContext httpContext, IHttpClientFactory httpClientFactory,
        ITokenService tokenService, IDomainAuditService audit, IPermissionService perm)
    {
        var request = httpContext.Request;
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanReadAsync(httpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Downloaded"), "denied", "access_denied", correlationId);
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(new { error = "forbidden", reason = "access_denied" });
            return;
        }

        var client = httpClientFactory.CreateClient("FileService");

        var resolveReq = await BuildResolveRequestAsync(personnelId, relationType, actor, correlationId, tokenService);
        var resolveResp = await client.SendAsync(resolveReq);

        if (!resolveResp.IsSuccessStatusCode)
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Downloaded"), "not_found", null, correlationId);
            httpContext.Response.StatusCode = (int)resolveResp.StatusCode;
            return;
        }

        var fileInfo = await resolveResp.Content.ReadFromJsonAsync<FileResolveResult>();
        if (fileInfo is null)
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Downloaded"), "error", "resolve_parse_failed", correlationId);
            httpContext.Response.StatusCode = 502;
            return;
        }

        var serviceToken = await tokenService.GetServiceTokenAsync();
        var contentReq = new HttpRequestMessage(HttpMethod.Get, $"internal/files/{fileInfo.FileId}/content");
        contentReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        contentReq.Headers.Add("X-Actor-User-Id", actor);
        contentReq.Headers.Add("X-Correlation-Id", correlationId);

        if (request.Headers.TryGetValue("Range", out var rangeHeader))
            contentReq.Headers.TryAddWithoutValidation("Range", rangeHeader.ToString());
        if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
            contentReq.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch.ToString());

        var contentResp = await client.SendAsync(contentReq, HttpCompletionOption.ResponseHeadersRead);
        httpContext.Response.StatusCode = (int)contentResp.StatusCode;

        if (!contentResp.IsSuccessStatusCode && contentResp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Downloaded"), "error", null, correlationId);
            return;
        }

        await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Downloaded"), "success", null, correlationId);

        if (contentResp.Content.Headers.ContentType is not null)
            httpContext.Response.ContentType = contentResp.Content.Headers.ContentType.ToString();
        if (contentResp.Content.Headers.ContentLength.HasValue)
            httpContext.Response.ContentLength = contentResp.Content.Headers.ContentLength.Value;
        if (contentResp.Content.Headers.TryGetValues("Content-Disposition", out var cdValues))
            httpContext.Response.Headers["Content-Disposition"] = cdValues.ToArray();
        if (contentResp.Content.Headers.TryGetValues("Content-Range", out var crValues))
            httpContext.Response.Headers["Content-Range"] = crValues.ToArray();
        if (contentResp.Headers.TryGetValues("ETag", out var etagValues))
            httpContext.Response.Headers["ETag"] = etagValues.ToArray();
        httpContext.Response.Headers["Accept-Ranges"] = "bytes";

        await contentResp.Content.CopyToAsync(httpContext.Response.Body);
    }

    // ─── UPLOAD ───────────────────────────────────────────────────────────────
    private static async Task<IResult> ProxyUploadAsync(
        string personnelId, string relationType,
        HttpRequest request, IHttpClientFactory httpClientFactory,
        ITokenService tokenService, IDomainAuditService audit, IPermissionService perm)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanWriteAsync(request.HttpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Uploaded"), "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "access_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");

        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data bekleniyor" });

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "dosya bulunamadi" });

        var classification = form["classification"].FirstOrDefault() ?? "internal";
        var serviceToken = await tokenService.GetServiceTokenAsync();

        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(file.OpenReadStream());
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);

        content.Add(fileContent,                       "file",             file.FileName);
        content.Add(new StringContent("personnel"),    "domain");
        content.Add(new StringContent("personnel"),    "entityType");
        content.Add(new StringContent(personnelId),    "entityId");
        content.Add(new StringContent(relationType),   "relationType");
        content.Add(new StringContent(classification), "classification");
        content.Add(new StringContent(file.FileName),  "originalFileName");

        var uploadReq = new HttpRequestMessage(HttpMethod.Post, "internal/files") { Content = content };
        uploadReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        uploadReq.Headers.Add("X-Actor-User-Id",  actor);
        uploadReq.Headers.Add("X-Correlation-Id", correlationId);

        var response = await client.SendAsync(uploadReq);
        var body = await response.Content.ReadAsStringAsync();

        await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Uploaded"),
            response.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(body, "application/json", Encoding.UTF8, (int)response.StatusCode);
    }

    // ─── ARCHIVE PROXY ────────────────────────────────────────────────────────
    private static async Task<IResult> ProxyArchiveAsync(
        string personnelId, string relationType,
        HttpRequest request, IHttpClientFactory httpClientFactory,
        ITokenService tokenService, IDomainAuditService audit, IPermissionService perm)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanWriteAsync(request.HttpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Archived"), "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "access_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");

        var resolveReq = await BuildResolveRequestAsync(personnelId, relationType, actor, correlationId, tokenService);
        var resolveResp = await client.SendAsync(resolveReq);

        if (!resolveResp.IsSuccessStatusCode)
        {
            await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Archived"), "not_found", null, correlationId);
            return Results.Content(await resolveResp.Content.ReadAsStringAsync(), "application/json", Encoding.UTF8, (int)resolveResp.StatusCode);
        }

        var fileInfo = await resolveResp.Content.ReadFromJsonAsync<FileResolveResult>();
        if (fileInfo is null) return Results.StatusCode(502);

        var serviceToken = await tokenService.GetServiceTokenAsync();
        var archiveReq = new HttpRequestMessage(HttpMethod.Post, $"internal/files/{fileInfo.FileId}/archive");
        archiveReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        archiveReq.Headers.Add("X-Actor-User-Id",  actor);
        archiveReq.Headers.Add("X-Correlation-Id", correlationId);

        var archiveResp = await client.SendAsync(archiveReq);
        var archiveBody = await archiveResp.Content.ReadAsStringAsync();

        await audit.WriteAsync(personnelId, actor, DomainAction(relationType, "Archived"),
            archiveResp.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(archiveBody, "application/json", Encoding.UTF8, (int)archiveResp.StatusCode);
    }

    // ─── DOSYA LİSTESİ ───────────────────────────────────────────────────────
    private static async Task<IResult> ListPersonnelFilesAsync(
        string personnelId, HttpRequest request,
        IHttpClientFactory httpClientFactory, ITokenService tokenService,
        IDomainAuditService audit, IPermissionService perm)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanReadAsync(request.HttpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelFilesListed", "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "access_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");
        var serviceToken = await tokenService.GetServiceTokenAsync();

        var listReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/files/list?domain=personnel&entityType=personnel&entityId={Uri.EscapeDataString(personnelId)}");
        listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        listReq.Headers.Add("X-Actor-User-Id", actor);
        listReq.Headers.Add("X-Correlation-Id", correlationId);

        var response = await client.SendAsync(listReq);
        var body = await response.Content.ReadAsStringAsync();

        await audit.WriteAsync(personnelId, actor, "PersonnelFilesListed",
            response.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(body, "application/json", Encoding.UTF8, (int)response.StatusCode);
    }

    // ─── PERSONEL ARAMA ──────────────────────────────────────────────────────
    private static async Task<IResult> SearchPersonnelAsync(
        HttpRequest request,
        NpgsqlDataSource db,
        IDomainAuditService audit,
        IPermissionService perm)
    {
        var user = request.HttpContext.User;
        var (actor, correlationId) = ExtractHeaders(request);
        var q = "%" + (request.Query["search"].FirstOrDefault() ?? "").Trim() + "%";

        var roles = user.FindAll("roles").Select(c => c.Value).ToHashSet();
        var ownId = GetPersonnelId(user) ?? "";

        bool readAll  = roles.Contains("personnel.files.read.all");
        bool readTeam = roles.Contains("personnel.files.read.team");
        bool readSelf = roles.Contains("personnel.files.read.self");

        if (!readAll && !readTeam && !readSelf)
        {
            await audit.WriteAsync("search", actor, "PersonnelSearched", "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        }

        string sql;
        object[] args;

        if (readAll)
        {
            sql = "SELECT personnel_id, display_name, department, title FROM yonetim.personnel WHERE display_name ILIKE $1 OR personnel_id ILIKE $1 ORDER BY display_name LIMIT 30";
            args = [q];
        }
        else if (readTeam && !string.IsNullOrEmpty(ownId))
        {
            sql = @"SELECT p.personnel_id, p.display_name, p.department, p.title
                    FROM yonetim.personnel p
                    WHERE p.personnel_id IN (
                        SELECT personnel_id FROM yonetim.team_members WHERE manager_id = $1
                        UNION SELECT $1
                    )
                    AND (p.display_name ILIKE $2 OR p.personnel_id ILIKE $2)
                    ORDER BY p.display_name LIMIT 30";
            args = [ownId, q];
        }
        else
        {
            sql = "SELECT personnel_id, display_name, department, title FROM yonetim.personnel WHERE personnel_id = $1 AND (display_name ILIKE $2 OR personnel_id ILIKE $2) LIMIT 1";
            args = [ownId, q];
        }

        await using var cmd = db.CreateCommand(sql);
        foreach (var (p, i) in args.Select((p, i) => (p, i + 1)))
            cmd.Parameters.AddWithValue(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<object>();
        while (await reader.ReadAsync())
            results.Add(new {
                personnelId  = reader.GetString(0),
                displayName  = reader.GetString(1),
                department   = reader.IsDBNull(2) ? null : reader.GetString(2),
                title        = reader.IsDBNull(3) ? null : reader.GetString(3)
            });

        await audit.WriteAsync("search", actor, "PersonnelSearched", "success", null, correlationId);
        return Results.Ok(results);
    }

    // ─── FİLE-ID BAZLI İÇERİK (multi-primary indirme) ────────────────────────
    private static async Task ProxyGetFileContentByIdAsync(
        string personnelId, Guid fileId,
        HttpContext httpContext, IHttpClientFactory httpClientFactory,
        ITokenService tokenService, IDomainAuditService audit, IPermissionService perm)
    {
        var request = httpContext.Request;
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanReadAsync(httpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelFileDownloaded", "denied", "access_denied", correlationId);
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(new { error = "forbidden", reason = "access_denied" });
            return;
        }

        var client = httpClientFactory.CreateClient("FileService");
        var serviceToken = await tokenService.GetServiceTokenAsync();

        if (!await FileBelongsToPersonnelAsync(client, tokenService, personnelId, fileId, actor, correlationId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelFileDownloaded", "denied", "file_scope_denied", correlationId);
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(new { error = "forbidden", reason = "file_scope_denied" });
            return;
        }

        var contentReq = new HttpRequestMessage(HttpMethod.Get, $"internal/files/{fileId}/content");
        contentReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        contentReq.Headers.Add("X-Actor-User-Id", actor);
        contentReq.Headers.Add("X-Correlation-Id", correlationId);

        if (request.Headers.TryGetValue("Range", out var range))
            contentReq.Headers.TryAddWithoutValidation("Range", range.ToString());
        if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
            contentReq.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch.ToString());

        var resp = await client.SendAsync(contentReq, HttpCompletionOption.ResponseHeadersRead);
        httpContext.Response.StatusCode = (int)resp.StatusCode;

        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelFileDownloaded", "error", null, correlationId);
            return;
        }

        await audit.WriteAsync(personnelId, actor, "PersonnelFileDownloaded", "success", null, correlationId);

        if (resp.Content.Headers.ContentType is not null)
            httpContext.Response.ContentType = resp.Content.Headers.ContentType.ToString();
        if (resp.Content.Headers.ContentLength.HasValue)
            httpContext.Response.ContentLength = resp.Content.Headers.ContentLength.Value;
        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var cd))
            httpContext.Response.Headers["Content-Disposition"] = cd.ToArray();
        if (resp.Headers.TryGetValues("ETag", out var etag))
            httpContext.Response.Headers["ETag"] = etag.ToArray();
        if (resp.Content.Headers.TryGetValues("Content-Range", out var cr))
            httpContext.Response.Headers["Content-Range"] = cr.ToArray();
        httpContext.Response.Headers["Accept-Ranges"] = "bytes";

        await resp.Content.CopyToAsync(httpContext.Response.Body);
    }

    // ─── FİLE-ID BAZLI ARŞİVLEME ─────────────────────────────────────────────
    private static async Task<IResult> ProxyArchiveFileByIdAsync(
        string personnelId, Guid fileId,
        HttpRequest request, IHttpClientFactory httpClientFactory,
        ITokenService tokenService, IDomainAuditService audit, IPermissionService perm)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!await perm.CanWriteAsync(request.HttpContext.User, personnelId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelFileArchived", "denied", "access_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "access_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");
        var serviceToken = await tokenService.GetServiceTokenAsync();

        if (!await FileBelongsToPersonnelAsync(client, tokenService, personnelId, fileId, actor, correlationId))
        {
            await audit.WriteAsync(personnelId, actor, "PersonnelFileArchived", "denied", "file_scope_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "file_scope_denied" }, statusCode: 403);
        }

        var archiveReq = new HttpRequestMessage(HttpMethod.Post, $"internal/files/{fileId}/archive");
        archiveReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        archiveReq.Headers.Add("X-Actor-User-Id", actor);
        archiveReq.Headers.Add("X-Correlation-Id", correlationId);

        var resp = await client.SendAsync(archiveReq);
        var body = await resp.Content.ReadAsStringAsync();

        await audit.WriteAsync(personnelId, actor, "PersonnelFileArchived",
            resp.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(body, "application/json", Encoding.UTF8, (int)resp.StatusCode);
    }

    // ─── YARDIMCI METODLAR ────────────────────────────────────────────────────

    private static string DomainAction(string relationType, string verb)
    {
        var pascal = string.Concat(relationType.Split('_')
            .Select(w => string.IsNullOrEmpty(w) ? w : char.ToUpper(w[0]) + w[1..]));
        return $"Personnel{pascal}{verb}";
    }

    private static (string actor, string correlationId) ExtractHeaders(HttpRequest request)
    {
        var actor = request.HttpContext.User.FindFirst("preferred_username")?.Value
                    ?? request.HttpContext.User.FindFirst("sub")?.Value
                    ?? "anonymous";
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        return (actor, correlationId);
    }

    private static string? GetPersonnelId(ClaimsPrincipal user)
    {
        var claimValue = user.FindFirst("personnel_id")?.Value;
        if (!string.IsNullOrWhiteSpace(claimValue))
            return claimValue;

        var username = user.FindFirst("preferred_username")?.Value
                       ?? user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(username)
            ? null
            : username.ToUpperInvariant();
    }

    private static async Task<HttpRequestMessage> BuildResolveRequestAsync(
        string personnelId, string relationType,
        string actor, string correlationId, ITokenService tokenService)
    {
        var serviceToken = await tokenService.GetServiceTokenAsync();
        var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/files/resolve?domain=personnel&entityType=personnel" +
            $"&entityId={Uri.EscapeDataString(personnelId)}&relationType={Uri.EscapeDataString(relationType)}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        req.Headers.Add("X-Actor-User-Id",  actor);
        req.Headers.Add("X-Correlation-Id", correlationId);
        return req;
    }

    private static async Task<bool> FileBelongsToPersonnelAsync(
        HttpClient client,
        ITokenService tokenService,
        string personnelId,
        Guid fileId,
        string actor,
        string correlationId)
    {
        var serviceToken = await tokenService.GetServiceTokenAsync();
        var listReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/files/list?domain=personnel&entityType=personnel&entityId={Uri.EscapeDataString(personnelId)}");
        listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        listReq.Headers.Add("X-Actor-User-Id", actor);
        listReq.Headers.Add("X-Correlation-Id", correlationId);

        var listResp = await client.SendAsync(listReq);
        if (!listResp.IsSuccessStatusCode)
            return false;

        var files = await listResp.Content.ReadFromJsonAsync<List<FileListItem>>();
        return files?.Any(f => f.FileId == fileId) == true;
    }

    private record FileResolveResult(
        [property: JsonPropertyName("fileId")]         Guid   FileId,
        [property: JsonPropertyName("domain")]         string Domain,
        [property: JsonPropertyName("relationType")]   string RelationType,
        [property: JsonPropertyName("contentType")]    string ContentType,
        [property: JsonPropertyName("extension")]      string Extension,
        [property: JsonPropertyName("sizeBytes")]      long   SizeBytes,
        [property: JsonPropertyName("sha256")]         string Sha256,
        [property: JsonPropertyName("classification")] string Classification,
        [property: JsonPropertyName("status")]         string Status);

    private record FileListItem(
        [property: JsonPropertyName("fileId")] Guid FileId);
}
