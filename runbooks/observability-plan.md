# Observability Plan

Bu runbook production candidate seviyesindeki sistemi izlenebilir hale getirmek için uygulanacak
sırayı tanımlar. Amaç sadece log görmek değil; bir isteğin Gateway'den YonetimApi/FlotaApi'ye,
oradan FileService'e ve DB/storage katmanına nasıl aktığını ölçülebilir ve takip edilebilir
hale getirmektir.

## Mevcut Durum

Sistemde bugün şu parçalar var:

- Gateway, FileServiceApi ve app API health endpoint'leri.
- FileService teknik audit: `files.audit_events`.
- Yonetim/Filo domain audit: `yonetim.audit_events`, `filo.audit_events`.
- `X-Correlation-Id` bazı app API -> FileService çağrılarında taşınıyor.
- Docker healthcheck'leri.
- Standart container logları.

Eksik kalan üretim parçaları:

- Her request için standart request id üretimi ve response header'a yazılması.
- Gateway access log formatında request id, upstream süresi, status, method, path.
- Tüm .NET servislerinde structured JSON log ve correlation scope.
- `/metrics` endpoint'leri.
- Prometheus scrape config.
- Grafana datasource/dashboard provisioning.
- Distributed tracing backend'i ve trace id ile log/audit ilişkilendirme.
- Container/node seviyesinde CPU, memory, disk, network dashboard'u.

## Öncelik Kararı

Observability önemli ama şu production kapılarının yerini almaz:

1. Firewall + NFS allowlist.
2. Secret rotasyonu.
3. Backup/restore otomasyonu.
4. Let's Encrypt + gerçek domain.

Bu dört başlık production güvenliği ve veri dayanıklılığı için önceliklidir. Observability bu
başlıklarla paralel ilerleyebilir, ama onların kapanışını geciktirmemelidir.

## Uygulama Sırası

### Faz 1 — Request Id ve Log Temeli

Hedef:

- Gateway gelen her istekte `X-Request-Id` üretir veya gelen değeri korur.
- Gateway `X-Request-Id` ve `X-Correlation-Id` header'larını upstream'e iletir.
- YonetimApi/FlotaApi/FileServiceApi bu değeri log scope'a alır.
- Response'a aynı id yazılır.
- Audit kayıtlarındaki `correlation_id` ile loglardaki request id eşleştirilebilir.

Kapanış kanıtı:

```bash
curl -k -I https://localhost:5090/health
# X-Request-Id header'ı görünür
```

Bir upload isteği için aynı id şuralarda bulunur:

- Gateway access log.
- YonetimApi veya FlotaApi log.
- FileServiceApi log.
- `files.audit_events.correlation_id`.
- Domain audit tablosu.

### Faz 2 — Metrics

Hedef:

- Her .NET servisinde `/metrics` endpoint'i.
- HTTP request count/duration/status metrics.
- FileService upload/download/archive sayaçları.
- Storage health, DB health ve NFS hata sayaçları.
- Prometheus yalnız internal Docker network veya ops allowlist üzerinden erişir.

Önerilen ilk metrikler:

| Metrik | Kaynak | Amaç |
|---|---|---|
| HTTP request duration | Gateway + .NET servisleri | Yavaş endpoint tespiti |
| HTTP status count | Gateway + .NET servisleri | 4xx/5xx artışı |
| Upload duration/bytes | FileServiceApi | Büyük dosya darboğazı |
| Download duration/bytes | FileServiceApi | Stream performansı |
| Storage unavailable count | FileServiceApi | NFS sorunu |
| DB operation error count | API servisleri | DB erişim sorunu |
| Auth failure count | YonetimApi/FlotaApi | Token/secret/Keycloak sorunu |

### Faz 3 — Prometheus + Grafana

Hedef:

- `prometheus` servisi internal scrape yapar.
- `grafana` datasource provisioning ile Prometheus'a bağlanır.
- Dashboard dosyaları repo içinde version-controlled tutulur.
- Production'da Grafana public açılmaz; VPN, SSH tunnel veya ops allowlist ile erişilir.

İlk dashboard panelleri:

- Gateway request rate/status/latency.
- YonetimApi ve FlotaApi request rate/status/latency.
- FileService upload/download latency ve hata oranı.
- DB/NFS health.
- Container CPU/memory/disk/network.

### Faz 4 — Distributed Tracing

Hedef:

- OpenTelemetry tracing etkinleşir.
- Gateway -> YonetimApi/FlotaApi -> FileServiceApi -> PostgreSQL/HTTP çağrıları aynı trace altında izlenir.
- Trace backend olarak Jaeger veya OpenTelemetry Collector + Grafana Tempo kullanılabilir.

İlk production için tracing opsiyonel ama değerlidir. Metrics ve request-id temeli oturmadan tracing'e
geçmek önerilmez.

### Faz 5 — Alerting

İlk alarm seti:

- Gateway 5xx oranı belirli eşiği aşarsa.
- FileService `storage_unavailable` artarsa.
- Backup systemd timer başarısız olursa.
- Restore testi belirlenen sürede koşmazsa.
- PostgreSQL health düşerse.
- Disk doluluk oranı kritik eşiğe yaklaşırsa.

## İlk Implementasyon Kararı

İlk teknik iş olarak **Faz 1** yapılmalıdır. Çünkü metrics/tracing/dashboard bundan sonra daha anlamlı
hale gelir. Faz 1 küçük, düşük riskli ve mevcut mimariyi bozmaz.

Faz 1 tamamlandıktan sonra Faz 2 + Faz 3 birlikte ele alınabilir:

```text
Faz 1: request id + structured logs
Faz 2: /metrics endpointleri
Faz 3: prometheus + grafana compose profili
Faz 4: tracing backend
Faz 5: alerting
```

