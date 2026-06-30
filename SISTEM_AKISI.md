# Sistem Akışı — İlk Girişten Son Çıkışa

Bu dosya, sistemin tüm servislerini ayağa kaldırmaktan başlayarak bir kullanıcının login olup dosya yüklemesine, indirmesine ve çıkış yapmasına kadar olan tam yolu, kullanılan komutlarla birlikte adım adım anlatır.

---

## Genel Mimari Hatırlatma

```
Tarayıcı (Client :5173)
    │
    ▼ HTTP :5090
Gateway (YARP)
    │
    ├──▶ YonetimApi (:5076)  ──(JWT + mTLS)──▶ FileServiceApi (:8080 iç)
    │         │                                        │
    │    yonetim.* DB                           files.* DB + storage
    │
    └──▶ FlotaApi (:5077)  ──(JWT + mTLS)──▶ FileServiceApi (aynı)

Keycloak (:8080) — token üretimi, JWKS servisi
PostgreSQL — tüm veritabanı (files.*, yonetim.*, filo.*)
```

---

## BÖLÜM 1 — Sistemi Başlatma

### 1.1 Ön koşul: NFS mount (UTM kullanılıyorsa)

Mac reboot sonrasında NFS mount kaybolur. Servisleri başlatmadan önce:

```bash
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
```

Mount'u doğrula:
```bash
ls /Volumes/platform-files/export/.probe
```

Dosya görünüyorsa storage hazır. NFS kullanmıyorsan (`test-storage/` ile çalışıyorsan) bu adımı atla.

### 1.2 Docker Compose ile tüm servisleri kaldır

```bash
cd ~/Desktop/dosya-sistemi-projesi
docker compose up --build -d
```

Bu komut sırasıyla şunu yapar:
1. `postgres` container'ı başlar → `db/docker-init/01-schema.sql` çalışır (şema), `02-seed.sql` çalışır (seed)
2. `keycloak` container'ı başlar → `keycloak/realm-platform.json` auto-import edilir (kullanıcılar, roller, client'lar)
3. `fileservice`, `yonetimapi`, `flotaapi`, `gateway` container'ları başlar

### 1.3 Servislerin sağlıklı olduğunu doğrula

```bash
docker compose ps
```

Tüm servisler `healthy` görünmeli. Geçici `starting` durumu normaldir — 30-60 saniye bekle.

Sağlık kontrolleri:
```bash
# Gateway
curl http://localhost:5090/health

# FileServiceApi (override.yml açıksa)
curl -k https://localhost:5205/health
```

Beklenen cevap:
```json
{"status":"healthy","service":"Gateway-01"}
{"status":"healthy","service":"FileServiceApi","checks":{"storage":{"status":"healthy"},"database":{"status":"healthy"}}}
```

### 1.4 Client'ı başlat (geliştirme modunda)

```bash
cd ~/Desktop/dosya-sistemi-projesi/client
npm run dev
```

Tarayıcıda `http://localhost:5173` açılır.

---

## BÖLÜM 2 — Kimlik Doğrulama (Login)

### 2.1 Kullanıcı login formuna bilgilerini girer

Tarayıcıda `http://localhost:5173` adresinde login ekranı görünür.

**Test kullanıcıları:**

| Kullanıcı | Şifre | Yetki |
|---|---|---|
| `hr001` | Demo1234! | Tüm personeli okur + yazar |
| `adm001` | Demo1234! | Tüm personeli okur + yazar |
| `m001` | Demo1234! | Yalnız ekibini okur (P001–P007) |
| `p001` | Demo1234! | Yalnız kendini okur |

### 2.2 Client → Keycloak: token isteği

Login butonuna basınca `client/src/api.ts → login()` fonksiyonu çalışır:

```
POST http://localhost:5173/realms/platform/protocol/openid-connect/token
  (Vite proxy → http://localhost:8080/realms/platform/protocol/openid-connect/token)

Body (form-urlencoded):
  grant_type=password
  client_id=frontend-test
  username=hr001
  password=Demo1234!
```

Keycloak başarılı yanıtta `access_token` ve `refresh_token` döner.

### 2.3 Client token'ı işler ve saklar

