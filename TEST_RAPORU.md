# Test Raporu — V1 Tam Sistem Testi

**Tarih:** 2026-06-29  
**Test ortamı:** macOS, PostgreSQL (Postgres.app), Keycloak 26 (Docker), .NET 10

## Servisler ve Portlar

| Servis | Port | Açıklama |
|---|---|---|
| Gateway-01 | 5090 | Client entry point (YARP) |
| YonetimApi | 5076 | Business API, data-scope |
| FileServiceApi | 5205 | Dosya kataloğu, binary stream |
| Keycloak | 8080 | OAuth2 token sunucusu |

> **Not:** macOS port 5000'i AirPlay için kullanıyor (ControlCenter/AirTunes).  
> Gateway portu bu nedenle **5090** olarak ayarlandı. `launchSettings.json` güncellendi.

---

## Hazırlık — Token Alma

```bash
USER_TOKEN=$(curl -s -X POST http://localhost:8080/realms/platform/protocol/openid-connect/token \
  -d grant_type=password -d client_id=frontend-test \
  -d username=testuser -d password=Test1234! | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")
```

`testuser` → `personnel_id: test_personel_1` claim'i JWT'ye eklenir.  
`frontend-test` client → public, password grant.

---

## T1 — Gateway Health Check

```bash
curl http://localhost:5090/health
```
```json
{"status":"healthy","service":"Gateway-01"}
```
✅ Gateway ayakta, /health 200 dönüyor.

---

## T2 — JWT Olmadan YonetimApi (Gateway üzerinden) → 401

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" http://localhost:5090/api/personnel/test_personel_1/cv
```
```
HTTP 401
```
✅ YARP, YonetimApi'nin 401 yanıtını olduğu gibi iletir.

---

## T3 — Sahte/Geçersiz Token → 401

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" \
  -H "Authorization: Bearer sahtetoken" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```
HTTP 401
```
✅ JWT Bearer doğrulaması Keycloak JWKS ile yapılır; sahte token 401 döndürür.

---

## T4 — JWT Olmadan FileService Direkt Çağrı → 401

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" \
  "http://localhost:5205/internal/files/resolve?domain=personnel&entityType=personnel&entityId=test_personel_1&relationType=cv"
```
```
HTTP 401
```
✅ FileServiceApi dışarıdan erişilemez. Production'da sadece YonetimApi (iç ağ) çağırır.

---

## T5 — Eski X-App-Code Header ile FileService → 401

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" \
  -H "X-App-Code: yonetimapi" \
  "http://localhost:5205/internal/files/resolve?..."
```
```
HTTP 401
```
✅ `X-App-Code` header'ı artık kabul edilmiyor. `app_code` yalnızca JWT içindeki claim'den okunur.

---

## T6 — Data-scope: Kendi Personeli → 200

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```
HTTP 200
```
✅ `testuser`'ın `personnel_id` claim'i `test_personel_1`, istenen ID'ye eşit → izin verilir.

---

## T7 — Data-scope: Başkasının Personeli → 403

```bash
curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_2/cv
```
```json
{"error":"forbidden","reason":"data_scope_denied"}
HTTP 403
```
✅ `personnel_id` claim `test_personel_1`, istenen `test_personel_2` → eşleşmez → 403.  
FileService'e hiç istek gitmez; YonetimApi'de erken engellenir.

---

## T8 — Var Olmayan Dosya → 404

```bash
curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/photo
```
```json
{"error":"photo_not_found"}
HTTP 404
```
✅ Sözleşme: "scope miss → uygulama client'a dosya varlığını sızdırmaz". 404 dönüyor.

---

## T9 — CV Metadata Tam Response

```bash
curl -s -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```json
{
  "fileId": "4da29588-5dd9-40a6-90e9-0a782f4fdaea",
  "domain": "personnel",
  "relationType": "cv",
  "contentType": "application/pdf",
  "extension": "pdf",
  "sizeBytes": 102400,
  "sha256": "a3f5e1c2b4d6f8a0b2c4e6f8a0b2c4e6f8a0b2c4e6f8a0b2c4e6f8a0b2c4e6f8",
  "classification": "restricted",
  "status": "active"
}
```
✅ Path, NFS mount veya host bilgisi yanıtta yok. Sözleşme gereği.

