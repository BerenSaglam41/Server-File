using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace OpsApi.Endpoints;

public static class OpsEndpoints
{
    private static readonly string StatusRoot =
        Environment.GetEnvironmentVariable("STATUS_ROOT") ?? "/ops/status-files";
    private static readonly int BackupRetain =
        int.TryParse(Environment.GetEnvironmentVariable("BACKUP_RETAIN"), out var retain) ? retain : 14;

    public static void MapOpsEndpoints(this WebApplication app)
    {
        var ops = app.MapGroup("/ops").RequireAuthorization("ops.read");

        ops.MapGet("/health",   GetHealth);
        ops.MapGet("/services", GetServices);
        ops.MapGet("/disk",     GetDisk);
        ops.MapGet("/alerts",   GetAlerts);
        ops.MapGet("/backups",  GetBackups);
        ops.MapGet("/version",  GetVersion);
        ops.MapGet("/dashboard", GetDashboard);
        ops.MapGet("/me", GetMe);
    }

    // GET /ops/health — her servisin HTTP health endpoint'ini çağırır
    private static async Task<IResult> GetHealth(IHttpClientFactory factory, NpgsqlDataSource db) =>
        Results.Ok(await BuildHealthAsync(factory, db));

    // GET /ops/dashboard — UI için tek çağrıda ops özeti
    private static async Task<IResult> GetDashboard(IHttpClientFactory factory, NpgsqlDataSource db)
    {
        var servicesTask = BuildServicesAsync();
        var healthTask = BuildHealthAsync(factory, db);

        await Task.WhenAll(servicesTask, healthTask);

        return Results.Ok(new
        {
            timestamp = DateTime.UtcNow,
            health = healthTask.Result,
            services = servicesTask.Result,
            disk = BuildDisk(),
            alerts = BuildAlerts(),
            backups = BuildBackups(),
            version = BuildVersion(),
        });
    }

    private static async Task<object> BuildHealthAsync(IHttpClientFactory factory, NpgsqlDataSource db)
    {
        var serviceSnapshot = LoadServiceStatus();
        var containers = serviceSnapshot?.Services;
        var results = new List<object>();
        var overall = "healthy";

        void Add(string name, string status, long? latencyMs = null, string? reason = null)
        {
            if (status != "healthy") overall = "degraded";
            results.Add(new { name, status, latency_ms = latencyMs, reason });
        }

        var client  = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        await AddHttpHealthAsync("yonetimapi", "http://yonetimapi:8080/health");
        await AddHttpHealthAsync("flotaapi", "http://flotaapi:8080/health");
        await AddHttpHealthAsync("keycloak", "http://keycloak:8080/realms/platform");

        await AddGatewayHealthAsync();
        await AddPostgresHealthAsync();

        Add("fileservice", IsContainerRunning(containers, "fileservice") ? "healthy" : "degraded", null,
            serviceSnapshot is null ? "service_status_missing" : "service_status");
        Add("opsapi", "healthy", null, "self");

        return new { status = overall, timestamp = DateTime.UtcNow, services = results };

        async Task AddHttpHealthAsync(string name, string url)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var resp = await client.GetAsync(url);
                sw.Stop();
                var status = resp.IsSuccessStatusCode ? "healthy" : "degraded";
                Add(name, status, sw.ElapsedMilliseconds, resp.IsSuccessStatusCode ? null : $"http_{(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Add(name, "unhealthy", null, ex.Message[..Math.Min(ex.Message.Length, 80)]);
            }
        }