`auth.ts → saveAuth()`:
- JWT `payload` base64url decode edilir (kütüphane kullanılmaz)
- `sub`, `preferred_username`, `personnel_id`, `roles`, `exp` claim'leri çıkarılır
- `access_token` + `refresh_token` `localStorage`'a yazılır
- Sayfa render'ı dashboard'a geçer

**`hr001` JWT içinde bu claim'ler bulunur:**
```json
{
  "preferred_username": "hr001",
  "personnel_id": "HR001",
  "roles": ["personnel.files.read.all", "personnel.files.write.all"],
  "exp": 1751291234
}
```

### 2.4 Token otomatik yenileme

`App.tsx`:
- Access token `exp`'den 60 saniye önce `refresh_token` grant ile Keycloak'tan yeni token alınır
- Sayfa yenilendiğinde `localStorage`'daki refresh token geçerliyse sessizce yenilenir
- Refresh token süresi dolmuşsa kullanıcı login sayfasına gönderilir

```
POST /realms/platform/protocol/openid-connect/token
  grant_type=refresh_token
  client_id=frontend-test
  refresh_token=<mevcut_refresh_token>
```

**Manuel terminal ile token alma:**
```bash
TOKEN=$(curl -s -X POST http://localhost:8080/realms/platform/protocol/openid-connect/token \
  -d grant_type=password \
  -d client_id=frontend-test \
  -d username=hr001 \
  -d password=Demo1234! | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")
```

---

## BÖLÜM 3 — Personel Arama

### 3.1 Dashboard açılışında otomatik arama

Dashboard açılınca boş query ile `GET /api/personnel?search=` çağrılır — tüm erişilebilir personel listelenir.

### 3.2 Kullanıcı arama kutusuna yazar

300ms debounce sonrasında:

```
GET http://localhost:5090/api/personnel?search=Ali
Authorization: Bearer <access_token>
```

**Gateway → YonetimApi**

YonetimApi `SearchPersonnelAsync`:
1. JWT doğrulanır (`AddJwtBearer`)
2. Kullanıcının rolleri okunur (`preferred_username = hr001`, `roles = [read.all, write.all]`)
3. Rol kapsamına göre SQL değişir:

```sql
-- read.all → tüm personel
SELECT personnel_id, display_name, department, title
FROM yonetim.personnel
WHERE display_name ILIKE '%Ali%' OR personnel_id ILIKE '%Ali%'
ORDER BY display_name LIMIT 30;

-- read.team → yalnız yöneticinin ekibi
SELECT p.personnel_id, p.display_name, p.department, p.title
FROM yonetim.personnel p
WHERE p.personnel_id IN (
    SELECT personnel_id FROM yonetim.team_members WHERE manager_id = 'M001'
    UNION SELECT 'M001'
) AND (p.display_name ILIKE '%Ali%' OR p.personnel_id ILIKE '%Ali%')
ORDER BY p.display_name LIMIT 30;

-- read.self → yalnız kendi kaydı
SELECT personnel_id, display_name, department, title
FROM yonetim.personnel
WHERE personnel_id = 'P001' AND (display_name ILIKE '%%' OR personnel_id ILIKE '%%')
LIMIT 1;
```

4. Sonuç JSON dizisi olarak döner

**Terminal ile doğrulama:**
```bash
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5090/api/personnel?search=Ali"
```

---

## BÖLÜM 4 — Personel Seçme ve Dosya Listeleme

### 4.1 Kullanıcı listeden bir personel seçer

Sidebar'dan personel kart'ına tıklanır → `PersonnelFileView` bileşeni açılır.

### 4.2 Dosya listesi çekme

```
GET http://localhost:5090/api/personnel/P001/files
Authorization: Bearer <access_token>
```

**Akış:**

```
Client
  → Gateway (:5090)
  → YonetimApi (:5076) — ListPersonnelFilesAsync
      1. JWT doğrula
      2. CanReadAsync(user, "P001") → rollere göre izin kontrolü
         - read.all → ✅
         - read.team → DB'de P001 ekip üyesi mi?
         - read.self → P001 == HR001? → hayır → 403
      3. Keycloak'tan service token al (client_credentials, önbellekli)
      4. FileServiceApi'ye ilet:
  → FileServiceApi (:8080) — ListFilesAsync
      GET internal/files/list?domain=personnel&entityType=personnel&entityId=P001
      JWT doğrula (app_code=yonetimapi) + mTLS doğrula
      SELECT * FROM files.references WHERE entity_id='P001'
        AND is_primary=true AND status='active'
      JOIN files.objects WHERE status='active'
      → JSON dizi döner
```

