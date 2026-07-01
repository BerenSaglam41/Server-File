using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace OpsApi.Endpoints;

public static class OpsEndpoints
{
    private static readonly string StatusRoot =
        Environment.GetEnvironmentVariable("STATUS_ROOT") ?? "/ops/status-files";

    public static void MapOpsEndpoints(this WebApplication app)
    {
        var ops = app.MapGroup("/ops").RequireAuthorization("ops.read");

        ops.MapGet("/health",   GetHealth);
        ops.MapGet("/services", GetServices);
        ops.MapGet("/disk",     GetDisk);
        ops.MapGet("/alerts",   GetAlerts);
        ops.MapGet("/backups",  GetBackups);
        ops.MapGet("/version",  GetVersion);
    }

    // GET /ops/health — her servisin HTTP health endpoint'ini çağırır
    private static async Task<IResult> GetHealth(IHttpClientFactory factory)
    {
        // gateway HTTPS-only; fileservice mTLS gerektirir — bunlar Docker socket ile /ops/services'ten izlenir
        var targets = new Dictionary<string, string>
        {
            ["yonetimapi"] = "http://yonetimapi:8080/health",
            ["flotaapi"]   = "http://flotaapi:8080/health",
            ["keycloak"]   = "http://keycloak:8080/realms/platform",
        };

        var client  = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var results = new List<object>();
        var overall = "healthy";

        foreach (var (name, url) in targets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var resp = await client.GetAsync(url);
                sw.Stop();
                var status = resp.IsSuccessStatusCode ? "healthy" : "degraded";
                if (status != "healthy") overall = "degraded";
                results.Add(new { name, status, latency_ms = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                sw.Stop();
                overall = "degraded";
                results.Add(new { name, status = "unhealthy", latency_ms = (long?)null, error = ex.Message[..Math.Min(ex.Message.Length, 80)] });
            }
        }

        return Results.Ok(new { status = overall, timestamp = DateTime.UtcNow, services = results });
    }

    // GET /ops/services — Docker socket üzerinden container listesi
    private static async Task<IResult> GetServices()
    {
        var dockerSocket = "/var/run/docker.sock";
        if (!File.Exists(dockerSocket))
            return Results.Ok(new { note = "Docker socket yok; geliştirme ortamı", services = Array.Empty<object>() });

        try
        {
            var handler = new SocketsHttpHandler();
            handler.ConnectCallback = async (_, ct) =>
            {
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(dockerSocket), ct);
                return new NetworkStream(sock, ownsSocket: true);
            };

            using var docker = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
            var json = await docker.GetStringAsync("/containers/json?all=true");

            using var doc = JsonDocument.Parse(json);
            var containers = doc.RootElement.EnumerateArray().Select(c =>
            {
                var names = c.GetProperty("Names").EnumerateArray()
                    .Select(n => n.GetString()!.TrimStart('/'))
                    .ToArray();
                return new
                {
                    name    = names.FirstOrDefault() ?? "?",
                    image   = c.GetProperty("Image").GetString(),
                    status  = c.GetProperty("Status").GetString(),
                    state   = c.GetProperty("State").GetString(),
                    created = DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("Created").GetInt64()).UtcDateTime,
                };
            }).ToList();

            return Results.Ok(new { count = containers.Count, services = containers });
        }
        catch (Exception ex)
        {
            return Results.Problem($"Docker socket hatası: {ex.Message}", statusCode: 502);
        }
    }

    // GET /ops/disk — .disk-status dosyasını okur
    private static IResult GetDisk()
    {
        var path = Path.Combine(StatusRoot, ".disk-status");
        if (!File.Exists(path))
            return Results.Ok(new { status = "unknown", note = "Henüz disk-check çalışmadı" });

        var fields = ParseStatusFile(path);
        return Results.Ok(new
        {
            status          = fields.GetValueOrDefault("status", "unknown"),
            timestamp       = fields.GetValueOrDefault("timestamp"),
            api_server_pct  = TryParseInt(fields.GetValueOrDefault("api_server_pct")),
            files01_pct     = TryParseInt(fields.GetValueOrDefault("files01_pct")),
            reason          = fields.GetValueOrDefault("reason"),
        });
    }

    // GET /ops/alerts — 3 status dosyasındaki uyarıları birleştirir
    private static IResult GetAlerts()
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

        return Results.Ok(new { count = alerts.Count, alerts });
    }

    // GET /ops/backups — backup dizinlerini listeler
    private static IResult GetBackups()
    {
        if (!Directory.Exists(StatusRoot))
            return Results.Ok(new { count = 0, backups = Array.Empty<object>() });

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
                var filesPath = Path.Combine(d.FullName, "files");
                var dbPath    = Path.Combine(d.FullName, "db");
                var exportPath = Path.Combine(d.FullName, "export");
                var dumpPath   = Path.Combine(d.FullName, "platformdb.dump");
                return new
                {
                    date          = d.Name,
                    files_size_mb = DirSizeMb(exportPath),
                    db_size_mb    = File.Exists(dumpPath)
                        ? Math.Round(new FileInfo(dumpPath).Length / 1_048_576.0, 2)
                        : (double?)null,
                    created       = d.CreationTimeUtc,
                };
            })
            .ToList();

        return Results.Ok(new
        {
            count         = dirs.Count,
            last_backup   = statusFields.GetValueOrDefault("timestamp"),
            last_status   = statusFields.GetValueOrDefault("status"),
            backups       = dirs,
        });
    }

    // GET /ops/version — build bilgileri + environment
    private static IResult GetVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        return Results.Ok(new
        {
            service     = "OpsApi",
            version     = assembly.GetName().Version?.ToString() ?? "1.0.0",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            commit_hash = Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown",
            started_at  = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            timestamp   = DateTime.UtcNow,
        });
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
}
