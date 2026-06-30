# Proje Durumu ve Devam Planı

Bu dosya, File-Service API / YonetimAPI projesinin şu ana kadar nereye geldiğini ve
sırada ne olduğunu anlatır. Yeni bir Claude oturumuna bu dosyayı ve aşağıda listelenen
5 .md dosyasını verirsen, tam olarak nereden devam edeceğini bilir.

## Okuman gereken kaynak dosyalar (proje kararları buradan geliyor)

Bu 5 doküman, projenin mimari kararlarının kaynağı. Kod buradaki kararları uygular,
bu kararları tartışmaya açmaz:

- `file-catalog-model.md` — merkezi dosya kataloğu (4 tablo) tasarımı
- `file-service-api-contract.md` — File-Service API endpoint sözleşmesi, auth modeli, hata kodları
- `file-service-intern-brief.md` — uygulama görev listesi, test senaryoları
- `files01-nfs-model.md` — Files-01 (NFS) dizin yapısı, dosya adlandırma kuralları
- `20-files01-nfs-personel-dosya-plani.md` — proje issue'su, karar geçmişi

## Genel mimari

```
Client -> Gateway-01 -> YonetimAPI -> File-Service API -> DB-01 (files.* şeması) -> Files-01 (storage)
```

- **Files-01**: sadece binary depolama. Gerçek ortamda NFS, test ortamında düz klasör (`test-storage/export/`).
- **DB-01 / `files.*` şeması**: merkezi dosya kataloğu. 4 tablo: `objects`, `references`, `app_policies`, `audit_events`.
- **File-Service API**: dosya kataloğunun tek sahibi. Policy kontrolü, audit, stream — hepsi burada.
- **YonetimAPI**: ilk consumer uygulama. Data-scope kontrolü yapar, FileService'i kendi servis kimliğiyle çağırır.

**Auth modeli:** OAuth2 client credentials (Keycloak `platform` realm). YonetimApi → FileServiceApi arası service JWT, Client → YonetimApi arası user JWT. Detay: "Auth mimarisi" bölümü.

## Test ortamı

**Birincil mod: Docker Compose** — tüm servisler container içinde.

- **Servis portları**: Gateway = `5090`, YonetimApi = `5076`, FileServiceApi = `5205` (iç), Keycloak = `8080`, PostgreSQL = container içi
- **DİKKAT:** macOS port 5000'i AirPlay (ControlCenter) için kullanıyor → Gateway 5090'da çalışır
- **Keycloak realm**: `platform` — `realm-platform.json` ile her fresh start'ta auto-import

### Proje klasör yapısı

```
dosya-sistemi-projesi/
  ├── db/
  │     ├── docker-init/
  │     │     ├── 01-schema.sql        <- files.* + yonetim.* + filo.* şema (Docker init)
  │     │     └── 02-seed.sql          <- app_policies + team_members seed
  │     └── file-catalog-schema-v4.sql <- eski manuel şema (referans amaçlı)
  ├── keycloak/
  │     └── realm-platform.json        <- Keycloak realm auto-import (roller + kullanıcılar)
  ├── certs/                           <- mTLS sertifikaları (key'ler .gitignore'da)
  │     ├── generate-certs.sh          <- CA + tüm servis sertifikalarını üretir
  │     ├── ca.crt                     <- Platform CA (10 yıl)
  │     ├── fileservice.crt/key        <- FileServiceApi server cert (CN=fileservice)
  │     ├── yonetimapi.crt/key         <- YonetimApi client cert
  │     └── filoapi.crt/key            <- FlotaApi client cert
  ├── docker-compose.yml               <- 6 servis: postgres, keycloak, fileservice, yonetimapi, flotaapi, gateway
  ├── docker-compose.override.yml      <- Dev-only port mapping (5205, 5076, 5077)
  ├── runbooks/
  │     ├── files01-nfs-setup.md               <- NFS kurulum runbook (üretim adımları)
  │     └── files01-nfs-kurulum-notlari.md     <- UTM VM kurulum oturumu notları
  ├── tools/
  │     └── migrate-legacy-files.py    <- Legacy dosya migration aracı
  ├── Gateway/                         <- YARP reverse proxy, port 5090 (Gateway-01)
  │     ├── Dockerfile
  │     ├── Program.cs
  │     └── appsettings.json
  ├── FileServiceApi/                  <- .NET minimal API, mTLS HTTPS:8080 (iç ağ)
  │     ├── Dockerfile
  │     ├── Models/
  │     │     ├── FileObject.cs
  │     │     ├── FileReference.cs
  │     │     ├── AppPolicy.cs
  │     │     ├── AuditEvent.cs
  │     │     └── RelationTypeConfig.cs <- kardinalite tanımı (single/multi)
  │     ├── Data/AppDbContext.cs
  │     ├── Services/AuditService.cs
  │     ├── Endpoints/FileEndpoints.cs <- 6 endpoint + magic-byte + staging + kardinalite
  │     ├── Program.cs                 <- Kestrel mTLS + JwtBearer + health check
  │     └── appsettings.json           <- ReadPath / StagingPath / ExportPath
  ├── YonetimApi/                      <- .NET minimal API, port 5076
  │     ├── Dockerfile
  │     ├── Services/
  │     │     ├── TokenService.cs
  │     │     ├── DomainAuditService.cs
  │     │     └── PermissionService.cs  <- IPermissionService / PersonnelPermissionService
  │     ├── Endpoints/PersonnelEndpoints.cs
  │     ├── Program.cs                 <- mTLS HttpClient + MapInboundClaims=false
  │     └── appsettings.json
  ├── FlotaApi/                        <- .NET minimal API, port 5077
  │     ├── Dockerfile
  │     ├── Services/
  │     │     ├── TokenService.cs
  │     │     ├── DomainAuditService.cs
  │     └── Endpoints/VehicleEndpoints.cs
  │     ├── Program.cs                 <- mTLS HttpClient (filoapi cert)
  │     └── appsettings.json
  └── test-storage/                    <- Files-01 local simülasyonu
        ├── export/                    ← ReadPath + ExportPath
        │     └── .probe               ← health check probe
        ├── staging/                   ← StagingPath (upload geçici alan)
        ├── manifests/                 ← migration manifestleri
        └── restore-tests/             ← restore test çıktıları
```

### Başlatma

**Docker Compose (birincil):**
```bash
docker compose up --build -d
docker compose ps   # tüm servisler healthy olana kadar bekle
```

**Yerel geliştirme (ikincil — sadece FileServiceApi/YonetimApi geliştirirken):**
```bash
# Önce Postgres ve Keycloak Docker'da çalışıyor olmalı
docker compose up -d postgres keycloak

cd FileServiceApi && dotnet run --launch-profile http
cd YonetimApi    && dotnet run --launch-profile http
cd Gateway       && dotnet run --launch-profile http
```

Client istekleri `http://localhost:5090/api/...` üzerinden gitmeli.

### Test için token alma

```bash
# p001 token — claim yoksa YonetimApi preferred_username -> P001 fallback'i kullanır
USER_TOKEN=$(curl -s -X POST http://localhost:8080/realms/platform/protocol/openid-connect/token \
  -d grant_type=password -d client_id=frontend-test \
  -d username=p001 -d password=Demo1234! | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

# Gateway üzerinden (doğru yol) → 200/404 (erişim var, dosya yoksa 404)
curl -H "Authorization: Bearer $USER_TOKEN" http://localhost:5090/api/personnel/P001/cv

# Başkasının personeline → 403 data_scope_denied
curl -H "Authorization: Bearer $USER_TOKEN" http://localhost:5090/api/personnel/P002/cv

# Gateway health check
curl http://localhost:5090/health
```

## FileServiceApi — 6 endpoint (tümü test edildi)

### `GET /internal/files/resolve` ✅
Query: `domain`, `entityType`, `entityId`, `relationType`. Headers: `Authorization: Bearer <JWT>` (zorunlu), `X-Correlation-Id`, `X-Actor-User-Id`.

Kontrol sırası: JWT yoksa/geçersizse 401 → JWT `app_code` claim'den policy lookup → policy yoksa 403 → policy domain/relationType'a izin vermiyorsa 403
→ **`is_primary = true AND status = active` referans** bulunamazsa 404 → object bulunamazsa/active değilse 404 → 200 JSON.

**Kritik:** Sorgu `r.IsPrimary && r.Status == "active"` filtresiyle yapılır. Single-primary tipler için bu tek satır döndürür. Multi-primary tipler için `FirstOrDefaultAsync` arbitrary bir satır döndürür — bunlar için `/list` endpoint'i kullanılmalı.

Test: 401, 403 (iki sebep), 404, 200.

### `GET /internal/files/{fileId}` ✅
Sadece metadata, binary yok. fileId ile direkt `objects` tablosuna gider.