Dönen yanıt örneği:
```json
[
  {
    "fileId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "domain": "personnel",
    "relationType": "cv",
    "contentType": "application/pdf",
    "originalFileName": "ozgecmis.pdf",
    "extension": "pdf",
    "sizeBytes": 45312,
    "sha256": "abc123...",
    "classification": "internal",
    "status": "active",
    "createdAt": "2026-06-29T10:30:00Z",
    "etag": "\"sha256:abc123...\""
  }
]
```

**Terminal ile doğrulama:**
```bash
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/files
```

---

## BÖLÜM 5 — Dosya Yükleme

### 5.1 Kullanıcı "Dosya Yükle" butonuna basar

Upload modal açılır. Kullanıcı dosya tipini seçer ve dosyayı sürükler / seçer.

### 5.2 Upload isteği

```
POST http://localhost:5090/api/personnel/P001/cv
Authorization: Bearer <access_token>
Content-Type: multipart/form-data

file: <binary>
```

**YonetimApi → FileServiceApi akışı:**

```
YonetimApi — ProxyUploadAsync("cv")
  1. JWT doğrula
  2. CanWriteAsync(user, "P001") → write.all veya (write.self AND HR001==P001)
  3. Service token al
  4. Multipart form yeniden oluştur (file + domain + entityType + entityId + relationType + ...)
  → FileServiceApi — CreateFileAsync
      1. JWT app_code=yonetimapi doğrula + mTLS doğrula
      2. app_policies'den yonetimapi için izin kontrolü
         - domain "personnel" ✅
         - relationType "cv" ✅
         - boyut < 10MB ✅
      3. Uzantı kontrolü: cv → yalnız "pdf" ✅
      4. Magic-byte kontrolü: ilk 4 byte == %PDF ✅
      5. Kardinalite kontrolü:
         SELECT cardinality FROM files.relation_type_config WHERE relation_type='cv'
         → 'single'
         SELECT * FROM files.references WHERE entity_id='P001' AND relation_type='cv'
           AND is_primary=true AND status='active'
         → varsa: objects.status='archived', references.status='revoked'
      6. file_id = yeni UUID, shard path = "personnel/3f/a8/3fa8....pdf"
      7. Staging'e yaz: /srv/files/staging/personnel/3f/a8/3fa8....pdf
      8. SHA256 hesapla (staging dosyasından)
      9. Atomic File.Move: staging → export (/srv/files/export/...)
     10. DB kayıt: files.objects (active) + files.references (active, is_primary=true)
         -- EF Core SaveChangesAsync: UPDATE (eski revoked) → INSERT (yeni) sırasıyla
         -- DB trigger trg_check_single_primary → INSERT sırasında kontrol eder
     11. files.audit_events kaydı
     ← 200 {fileId: "...", ...}
  5. yonetim.audit_events kaydı (PersonnelCvUploaded, success)
  ← 200
```

**Terminal ile doğrulama:**
```bash
python3 -c "open('/tmp/t.pdf','wb').write(b'%PDF-1.4\n%%EOF\n')"

curl -X POST \
  -F "file=@/tmp/t.pdf" \
  -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/cv
```

Hata kodları:
- `415` — yanlış uzantı (örn. cv alanına .png) veya magic-byte uyuşmazlığı (sahte PDF)
- `413` — dosya 10MB'ı aşıyor
- `403` — yazma yetkisi yok

---

## BÖLÜM 6 — Dosya İndirme

### 6.1 Kullanıcı dosya kartındaki indirme butonuna basar

```
GET http://localhost:5090/api/personnel/P001/files/<fileId>/content
Authorization: Bearer <access_token>
```

**YonetimApi → FileServiceApi akışı:**