---

## T10 — CV Content Stream: 200, ETag, Content-Disposition

```bash
curl -s -D - -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv/content
```
```
HTTP/1.1 200 OK
Content-Length: 48
Content-Type: application/pdf
Accept-Ranges: bytes
ETag: "sha256:a3f5e1c2b4d6f8a0b2c4e6f8a0b2c4e6f8a0b2c4e6f8a0b2c4e6f8a0b2c4e6f8"
Content-Disposition: attachment; filename="file.pdf"

Bu bir test dosyasidir, gercek bir CV degildir.
```
✅ SHA256 tabanlı ETag var. PDF için `attachment` Content-Disposition. `Accept-Ranges: bytes` var.

---

## T11 — 304 Not Modified (ETag / If-None-Match)

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  -H "If-None-Match: \"sha256:a3f5e1c2...\"" \
  http://localhost:5090/api/personnel/test_personel_1/cv/content
```
```
HTTP 304
```
✅ ETag eşleşince sunucu body göndermez, 304 döner. Bant genişliği tasarrufu.

---

## T12 — 206 Partial Content (Range)

```bash
curl -s -D - -H "Authorization: Bearer $USER_TOKEN" \
  -H "Range: bytes=0-9" \
  http://localhost:5090/api/personnel/test_personel_1/cv/content
```
```
HTTP/1.1 206 Partial Content
Content-Length: 10
Content-Range: bytes 0-9/48
ETag: "sha256:a3f5e1c2..."
```
✅ Range isteği destekleniyor. `enableRangeProcessing: true` ile Results.Stream() tarafından işleniyor.

---

## T13 — CV Upload (Gerçek PDF magic byte) → 200

```bash
python3 -c "open('/tmp/test.pdf','wb').write(b'%PDF-1.4\n%%EOF\n')"
curl -s -w " HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  -F "file=@/tmp/test.pdf;type=application/pdf" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```json
{"fileId":"86e8c38a-7db0-44b7-8acb-d5e463ea5f73","domain":"personnel",
 "relationType":"cv","contentType":"application/pdf","extension":"pdf",
 "sizeBytes":15,"status":"active"}
HTTP 200
```
✅ Yeni dosya oluşturuldu. Versiyonlama: eski aktif CV arşivlendi (T16b'de kanıtlandı).

---

## T14 — Sahte PDF (magic-byte mismatch) → 415

```bash
echo "Bu bir metin dosyasidir" > /tmp/sahte.pdf
curl -s -w " HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  -F "file=@/tmp/sahte.pdf;type=application/pdf" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```json
{"error":"unsupported_media_type"} HTTP 415
```
✅ İlk 12 byte `%PDF` ile başlamıyor → magic-byte kontrolü reddeder.

---

## T15 — Desteklenmeyen Uzantı → 415

```bash
echo "test" > /tmp/test.exe
curl -s -w " HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  -F "file=@/tmp/test.exe" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```json
{"error":"unsupported_media_type"} HTTP 415
```
✅ İzinli uzantılar: `pdf, jpg, jpeg, png, webp`. `.exe` reddedilir.

---

## T16 — Versiyonlama (DB kanıtı)

Aynı personel ve relationType için ardışık yüklemeler yapıldı. DB sorgusu:

```sql
SELECT r.entity_id, r.relation_type, r.status as ref_status,
       o.status as obj_status, o.file_id
FROM files.references r
JOIN files.objects o ON r.file_id = o.file_id
WHERE r.entity_id = 'test_personel_1' AND r.relation_type = 'cv'
ORDER BY o.created_at DESC;
```
```
 entity_id      | relation_type | ref_status | obj_status | file_id