Kontrol sırası: JWT `app_code` claim → policy.can_read → object active mi → policy domain'e izin veriyor mu → 200 JSON.

Response: `fileId`, `domain`, `contentType`, `extension`, `originalFileName`, `sizeBytes`, `sha256`,
`classification`, `status`, `createdAt`, `etag`.

Test: 200, 401, 403, 404.

### `GET /internal/files/{fileId}/content` ✅
Binary stream. `Results.Stream()` kullanılıyor (`Results.File()` değil — PhysicalFileHttpResult dosya
mtime'ından kendi ETag'ını üretip bizimkini ezerdi).

Kontrol sırası: JWT `app_code` claim → policy.can_read → object active mi → policy domain → path traversal kontrolü
→ If-None-Match == ETag ise 304 → disk'te dosya var mı (503) → header'ları set et → stream et.

Özellikler:
- SHA256 tabanlı ETag: `"sha256:<hash>"`
- If-None-Match → 304 Not Modified
- Content-Disposition: resimler `inline`, dökümanlar `attachment; filename="<originalFileName>"`
- Path traversal koruması: `Path.GetFullPath()` ile root boundary kontrolü
- Range / 206 Partial Content: `enableRangeProcessing: true`

Test: 200, 206, 304, 401, 403, 404, 503.

### `POST /internal/files` ✅
Multipart form-data upload. Form alanları: `file`, `domain`, `entityType`, `entityId`, `relationType`,
`classification`, `originalFileName`.

Kontrol sırası: JWT `app_code` claim → policy.can_create → form'da dosya var mı (400) → policy domain/relationType
→ boyut limiti (413) → uzantı izinli mi (415: pdf/jpg/jpeg/png/webp) → **magic-byte kontrolü (415)** →
**kardinalite kontrolü** (bkz. Kardinalite Sistemi bölümü) →
file_id üret → shard path üret (`domain/XX/XX/uuid.ext`) → **staging'e yaz → SHA256 staging'den hesapla → atomic File.Move → export** → DB kayıt → audit. DB başarısız olursa export dosyası silinir (rollback).

Magic-byte kontrolü (ilk 12 byte):
- PDF: `%PDF` = `25 50 44 46`
- JPEG: `FF D8 FF`
- PNG: `89 50 4E 47 0D 0A 1A 0A`
- WebP: `RIFF....WEBP`

Test: 200 (gerçek dosya), 401, 403, 413, 415 (yanlış uzantı), 415 (magic-byte mismatch).

**DİKKAT:** `yeni-test-cv.pdf` gerçek PDF değil (düz metin). Upload testleri için gerçek magic byte gerekli:
```python
# Terminal'de bir kere çalıştır:
python3 -c "open('/tmp/t.pdf','wb').write(b'%PDF-1.4\n%%EOF\n')"
```

### `POST /internal/files/{fileId}/archive` ✅
Dosyayı `archived` statüsüne çeker. Hard delete V1'de yok.

Kontrol sırası: JWT `app_code` claim → policy.can_archive → object var mı (404) → policy domain → **status != active ise** idempotent 200 dön → `objects.status = archived` + `references.status = revoked` → audit.

**Kritik:** `references.status` da `revoked`'a çekilmezse `uq_primary_per_entity` constraint kalkmaz ve bir sonraki upload aynı entity/relationType için constraint ihlali verir. İkisi birlikte aynı `SaveChangesAsync` içinde güncelleniyor.

**İdempotent kural:** `status != "active"` olan her nesne (archived/revoked/deleted) için archive no-op'tur. Yalnız `"archived"` değil tüm terminal durumlar kapsanır.

Test: 200 (archive), 200 (idempotent ikinci çağrı), 401, 403, 404.

### `GET /internal/files/list` ✅
Bir entity'nin tüm aktif dosyalarını döner.

Query: `domain`, `entityType`, `entityId`. Headers: `Authorization: Bearer <JWT>`.

Filtre: `references.is_primary = true AND references.status = active AND objects.status = active`.
Response: aynı entity'nin CV ve fotoğrafı varsa her ikisi de dizide döner.

Test: 200 (liste), 401, 403.

### Kritik not — AuditService fileId kuralı

`fileId` parametresi, sadece nesnenin DB'de GERÇEKTEN var olduğu kesinleştikten **sonra** geçilmeli.
Öncesinde `null` geçilmeli. Sebep: `audit_events.file_id` üzerinde FK var (`files.objects`), var olmayan
UUID geçmek `DbUpdateException` fırlatır. Bu hata bir kez yaşandı, düzeltildi.

## YonetimApi — 16 endpoint

`PersonnelEndpoints.cs` — shared helper pattern: `ProxyGetMetadataAsync`, `ProxyGetContentAsync`,
`ProxyUploadAsync`, `ProxyArchiveAsync`, `ListPersonnelFilesAsync`. Her helper `relationType` parametresi alır.
**Arşivleme mantığı artık YonetimApi'de değil, FileServiceApi'nin `CreateFileAsync`'inde** (kardinalite konfigürasyonuna göre).

| Endpoint | Kardinalite | Açıklama |
|---|---|---|
| `GET /api/personnel/{id}/files` | — | Tüm aktif primary dosyaları listele |
| `GET /api/personnel/{id}/cv` | single | CV metadata (resolve proxy) |
| `GET /api/personnel/{id}/cv/content` | single | CV stream |
| `POST /api/personnel/{id}/cv` | single | CV yükleme — FileServiceApi eskiyi arşivler |
| `POST /api/personnel/{id}/cv/archive` | single | CV açık arşivleme |
| `GET /api/personnel/{id}/photo` | single | Fotoğraf metadata |
| `GET /api/personnel/{id}/photo/content` | single | Fotoğraf stream |
| `POST /api/personnel/{id}/photo` | single | Fotoğraf yükleme — FileServiceApi eskiyi arşivler |
| `POST /api/personnel/{id}/photo/archive` | single | Fotoğraf açık arşivleme |
| `GET /api/personnel/{id}/official-document` | single | Resmi evrak metadata |
| `GET /api/personnel/{id}/official-document/content` | single | Resmi evrak stream |
| `POST /api/personnel/{id}/official-document` | single | Resmi evrak yükleme |
| `POST /api/personnel/{id}/official-document/archive` | single | Resmi evrak arşivleme |
| `POST /api/personnel/{id}/document` | multi | Belge yükleme — eskiler korunur |
| `POST /api/personnel/{id}/attachment` | multi | Ek dosya yükleme — eskiler korunur |

Tüm endpoint'lerde:
- `Authorization: Bearer <service-token>` — YonetimApi, Keycloak'tan aldığı service token'ı ekler (client bunu göremez/değiştiremez)
- `X-Actor-User-Id` — YonetimApi'nin doğruladığı user JWT'nin `preferred_username` claim'inden set edilir (client'ın gönderdiği değere güvenilmez)
- `X-Correlation-Id` client'tan forward edilir
- `Range` ve `If-None-Match` header'ları FileService'e iletilir

Content endpoint header forwarding — kritik HttpClient davranışı:
- `Content-Disposition`, `Content-Range` → `contentResponse.Content.Headers` (Content headers)
- `ETag`, `Accept-Ranges` → `contentResponse.Headers` (Response headers)
- Yanlış koleksiyona bakılırsa header boş döner (eski kodda bu bug vardı, düzeltildi)

## Veritabanı seed (Docker)

Docker Compose her `down -v` sonrasında PostgreSQL volume sıfırlanır; `db/docker-init/` init script'leri yeniden çalışır.

**Kalıcı seed (`02-seed.sql`):**
```sql
-- files.relation_type_config (kardinalite)
('cv',                'single', ...)  ('photo',   'single', ...)  ('official_document', 'single', ...)
('document',          'multi',  ...)  ('attachment','multi', ...)  ('report',            'multi',  ...)

-- files.app_policies
('yonetimapi', ARRAY['personnel'], ARRAY['photo','cv','official_document','document','attachment'],     true, true, true, 10485760)
('filoapi',    ARRAY['fleet'],     ARRAY['photo','document','official_document','attachment','report'], true, true, true, 20971520)
```

Test sırasında oluşturulan `files.objects` / `files.references` kayıtları geçicidir — `docker compose down -v` sonrasında silinir. Yerel Postgres.app'teki manuel test verisi (test_personel_1 vb.) artık birincil test ortamı değildir.

## Auth mimarisi (TAMAMLANDI ✅)

Üç katmanlı auth: kullanıcı kimliği (Keycloak JWT) + iş izni (YonetimApi RBAC) + servis kimliği (mTLS).