```
YonetimApi — ProxyGetFileContentByIdAsync
  1. JWT doğrula
  2. CanReadAsync(user, "P001") → izin kontrolü
  3. FileService list'ten P001'in dosyaları → fileId listede var mı?
     (sahte fileId ile başka personelin dosyasına erişim engeli)
  4. Service token al
  5. GET internal/files/<fileId>/content ilet
  → FileServiceApi — GetContentAsync
      1. JWT + mTLS doğrula
      2. files.objects tablosundan nesne bul
      3. SHA256 doğrula (disk vs DB)
         eşleşmiyorsa → 409 hash_mismatch
      4. If-None-Match == ETag ise → 304 Not Modified
      5. /srv/files/export/<path> stream et
         - resimler: Content-Disposition: inline
         - PDF/belgeler: Content-Disposition: attachment; filename="ozgecmis.pdf"
         - Range header varsa → 206 Partial Content
```

Client `fetchFileBlob()` fonksiyonu:
- Response blob alır
- `Content-Disposition` header'ından dosya adını çıkarır
- Tarayıcıda `<a>` elemanı oluşturup tıklar → dosya indirilir
- Blob URL'i temizler

**Terminal ile doğrulama:**
```bash
FILE_ID="<yukarda-alinan-fileId>"
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5090/api/personnel/P001/files/$FILE_ID/content" \
  -o indirilen-dosya.pdf
```

ETag testi (304):
```bash
ETAG=$(curl -si -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5090/api/personnel/P001/files/$FILE_ID/content" | grep -i etag | awk '{print $2}')

curl -si \
  -H "Authorization: Bearer $TOKEN" \
  -H "If-None-Match: $ETAG" \
  "http://localhost:5090/api/personnel/P001/files/$FILE_ID/content"
# → HTTP/1.1 304 Not Modified
```

---

## BÖLÜM 7 — Dosya Arşivleme

İki farklı arşivleme yolu vardır:

### 7A — Single-primary tipler (cv, photo, official_document)

```
POST http://localhost:5090/api/personnel/P001/cv/archive
Authorization: Bearer <access_token>
```

```
YonetimApi — ProxyArchiveAsync("cv")
  1. JWT + CanWriteAsync kontrolü
  2. Service token al
  3. FileService resolve → aktif CV'nin fileId'si bulunur
  4. POST internal/files/<fileId>/archive ilet
  → FileServiceApi — ArchiveFileAsync
      status='archived' DEĞİLSE (idempotent):
        objects.status = 'archived'
        references.status = 'revoked'
        SaveChangesAsync
        files.audit_events kaydı
  5. yonetim.audit_events kaydı (PersonnelCvArchived)
  ← 200
```

**Terminal ile doğrulama:**
```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/cv/archive

# Doğrula — liste boş olmalı
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/files
# → []
```

### 7B — Multi-primary tipler (document, attachment, report)

```
POST http://localhost:5090/api/personnel/P001/files/<fileId>/archive
Authorization: Bearer <access_token>
```

YonetimApi önce P001'in dosya listesinden `fileId`'nin bu personele ait olduğunu doğrular, sonra FileServiceApi'ye iletir.

**Terminal ile doğrulama:**
```bash
DOC_FILE_ID="<belge-fileId>"
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5090/api/personnel/P001/files/$DOC_FILE_ID/archive"
```

---

## BÖLÜM 8 — Kardinalite Davranışı

Sisteme iki kez CV yüklenirse ne olur:

```bash
# 1. CV yükle
curl -X POST -F "file=@/tmp/t.pdf" \
  -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/cv

# 2. İkinci CV yükle
curl -X POST -F "file=@/tmp/t2.pdf" \
  -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/cv

# Liste: yalnız 1 aktif CV görünmeli
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:5090/api/personnel/P001/files
```

DB'de durum:
```sql
SELECT fo.status, fr.status, fr.is_primary, fr.relation_type, fo.created_at
FROM files.objects fo
JOIN files.references fr ON fo.file_id = fr.file_id
WHERE fr.entity_id = 'P001' AND fr.relation_type = 'cv'
ORDER BY fo.created_at;

-- Sonuç:
-- archived | revoked | true | cv | 2026-06-29 10:00
-- active   | active  | true | cv | 2026-06-29 10:05
```

