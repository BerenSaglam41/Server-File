# Kanıt: X-Accel-Redirect ile Byte Delivery Gateway'e Taşındı

**Tarih:** 2026-07-02
**Kapsam:** Ticket tabanlı indirme — byte artık FileServiceApi/YonetimApi'den değil, Gateway (nginx)'ten akıyor
**Durum:** ✅ Uçtan uca çalışıyor, güvenlik sınırları test edildi, 1 kozmetik fark bulundu (ETag şeması)

---

## Karar ve Kapsam

Kullanıcı, `file-service-api-contract.md`'nin "Byte delivery File-Service'ten geçmemeli" hedefini
gerçekleştirmek istedi. Önceden bu, Gateway'e NFS erişimi ve FileServiceApi'ye yeni bir güven sınırı
gerektirdiği için ayrı bir aşamaya bırakılmıştı. Bu oturumda tasarım netleştirilip **onaylı şekilde**
uygulandı:

- Gateway'e **yeni bir NFS bağlantısı açılmadı** — api-server'da zaten var olan (ve `ro`) host NFS
  mount'u (`/mnt/platform-files/export`) Gateway container'ına salt-okunur **bind-mount** edildi.
- FileServiceApi'nin ticket-consume endpoint'ine **tek, dar kapsamlı bir mTLS-only istisna** eklendi:
  sadece CN=gateway olan sertifikalar, JWT olmadan bu **tek** endpoint'i çağırabilir. Diğer tüm
  `/internal/*` endpoint'ler (ticket oluşturma dahil) hem mTLS hem JWT istemeye devam ediyor.

## Mimari (Yeni Akış)

```
Client -> Gateway -> YonetimApi (RBAC + ownership) -> FileServiceApi: ticket oluştur (mTLS+JWT, değişmedi)
Client, ticket'ı Gateway'in YENİ /files/download/{ticket} yoluna götürür
Gateway -> FileServiceApi: /internal/download-tickets/{ticket}/consume (SADECE mTLS, CN=gateway, JWT YOK)
FileServiceApi: ticket'ı atomik tüketir, dosyanın aktif olduğunu doğrular, X-Accel-Redirect header'ı döner
  (byte'ı KENDİSİ OKUMAZ)
Gateway (nginx): X-Accel-Redirect'i yakalar, /protected-download/ (internal, dışarıdan asla erişilmez)
  location'ından, host'tan bind-mount edilmiş salt-okunur export/ dizininden byte'ı doğrudan servis eder
```

## Ne Değişti

- `FileServiceApi/Endpoints/DownloadTicketEndpoints.cs → ConsumeTicketAsync`: Artık `StreamContentAsync`
  çağırmıyor. mTLS client sertifikasının CN'ini kontrol ediyor (`gateway` değilse `403`), ticket'ı atomik
  tüketiyor, `X-Accel-Redirect: /protected-download/{relativePath}` + `ETag`/`Content-Disposition`/
  `Content-Type` header'larını set edip boş body ile `200` dönüyor.
- `POST /internal/download-tickets` (oluşturma) **değişmedi** — hâlâ mTLS+JWT, `AllowedClientCNs`'e
  sadece `gateway` eklendi ama bu endpoint JWT olmadan hâlâ `401` veriyor (test edildi).
- `certs/generate-certs.sh`: `gateway-client` (CN=gateway, clientAuth) sertifikası eklendi.
- `docker-compose.yml`: Gateway'e `gateway-client.crt/.key` + `ca.crt` mount edildi, host'un mevcut
  `export/` mount'u `/protected-files:ro` olarak bind-mount edildi.