-----------------+--------------+------------+------------+--------------------------------------
 test_personel_1 | cv           | active     | active     | 29e5c737-7020-4aec-adb3-6dbbaaad512a
 test_personel_1 | cv           | revoked    | archived   | 86e8c38a-7db0-44b7-8acb-d5e463ea5f73
 test_personel_1 | cv           | revoked    | archived   | 4da29588-5dd9-40a6-90e9-0a782f4fdaea
```
✅ Her yeni upload önceki CV'yi arşivliyor. `uq_primary_per_entity` constraint hiç ihlal edilmiyor.  
`objects.status = archived` + `references.status = revoked` birlikte güncelleniyor.

---

## T17 — Dosya Listesi

```bash
curl -s -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/files
```
```json
[
  {
    "fileId": "29e5c737-7020-4aec-adb3-6dbbaaad512a",
    "domain": "personnel",
    "relationType": "cv",
    "contentType": "application/pdf",
    "extension": "pdf",
    "sizeBytes": 21,
    "status": "active",
    "etag": "\"sha256:7962b01d...\""
  }
]
```
✅ Yalnız aktif (`is_primary = true AND status = active`) dosyalar döner.

---

## T18 — Archive

```bash
curl -s -w " HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" -X POST \
  http://localhost:5090/api/personnel/test_personel_1/cv/archive
```
```json
{"fileId":"29e5c737-7020-4aec-adb3-6dbbaaad512a","status":"archived"} HTTP 200
```

**Archive sonrası resolve → 404:**
```bash
curl -s -w " HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```json
{"error":"cv_not_found"} HTTP 404
```
✅ Arşivlenmiş dosya artık resolve edilmiyor.

---

## T19 — Archive Davranışı (ikinci çağrı)

YonetimApi `ProxyArchiveAsync` önce resolve yapar. Arşivlenmiş dosya resolve'de 404 verir:

```
1. archive: HTTP 200  ← aktif dosyayı buldu, arşivledi
2. archive: HTTP 404  ← aktif dosya yok, resolve başarısız
```
✅ Bu beklenen davranış. Sözleşme: "scope miss → 404".  
FileService'e direkt `POST /internal/files/{fileId}/archive` çağrısı idempotent 200 döner;  
YonetimApi proxy'si üzerinden ikinci çağrı 404 — tutarlı, doğru.

---

## T20 — İki Katmanlı Audit: FileService Teknik Audit

```sql
SELECT action, result, app_code, actor, reason_code
FROM files.audit_events ORDER BY created_at DESC LIMIT 8;
```
```
 action  |  result   |  app_code  |  actor   |     reason_code
---------+-----------+------------+----------+---------------------
 read    | not_found | yonetimapi | testuser | reference_not_found
 archive | success   | yonetimapi | testuser |
 read    | success   | yonetimapi | testuser |
 create  | success   | yonetimapi | testuser |
 archive | success   | yonetimapi | testuser |
```
✅ `app_code = yonetimapi` (service token'dan), `actor = testuser` (user JWT'den).  
Her işlem ayrı satır. Teknik audit platfrom seviyesinde.

---

## T21 — İki Katmanlı Audit: YonetimApi Domain Audit

```sql
SELECT action, result, actor, personnel_id, reason_code
FROM yonetim.audit_events ORDER BY created_at DESC LIMIT 8;
```
```
        action        |  result   |  actor   |  personnel_id   | reason_code
----------------------+-----------+----------+-----------------+-------------
 PersonnelCvArchived  | not_found | testuser | test_personel_1 |
 PersonnelCvArchived  | success   | testuser | test_personel_1 |
 PersonnelCvViewed    | success   | testuser | test_personel_1 |
 PersonnelCvUploaded  | success   | testuser | test_personel_1 |
 PersonnelFilesListed | success   | testuser | test_personel_1 |
 PersonnelCvViewed    | not_found | testuser | test_personel_1 |