```
Client ─(user JWT)──▶ Gateway ──▶ YonetimApi ─(service JWT + client cert)──▶ FileServiceApi
        Keycloak              RBAC kontrolü       Keycloak client_credentials      JWT + mTLS
        password grant        IPermissionService  + mTLS ile FileServiceApi'ye     app_code claim
```

**Keycloak realm `platform` içeriği:**
- Client `yonetimapi` — confidential, client_credentials, `app_code: "yonetimapi"` hardcoded claim
- Client `filoapi` — confidential, client_credentials, `app_code: "filoapi"` hardcoded claim
- Client `frontend-test` — public, password grant; `personnel_id` + `vehicle_id` + `roles` (realm-roles mapper) claim'leri
- Kullanıcılar ve rolleri → bkz. **RBAC** bölümü

**FileServiceApi auth:**
- Kestrel HTTPS + `ClientCertificateMode.RequireCertificate` → CN izin listesi + CA chain doğrulaması
- `AddJwtBearer` → Keycloak JWKS ile token doğrulama
- `app_code` = JWT `app_code` claim
- Her istekte iki katman: TLS el sıkışması (cert) + HTTP seviyesinde JWT

**YonetimApi auth:**
- Gelen user JWT → `AddJwtBearer` + `MapInboundClaims = false` ile doğrulanır
  - `MapInboundClaims = false` zorunlu: .NET varsayılan olarak `"roles"` claim adını `ClaimTypes.Role` URI'sine eşler; bu ayar olmadan `user.FindAll("roles")` boş döner
- `actor` = JWT `preferred_username` claim
- **İzin kontrolü**: `IPermissionService.CanReadAsync` / `CanWriteAsync` → `permission × action × scope` modeli → yoksa 403
  - Detay: **RBAC** bölümüne bak
- `ITokenService` (singleton) → `client_credentials` grant ile service token alır, 30 saniye erken expire eder
- FileServiceApi'ye tüm istekler: `Authorization: Bearer <service-token>` + mTLS client cert