- `nginx/nginx.conf`: `/files/download/{ticket}` (mTLS ile FileServiceApi'yi çağırır) ve
  `/protected-download/` (`internal`, sadece X-Accel ile erişilir) location'ları eklendi.
- `YonetimApi/Endpoints/DownloadTicketEndpoints.cs`: Eski `GET /api/personnel/download/{ticket}` proxy
  endpoint'i **tamamen kaldırıldı** (artık gerekmiyor, Gateway doğrudan çağırıyor). `downloadUrl` artık
  `/files/download/{ticket}` döner.

## Testler — Hepsi Geçti

### Uçtan Uca Akış

1. Ticket oluşturuldu, `downloadUrl: /files/download/<ticket>` döndü.
2. Bu URL'e cookie'siz `GET` → `200`, `Content-Length: 110567`, doğru `Content-Type`/
   `Content-Disposition`. Doğrudan indirmeyle `diff` → **birebir aynı içerik**.
3. Aynı ticket tekrar denendi → `404` (tek kullanım korunuyor, X-Accel'e rağmen değişmedi).
4. `Range: bytes=0-99` ile indirme → `206 Partial Content`, `Content-Length: 100` — nginx'in kendi statik
   dosya sunumu Range'i doğru destekliyor.

### Güvenlik Sınırları

| Test | Sonuç |
|---|---|
| `/protected-download/...` doğrudan dışarıdan istek | `404` (nginx `internal` direktifi) |
| Eski `GET /api/personnel/download/{ticket}` hâlâ var mı | `404` (tamamen kaldırıldı) |
| `/internal/` toptan blok hâlâ çalışıyor mu | `404` |
| Yanlış CN, TLS'i geçmeyen (CN=fileservice, allowlist'te yok) | TLS handshake seviyesinde reddedildi |
| Yanlış CN, TLS'i geçen ama gateway olmayan (CN=yonetimapi) | Uygulama seviyesinde `403 forbidden`, audit'te `reason_code=gateway_cn_required`, `app_code=yonetimapi` kaydedildi |
| Doğru CN (gateway) ama JWT'siz `POST .../download-tickets` (oluşturma) | `401` — **diğer endpoint'ler hâlâ JWT istiyor, doğrulandı** |
| Başarılı consume çağrılarının audit'i | `files.audit_events`'te `action=read, app_code=gateway` doğru yazılıyor |

### Regresyon

- `tools/server-smoke-test.sh` → 23/23 `[OK]`.
- `tools/server-safe-test-suite.sh` → tüm senaryolar `[OK]` (403 yetkilendirme matrisi, 3.8MB dosya
  indirme, 20 eşzamanlı login dahil).
- `platform-backup.service` (+ otomatik tetiklenen `restore-test`) → `ExecMainStatus=0`.

## Bilinen, Kozmetik Fark: ETag Şeması

Doğrudan indirme yolu (`/api/personnel/{id}/cv/content` vb.) `ETag: "sha256:<hash>"` formatı kullanıyor.
X-Accel yolunda ise nginx, dosyayı kendi statik dosya modülüyle sunduğu için **kendi ETag'ini**
(mtime+size tabanlı, örn. `"6a43a679-1afe7"`) üretiyor — FileServiceApi'nin gönderdiği sha256 tabanlı
ETag, nginx'in X-Accel-Redirect sonrası kendi internal location'ının ürettiği header'larla **geçersiz
kılınıyor**. Bu, nginx'in standart, belgelenmiş davranışı (X-Accel hedefindeki dosya bilgisi, backend'in
gönderdiği ETag'in önüne geçer).

**Etki değerlendirmesi:** Fonksiyonel bir sorun değil — conditional GET/304 caching nginx'in kendi ETag
şemasıyla hâlâ doğru çalışıyor (tek fark, format tutarsızlığı). Ayrıca ticket'lar tek kullanımlık olduğu
için, FileServiceApi'nin kendi sha256-ETag/304 kısayolu zaten pratikte neredeyse hiç tetiklenmiyordu (aynı
ticket'la ikinci istek zaten `404` alıyor) — bu X-Accel değişikliğinden önce de böyleydi. İstenirse V2'de
nginx `add_header`/`$upstream_http_etag` ile sha256 ETag'i korunacak şekilde ayarlanabilir, ama şu an
işlevsel bir öncelik değil.

## Kapsam Dışı (Bilinçli, Değişmedi)

- Ticket dışı, normal `/api/personnel/{id}/cv/content` gibi indirme endpoint'leri **hâlâ eski V1 backend
  proxy akışını** kullanıyor (byte FileServiceApi→YonetimApi→Gateway üzerinden akıyor) —
  `file-service-api-contract.md`'nin kendi ayrımına göre bu zaten beklenen: X-Accel/ticket modeli özellikle
  "performans baskısı" senaryoları için V2 opsiyonu, V1 baseline'ı değiştirmiyor.
- Client React uygulaması ticket sistemini hiç kullanmıyor (backend-only özellik, doğrulandı — kod
  tabanında hiç referans yok), bu yüzden bu değişiklik hiçbir frontend kodunu etkilemedi/kırmadı.
