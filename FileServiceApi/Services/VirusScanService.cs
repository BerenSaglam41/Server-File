using System.Net.Sockets;
using System.Text;

namespace FileServiceApi.Services;

// clamd'e (ClamAV daemon) kendi düz metin INSTREAM protokolüyle, ham TCP soket
// üzerinden konuşur — 3. parti NuGet paketi gerekmez. Fail-closed: clamd'e
// ulaşılamıyorsa veya taramada hata olursa "temiz" varsayılmaz, reddedilir.
public interface IVirusScanService
{
    Task<VirusScanResult> ScanAsync(Stream content, CancellationToken ct = default);
}

public enum VirusScanOutcome { Clean, Infected, Unavailable }

public record VirusScanResult(VirusScanOutcome Outcome, string? Detail);

public class ClamAvVirusScanService(IConfiguration config, ILogger<ClamAvVirusScanService> logger) : IVirusScanService
{
    private const int ChunkSize = 65536;

    public async Task<VirusScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        var host = config["ClamAv:Host"] ?? "clamav";
        var port = config.GetValue<int>("ClamAv:Port", 3310);

        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, connectCts.Token);

            using var stream = client.GetStream();

            // INSTREAM komutu: "z" ile başlayan komutlar null-terminated'dır.
            var command = Encoding.ASCII.GetBytes("zINSTREAM\0");
            await stream.WriteAsync(command, ct);

            var buffer = new byte[ChunkSize];
            int bytesRead;
            while ((bytesRead = await content.ReadAsync(buffer, ct)) > 0)
            {
                var lengthPrefix = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(bytesRead);
                await stream.WriteAsync(BitConverter.GetBytes(lengthPrefix), ct);
                await stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
            // Bitiş: 4 byte sıfır uzunluk.
            await stream.WriteAsync(new byte[4], ct);

            var responseBuffer = new byte[512];
            var totalRead = await stream.ReadAsync(responseBuffer, ct);
            var response = Encoding.ASCII.GetString(responseBuffer, 0, totalRead).TrimEnd('\0', '\r', '\n');

            if (response.Contains("FOUND"))
                return new VirusScanResult(VirusScanOutcome.Infected, response);

            if (response.Contains("OK"))
                return new VirusScanResult(VirusScanOutcome.Clean, response);

            // Beklenmeyen yanıt (örn. "INSTREAM size limit exceeded. ERROR") — fail-closed.
            logger.LogWarning("ClamAV beklenmeyen yanıt döndü, fail-closed uygulanıyor: {Response}", response);
            return new VirusScanResult(VirusScanOutcome.Unavailable, response);
        }
        catch (Exception ex)
        {
            // clamd'e ulaşılamıyor (bağlantı reddi, timeout, DNS vb.) — fail-closed.
            logger.LogWarning(ex, "ClamAV'a ulaşılamadı, fail-closed uygulanıyor.");
            return new VirusScanResult(VirusScanOutcome.Unavailable, ex.Message);
        }
    }
}
