using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using FlotaApi.Services;

namespace FlotaApi.Endpoints;

public static class VehicleEndpoints
{
    public static void MapVehicleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/vehicles").RequireAuthorization();

        // Fotoğraf
        group.MapGet("/{vehicleId}/photo",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyGetMetadataAsync(vehicleId, "photo", req, f, tokens, audit));

        group.MapGet("/{vehicleId}/photo/content",
            (string vehicleId, HttpContext ctx, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyGetContentAsync(vehicleId, "photo", ctx, f, tokens, audit));

        group.MapPost("/{vehicleId}/photo",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyUploadAsync(vehicleId, "photo", req, f, tokens, audit));

        group.MapPost("/{vehicleId}/photo/archive",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyArchiveAsync(vehicleId, "photo", req, f, tokens, audit));

        // Belge
        group.MapGet("/{vehicleId}/document",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyGetMetadataAsync(vehicleId, "document", req, f, tokens, audit));

        group.MapGet("/{vehicleId}/document/content",
            (string vehicleId, HttpContext ctx, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyGetContentAsync(vehicleId, "document", ctx, f, tokens, audit));

        group.MapPost("/{vehicleId}/document",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyUploadAsync(vehicleId, "document", req, f, tokens, audit));

        group.MapPost("/{vehicleId}/document/archive",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyArchiveAsync(vehicleId, "document", req, f, tokens, audit));

        // Resmi evrak (single-primary)
        group.MapGet("/{vehicleId}/official-document",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyGetMetadataAsync(vehicleId, "official_document", req, f, tokens, audit));

        group.MapGet("/{vehicleId}/official-document/content",
            (string vehicleId, HttpContext ctx, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyGetContentAsync(vehicleId, "official_document", ctx, f, tokens, audit));

        group.MapPost("/{vehicleId}/official-document",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyUploadAsync(vehicleId, "official_document", req, f, tokens, audit));

        group.MapPost("/{vehicleId}/official-document/archive",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyArchiveAsync(vehicleId, "official_document", req, f, tokens, audit));

        // Ek dosya (multi-primary)
        group.MapPost("/{vehicleId}/attachment",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyUploadAsync(vehicleId, "attachment", req, f, tokens, audit));

        // Rapor (multi-primary)
        group.MapPost("/{vehicleId}/report",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ProxyUploadAsync(vehicleId, "report", req, f, tokens, audit));

        // Dosya listesi
        group.MapGet("/{vehicleId}/files",
            (string vehicleId, HttpRequest req, IHttpClientFactory f, ITokenService tokens, IDomainAuditService audit) =>
                ListVehicleFilesAsync(vehicleId, req, f, tokens, audit));
    }

    // ─── METADATA ─────────────────────────────────────────────────────────────
    private static async Task<IResult> ProxyGetMetadataAsync(
        string vehicleId,
        string relationType,
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        ITokenService tokenService,
        IDomainAuditService audit)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!HasVehicleAccess(request.HttpContext.User, vehicleId))
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Viewed"), "denied", "data_scope_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "data_scope_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");
        var req = await BuildResolveRequestAsync(vehicleId, relationType, actor, correlationId, tokenService);
        var response = await client.SendAsync(req);
        var body = await response.Content.ReadAsStringAsync();

