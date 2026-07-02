using System.Net;
using System.Text.Json;

namespace FileServiceApi.Services;

// Files-01 üzerindeki FilesPublisher servisine (mTLS korumalı) yazma isteklerini
// gönderir. FileServiceApi artık dosya içeriğini kendi NFS mount'una yazmaz —
// bu tek canlı yazma ihtiyacı buraya taşındı (files01-nfs-model.md'nin
// "kontrollü operasyon kullanıcısı" ilkesine uygun).
public interface IFilesPublisherClient
{
    Task<(string Sha256, long SizeBytes)> PublishAsync(string relativePath, Stream content, long contentLength, CancellationToken ct = default);
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}

public class FilesPublisherException(HttpStatusCode statusCode, string body)
    : Exception($"FilesPublisher hata döndü: {statusCode} — {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public class FilesPublisherClient(IHttpClientFactory httpClientFactory) : IFilesPublisherClient
{
    public async Task<(string Sha256, long SizeBytes)> PublishAsync(
        string relativePath, Stream content, long contentLength, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("FilesPublisher");
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentLength = contentLength;

        var resp = await client.PostAsync($"publish?relativePath={Uri.EscapeDataString(relativePath)}", streamContent, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new FilesPublisherException(resp.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var sha256 = doc.RootElement.GetProperty("sha256").GetString()!;
        var sizeBytes = doc.RootElement.GetProperty("sizeBytes").GetInt64();
        return (sha256, sizeBytes);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient("FilesPublisher");
            await client.DeleteAsync($"publish?relativePath={Uri.EscapeDataString(relativePath)}", ct);
        }
        catch
        {
            // best-effort rollback — DB tarafı zaten geri alınıyor, fiziksel
            // yetim dosya kalırsa bile katalog tutarlılığı bozulmaz.
        }
    }
}