Aynı şeyi `document` ile yaparsak iki kayıt da aktif kalır (multi-primary).

---

## BÖLÜM 9 — RBAC Sınır Testleri

```bash
P_TOKEN=$(curl -s ... -d username=p001 -d password=Demo1234! | python3 ... .access_token)
M_TOKEN=$(curl -s ... -d username=m001 -d password=Demo1234! | python3 ... .access_token)

# p001 → kendi kaydı → 200/404 (dosya yoksa 404)
curl -H "Authorization: Bearer $P_TOKEN" \
  http://localhost:5090/api/personnel/P001/files

# p001 → başkasının kaydı → 403
curl -H "Authorization: Bearer $P_TOKEN" \
  http://localhost:5090/api/personnel/P002/files

# p001 → upload → 403 (write.self rolü yok)
curl -X POST -F "file=@/tmp/t.pdf" \
  -H "Authorization: Bearer $P_TOKEN" \
  http://localhost:5090/api/personnel/P001/cv

# m001 → ekibindeki P001 → 200/404
curl -H "Authorization: Bearer $M_TOKEN" \
  http://localhost:5090/api/personnel/P001/files

# m001 → ekip dışı P008 → 403
curl -H "Authorization: Bearer $M_TOKEN" \
  http://localhost:5090/api/personnel/P008/files

# m001 → upload → 403 (write.team rolü yok)
curl -X POST -F "file=@/tmp/t.pdf" \
  -H "Authorization: Bearer $M_TOKEN" \
  http://localhost:5090/api/personnel/P001/cv
```

---

## BÖLÜM 10 — Çıkış Yapma

Tarayıcıda sağ üstteki çıkış butonuna basılır:

1. `clearAuth()` → `localStorage`'dan token silinir
2. `sessionStorage` temizlenir
3. React state `auth = null` olur
4. Login ekranı tekrar render edilir

**Token iptal edilmiyor** (V1'de Keycloak logout çağrısı yok). Access token `exp` süresine kadar teknik olarak geçerli, ama `localStorage`'dan silindiği için kullanılamaz.

---

## BÖLÜM 11 — Sistemi Durdurma

### Veriyi koruyarak durdur (container'ları durdur, volume'ları koru)

```bash
docker compose stop
```

Yeniden başlatmak için:
```bash
docker compose start
```

### Tüm veriyi sıfırla ve baştan başla (volume'ları da sil)

```bash
docker compose down -v
docker compose up --build -d
```

Bu komut:
- Tüm container'ları kaldırır
- PostgreSQL volume'unu siler (tüm dosya kayıtları, audit logları, vb. silinir)
- Keycloak realm sıfırlanır
- `01-schema.sql` + `02-seed.sql` sıfırdan çalışır
- **Fiziksel dosyalar silinmez** (`test-storage/export/` veya NFS'teki binary'ler korunur)

---

## Özet Tablo

| Adım | Client URL | Backend | Komut |
|---|---|---|---|
| Login | `POST /realms/.../token` | Keycloak | `curl -d grant_type=password ...` |
| Personel arama | `GET /api/personnel?search=` | YonetimApi → yonetim.personnel | `curl -H "Authorization: Bearer $T" .../personnel?search=Ali` |
| Dosya listesi | `GET /api/personnel/{id}/files` | YonetimApi → FileServiceApi → files.* | `curl ... /personnel/P001/files` |
| Dosya yükleme | `POST /api/personnel/{id}/cv` | YonetimApi → FileServiceApi → storage | `curl -X POST -F "file=@..." .../cv` |
| Dosya indirme | `GET /api/personnel/{id}/files/{fid}/content` | YonetimApi → FileServiceApi → stream | `curl ... /files/$FID/content -o out.pdf` |
| Arşivleme (single) | `POST /api/personnel/{id}/cv/archive` | YonetimApi → FileServiceApi | `curl -X POST .../cv/archive` |
| Arşivleme (multi) | `POST /api/personnel/{id}/files/{fid}/archive` | YonetimApi → FileServiceApi | `curl -X POST .../files/$FID/archive` |
| Çıkış | — | localStorage temizlenir | — |