        var result = response.StatusCode == System.Net.HttpStatusCode.OK ? "success"
                   : response.StatusCode == System.Net.HttpStatusCode.NotFound ? "not_found" : "error";
        await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Viewed"), result, null, correlationId);

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
        string vehicleId,
        string relationType,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory,
        ITokenService tokenService,
        IDomainAuditService audit)
    {
        var request = httpContext.Request;
        var (actor, correlationId) = ExtractHeaders(request);

        if (!HasVehicleAccess(httpContext.User, vehicleId))
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Downloaded"), "denied", "data_scope_denied", correlationId);
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(new { error = "forbidden", reason = "data_scope_denied" });
            return;
        }

        var client = httpClientFactory.CreateClient("FileService");

        var resolveReq = await BuildResolveRequestAsync(vehicleId, relationType, actor, correlationId, tokenService);
        var resolveResp = await client.SendAsync(resolveReq);

        if (!resolveResp.IsSuccessStatusCode)
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Downloaded"), "not_found", null, correlationId);
            httpContext.Response.StatusCode = (int)resolveResp.StatusCode;
            return;
        }

        var fileInfo = await resolveResp.Content.ReadFromJsonAsync<FileResolveResult>();
        if (fileInfo is null)
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Downloaded"), "error", "resolve_parse_failed", correlationId);
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
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Downloaded"), "error", null, correlationId);
            return;
        }

        await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Downloaded"), "success", null, correlationId);

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
        if (contentResp.Headers.TryGetValues("Accept-Ranges", out var arValues))
            httpContext.Response.Headers["Accept-Ranges"] = arValues.ToArray();

        await contentResp.Content.CopyToAsync(httpContext.Response.Body);
    }

    // ─── UPLOAD ───────────────────────────────────────────────────────────────
    private static async Task<IResult> ProxyUploadAsync(
        string vehicleId,
        string relationType,
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        ITokenService tokenService,
        IDomainAuditService audit)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!HasVehicleAccess(request.HttpContext.User, vehicleId))
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Uploaded"), "denied", "data_scope_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "data_scope_denied" }, statusCode: 403);
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
        content.Add(new StringContent("fleet"),        "domain");
        content.Add(new StringContent("vehicle"),      "entityType");
        content.Add(new StringContent(vehicleId),      "entityId");
        content.Add(new StringContent(relationType),   "relationType");
        content.Add(new StringContent(classification), "classification");
        content.Add(new StringContent(file.FileName),  "originalFileName");

        var uploadReq = new HttpRequestMessage(HttpMethod.Post, "internal/files") { Content = content };
        uploadReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        uploadReq.Headers.Add("X-Actor-User-Id",  actor);
        uploadReq.Headers.Add("X-Correlation-Id", correlationId);

        var response = await client.SendAsync(uploadReq);
        var body = await response.Content.ReadAsStringAsync();

        await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Uploaded"),
            response.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(body, "application/json", Encoding.UTF8, (int)response.StatusCode);
    }

    // ─── ARCHIVE PROXY ────────────────────────────────────────────────────────
    private static async Task<IResult> ProxyArchiveAsync(
        string vehicleId,
        string relationType,
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        ITokenService tokenService,
        IDomainAuditService audit)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!HasVehicleAccess(request.HttpContext.User, vehicleId))
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Archived"), "denied", "data_scope_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "data_scope_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");

        var resolveReq = await BuildResolveRequestAsync(vehicleId, relationType, actor, correlationId, tokenService);
        var resolveResp = await client.SendAsync(resolveReq);

        if (!resolveResp.IsSuccessStatusCode)
        {
            await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Archived"), "not_found", null, correlationId);
            return Results.Content(await resolveResp.Content.ReadAsStringAsync(), "application/json", Encoding.UTF8, (int)resolveResp.StatusCode);
        }

        var fileInfo = await resolveResp.Content.ReadFromJsonAsync<FileResolveResult>();
        if (fileInfo is null) return Results.StatusCode(502);

        var serviceToken = await tokenService.GetServiceTokenAsync();
        var archiveReq = new HttpRequestMessage(HttpMethod.Post, $"internal/references/{fileInfo.ReferenceId}/archive");
        archiveReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        archiveReq.Headers.Add("X-Actor-User-Id",  actor);
        archiveReq.Headers.Add("X-Correlation-Id", correlationId);

        var archiveResp = await client.SendAsync(archiveReq);
        var archiveBody = await archiveResp.Content.ReadAsStringAsync();

        await audit.WriteAsync(vehicleId, actor, DomainAction(relationType, "Archived"),
            archiveResp.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(archiveBody, "application/json", Encoding.UTF8, (int)archiveResp.StatusCode);
    }

    // ─── DOSYA LİSTESİ ───────────────────────────────────────────────────────
    private static async Task<IResult> ListVehicleFilesAsync(
        string vehicleId,
        HttpRequest request,
        IHttpClientFactory httpClientFactory,
        ITokenService tokenService,
        IDomainAuditService audit)
    {
        var (actor, correlationId) = ExtractHeaders(request);

        if (!HasVehicleAccess(request.HttpContext.User, vehicleId))
        {
            await audit.WriteAsync(vehicleId, actor, "VehicleFilesListed", "denied", "data_scope_denied", correlationId);
            return Results.Json(new { error = "forbidden", reason = "data_scope_denied" }, statusCode: 403);
        }

        var client = httpClientFactory.CreateClient("FileService");
        var serviceToken = await tokenService.GetServiceTokenAsync();

        var listReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/files/list?domain=fleet&entityType=vehicle&entityId={Uri.EscapeDataString(vehicleId)}");
        listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        listReq.Headers.Add("X-Actor-User-Id", actor);
        listReq.Headers.Add("X-Correlation-Id", correlationId);

        var response = await client.SendAsync(listReq);
        var body = await response.Content.ReadAsStringAsync();

        await audit.WriteAsync(vehicleId, actor, "VehicleFilesListed",
            response.IsSuccessStatusCode ? "success" : "error", null, correlationId);

        return Results.Content(body, "application/json", Encoding.UTF8, (int)response.StatusCode);
    }

    // ─── YARDIMCI METODLAR ────────────────────────────────────────────────────

    private static string DomainAction(string relationType, string verb)
    {
        var pascal = string.Concat(relationType.Split('_')
            .Select(w => string.IsNullOrEmpty(w) ? w : char.ToUpper(w[0]) + w[1..]));
        return $"Vehicle{pascal}{verb}";
    }

    private static bool HasVehicleAccess(ClaimsPrincipal user, string vehicleId)
    {
        var ownVehicleId = user.FindFirst("vehicle_id")?.Value;
        return !string.IsNullOrEmpty(ownVehicleId) && ownVehicleId == vehicleId;
    }

    private static (string actor, string correlationId) ExtractHeaders(HttpRequest request)
    {
        var actor = request.HttpContext.User.FindFirst("preferred_username")?.Value
                    ?? request.HttpContext.User.FindFirst("sub")?.Value
                    ?? "anonymous";
        var correlationId = request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        return (actor, correlationId);
    }

    private static async Task<HttpRequestMessage> BuildResolveRequestAsync(
        string vehicleId,
        string relationType,
        string actor,
        string correlationId,
        ITokenService tokenService)
    {
        var serviceToken = await tokenService.GetServiceTokenAsync();
        var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/files/resolve?domain=fleet&entityType=vehicle" +
            $"&entityId={Uri.EscapeDataString(vehicleId)}&relationType={Uri.EscapeDataString(relationType)}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceToken);
        req.Headers.Add("X-Actor-User-Id",  actor);
        req.Headers.Add("X-Correlation-Id", correlationId);
        return req;
    }

    private record FileResolveResult(
        [property: JsonPropertyName("fileId")]         Guid   FileId,
        [property: JsonPropertyName("referenceId")]    long   ReferenceId,
        [property: JsonPropertyName("domain")]         string Domain,
        [property: JsonPropertyName("relationType")]   string RelationType,
        [property: JsonPropertyName("contentType")]    string ContentType,
        [property: JsonPropertyName("extension")]      string Extension,
        [property: JsonPropertyName("sizeBytes")]      long   SizeBytes,
        [property: JsonPropertyName("sha256")]         string Sha256,
        [property: JsonPropertyName("classification")] string Classification,
        [property: JsonPropertyName("status")]         string Status);
}