**Test sonuçları:**
- JWT olmadan YonetimApi → 401 ✅
- user JWT ile YonetimApi → 200 ✅
- JWT olmadan FileServiceApi direkt → 401 ✅
- Sertifikasız FileServiceApi direkt → TLS reddi (HTTP 000) ✅
- p001 → kendi personeli → 200/404 ✅
- p001 → başkasının personeli → 403 ✅
- hr001/adm001 → herkes okuma + yazma → 200 ✅
- m001/m002/m003 → ekibi okuma, yazma yok → 200 / 403 ✅
- actor audit tablosunda `preferred_username` (`p001`, `hr001`, vb.) ✅
- app_code audit tablosunda `yonetimapi` (service token'dan) ✅
- 304 Not Modified (ETag) ✅
- Upload + magic-byte ✅
- Sahte PDF → 415 ✅

## Files-01 NFS Entegrasyonu (TAMAMLANDI ✅)

UTM Ubuntu VM, Files-01 (gerçek NFS sunucusu) olarak yapılandırıldı. Mac üzerindeki Docker servisleri artık `test-storage/` yerine NFS üzerinden dosya okuma/yazma yapıyor.

**Topoloji:**
- Ubuntu VM IP: `192.168.64.3` — NFS server (`/srv/files` export)
- Mac: `/Volumes/platform-files` → NFS mount → Docker container `/app/storage`

**Yapılan değişiklikler:**
- `docker-compose.yml`: `./test-storage` → `/Volumes/platform-files`
- `FileServiceApi/appsettings.json`: tüm path'ler `/Volumes/platform-files/...`

**Bilinen kısıtlar (dev ortamı):**
- `/Volumes/platform-files` mount Mac reboot'ta kaybolur — `sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files` ile yeniden mount gerekir
- Ubuntu'da `chmod -R 777 /srv/files` uygulandı (üretimde grup modeli kullanılacak)

Detay: `runbooks/files01-nfs-kurulum-notlari.md`

## NOT YAPILANLAR (kasıtlı kapsam dışı bırakılan)
- **ETag / If-None-Match — YonetimApi tarafında**: YonetimApi kendi ETag'ını üretmiyor;
  FileService'ten gelen ETag'ı client'a iletiyor. Bu V1 için yeterli.

## Dosya versiyonlama (TAMAMLANDI ✅)

Yeni dosya yüklenirken kardinalite konfigürasyonuna göre davranış belirlenir:

- **single-primary tipler** (cv, photo, official_document): `CreateFileAsync` (FileServiceApi) eski aktif primary'yi atomik olarak `archived` + `revoked` yapar, yeni dosyayı `active + is_primary=true` ekler. Tüm değişiklikler tek `SaveChangesAsync` içinde gerçekleşir.
- **multi-primary tipler** (document, attachment, report): Eski kayıtlara dokunulmaz; yeni dosya `active + is_primary=true` olarak eklenir. Tüm aktif dosyalar listede görünür.
- `ArchiveFileAsync` (FileServiceApi): `objects.status = archived` + `references.status = revoked` birlikte güncelleniyor (hem single hem multi için elle arşivleme yolu).
- **YonetimApi `ProxyUploadAsync`**: önceki oturumda bulunan "resolve → archive öncesi" bloğu kaldırıldı; kardinalite mantığı tamamen FileServiceApi'ye taşındı.

## Domain audit mimarisi (TAMAMLANDI ✅)

Sözleşmedeki iki katmanlı audit uygulandı:

- **FileService** → `files.audit_events` (teknik: hangi app, hangi dosya, hangi eylem)
- **YonetimApi** → `yonetim.audit_events` (domain: hangi kullanıcı, hangi personel, hangi iş olayı)

YonetimApi DB bağlantısı için `Npgsql 10.0.3` paketi eklendi. `IDomainAuditService` / `DomainAuditService` singleton olarak kaydedildi. Bağlantı dizesi: `appsettings.json` → `ConnectionStrings:PlatformDb`.

`yonetim.audit_events` tablo alanları: `personnel_id`, `actor`, `action`, `result`, `reason_code`, `correlation_id`, `created_at`.

Her endpoint için yazılan action'lar (PascalCase, `DomainAction(relationType, verb)` helper ile üretiliyor):
- `PersonnelCvViewed` / `PersonnelPhotoViewed` — metadata okuma
- `PersonnelCvDownloaded` / `PersonnelPhotoDownloaded` — stream indirme
- `PersonnelCvUploaded` / `PersonnelPhotoUploaded` — yükleme
- `PersonnelCvArchived` / `PersonnelPhotoArchived` — arşivleme
- `PersonnelFilesListed` — liste

`data_scope_denied` durumlarında da `result = denied, reason_code = data_scope_denied` yazılıyor. Audit hatası ana akışı engellemez (try/catch + log).

## Gateway-01 (TAMAMLANDI ✅)

YARP (Yet Another Reverse Proxy) tabanlı .NET gateway projesi eklendi.

- Port `5090` — client entry point (5000 macOS AirPlay'e çakışıyor)
- Route: `GET|POST /api/{**catch-all}` → YonetimApi `http://localhost:5076`
- Health check: `GET /health` → `{ status: "healthy", service: "Gateway-01" }`
- FileServiceApi (`5205`) ve YonetimApi (`5076`) production'da dışa kapalı tutulur; client yalnız `5090`'a erişir
- Route ve cluster config `appsettings.json`'da — yeni uygulama eklenince buraya yeni route yazılır

## App İzolasyonu (TAMAMLANDI ✅)

`files.app_policies` tablosundaki `app_code` bazlı domain/relationType izinleri test edildi.

- `filoapi` Keycloak client'ı oluşturuldu (`kcadm.sh` ile Docker container içinde)
- `files.app_policies`: `('filoapi', ARRAY['fleet'], ARRAY['photo','document'], true, true, true, 20971520)`
- Test sonuçları:
  - filoapi → fleet domain: ✅ 200
  - filoapi → personnel domain: ✅ 403 (çapraz domain yasak)
  - yonetimapi → fleet domain: ✅ 403 (çapraz domain yasak)
  - filoapi → fleet + cv relationType: ✅ 403 (izinsiz dosya tipi)

**Önemli:** `filoapi` Keycloak client'ı `realm-platform.json`'a kalıcı olarak eklendi.
Secret: `filoapi-secret`. Mevcut instance'da secret uyumsuzluğu `kcadm.sh` ile düzeltildi.
`docker compose down -v && up` sonrasında otomatik import edilir.

## Hash Verification (TAMAMLANDI ✅)

`FileServiceApi/Endpoints/FileEndpoints.cs` → `GetContentAsync`:

Dosya binary'si diskten okunmadan önce SHA256 doğrulaması yapılıyor:
- Disk'teki hash ≠ DB'deki `objects.sha256` → 409 `hash_mismatch` + audit kaydı
- Binary disk'te yoksa → 503 `storage_unavailable`

Test sonuçları (BIN-1/2/3):
- Normal indirme: ✅ 200
- Binary missing: ✅ 503
- Hash mismatch: ✅ 409

## Health Check (TAMAMLANDI ✅)

`GET /health` — FileServiceApi üzerinde, auth gerektirmez.

İki kontrol yapar:
- **storage**: `FileStorage:ReadPath/.probe` dosyasını okumayı dener. Başarısızsa `probe_not_found` / `probe_read_failed`.
- **database**: `SELECT 1` sorgusu. Başarısızsa `db_unreachable`.

Her iki check geçerse HTTP 200, herhangi biri çökerse HTTP 503 + hangi component'in neden çöktüğü JSON'da gösterilir.

```json
// 200 healthy
{"status":"healthy","service":"FileServiceApi","checks":{"storage":{"status":"healthy"},"database":{"status":"healthy"}}}

// 503 unhealthy (storage down)
{"status":"unhealthy","service":"FileServiceApi","checks":{"storage":{"status":"unhealthy","reason":"probe_not_found"},"database":{"status":"healthy"}}}
```

NFS entegrasyonu tamamlandı — bkz. "Files-01 NFS Entegrasyonu" bölümü ve `runbooks/files01-nfs-kurulum-notlari.md`.

Test: H1 (normal → 200) ✅, H2 (storage down → 503) ✅, H3 (restore → 200) ✅

## Staging → Export Akışı (TAMAMLANDI ✅)

`file-service-api-contract.md` adım 5 — *"Binary Files-01 staging/export akışıyla yazılır"* tam olarak uygulandı.

**Upload akışı (`FileEndpoints.cs → CreateFileAsync`):**
1. Binary `StagingPath`'e yazılır (`/srv/files/staging` veya test: `test-storage/staging`).
2. SHA256, staging dosyasından hesaplanır (disk write bütünlüğü de doğrulanır).
3. `File.Move` ile staging → export atomic rename (aynı FS → rename).
4. DB kayıtları oluşur (`files.objects` + `files.references`).
5. DB insert başarısız olursa export dosyası silinir (rollback).

**Config anahtarları:**

| Anahtar | Dev (UTM NFS) değeri | Production hedefi |
|---|---|---|
| `FileStorage:ReadPath` | `/Volumes/platform-files/export` | `/mnt/platform-files` (NFS ro mount) |
| `FileStorage:StagingPath` | `/Volumes/platform-files/staging` | `/srv/files/staging` (Files-01 yerel) |
| `FileStorage:ExportPath` | `/Volumes/platform-files/export` | `/srv/files/export` |

**Dizin yapısı** (`files01-nfs-model.md` ile birebir uyumlu):
```
/srv/files/
  export/            ← NFS export / ReadPath
  staging/           ← Upload yazma alanı — üretimde NFS dışı, Files-01 yerel
  manifests/         ← Migration manifestleri (PII yok)
  restore-tests/     ← Restore test çıktıları (PII yazılmaz)
```

**Dev ortamı istisnası:** Mevcut UTM kurulumunda `/srv/files` tamamı NFS üzerinden mount ediliyor (`192.168.64.3:/srv/files`), dolayısıyla `staging/` de NFS üzerinden erişilebilir. Bu durum `files01-nfs-kurulum-notlari.md`'de "eksik" olarak işaretlenmiş. Üretimde `staging/` Files-01 host'unda yerel kalmalı, NFS yalnız `export/`'u sunmalı; atomic `File.Move` aynı FS garantisini bu şekilde korur.

## ReadPath / StagingPath / ExportPath Ayrımı (TAMAMLANDI ✅)

MD'lerin NFS read-only sınırına ve staging akışına uygun olarak `FileStorage:RootPath` üçe bölündü:

| Anahtar | Kullanım | Production değeri |
|---|---|---|
| `FileStorage:ReadPath` | Dosya okuma + health check probe | `/mnt/platform-files` (NFS ro mount) |
| `FileStorage:StagingPath` | Upload ilk yazma — üretimde NFS dışı, dev'de NFS üzerinde (bkz. dev istisnası) | `/srv/files/staging` |
| `FileStorage:ExportPath` | Atomic rename hedefi — kalıcı alan | `/srv/files/export` |

`FileEndpoints.cs`:
- `GetContentAsync` → `ReadPath`
- `CreateFileAsync` → staging yazma + SHA256 → atomic `File.Move` → `ExportPath`

`Program.cs` health check → `ReadPath`

Migration tool (`tools/migrate-legacy-files.py`):
- `--export-path` ile doğrudan export'a yazar (migration tool staging adımını atlar; Files-01 üzerinde doğrudan çalışır)

## NFS Runbook (TAMAMLANDI ✅)

`runbooks/files01-nfs-setup.md` oluşturuldu ve staging akışı yansıtacak şekilde güncellendi. İçerik:
- Files-01 tam dizin yapısı: `export/`, `staging/`, `manifests/`, `restore-tests/`
- İzin modeli: `files-nfs-ro` (export, read-only), `files-publishers` (staging, manifests)
- NFSv4.2 read-only export (`/etc/exports`) — **yalnız `export/` export edilir, `staging/` asla export edilmez**
- File-Service runtime mount (`/mnt/platform-files`, `/etc/fstab`)
- Config: `ReadPath` / `StagingPath` / `ExportPath` production değerleri
- Doğrulama kapıları: NFS port, mount, probe okuma, yazma reddi, API health check, NFS down senaryosu, backup restore
- Sorun giderme tablosu

NFS'e geçişte `appsettings.json` → `ReadPath: /mnt/platform-files`, `StagingPath: /srv/files/staging`, `ExportPath: /srv/files/export` yapılır.

## Migration Tooling (TAMAMLANDI ✅)

`tools/migrate-legacy-files.py` oluşturuldu. `files01-nfs-model.md` Migration Manifesti şemasını uygular.

Özellikler:
- Kaynak dizini tarar, izinsiz uzantıları (`.exe` vb.) atlar
- Her dosya için UUID oluşturur, SHA256 hesaplar, shard path üretir (`domain/XX/XX/uuid.ext`)
- Dosyayı hedef storage'a kopyalar, copy sonrası SHA256 doğrular
- `files.objects` + `files.references` DB kayıtları oluşturur
- CSV manifest yazar: `file_id, entity_type, file_type, target_relative_path, extension, size_bytes, sha256, source_alias, migration_status, checked_at, notes`
- `--dry-run` ile dosya kopyalamadan ve DB'ye yazmadan manifest üretir

```bash
python3 tools/migrate-legacy-files.py \
  --source /path/to/legacy \
  --export-path /srv/files/export \
  --domain personnel \
  --entity-type personnel \
  --relation-type cv \
  --app-code yonetimapi \
  --entity-id <personnel_id> \
  --dry-run
```

Güvenlik ve sağlamlık düzeltmeleri uygulandı:
- Magic-byte kontrolü (PDF/JPEG/PNG/WebP) — uyuşmayan dosya `skipped`
- Aktif eski referans varsa önce `revoked` + `archived` yapıldıktan sonra yeni kayıt ekleniyor
- Duplicate SHA256 kontrolü (DB'de aynı hash varsa `skipped` + mevcut `file_id` notu)
- Rollback: DB insert başarısız olursa kopyalanan dosya silinir (atomiklik)
- `source_alias` varsayılan olarak `SHA256(dosya_adı)[:16]` — PII koruması; `--include-source-names` ile opt-in
- `skipped` sayacı uzantı/magic/duplicate/entity_id eksik tüm durumlarda artırılıyor
- `--entity-id` veya `--entity-id-map` zorunlu; dosya adından entity_id üretilmiyor
- `--entity-id-map`: `source_filename,entity_id` CSV'si ile çok varlıklı batch desteği

Test: dry-run ✅, magic-byte ✅, archive önceki ✅, duplicate skip ✅, rollback ✅, entity-id-map ✅

## objects/references Status ve is_primary Doğruluğu (TAMAMLANDI ✅)

`file-catalog-model.md` ve şema CHECK constraint'leriyle tam uyum sağlandı.

**Geçerli status değerleri (şema CHECK):**

| Tablo | Geçerli değerler | Kod tarafından set edilenler |
|---|---|---|
| `files.objects.status` | `active`, `revoked`, `archived`, `deleted` | `active` (create), `archived` (archive) |
| `files.references.status` | `active`, `revoked` | `active` (create), `revoked` (archive) |
| `files.references.is_primary` | boolean | daima `true` (V1'de tüm referanslar primary) |

**Düzeltilen iki hata:**

1. **`ResolveAsync` — `is_primary` filtresi eksikti.**
   Sorgu artık `r.IsPrimary && r.Status == "active"` filtresiyle yapılıyor. `uq_primary_per_entity` yalnız `is_primary=true AND status=active` kombinasyonunu unique kılıyor; non-primary active referanslar da var olabilir.

2. **`ArchiveFileAsync` — idempotent check yalnız `"archived"` kapsıyordu.**
   `objects.status` `revoked` veya `deleted` de olabilir. Eski kod bu durumda nesneyi `archived`'a çekiyordu (yanlış state transition). Düzeltme: `status != "active"` olan her nesne için archive no-op — mevcut status döndürülür.

**DB güvence katmanı:**
`uq_primary_per_entity` partial index kaldırıldı; yerine `trg_check_single_primary` trigger eklendi. Trigger `files.relation_type_config` tablosuna bakarak yalnız `single` kardinaliteli tipler için çift aktif primary girişimini engeller. `multi` tipler trigger'ı atlar. Uygulama yine de arşivleme yapıyor; trigger yalnız bug senaryolarına karşı korur.

## Port İzolasyonu ve Güvenlik Kontrolleri (TAMAMLANDI ✅)

`fileservice`, `yonetimapi`, `flotaapi` portları `docker-compose.yml`'den kaldırıldı; `docker-compose.override.yml`'e taşındı.

| Ortam | Komut | Açık portlar |
|---|---|---|
| Dev | `docker compose up` (override otomatik yüklenir) | 5090, 8080 + 5205, 5076, 5077 (test kolaylığı) |
| Production | `docker compose -f docker-compose.yml up` | **Yalnız 5090 (Gateway) + 8080 (Keycloak)** |

**DİKKAT:** `docker-compose.override.yml` yanlışlıkla production'da kullanılmamalı. Production deploy'da her zaman `-f docker-compose.yml` ile açıkça belirt.

**Doğrulanan kontroller:**

| Test | Sonuç |
|---|---|
| Production config: yalnız 5090 + 8080 published | ✅ |
| Gateway `/internal/files/**` → 404 | ✅ |
| Gateway `/api/personnel/**` → YonetimApi | ✅ |
| Gateway `/api/vehicles/**` → FlotaApi | ✅ |
| Gateway `/api/fleet/**` (tanımsız route) → 404 | ✅ |
| Host'tan 5205/5076/5077 → bağlantı reddedildi (production modunda) | ✅ |
| yonetimapi container → `fileservice:8080` DNS erişimi | ✅ (uygulama logu + uçtan uca 200) |
| flotaapi container → `fileservice:8080` DNS erişimi | ✅ |

**Keycloak 8080 production kararı:**

Keycloak'ın dışa açık kalması bilinçli bir karardır. İstemcilerin token alması (`POST /realms/platform/.../token`) ve servislerin JWKS çekmesi (`GET /realms/platform/.../certs`) için erişilebilir olmalıdır.

| Yol | Dev | Production önerisi |
|---|---|---|
| `/realms/platform/...` (token, JWKS, discovery) | 8080 açık | Açık kalabilir veya Gateway'e route edilebilir |
| `/admin/**` (Keycloak yönetim konsolu) | 8080 açık | **Firewall ile kapatılmalı** |

Production'da minimum yapılması gerekenler:
1. Keycloak admin konsolunu (`/admin`) dış erişime kapat (firewall veya Keycloak `hostname-admin` config)
2. Keycloak 8080'i yalnız yerel ağa sınırla (UTM subnet için uygundur)

Gateway üzerinden Keycloak routing teknik olarak mümkündür (YARP route `/realms/**` → `keycloak:8080`) ama JWT `iss` claim'i ve MetadataAddress eşleşmesi için ek config gerekir — V1 kapsam dışı bırakıldı.

## Docker Konteynerizasyonu (TAMAMLANDI ✅)

Tüm sistem docker compose ile çalışıyor: `postgres`, `keycloak`, `fileservice`, `yonetimapi`, `gateway`.

**Dockerfile'lar:** `FileServiceApi/Dockerfile`, `YonetimApi/Dockerfile`, `Gateway/Dockerfile` — .NET 10, multi-stage build.

**Veritabanı init:** `db/docker-init/01-schema.sql` (tüm şema), `db/docker-init/02-seed.sql` (app_policies seed).

**JWT issuer düzeltmesi:** Docker içinde MetadataAddress `http://keycloak:8080`'den çekilen OIDC discovery `issuer=http://keycloak:8080/realms/platform` döndürüyor. Ama token'daki `iss=http://localhost:8080/realms/platform`. Düzeltme: `TokenValidationParameters.ValidIssuers = [authority]` — .NET her iki issuer'ı da geçerli sayıyor.

**Keycloak healthcheck:** `curl` container'da yok; bash `/dev/tcp` + `printf` ile HTTP GET.

**Başlatma:**
```bash
docker compose up --build -d
# Health bekleme
docker compose ps
```

**Test sonuçları:** D1–D16 tümü geçti (bak: TEST_RAPORU.md Docker bölümü).

**Network:** `platform-net` bridge. Servisler arası iletişim container adıyla (`keycloak:8080`, `fileservice:8080`).

## Hata Ele Alma Sağlamlaştırması (TAMAMLANDI ✅)

Sunucu/storage çöküşü senaryoları için eksik hata ele alma tamamlandı:

| Senaryo | Önceki durum | Sonrası |
|---|---|---|
| NFS staging yazma hatası (`IOException`) | unhandled 500 + orphan staging dosyası | 503 `storage_unavailable` + staging cleanup |
| `File.Move` hatası | unhandled 500 + orphan staging dosyası | 503 `storage_unavailable` + staging cleanup |
| NFS okuma hatası (hash check sırasında) | unhandled 500 | 503 `storage_unavailable` |
| `ArchiveFileAsync` DB hatası | unhandled 500, body yok | 500 `internal_error` JSON body |
| `Resolve` response'unda `etag` eksikti | contract ihlali | `etag` alanı eklendi |

**Desteklenen dosya tipleri (tümü magic-byte korumalı):** `pdf`, `jpg`, `jpeg`, `png`, `webp`

**Tüm MD hata kodları uygulandı:** 401, 403, 404, 409, 410, 413, 415, 500, 503

## FlotaApi — İkinci Consumer App (TAMAMLANDI ✅)

`filoapi` Keycloak client'ı üzerine ikinci consumer app yazıldı. YonetimApi ile aynı anda çalışıyor.

**Mimari:**
```
Gateway:5090
  /api/personnel/** → YonetimApi:5076  (personnel domain, yonetimapi JWT)
  /api/vehicles/**  → FlotaApi:5077    (fleet domain,     filoapi JWT)
```

**Yeni bileşenler:**
- `FlotaApi/` — YonetimApi pattern'inin fleet karşılığı
- `filo.audit_events` DB tablosu — domain audit (vehicle_id, actor, action, result)
- `keycloak/realm-platform.json` — `fleetuser` kullanıcısı (`vehicle_id: test_arac_1`) + `vehicle_id` mapper

**Endpoint'ler (15 adet):**

| Endpoint | Kardinalite | Açıklama |
|---|---|---|
| `GET /api/vehicles/{id}/files` | — | Tüm aktif primary dosyaları listele |
| `GET /api/vehicles/{id}/photo` | single | Fotoğraf metadata |
| `GET /api/vehicles/{id}/photo/content` | single | Fotoğraf stream |
| `POST /api/vehicles/{id}/photo` | single | Fotoğraf yükleme |
| `POST /api/vehicles/{id}/photo/archive` | single | Fotoğraf arşivleme |
| `GET /api/vehicles/{id}/document` | multi | Belge metadata (resolve — arbitrary) |
| `GET /api/vehicles/{id}/document/content` | multi | Belge stream |
| `POST /api/vehicles/{id}/document` | multi | Belge yükleme — eskiler korunur |
| `POST /api/vehicles/{id}/document/archive` | multi | Belge arşivleme |
| `GET /api/vehicles/{id}/official-document` | single | Resmi evrak metadata |
| `GET /api/vehicles/{id}/official-document/content` | single | Resmi evrak stream |
| `POST /api/vehicles/{id}/official-document` | single | Resmi evrak yükleme |
| `POST /api/vehicles/{id}/official-document/archive` | single | Resmi evrak arşivleme |
| `POST /api/vehicles/{id}/attachment` | multi | Ek dosya yükleme |
| `POST /api/vehicles/{id}/report` | multi | Rapor yükleme |

**İzolasyon test sonuçları (önceki fleet smoke seti):**

| Test | Beklenen | Sonuç |
|------|----------|-------|
| fleetuser → kendi aracı fotoğraf yükleme | 200 | ✅ |
| personel kullanıcısı → kendi personeli CV yükleme | 200 | ✅ |
| personel kullanıcısı → araç endpoint (vehicle_id claim yok) | 403 data_scope_denied | ✅ |
| fleetuser → personel endpoint (personnel_id claim yok) | 403 data_scope_denied | ✅ |
| fleetuser → başka araç (test_arac_2) | 403 data_scope_denied | ✅ |
| yonetimapi service token → fleet domain | 403 forbidden (app policy) | ✅ |
| Token yok | 401 | ✅ |
| filo.audit_events yazılıyor | VehiclePhotoUploaded / Viewed | ✅ |
| yonetim.audit_events ayrı tutuluyor | PersonnelCvUploaded / Viewed | ✅ |

**Fleet'e özel:** `document` tipi (PDF) `yonetimapi`'de yok — `filoapi` policy'si `ARRAY['photo','document']` kapsıyor, YonetimApi'nin `ARRAY['photo','cv']` kapsamaması cross-domain izolasyonunu doğrular.

## RBAC — Rol Tabanlı Erişim Kontrolü (TAMAMLANDI ✅)

YonetimApi'ye `permission × action × scope` modeliyle erişim kontrolü eklendi.

**Model:** Her Keycloak realm rolü `{permission}.{action}.{scope}` formatında üç boyutu tek başına taşır.

```
permission = hangi kaynak    → personnel.files
action     = ne yapmak       → read | write
scope      = kimin üzerinde  → self | team | all
```

**Tanımlanan roller:**

| Keycloak rolü | Ne yapabilir |
|---|---|
| `personnel.files.read.self` | Yalnız kendi personel kaydını okur |
| `personnel.files.read.team` | Kendi + DB'deki ekibinin kaydını okur |
| `personnel.files.read.all` | Tüm personel kayıtlarını okur |
| `personnel.files.write.self` | Kendi dosyasını yükler/arşivler |
| `personnel.files.write.all` | Herkese dosya yükler/arşivler |

**Test kullanıcıları:**

| Kullanıcı | Şifre | Atanan roller | `personnel_id` |
|---|---|---|---|
| `hr001` | Demo1234! | `personnel.files.read.all` + `personnel.files.write.all` | HR001 |
| `adm001` | Demo1234! | `personnel.files.read.all` + `personnel.files.write.all` | ADM001 |
| `m001` | Demo1234! | `personnel.files.read.team` | M001 |
| `m002` | Demo1234! | `personnel.files.read.team` | M002 |
| `m003` | Demo1234! | `personnel.files.read.team` | M003 |
| `p001` ... `p024` | Demo1234! | `personnel.files.read.self` | P001 ... P024 |

**Eklenen / değiştirilen bileşenler:**

- `YonetimApi/Services/PermissionService.cs`
  - `HasPermissionAsync(user, permission, action, targetId)` — genel çözüm motoru
  - Kontrol sırası: `{permission}.{action}.all` → `{permission}.{action}.team` (DB) → `{permission}.{action}.self`
  - `CanReadAsync` → `HasPermissionAsync(..., "read", ...)` çağırır
  - `CanWriteAsync` → `HasPermissionAsync(..., "write", ...)` çağırır

- `keycloak/realm-platform.json`
  - 5 realm rolü (`personnel.files.*`) + `realm-roles` JWT mapper (`claim.name: roles`, multivalued)
  - 29 geçici personel kullanıcısı + yukarıdaki rol atamaları

- `db/docker-init/01-schema.sql` — `yonetim.team_members` (manager_id + personnel_id PK)
- `db/docker-init/02-seed.sql` — HR/admin/manager/personel seed'i ve `M001/M002/M003` ekip ilişkileri

**Neden bu model daha iyi:**
- Keycloak rolü ne kaynağa, ne yapmak istediğine, kimin üzerinde çalıştığına dair tam bilgiyi taşır — kural kod içinde gömülü değil
- Yeni izin eklemek için sadece yeni rol tanımlanır ve atanır, kod değişmez
- `CanReadAsync` / `CanWriteAsync` her uygulama için aynı motor kullanır

**Test senaryoları:**

```bash
BASE="http://localhost:8080/realms/platform/protocol/openid-connect/token"

USER_TOKEN=$(curl -s $BASE -d grant_type=password -d client_id=frontend-test \
  -d username=p001 -d password=Demo1234! | jq -r .access_token)

HR_TOKEN=$(curl -s $BASE -d grant_type=password -d client_id=frontend-test \
  -d username=hr001 -d password=Demo1234! | jq -r .access_token)

MGR_TOKEN=$(curl -s $BASE -d grant_type=password -d client_id=frontend-test \
  -d username=m001 -d password=Demo1234! | jq -r .access_token)

# p001 → kendi → 200/404 (erişim var, dosya yoksa 404)
curl -H "Authorization: Bearer $USER_TOKEN" http://localhost:5090/api/personnel/P001/cv

# p001 → başkası → 403
curl -H "Authorization: Bearer $USER_TOKEN" http://localhost:5090/api/personnel/P002/cv

# p001 → kendi upload → 403 (write.self yok)
curl -X POST -F "file=@test.pdf" -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/P001/cv

# hr001 → herkes → 200/404
curl -H "Authorization: Bearer $HR_TOKEN" http://localhost:5090/api/personnel/P002/cv

# hr001 → upload → 200
curl -X POST -F "file=@test.pdf" -H "Authorization: Bearer $HR_TOKEN" \
  http://localhost:5090/api/personnel/P001/cv

# m001 → ekibindeki → 200/404
curl -H "Authorization: Bearer $MGR_TOKEN" http://localhost:5090/api/personnel/P001/cv

# m001 → ekip dışı → 403
curl -H "Authorization: Bearer $MGR_TOKEN" http://localhost:5090/api/personnel/P008/cv

# m001 → upload → 403 (write.team yok)
curl -X POST -F "file=@test.pdf" -H "Authorization: Bearer $MGR_TOKEN" \
  http://localhost:5090/api/personnel/P001/cv
```

**Önemli — `MapInboundClaims = false` (Program.cs):**
.NET'in `JwtSecurityTokenHandler` varsayılan olarak JWT'deki `"roles"` claim adını `ClaimTypes.Role` URI'sine yeniden adlandırır. `user.FindAll("roles")` boş döner. `MapInboundClaims = false` ile JWT'den gelen tüm claim adları olduğu gibi korunur.

**Test sonuçları (12/12 ✅):**

| Test | Beklenen | Sonuç |
|---|---|---|
| p001 → kendi kaydı GET cv | 404 (erişti, dosya yok) | ✅ |
| p001 → başkasının kaydı GET cv | 403 | ✅ |
| p001 → kendi upload (write.self yok) | 403 | ✅ |
| hr001 → P001 GET cv | 404 (erişti) | ✅ |
| hr001 → P002 GET cv | 404 (erişti) | ✅ |
| hr001 → P001 upload | 200 | ✅ |
| adm001 → P002 upload | 200 | ✅ |
| m001 → ekibindeki P001 GET cv | 200/404 | ✅ |
| m001 → ekibindeki P002 GET cv | 200/404 | ✅ |
| m001 → ekip dışı P008 GET cv | 403 | ✅ |
| m001 → ekibindeki upload (write.team yok) | 403 | ✅ |

**Başlatma:** `docker compose down -v && docker compose up --build -d`

## mTLS — Servis Kimliği Güçlendirme (TAMAMLANDI ✅)

`file-service-api-contract.md` Auth Modeli bölümüne uygun olarak mTLS eklendi. JWT auth korundu, mTLS üstüne **ek güvence katmanı** olarak eklendi.

**Kapsam:** YonetimApi → FileServiceApi ve FlotaApi → FileServiceApi arası iletişim.

```
YonetimApi  ─(JWT + client cert)──▶  FileServiceApi
FlotaApi    ─(JWT + client cert)──▶       (TLS: server cert CN=fileservice)
```

**Sertifika yapısı (`certs/`):**

| Dosya | Tür | CN | Amaç |
|---|---|---|---|
| `ca.crt` / `ca.key` | CA (10 yıl) | `platform-ca` | Tüm sertifikaları imzalar |
| `fileservice.crt/key` | Server (825 gün) | `fileservice` | Kestrel HTTPS, SAN=fileservice,localhost |
| `yonetimapi.crt/key` | Client (825 gün) | `yonetimapi` | YonetimApi → FileServiceApi |
| `filoapi.crt/key` | Client (825 gün) | `filoapi` | FlotaApi → FileServiceApi |

**`certs/generate-certs.sh`** — tüm sertifikaları tek komutla üretir. Süresi dolan sertifikaları yenilemek için tekrar çalıştır + `docker compose up --build -d`.

**Uygulanan değişiklikler:**

- `FileServiceApi/Program.cs`
  - Kestrel: `ListenAnyIP(8080, HTTPS)` — `ClientCertificateMode.RequireCertificate`
  - `ClientCertificateValidation`: CN izin listesinde (`yonetimapi` / `filoapi`) + CA chain doğrulaması
  - Koşullu: `Mtls:ServerCertPath` boşsa plain HTTP (local dev için)

- `YonetimApi/Program.cs` ve `FlotaApi/Program.cs`
  - `AddHttpClient("FileService").ConfigurePrimaryHttpMessageHandler`:
    - `HttpClientHandler.ClientCertificates` ← PEM cert yüklenir (Linux için PKCS12 export trick)
    - `ServerCertificateCustomValidationCallback` ← platform CA chain doğrulaması
  - `FileService:BaseUrl` artık `https://fileservice:8080`

- `docker-compose.yml`
  - Cert dosyaları `:ro` mount ile her servise bağlandı
  - `Mtls__*` env var'ları eklendi
  - `FileService__BaseUrl: "https://fileservice:8080"` (HTTP → HTTPS)

**Test sonuçları:**

| Test | Sonuç |
|---|---|
| hr001 upload (JWT + mTLS zinciri uçtan uca) | ✅ 200 |
| hr001 GET cv (JWT + mTLS zinciri) | ✅ 200 |
| p001 kendi kaydı GET cv | ✅ 200/404 |
| p001 başkasının kaydı → 403 | ✅ 403 |
| Sertifikasız direkt erişim → TLS reddi | ✅ curl exit=16, HTTP=000 |
| İzinsiz CN (hacker sertifikası) → TLS reddi | ✅ curl exit=16, HTTP=000 |
| Geçerli yonetimapi sertifikası, JWT eksik → 401 | ✅ 401 (TLS geçti, JWT katmanında durdu) |

**Sertifika yenileme:**
```bash
bash certs/generate-certs.sh   # yeni sertifikalar üretir
docker compose up --build -d   # servisleri yeniden başlatır
```

**Private key güvenliği:** `certs/.gitignore` → `*.key` ve `ca.srl` dosyaları commit edilmez.

## Relation Tipi Kardinalite Sistemi (TAMAMLANDI ✅)

`files.relation_type_config` tablosuyla her relation tipi için kardinalite (single / multi) konfigüre edilir.

**Kural:**
- **single**: aynı `(app_code, entity_type, entity_id, relation_type)` için her an yalnız bir `is_primary=true AND status=active` referans olabilir. Yeni upload → eski `archived` + `revoked`, yeni `active + is_primary=true`.
- **multi**: birden fazla aktif primary olabilir. Yeni upload eskiye dokunmaz; hepsi `active + is_primary=true` olarak listede görünür.

**Tanımlı tipler:**

| Relation Type | Kardinalite | Açıklama |
|---|---|---|
| `cv` | single | Özgeçmiş — her an yalnız bir aktif |
| `photo` | single | Fotoğraf — her an yalnız bir aktif |
| `official_document` | single | Resmi evrak — her an yalnız bir aktif |
| `document` | multi | Genel belge — birden fazla aktif olabilir |
| `attachment` | multi | Ek dosya — birden fazla aktif olabilir |
| `report` | multi | Rapor — birden fazla aktif olabilir |

**Bilinmeyen tipler:** Tabloda `multi` olarak kayıtlı değilse uygulama ve DB trigger her ikisi de `single` davranışı uygular.

**Mimari:**
- `files.relation_type_config` tablosu — global tanım, app'e özgü değil
- `FileServiceApi/Endpoints/FileEndpoints.cs → CreateFileAsync` — kardinalite lookup yaparak tek `SaveChangesAsync` içinde eski arşivleme + yeni eklemeyi atomik gerçekleştirir
- `YonetimApi/PersonnelEndpoints.cs`, `FlotaApi/VehicleEndpoints.cs` → `ProxyUploadAsync` artık pre-archive yapmıyor; doğrudan FileServiceApi'ye upload gönderir
- `files.check_single_primary` DB trigger — application hatasına karşı güvence; `single` tipler için çift aktif primary girişimini yakalar

**List/Resolve davranışı:**
- `GET /internal/files/list`: `is_primary=true AND status=active` filtresiyle tüm dosyalar döner — single için 1 satır, multi için N satır
- `GET /internal/files/resolve`: `is_primary=true AND status=active` ilk satır — single için deterministik, multi için `/list` tercih edilmeli

**`DomainAction` helper güncellendi:** `official_document` gibi alt çizgili tipler PascalCase audit action üretir (`PersonnelOfficialDocumentUploaded`).

**Test sonuçları (17/17 ✅):**

| Test | Beklenen | Sonuç |
|---|---|---|
| CV 1. upload | 200 | ✅ |
| CV 2. upload (trigger false-positive?) | 200 (no violation) | ✅ |
| Listede aktif CV sayısı | 1 | ✅ |
| Photo 1. upload | 200 | ✅ |
| Photo 2. upload (trigger false-positive?) | 200 (no violation) | ✅ |
| Listede aktif photo sayısı | 1 | ✅ |
| Document 1/2/3. upload | 200 | ✅ |
| Listede aktif document sayısı | 3 | ✅ |
| Multi-primary list 3 adet döndü | ≥2 | ✅ |
| Multi-primary list HTTP 200 | 200 | ✅ |
| EF Core UPDATE→INSERT sırası → trigger sağlam | no violation | ✅ |
| Karışık entity: CV=1, photo=1, document=2 | 1, 1, 2 | ✅ |

**Trigger + EF Core sırası doğrulandı:** EF Core `SaveChangesAsync` UPDATE'leri INSERT'lerden önce gönderiyor. 2. CV upload'da eski referans `status=revoked` olarak güncelleniyor, ardından yeni referans ekleniyor. Trigger INSERT sırasında eski revoked kaydı görmüyor → false-positive yok.

## Client UI — Personel Dosya Yönetimi (TAMAMLANDI ✅)

**Amaç:** Personel dosyaları için Gateway üzerinden çalışan, Keycloak JWT kullanan, FileService katalog modeline uyumlu React/Vite client.

### Kapsam

- Login ekranı: `frontend-test` public client ile Keycloak password grant.
- Session yönetimi: access token `sessionStorage` içinde tutulur, JWT `exp` dolunca temizlenir.
- Personel arama: `/api/personnel?search=...`
- Dosya listeleme: `/api/personnel/{personnelId}/files`
- Dosya indirme: `/api/personnel/{personnelId}/files/{fileId}/content`
- Dosya yükleme: `cv`, `photo`, `official_document`, `document`, `attachment`
- Dosya arşivleme:
  - Single-primary tipler için relation endpoint'i: `/api/personnel/{personnelId}/{relationType}/archive`
  - Multi-primary tipler için fileId endpoint'i: `/api/personnel/{personnelId}/files/{fileId}/archive`
- RBAC görünürlüğü: write yetkisi yoksa upload/archive aksiyonları UI'da gösterilmez.

### Client/API Uyum Notları

- File list response `originalFileName` ve `createdAt` alanlarıyla zenginleştirildi.
- Client `PersonnelFile` tipi FileService response shape'iyle eşitlendi:
  - `sizeBytes`
  - `originalFileName`
  - `createdAt`
  - `etag`
- Upload modalındaki relation type seçimi obje/string karışıklığından çıkarıldı; `UploadRelationType` union tipi eklendi.
- File card artık olmayan `fileName`, `fileSize`, `uploadedAt` alanlarını beklemiyor.
- Client auth state artık access token yanında refresh token da saklar. Sayfa refresh edildiğinde access token süresi dolmuşsa Keycloak `refresh_token` grant ile sessiz yenilenir; oturum açıkken token süresi dolmadan otomatik refresh yapılır.
- Auth storage `sessionStorage` yerine `localStorage` kullanır. Refresh token süresi dolduğunda kayıt temizlenir ve kullanıcı yeniden login olur.

### Güvenlik Düzeltmesi

FileId bazlı indirme/arşivleme akışında personel bağlamı doğrulandı. `YonetimApi`, `/files/{fileId}/content` ve `/files/{fileId}/archive` çağrısından önce FileService listesi üzerinden ilgili `fileId`'nin aynı `personnelId` altında aktif olduğunu kontrol eder. Böylece kullanıcı kendi erişebildiği personel path'i altında başka bir personele ait bilinen `fileId` ile işlem yapamaz.

### 2026-06-29 Upload / Seed Düzeltmesi

- Eski 4 kişilik test verisi ve personel domain'indeki eski dosya referansları temizlendi.
- Yeni geçici personel seti yüklendi: `HR001`, `ADM001`, `M001-M003`, `P001-P024`.
- Keycloak login kullanıcıları lowercase personel id olarak tanımlandı:
  - `hr001 / Demo1234!` → read/write all
  - `adm001 / Demo1234!` → read/write all
  - `m001`, `m002`, `m003 / Demo1234!` → read team
  - `p001` ... `p024 / Demo1234!` → read self
- Keycloak runtime'da sonradan yazılan custom `personnel_id` attribute'u token'a düşmediği için `YonetimApi` data-scope kontrolünde güvenli fallback eklendi: `personnel_id` claim yoksa `preferred_username.ToUpperInvariant()` personel id olarak kullanılır.
- Upload 500 nedeni: NFS staging altında eski silmeden kalan stale `staging/personnel/82` entry'si `Directory.CreateDirectory` sırasında `Invalid argument` hatası üretiyordu. Staging personel dizini temizlendi ve FileService container'ı yenilendi.
- Doğrulanan akış: `hr001` ile `POST /api/personnel/P001/cv` Gateway üzerinden 200 döndü; ardından `GET /api/personnel/P001/files` yüklenen dosyayı listeledi.
- Arşiv davranışı doğrulandı:
  - `POST /api/personnel/P001/cv/archive` → 200
  - `files.objects.status` → `archived`
  - `files.references.status` → `revoked`
  - `GET /api/personnel/P001/files` → `[]`
  - `files.audit_events` → `create/read/archive success`
  - Hard delete yok; fiziksel temizlik retention politikasına bırakılır.

### Doğrulama

| Kontrol | Sonuç |
|---|---|
| `npm run build` | ✅ |
| `dotnet build FileServiceApi/FileServiceApi.csproj` | ✅ |
| `dotnet build YonetimApi/YonetimApi.csproj` | ✅ |
| `hr001` upload `P001/cv` | ✅ |
| `hr001` archive `P001/cv` | ✅ |
| `m001` ekip araması | ✅ 8 kayıt |
| `p001` self araması | ✅ 1 kayıt |
| Vite dev server login ekranı render | ✅ |
| Browser console error | ✅ Yok |
| Keycloak password grant refresh token dönüyor | ✅ |
| Keycloak refresh_token grant yeni access token dönüyor | ✅ |

### 2026-06-29 Negatif Senaryo ve Performans Kontrolü

**Ek düzeltmeler:**

- FileService upload validasyonu relation type bazında daraltıldı:
  - `cv` → yalnız `pdf`
  - `photo` → yalnız `jpg/jpeg/png/webp`
  - `official_document`, `document`, `attachment` → `pdf/jpg/jpeg/png/webp`
  - `report` → yalnız `pdf`
- `Content-Type` kontrolü eklendi. `application/octet-stream` fallback olarak kabul edilir; açık gelen MIME değeri uzantıyla çelişirse 415 döner.
- NFS/staging dizini oluşturma hatası da storage try/catch kapsamına alındı. Stale NFS entry veya izin/path problemi artık 500 yerine `503 storage_unavailable` olarak döner ve audit'e `storage_write_failed` yazılır.

**Gateway üzerinden doğrulanan güncel sonuçlar:**

| Kontrol | Beklenen | Sonuç |
|---|---:|---:|
| Gateway health | 200 | ✅ 200 |
| Token yokken personel arama | 401 | ✅ 401 |
| FileService'e hosttan plain HTTP direkt erişim | engelli | ✅ HTTP 000 / mTLS-HTTPS |
| `hr001` personel arama | 29 kayıt | ✅ 29 |
| `p001` personel arama | 1 kayıt | ✅ 1 |
| `m001` personel arama | 8 kayıt | ✅ 8 |
| `p001` → `P002` CV upload | 403 | ✅ 403 |
| `m001` → `P008` dosya listesi | 403 | ✅ 403 |
| Sahte PDF magic-byte mismatch | 415 | ✅ 415 |
| PDF'i `photo` alanına yükleme | 415 | ✅ 415 |
| PNG'i `photo` alanına yükleme | 200 | ✅ 200 |
| Aynı personele 2 CV | yalnız 1 aktif CV | ✅ 1 aktif, 1 revoked/archived |
| Aynı personele 2 document | 2 aktif document | ✅ 2 aktif |
| FileId ile document archive | listeden düşer | ✅ 1 document kaldı |
| FileService audit | create/archive denied/success kayıtları | ✅ |
| Yonetim domain audit | denied/success kayıtları | ✅ |

**Hız notları:**

- Upload şu an iki katmandan geçiyor: Client → Gateway → YonetimApi → FileService. YonetimApi `ReadFormAsync` ile dosyayı alıp tekrar multipart olarak FileService'e gönderiyor; FileService de formu tekrar okuyor. Büyük dosyada gecikmenin ana sebeplerinden biri bu çift proxy/buffer akışı.
- FileService upload sırasında dosyayı staging'e yazar, sonra SHA256 için staging dosyasını tekrar okur, sonra export'a taşır. Bu doğruluk için iyi ama büyük dosyada ikinci disk okuması maliyetlidir.
- Download tarafında `GetContentAsync` her içerik isteğinde dosyayı komple SHA256 hesaplayarak doğruluyor. Bu hash mismatch testini güçlü yapar ama büyük dosyalarda indirme başlamadan gecikme yaratır.
- Docker dev ortamında staging ve export aynı bind volume altında çalışıyor. Gerçek Files-01 modelinde staging'in FileService runtime host'unda yerel, export'un Files-01 tarafında olması daha hızlı ve stale NFS riskini azaltır.

**Hızlandırma için güvenli V2 adayları:**

1. YonetimApi upload proxy'sini streaming multipart forward'a çevirmek.
2. Upload sırasında SHA256'i dosya yazılırken hesaplamak; staging dosyasını ikinci kez okumamak.
3. Download'da her istekte tam hash yerine ETag/DB hash'e güvenmek; hash doğrulamayı background audit/health probe veya opsiyonel verify endpoint'e taşımak.
4. Client upload progress bar ve timeout/error mesajlarını iyileştirmek; büyük dosyada "site çöktü" hissini azaltmak.
5. Dev compose'ta staging'i NFS/bind export'tan ayırmak; staging için container-local veya ayrı local volume kullanmak.

### Kapsam Dışı

- Fleet/Flota UI yok; `FlotaApi` backend consumer olarak mevcut.
- Refresh token akışı yok; access token süresi dolunca yeniden login gerekir.
- Production frontend hosting/Nginx imajı henüz eklenmedi; client şu an Vite dev/build çıktısı olarak hazır.

## Client UI — Son Düzeltmeler (2026-06-30)

### Bug Fix: `official_document` URL uyumsuzluğu

`api.ts → uploadFile` ve `archiveSinglePrimary` fonksiyonları `relationType` değerini doğrudan URL'e ekliyordu. Backend `/official-document` (tire) beklerken client `/official_document` (alt çizgi) gönderiyordu → 404.

Düzeltme: `toUrlSegment()` helper eklendi; `relationType.replace(/_/g, '-')` URL segment'ine dönüştürür. `cv` ve `photo` bu işlemden etkilenmez (alt çizgi içermiyor).

### PersonnelFileView: useCallback bağımlılık düzeltmesi

`loadFiles` fonksiyonu `useCallback` ile sarıldı. ESLint exhaustive-deps uyarısı giderildi. `auth.token` değiştiğinde (token yenilendiğinde) liste otomatik yenilenir.

### Doğrulama

| Kontrol | Sonuç |
|---|---|
| `npm run build` (TypeScript + Vite) | ✅ 0 hata |
| `official_document` upload URL | ✅ `/official-document` olarak iletiliyor |
| `official_document` archive URL | ✅ `/official-document/archive` olarak iletiliyor |

## SIRADAKİ ADIM

- **V2 Download Ticket**: `file-service-api-contract.md`'deki V2 model — performans baskısı oluşursa değerlendirilecek.
- **Sertifika rotasyonu**: Prod'da cert süresi dolmadan yenileme prosedürü (sıfır kesinti için rolling restart).
- **Frontend production packaging**: Client için Nginx/static hosting imajı ve compose servisi eklenecekse ayrı teslim kalemi olarak yapılmalı.

## Bilinen tuzaklar

- `dotnet run` komutunu çalıştırırken yanlış dizinde olunursa yanlış servis başlar.
  Her zaman `pwd` ile doğrula veya `cd FileServiceApi && dotnet run` şeklinde zincirle.
- Arka planda çalışan eski bir servis `dotnet run` başlatmayı engeller (port already in use).
  `lsof -ti:<port> | xargs kill -9` ile temizle.
- `curl -I` HEAD isteği gönderir — GET endpoint'leri için 405 döner; içerik testlerinde
  `-X GET` veya `-v` kullan.
- `yeni-test-cv.pdf` gerçek PDF değil. Upload testinde magic-byte hatası alırsın.