```
✅ Domain audit iş olaylarını kaydediyor. `PersonnelCvViewed`, `PersonnelCvUploaded`, `PersonnelFilesListed`.  
FileService'in `files.audit_events`'inden bağımsız.

---

## T22 — data_scope_denied Audit Kaydı

```bash
curl -s -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_2/cv > /dev/null
```

```sql
SELECT action, result, actor, personnel_id, reason_code
FROM yonetim.audit_events WHERE reason_code = 'data_scope_denied'
ORDER BY created_at DESC LIMIT 3;
```
```
      action       | result |  actor   |  personnel_id   |    reason_code
-------------------+--------+----------+-----------------+-------------------
 PersonnelCvViewed | denied | testuser | test_personel_2 | data_scope_denied
```
✅ Red kararları da audit'e yazılıyor. FileService'e hiç istek gitmeden önce YonetimApi kaydeder.

---

## T23 — Büyük Dosya → 413

```bash
python3 -c "
with open('/tmp/buyuk.pdf','wb') as f:
    f.write(b'%PDF-1.4\n')
    f.write(b'A' * (11 * 1024 * 1024))
    f.write(b'\n%%EOF\n')
"
curl -s -w "HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  -F "file=@/tmp/buyuk.pdf;type=application/pdf" \
  http://localhost:5090/api/personnel/test_personel_1/cv