        async Task AddGatewayHealthAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                using var gateway = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await gateway.GetAsync("https://gateway/health");
                sw.Stop();
                Add("gateway", resp.IsSuccessStatusCode ? "healthy" : "degraded", sw.ElapsedMilliseconds,
                    resp.IsSuccessStatusCode ? null : $"http_{(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var dockerStatus = IsContainerRunning(containers, "gateway") ? "degraded" : "unhealthy";
                Add("gateway", dockerStatus, null, ex.Message[..Math.Min(ex.Message.Length, 80)]);
            }
        }

        async Task AddPostgresHealthAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var conn = await db.OpenConnectionAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "select 1";
                await cmd.ExecuteScalarAsync();
                sw.Stop();
                Add("postgres", "healthy", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Add("postgres", "unhealthy", null, ex.Message[..Math.Min(ex.Message.Length, 80)]);
            }
        }
    }

    // GET /ops/services — host tarafında üretilen status snapshot dosyasından servis listesi
    private static async Task<IResult> GetServices() => Results.Ok(await BuildServicesAsync());

    private static Task<object> BuildServicesAsync()
    {
        var snapshot = LoadServiceStatus();
        if (snapshot is null)
            return Task.FromResult<object>(new { note = "Servis status dosyası yok; tools/services-status.sh henüz çalışmadı", count = 0, services = Array.Empty<object>() });

        return Task.FromResult<object>(new
        {
            status = snapshot.Status,
            timestamp = snapshot.Timestamp,
            count = snapshot.Services.Count,
            services = snapshot.Services.Select(c =>
            {
                return new
                {
                    name = c.Name,
                    service = c.Service,
                    image = c.Image,
                    status = c.Status,
                    state = c.State,
                    created = c.Created,
                    started_at = c.StartedAt,
                    age_seconds = c.AgeSeconds,
                    restart_count = c.RestartCount,
                    cpu = c.Cpu,
                    memory = c.Memory,
                };
            }).ToList()
        });
    }

    // GET /ops/me — UI'nin kullanıcının ops kimliğini doğrulaması için
    private static IResult GetMe(HttpContext ctx)
    {
        var roles = ExtractRealmRoles(ctx.User).OrderBy(r => r).ToArray();
        return Results.Ok(new
        {
            username = ctx.User.FindFirst("preferred_username")?.Value
                       ?? ctx.User.FindFirst("sub")?.Value
                       ?? "anonymous",
            authenticated = ctx.User.Identity?.IsAuthenticated == true,
            roles,
            permissions = new
            {
                read = roles.Any(r => r is "ops.read" or "ops.execute" or "ops.admin"),
                execute = roles.Any(r => r is "ops.execute" or "ops.admin"),
                admin = roles.Contains("ops.admin"),
            },
        });
    }

    // GET /ops/disk — .disk-status dosyasını okur
    private static IResult GetDisk() => Results.Ok(BuildDisk());

    private static object BuildDisk()
    {
        var path = Path.Combine(StatusRoot, ".disk-status");
        if (!File.Exists(path))
            return new { status = "unknown", note = "Henüz disk-check çalışmadı" };

        var fields = ParseStatusFile(path);
        return new
        {
            status          = fields.GetValueOrDefault("status", "unknown"),
            timestamp       = fields.GetValueOrDefault("timestamp"),
            api_server_pct  = TryParseInt(fields.GetValueOrDefault("api_server_pct")),
            files01_pct     = TryParseInt(fields.GetValueOrDefault("files01_pct")),
            reason          = fields.GetValueOrDefault("reason"),
        };
    }

    // GET /ops/alerts — 3 status dosyasındaki uyarıları birleştirir
    private static IResult GetAlerts() => Results.Ok(BuildAlerts());

    private static object BuildAlerts()
    {
        var sources = new[]
        {
            (".disk-status",    "disk"),
            (".backup-status",  "backup"),
            (".restore-status", "restore"),
        };

        var alerts = new List<object>();

        foreach (var (file, source) in sources)
        {
            var path = Path.Combine(StatusRoot, file);
            if (!File.Exists(path)) continue;

            var fields = ParseStatusFile(path);
            var status = fields.GetValueOrDefault("status", "unknown");
            var ts     = fields.GetValueOrDefault("timestamp");
            var reason = fields.GetValueOrDefault("reason", "none");

            if (status is "warning" or "critical" or "error" or "failed")
            {
                alerts.Add(new
                {
                    source,
                    severity  = status is "critical" or "error" or "failed" ? "critical" : "warning",
                    status,
                    reason,
                    timestamp = ts,
                });
            }
        }

        return new { count = alerts.Count, alerts };
    }

    // GET /ops/backups — backup dizinlerini listeler
    private static IResult GetBackups() => Results.Ok(BuildBackups());

    private static object BuildBackups()
    {
        if (!Directory.Exists(StatusRoot))
            return new
            {
                count = 0,
                total_size_mb = 0d,
                retention_limit = BackupRetain,
                retention_used_pct = 0,
                last_backup = (string?)null,
                last_status = (string?)null,
                backups = Array.Empty<object>()
            };

        var statusFields = File.Exists(Path.Combine(StatusRoot, ".backup-status"))
            ? ParseStatusFile(Path.Combine(StatusRoot, ".backup-status"))
            : new Dictionary<string, string>();

        // Backup dizin formatı: 20260701T071527Z (YYYYMMDDTHHMMSSz)
        var dirs = Directory.GetDirectories(StatusRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => d.Name.Length >= 15 && d.Name[8] == 'T')
            .OrderByDescending(d => d.Name)
            .Select(d =>
            {
                var exportPath = Path.Combine(d.FullName, "export");
                var dumpPath   = Path.Combine(d.FullName, "platformdb.dump");
                var filesSizeMb = DirSizeMb(exportPath);
                var dbSizeMb = File.Exists(dumpPath)
                    ? Math.Round(new FileInfo(dumpPath).Length / 1_048_576.0, 2)
                    : (double?)null;
                return new
                {
                    date          = d.Name,
                    files_size_mb = filesSizeMb,
                    db_size_mb    = dbSizeMb,
                    total_size_mb = Math.Round((filesSizeMb ?? 0) + (dbSizeMb ?? 0), 2),
                    created       = d.CreationTimeUtc,
                };
            })
            .ToList();

        var totalSizeMb = Math.Round(dirs.Sum(d => d.total_size_mb), 2);
        var retentionUsedPct = BackupRetain > 0
            ? (int)Math.Floor(dirs.Count * 100.0 / BackupRetain)
            : 0;

        return new
        {
            count         = dirs.Count,
            total_size_mb = totalSizeMb,
            retention_limit = BackupRetain,
            retention_used_pct = retentionUsedPct,
            last_backup   = statusFields.GetValueOrDefault("timestamp"),
            last_status   = statusFields.GetValueOrDefault("status"),
            backups       = dirs,
        };
    }

    // GET /ops/version — build bilgileri + environment
    private static IResult GetVersion() => Results.Ok(BuildVersion());

    private static object BuildVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var commit = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown";
        var startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return new
        {
            service     = "OpsApi",
            version     = Environment.GetEnvironmentVariable("BUILD_VERSION")
                          ?? assembly.GetName().Version?.ToString()
                          ?? "1.0.0",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            commit_hash = commit,
            commit_short = commit == "unknown" ? "unknown" : commit[..Math.Min(commit.Length, 8)],
            branch      = Environment.GetEnvironmentVariable("GIT_BRANCH") ?? "unknown",
            build_time  = Environment.GetEnvironmentVariable("BUILD_TIME") ?? "unknown",
            started_at  = startedAt,
            uptime_seconds = (long)Math.Max(0, (DateTime.UtcNow - startedAt).TotalSeconds),
            timestamp   = DateTime.UtcNow,
        };
    }

    private static ServiceSnapshot? LoadServiceStatus()
    {
        var path = Path.Combine(StatusRoot, ".services-status.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("services", out var services) ||
                services.ValueKind != JsonValueKind.Array)
                return null;

            var timestampText = GetString(doc.RootElement, "timestamp");
            DateTime.TryParse(timestampText, out var timestamp);
            var status = GetString(doc.RootElement, "status") ?? "unknown";
            var containers = services.EnumerateArray().Select(s =>
            {
                var created = GetString(s, "created");
                DateTime.TryParse(created, out var createdAt);
                var startedAt = GetString(s, "started_at");
                DateTime.TryParse(startedAt, out var started);
                return new ServiceContainer(
                    GetString(s, "name") ?? GetString(s, "service") ?? "?",
                    GetString(s, "service") ?? GetString(s, "name") ?? "?",
                    GetString(s, "image") ?? "",
                    GetString(s, "status") ?? "",
                    GetString(s, "state") ?? "",
                    createdAt == default ? DateTime.MinValue : createdAt.ToUniversalTime(),
                    started == default ? (DateTime?)null : started.ToUniversalTime(),
                    TryParseInt(GetString(s, "age_seconds")),
                    TryParseInt(GetString(s, "restart_count")),
                    GetString(s, "cpu"),
                    GetString(s, "memory"));
            }).ToList();

            return new ServiceSnapshot(
                status,
                timestamp == default ? (DateTime?)null : timestamp.ToUniversalTime(),
                containers);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsContainerRunning(List<ServiceContainer>? containers, string namePart) =>
        containers?.Any(c =>
            (c.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase) ||
             c.Service.Contains(namePart, StringComparison.OrdinalIgnoreCase)) &&
            (c.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
             c.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) ||
             c.Status.Contains("healthy", StringComparison.OrdinalIgnoreCase))) == true;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static IReadOnlyCollection<string> ExtractRealmRoles(System.Security.Claims.ClaimsPrincipal user)
    {
        var realmAccess = user.FindFirst("realm_access")?.Value;
        if (realmAccess is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (!doc.RootElement.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
                return [];

            return roles.EnumerateArray()
                .Select(r => r.GetString())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r!)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, string> ParseStatusFile(string path)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
                result[line[..idx]] = line[(idx + 1)..];
        }
        return result;
    }

    private static double? DirSizeMb(string path)
    {
        if (!Directory.Exists(path)) return null;
        try
        {
            var bytes = new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
            return Math.Round(bytes / 1_048_576.0, 1);
        }
        catch { return null; }
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, out var v) ? v : null;

    private sealed record ServiceSnapshot(
        string Status,
        DateTime? Timestamp,
        List<ServiceContainer> Services);

    private sealed record ServiceContainer(
        string Name,
        string Service,
        string Image,
        string Status,
        string State,
        DateTime Created,
        DateTime? StartedAt,
        int? AgeSeconds,
        int? RestartCount,
        string? Cpu,
        string? Memory);
}