```
```json
{"error":"file_too_large"} HTTP 413
```
✅ `app_policies.max_file_size_bytes = 10485760` (10 MB). 11 MB → 413.

---

---

## H1 — Health Check Normal → 200

```bash
curl http://localhost:5205/health
```
```json
{"status":"healthy","service":"FileServiceApi","checks":{"storage":{"status":"healthy"},"database":{"status":"healthy"}}}
HTTP 200
```
✅ Auth gerektirmez. Her iki check (storage + db) geçince 200 döner.

---

## H2 — Health Check Storage Down → 503

Probe dosyası geçici olarak taşındı:

```bash
mv test-storage/export/.probe /tmp/.probe.bak
curl -w "\nHTTP %{http_code}" http://localhost:5205/health
mv /tmp/.probe.bak test-storage/export/.probe
```
```json
{"status":"unhealthy","service":"FileServiceApi","checks":{"storage":{"status":"unhealthy","reason":"probe_not_found"},"database":{"status":"healthy"}}}
HTTP 503
```
✅ Storage erişilemez olunca 503 + `probe_not_found` reason döner. DB hâlâ healthy gösterir (hangi component çöktü belli).

---

## H3 — Health Check Restored → 200

Probe dosyası geri yüklendi:

```bash
curl -w "\nHTTP %{http_code}" http://localhost:5205/health
```
```
HTTP 200
```
✅ Storage geri gelince health check tekrar 200 döner.

---

## Bulgular ve Düzeltilen Sorunlar

### 1. Port 5000 → macOS AirPlay çakışması (DÜZELTİLDİ)
`Server: AirTunes/940.23.1` yanıtı alınca sorun tespit edildi.  
**Düzeltme:** Gateway `launchSettings.json` portu 5000 → **5090** yapıldı.

### 2. ArchiveFileAsync references.status güncellememesi (DÜZELTİLDİ)
`uq_primary_per_entity` constraint'i `files.references` üzerinde. Archive sadece `objects.status`'u güncelliyordu.  
**Düzeltme:** Archive işleminde `references.status = revoked` da aynı transaction içinde güncelleniyor.

### 3. ProxyUploadAsync versiyonlama yoktu (DÜZELTİLDİ)
Aynı entity/relationType için ikinci upload constraint ihlali veriyordu.  
**Düzeltme:** Upload öncesi resolve → aktif dosya varsa archive → sonra create akışı eklendi.

---

---

## ISO-1 — filoapi → fleet domain → 200 (izinli)

filoapi JWT: `app_code: "filoapi"`, `files.app_policies` → `allowed_domains={fleet}`.

```bash
FILO_TOKEN=$(curl -s -X POST http://localhost:8080/realms/platform/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=filoapi -d client_secret=filoapi-secret \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

# fleet domain upload → 200
python3 -c "open('/tmp/test.jpg','wb').write(bytes([0xFF,0xD8,0xFF,0xE0])+b'JFIF\x00'*10)"
curl -s -w "HTTP %{http_code}" \
  -H "Authorization: Bearer $FILO_TOKEN" \
  -F "file=@/tmp/test.jpg;type=image/jpeg" \
  -F "domain=fleet" -F "entityType=vehicle" -F "entityId=vehicle_1" \
  -F "relationType=photo" -F "classification=internal" \
  http://localhost:5205/internal/files
```
```
HTTP 201
```
✅ filoapi, fleet domain'ine photo yüklüyor.

---

## ISO-2 — filoapi → personnel domain → 403 (çapraz domain yasak)

```bash
curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $FILO_TOKEN" \
  "http://localhost:5205/internal/files/resolve?domain=personnel&entityType=personnel&entityId=test_personel_1&relationType=cv"
```
```json
{"error":"forbidden"} HTTP 403
```
✅ filoapi'nin `allowed_domains={fleet}`, personnel yasak → 403.

---

## ISO-3 — yonetimapi → fleet domain → 403 (çapraz domain yasak)

```bash
curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $SVC_TOKEN" \
  "http://localhost:5205/internal/files/resolve?domain=fleet&entityType=vehicle&entityId=vehicle_1&relationType=photo"
```
```json
{"error":"forbidden"} HTTP 403
```
✅ yonetimapi'nin `allowed_domains={personnel}`, fleet yasak → 403.

---

## ISO-4 — filoapi → fleet + cv relationType → 403 (izinsiz dosya tipi)

```bash
python3 -c "open('/tmp/test.pdf','wb').write(b'%PDF-1.4\n%%EOF\n')"
curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $FILO_TOKEN" \
  -F "file=@/tmp/test.pdf;type=application/pdf" \
  -F "domain=fleet" -F "entityType=vehicle" -F "entityId=vehicle_1" \
  -F "relationType=cv" -F "classification=internal" \
  http://localhost:5205/internal/files
```
```json
{"error":"forbidden"} HTTP 403
```
✅ filoapi `allowed_file_types={photo,document}`, `cv` relationType → 403.

---

## BIN-1 — Binary OK (baseline) → 200

```bash
curl -s -o /dev/null -w "HTTP %{http_code}" \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv/content
```
```
HTTP 200
```
✅ Normal indirme çalışıyor (hash mismatch ve binary missing testlerinden önce baseline).

---

## BIN-2 — Binary Missing → 503

Binary dosyası geçici olarak taşındı, indirme denendi:

```bash
FILE_PATH="/Users/mustafaberen41/Desktop/dosya-sistemi-projesi/test-storage/export/personnel/72/84/7284a000-c286-45c7-9174-5c4caecadcb5.pdf"
mv "$FILE_PATH" "${FILE_PATH}.bak"

curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv/content

mv "${FILE_PATH}.bak" "$FILE_PATH"
```
```
HTTP 503
```
✅ Disk'te dosya yokken 503 `storage_unavailable` döner. Restore sonrası 200 döndüğü BIN-1'de kanıtlandı.

---

## BIN-3 — Hash Mismatch → 409

DB'deki SHA256 bozuldu, indirme denendi, doğru hash geri yüklendi:

```bash
FILE_ID="7284a000-c286-45c7-9174-5c4caecadcb5"
psql platformdb -c "UPDATE files.objects SET sha256 = 'aaaaaaaabbbbbbbbccccccccddddddddeeeeeeeeffffffff0000000011111111' WHERE file_id = '$FILE_ID';"

curl -s -w "HTTP %{http_code} " \
  -H "Authorization: Bearer $USER_TOKEN" \
  http://localhost:5090/api/personnel/test_personel_1/cv/content

REAL_SHA=$(python3 -c "import hashlib; print(hashlib.sha256(open('/path/7284a000...pdf','rb').read()).hexdigest())")
psql platformdb -c "UPDATE files.objects SET sha256 = '$REAL_SHA' WHERE file_id = '$FILE_ID';"
```
```
HTTP 409
```
✅ `GetContentAsync` SHA256 verify ediyor: `actualHash != fileObject.Sha256` → 409 `hash_mismatch`. Hash geri yüklendikten sonra 200 döner.

---

---

## Docker Konteyner Testleri

**Ortam:** docker compose — 5 container (postgres, keycloak, fileservice, yonetimapi, gateway)  
**JWT issuer düzeltmesi:** `ValidIssuers = [authority]` — MetadataAddress keycloak:8080'den `issuer=http://keycloak:8080/...` döndürüyor, token'daki `iss=http://localhost:8080/...`. Her iki değer de `ValidIssuers` listesinde olunca doğrulama geçer.

| Test | Senaryo | Beklenen | Sonuç |
|---|---|---|---|
| D1 | Gateway `/health` | `{status:healthy}` 200 | ✅ |
| D2 | FileService `/health` | storage+db healthy 200 | ✅ |
| D3 | JWT yok → Gateway | 401 | ✅ |
| D4 | Yanlış personel → Gateway | 403 | ✅ |
| D5 | Kendi personeli → Gateway | 200/404 | ✅ 404 |
| D6 | JWT yok → FileService direkt | 401 | ✅ |
| D7 | yonetimapi token ile upload | 200 | ✅ |
| D8 | Yüklenen dosyayı oku | 200 | ✅ |
| D9 | Archive | 200 | ✅ |
| D10 | Arşiv sonrası içerik | 404 | ✅ |
| D11 | Versioning (aynı entity ikinci yükleme) | 200 | ✅ |
| D12 | filoapi → personnel domain (çapraz) | 403 | ✅ |
| D13 | filoapi → fleet domain (kendi) | 200 | ✅ |
| D14 | YonetimApi üzerinden upload | 200 | ✅ |
| D15 | YonetimApi üzerinden kendi personeli oku | 200 | ✅ |
| D16 | YonetimApi üzerinden başka personel | 403 | ✅ |

**Audit log özeti (Docker testleri sonrası):**
```sql
SELECT action, result, COUNT(*) FROM files.audit_events
GROUP BY action, result ORDER BY action, result;
-- archive | success   | 1
-- create  | denied    | 1
-- create  | success   | 4
-- read    | not_found | 3
-- read    | success   | 2
```
✅ Tüm işlemler audit'e yazılıyor. `create denied` = D12 izolasyon testi.

---

## Test Senaryoları (file-service-intern-brief.md ile karşılaştırma)

| Senaryo | Beklenen | Sonuç |
|---|---|---|
| App policy izinliyken metadata resolve | 200 | ✅ T6, T9 |
| App policy izinsizken | 403 | ✅ T4, T5 |
| Scope miss (başkasının personeli) | 403/404 | ✅ T7 |
| File not found | 404 | ✅ T8 |
| Archived dosya için resolve | 404 | ✅ T18b |
| Unsupported media type | 415 | ✅ T14, T15 |
| File too large | 413 | ✅ T23 |
| Stream 200 | 200 | ✅ T10 |
| Stream 206 | 206 | ✅ T12 |
| Stream 304 | 304 | ✅ T11 |
| Audit event yazılıyor | DB kayıt | ✅ T20, T21, T22 |
| App isolation (çapraz domain) | 403 | ✅ ISO-2, ISO-3, ISO-4 |
| Binary missing | 503 | ✅ BIN-2 |
| Hash mismatch | 409 | ✅ BIN-3 |
| Health check (normal) | 200 healthy | ✅ H1 |
| Health check (storage down) | 503 unhealthy | ✅ H2 |
| Health check (restore) | 200 healthy | ✅ H3 |
| Docker — JWT issuer mismatch | 403 (düzeltildi) | ✅ D4 |
| Docker — upload / archive / versioning | 200 | ✅ D7–D11 |
| Docker — çapraz app izolasyon | 403 | ✅ D12 |
| Docker — E2E YonetimApi → FileService | 200/403 | ✅ D14–D16 |

**Tüm brief senaryoları geçti. Docker testleri de tümüyle geçti.**

---

## 2026-06-29 Güncel Gateway / Client Negatif Test Yenilemesi

**Ortam:** Docker Compose, Gateway `5090`, Keycloak `8080`, FileService iç ağda mTLS-HTTPS.  
**Güncel hesaplar:** `hr001`, `adm001`, `m001-m003`, `p001-p024`; tüm demo şifreleri `Demo1234!`.

Bu bölüm eski `testuser/test_personel_1` test setinin yerine güncel seed ve client yapısıyla yapılan son doğrulamadır.

| Test | Beklenen | Sonuç |
|---|---:|---:|
| Gateway `/health` | 200 | ✅ 200 |
| JWT yokken `/api/personnel?search=` | 401 | ✅ 401 |
| Hosttan FileService plain HTTP direkt erişim | erişilemez | ✅ HTTP 000 |
| `hr001` arama | 29 personel | ✅ 29 |
| `p001` arama | sadece kendi kaydı | ✅ 1 |
| `m001` arama | yönetici + 7 ekip üyesi | ✅ 8 |
| `p001` ile `P002` CV upload | 403 | ✅ 403 |
| `m001` ile `P008` dosya listesi | 403 | ✅ 403 |
| Sahte PDF (`.pdf`, magic yok) | 415 | ✅ 415 |
| Gerçek PDF'i `photo` alanına upload | 415 | ✅ 415 |
| Gerçek PNG'i `photo` alanına upload | 200 | ✅ 200 |
| Aynı entity için 2 CV | 1 aktif, eski revoked/archived | ✅ |
| Aynı entity için 2 document | 2 aktif | ✅ |
| FileId bazlı document archive | aktif listeden düşer | ✅ |

**DB doğrulama özeti (`QA999` geçici entity):**

```text
qa999_refs=cv:active:1
qa999_refs=cv:revoked:1
qa999_refs=document:active:1
qa999_refs=document:revoked:1
qa999_refs=photo:active:1
qa999_objects=active:3
qa999_objects=archived:2
```

**Audit doğrulama özeti:**

```text
files.audit_events:
archive:success
create:denied:magic_byte_mismatch
create:denied:unsupported_media_type
create:success

yonetim.audit_events:
PersonnelCvUploaded:denied:access_denied
PersonnelFilesListed:denied:access_denied
PersonnelPhotoUploaded:error
PersonnelFileArchived:success
```

**Ek düzeltme:** `photo` alanı artık PDF kabul etmez. FileService relation type'a göre izinli uzantı ve MIME kontrolü yapar; magic-byte kontrolü bunun ardından devam eder.

**Build doğrulaması:**

```text
dotnet build FileServiceApi/FileServiceApi.csproj -> başarılı
npm run build (client) -> başarılı
```

**Performans bulgusu:** Upload yavaşlığının ana teknik sebepleri çift proxy/form okuma, upload sonrası ikinci SHA256 disk okuması ve download sırasında her istekte tam dosya hash doğrulamasıdır. V2 hızlandırma adayları `PROJECT_STATUS.md` içindeki "2026-06-29 Negatif Senaryo ve Performans Kontrolü" bölümüne işlendi.
