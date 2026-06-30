# Sistem Mimarisi — Tam Referans Dokümanı

Bu doküman, sistemin her katmanını — auth, storage, RBAC, audit, dosya akışı — baştan sona açıklar.
Kod ile birlikte okunacak şekilde tasarlandı; her başlık gerçek implementasyona doğrudan karşılık gelir.

---

## 1. Sisteme Genel Bakış

```
Kullanıcı
   │
   │  browser / curl
   ▼
┌──────────────────────┐
│   Gateway (nginx)    │  Port 5090 — dış dünyaya açık tek kapı
│   nginx:alpine       │
└──────────┬───────────┘
           │
     /api/personnel/*          /api/vehicles/*
           │                         │
           ▼                         ▼
┌─────────────────┐      ┌─────────────────┐
│   YonetimApi    │      │    FlotaApi     │
│   .NET / :8080  │      │   .NET / :8080  │
└────────┬────────┘      └────────┬────────┘
         │                        │
         │   mTLS + service JWT   │
         └──────────┬─────────────┘
                    ▼
         ┌─────────────────────┐
         │   FileServiceApi    │  Sadece container ağından erişilebilir
         │   .NET / HTTPS:8080 │  Dışarıya asla açık değil
         └──────────┬──────────┘
                    │
          ┌─────────┴──────────┐
          ▼                    ▼
   ┌─────────────┐    ┌──────────────────┐
   │  PostgreSQL  │    │  Files-01 (NFS)  │
   │  platformdb  │    │  /app/storage    │
   └─────────────┘    └──────────────────┘
```

Sistemde iki ayrı domain var:

| Domain | Tüketici | Keycloak client | Dosya tipleri |
|--------|----------|-----------------|---------------|
| `personnel` | YonetimApi | `yonetimapi` | cv, photo, official_document, document, attachment |
| `fleet` | FlotaApi | `filoapi` | photo, document, official_document, attachment, report |

---

## 2. Kimlik ve Token Sistemi

### 2.1 İki Ayrı Kimlik Zinciri

Sistemde **aynı anda iki farklı kimlik** çalışır ve birbirlerine karışmaz:

```
[1] KULLANICI KİMLİĞİ          [2] SERVİS KİMLİĞİ
─────────────────────          ────────────────────
Keycloak → user JWT            Keycloak → service JWT
"Bu isteği kim yapıyor?"       "Bu isteği hangi uygulama yapıyor?"
Taşıyan: client browser        Taşıyan: YonetimApi / FlotaApi
Geçerlilik: 5 dk (kısa)       Geçerlilik: 5 dk (otomatik yenilenir)
Hedef: YonetimApi              Hedef: FileServiceApi
```

Bu ayrımın sebebi: FileServiceApi kullanıcı kimliğinden haberdar değildir.
Yalnızca "hangi uygulama çağırıyor, o uygulamanın policy'si ne?" sorusunu yanıtlar.
Kullanıcı kimliği YonetimApi katmanında doğrulanır ve gerekli kontroller yapılır.

### 2.2 Keycloak Realm: `platform`

```
Keycloak realm: platform
│
├── Client: frontend-test  (public, password grant)
│     Kullanıcılar bu client üzerinden token alır.
│     Claims: sub, preferred_username, personnel_id, roles[]
│
├── Client: yonetimapi  (confidential, client_credentials)
│     YonetimApi, FileServiceApi'ye çağrı yaparken bu client ile token alır.
│     Claims: app_code = "yonetimapi"
│
└── Client: filoapi  (confidential, client_credentials)
      FlotaApi, FileServiceApi'ye çağrı yaparken bu client ile token alır.
      Claims: app_code = "filoapi"
```

### 2.3 Kullanıcı Token'ı (User JWT)

Kullanıcı login olduğunda Keycloak aşağıdaki JWT'yi imzalar:

```json
{
  "sub": "uuid-of-user",
  "preferred_username": "hr001",
  "personnel_id": "HR001",
  "roles": ["personnel.files.read.all", "personnel.files.write.all"],
  "iss": "http://localhost:8080/realms/platform",
  "exp": 1234567890
}
```

Bu token:
- YonetimApi'ye her istekte `Authorization: Bearer <token>` header'ında gider
- FileServiceApi'ye **hiçbir zaman ulaşmaz**
- YonetimApi bunu doğrular, içindeki `roles` claim'ini RBAC için kullanır

### 2.4 Service Token (Service JWT)

YonetimApi, FileServiceApi'ye her çağrıdan önce Keycloak'tan kendi adına ayrı bir token alır:

```
YonetimApi → POST /realms/platform/.../token
             grant_type=client_credentials
             client_id=yonetimapi
             client_secret=yonetimapi-secret-v1

Keycloak  → {
               "access_token": "eyJ...",   ← app_code: "yonetimapi"
               "expires_in": 300
             }
```

Bu token **kullanıcının token'ının kopyası değildir.** YonetimApi'nin kendi kimliğidir.
`ITokenService` singleton olarak çalışır, token'ı 30 saniye erken expire sayarak önbelleğe alır.

---

## 3. Auth Akışı — Adım Adım

### 3.1 Login

```
Kullanıcı         Client          Keycloak
   │               │                 │
   │─── giriş ────▶│                 │
   │               │─── POST /token ▶│
   │               │    (username,   │
   │               │     password)   │
   │               │◀── user JWT ────│
   │               │    (imzalı,     │
   │               │     roller ile) │
   │               │                 │
   │               │  localStorage'a kaydeder:
   │               │  { token, refreshToken,
   │               │    refreshExpiresAt, user }
```

### 3.2 API İsteği (örnek: CV indirme)

```
Client            Gateway           YonetimApi        FileServiceApi
  │                  │                  │                   │
  │──GET /api/personnel/P001/cv/content▶│                   │
  │   Authorization: Bearer <user JWT>  │                   │
  │                  │                  │                   │
  │                  │  (nginx proxy)   │                   │
  │                  │─────────────────▶│                   │
  │                  │                  │                   │
  │                  │           [1] user JWT doğrula       │
  │                  │           [2] RBAC kontrol           │
  │                  │               (roles claim'den)      │
  │                  │           [3] service token al       │
  │                  │               (Keycloak'tan)         │
  │                  │                  │                   │
  │                  │                  │──GET /internal/files/resolve▶│
  │                  │                  │   Authorization: Bearer <service JWT>
  │                  │                  │   X-Actor-User-Id: hr001
  │                  │                  │   (+ mTLS client cert)
  │                  │                  │                   │
  │                  │                  │           [4] service JWT doğrula
  │                  │                  │           [5] mTLS cert doğrula
  │                  │                  │           [6] app_policy kontrol
  │                  │                  │           [7] DB'den fileId bul
  │                  │                  │◀── fileId, metadata ──────────│
  │                  │                  │                   │
  │                  │                  │──GET /internal/files/{id}/content▶│
  │                  │                  │   (aynı service JWT + mTLS)   │
  │                  │                  │                   │
  │                  │                  │           [8] dosyayı stream et
  │                  │                  │◀── byte stream ───────────────│
  │                  │◀─────────────────│                   │
  │◀─────────────────│                  │                   │
  │  byte stream     │                  │                   │
```

### 3.3 YonetimApi'deki Kontroller (adım [1-3])

```
user JWT alındı
      │
      ▼
[1] JWT imzası Keycloak public key (JWKS) ile doğrula
    → geçersizse 401
      │
      ▼
[2] RBAC: IPermissionService.CanReadAsync(user, targetPersonnelId)
    ┌─ roles.Contains("personnel.files.read.all")   → tüm personele erişir
    ├─ roles.Contains("personnel.files.read.team")  → DB'de ekibindeki personele erişir
    └─ roles.Contains("personnel.files.read.self")  → yalnız kendi ID'sine erişir
    → izin yoksa 403
      │
      ▼
[3] ITokenService.GetServiceTokenAsync()
    → önbellekte taze token varsa direkt kullanır
    → yoksa/süresi dolduysa Keycloak'a client_credentials isteği atar
```

### 3.4 FileServiceApi'deki Kontroller (adım [4-7])

```
service JWT + mTLS cert alındı
      │
      ▼
[4] mTLS: Kestrel TLS el sıkışmasında client sertifikası zorunlu
    → CA chain doğrula (platform-ca imzalamış mı?)
    → CN whitelist kontrol: yalnız "yonetimapi" veya "filoapi"
    → geçersiz CN → TLS reddi (bağlantı kurulmaz)
      │
      ▼
[5] JWT: AddJwtBearer middleware
    → Keycloak JWKS'den public key çek
    → imza doğrula, expire kontrol
    → geçersizse 401
      │
      ▼
[6] app_code claim'den policy lookup
    app_code = "yonetimapi" → files.app_policies tablosundan çek:
    {
      can_read: true, can_create: true, can_archive: true,
      allowed_domains: ["personnel"],
      allowed_file_types: ["cv","photo","official_document","document","attachment"],
      max_file_size_bytes: 10485760
    }
    → policy yoksa / domain/tür izinsizse 403
      │
      ▼
[7] İş mantığı (resolve/list/content/create/archive)
```

---

## 4. RBAC Modeli

### 4.1 Rol Yapısı

Her Keycloak rolü üç boyutu tek isimde taşır:

```
{kaynak}.{eylem}.{kapsam}

personnel.files.read.self   → yalnız kendi personnel kaydını okur
personnel.files.read.team   → kendi + DB'deki ekibini okur
personnel.files.read.all    → tüm personeli okur
personnel.files.write.self  → kendi dosyasını yükler/arşivler
personnel.files.write.all   → herkese dosya yükler/arşivler
```

### 4.2 Kapsam Belirleme

```
read.all  → SELECT * FROM personnel WHERE name ILIKE $1
read.team → SELECT * WHERE personnel_id IN (
              SELECT personnel_id FROM team_members WHERE manager_id = $ownId
              UNION SELECT $ownId
            )
read.self → SELECT * WHERE personnel_id = $ownId
```

### 4.3 Test Kullanıcıları

| Kullanıcı | Şifre | Rolü | Erişim |
|-----------|-------|------|--------|
| hr001 | Demo1234! | read.all + write.all | Tüm personel, tüm işlemler |
| adm001 | Demo1234! | read.all + write.all | Tüm personel, tüm işlemler |
| m001/m002/m003 | Demo1234! | read.team | Kendi + ekibi (read-only) |
| p001..p024 | Demo1234! | read.self | Yalnız kendi kaydı (read-only) |

---

## 5. mTLS — Servis Sertifika Katmanı

### 5.1 Sertifika Hiyerarşisi

```
platform-ca  (CA, 10 yıl)
  ├── fileservice.crt  (CN=fileservice, SAN=fileservice,localhost)  ← server cert
  ├── yonetimapi.crt   (CN=yonetimapi)  ← client cert
  └── filoapi.crt      (CN=filoapi)     ← client cert
```

### 5.2 TLS握手 Akışı

```
YonetimApi                          FileServiceApi (Kestrel)
    │                                       │
    │──── TLS ClientHello ─────────────────▶│
    │◀─── ServerHello + fileservice.crt ────│
    │     (platform-ca ile doğrula)         │
    │──── yonetimapi.crt gönder ───────────▶│
    │                             CN "yonetimapi" whitelist'te mi? ✓
    │◀─── TLS el sıkışması tamam ───────────│
    │                                       │
    │──── HTTPS isteği (service JWT) ──────▶│
```

JWT ve mTLS birbirini ikame etmez; **her ikisi de aynı anda zorunlu**:
- mTLS: "Bu bağlantıyı açan servis güvenilir mi?" (ağ katmanı)
- JWT: "Bu isteği yapan app_code izinli mi?" (uygulama katmanı)

---

## 6. Dosya Depolama Modeli

### 6.1 Fiziksel Yapı (Files-01 / NFS)

```
/app/storage/           ← container içi bağlama noktası
  export/               ← ReadPath = kalıcı depolama (NFS export)
    personnel/
      ab/
        cd/
          abcd1234-....pdf     ← UUID bazlı shard
  staging/              ← StagingPath = geçici yazma alanı
    personnel/
      ...                      ← başarılı yüklemede buradan export'a taşınır
```

Dosya adı formatı: `{domain}/{uuid[0:2]}/{uuid[2:4]}/{uuid}.{ext}`

### 6.2 Upload Akışı (Atom)

```
1. Magic-byte kontrolü (ilk 12 byte okur, dosyayı ret etmeden önce kapat)
2. File stream → staging/{relative_path}'e yaz
3. Staging dosyasından SHA256 hesapla (disk write bütünlüğü de doğrulanır)
4. File.Move(staging → export)   ← aynı dosya sistemi → atomic rename
5. DB: files.objects + files.references INSERT
   (DB fail → export'tan da sil = rollback)
6. Audit: create/success
```

Neden staging?
- Eksik yükleme (network kesildi) asla export'a karışmaz
- SHA256 tamamlanmış ve diske yazılmış byte'lardan hesaplanır

### 6.3 Download Akışı

```
1. DB: fileId → objects tablosundan metadata çek
2. If-None-Match == ETag ("sha256:<hash>") → 304 döndür (disk okuma yok)
3. Path traversal kontrolü (normalizedFull.StartsWith(normalizedRoot))
4. File.Exists kontrolü → yoksa 503
5. Content-Disposition header: RFC 5987 encoding
6. Results.Stream(fileStream, enableRangeProcessing: true)
   → Range header varsa 206 Partial Content
   → yoksa 200 tam stream
```

SHA256 **indirmede yeniden hesaplanmaz** — yükleme anında doğrulanmış ve DB'ye kaydedilmiştir.
ETag = `"sha256:<hash>"` — içerik değişmediği sürece 304 ile cevap verilir, disk okunmaz.

---

## 7. Veritabanı Yapısı

### 7.1 Şema Haritası

```
platformdb
├── files.*           ← FileServiceApi'nin sahibi olduğu tablolar
│     ├── objects          dosya metadata kataloğu
│     ├── references       entity ↔ dosya bağlantıları
│     ├── app_policies     hangi app hangi domain/tür'e erişebilir
│     ├── relation_type_config  kardinalite (single/multi)
│     └── audit_events     teknik audit (app bazlı)
│
├── yonetim.*         ← YonetimApi'nin sahibi olduğu tablolar
│     ├── personnel        personel listesi (ad, departman, unvan)
│     ├── team_members     yönetici-ekip ilişkisi (RBAC.team için)
│     └── audit_events     domain audit (kullanıcı bazlı)
│
└── filo.*            ← FlotaApi'nin sahibi olduğu tablolar
      ├── vehicles         araç listesi
      └── audit_events     fleet domain audit
```

### 7.2 files.objects

```sql
file_id       UUID PK
domain        TEXT          -- "personnel" | "fleet"
relative_path TEXT          -- "personnel/ab/cd/abcd....pdf"  (asla dışarı verilmez)
content_type  TEXT          -- "application/pdf"
extension     TEXT          -- "pdf"
original_file_name TEXT     -- yükleyen kullanıcının dosya adı
size_bytes    BIGINT
sha256        TEXT          -- hex, upload anında hesaplanır
classification TEXT         -- "internal" | "confidential" | "public"
status        TEXT          -- "active" | "archived" | "revoked" | "deleted"
created_by_app  TEXT
created_by_user TEXT
created_at    TIMESTAMPTZ
updated_at    TIMESTAMPTZ
```

### 7.3 files.references

```sql
file_id       UUID FK → objects
app_code      TEXT          -- hangi app bu referansı oluşturdu
entity_type   TEXT          -- "personnel" | "vehicle"
entity_id     TEXT          -- "P001", "ARAC-42"
relation_type TEXT          -- "cv" | "photo" | "document" ...
is_primary    BOOLEAN       -- single-primary sisteminde aktif olan
status        TEXT          -- "active" | "revoked"
created_at    TIMESTAMPTZ
```

### 7.4 files.app_policies

```sql
app_code           TEXT PK  -- "yonetimapi" | "filoapi"
can_read           BOOLEAN
can_create         BOOLEAN
can_archive        BOOLEAN
allowed_domains    TEXT[]   -- {"personnel"} veya {"fleet"}
allowed_file_types TEXT[]   -- {"cv","photo",...}
max_file_size_bytes BIGINT
```

Bu tablo **cross-domain izolasyonu** sağlar:
- `yonetimapi` → `fleet` domain'e erişemez → 403
- `filoapi` → `personnel` domain'e erişemez → 403

---

## 8. Kardinalite Sistemi

### 8.1 Single-Primary vs Multi-Primary

| Tip | Kardinalite | Davranış |
|-----|-------------|----------|
| cv | single | Yeni yüklemede eski arşivlenir, tek aktif kalır |
| photo | single | Aynı |
| official_document | single | Aynı |
| document | multi | Birden fazla aktif olabilir, hepsi listede görünür |
| attachment | multi | Aynı |
| report | multi | Aynı |

### 8.2 Single-Primary Upload Akışı

```
Yeni CV yükle
      │
      ▼
references tablosunda bu entity'nin aktif primary CV'si var mı?
      │
  ┌───▼──────────────────────────────────────────────┐
  │  VAR: objects.status = "archived"                │
  │       references.status = "revoked"              │
  └─────────────────────────────────────────────────-┘
      │
      ▼
Yeni objects + references INSERT (status=active, is_primary=true)
      │
      ▼
Tek SaveChangesAsync → atomik (ya hepsi ya hiçbiri)
```

DB güvencesi: `trg_check_single_primary` trigger → aynı anda iki aktif primary girişimini yakalar.

---

## 9. Audit Log Sistemi

### 9.1 İki Katmanlı Audit

```
files.audit_events           yonetim.audit_events
──────────────────           ────────────────────
Teknik katman                Domain katman
"Hangi app, hangi dosyaya,   "Hangi kullanıcı, hangi personele,
 hangi işlemi yaptı?"         hangi iş olayını yaptı?"
app_code = "yonetimapi"      actor = "hr001"
file_id = UUID               personnel_id = "P001"
action = "read"              action = "PersonnelCvDownloaded"
result = "success"           result = "success"
```

Her istek her iki tabloya da yazar — biri teknik, diğeri iş seviyesinde izlenebilirlik sağlar.

### 9.2 Audit Yazılmayan Durumlar

- **304 Not Modified** yanıtları: cache hit'tir, gerçek veri erişimi yoktur → audit yazılmaz
- **mTLS reddi**: TLS katmanında düşer, uygulama kodu hiç çalışmaz → audit kaydı olmaz

---

## 10. Gateway (nginx) Güvenlik Katmanı

```
nginx location kuralları:

/internal/            → 404  (FileServiceApi iç endpoint'leri asla dışarıya açılmaz)
/api/personnel/*      → yonetimapi:8080
/api/vehicles/*       → flotaapi:8080
/health               → {"status":"healthy","service":"Gateway-Nginx"}
/*                    → 404  (tanımsız her route reddedilir)

Hata yanıtları (JSON):
502 upstream_unavailable  → servis çöktüyse
504 upstream_timeout      → 120 saniye içinde cevap gelmediyse

Upload/download için:
client_max_body_size    20m  (personnel) / 25m (fleet)
proxy_request_buffering off  → büyük dosyalar tampona alınmaz, stream edilir
proxy_buffering         off  → download da stream edilir
```

nginx sadece yönlendirir. JWT'yi okumaz, doğrulamaz, değiştirmez.
Tüm kimlik/yetki kararları YonetimApi ve FileServiceApi'de verilir.

---

## 11. Token Yenileme (Refresh Token)

```
Access token süresi doluyor (5 dk)
      │
      ▼
Client: isAccessTokenFresh(auth, skewSeconds=30) → false
      │
      ▼
refreshLogin(refreshToken) →
  POST /realms/platform/.../token
  grant_type=refresh_token
  refresh_token=<mevcut refresh token>
      │
      ▼
Keycloak → yeni access_token + (varsa) yeni refresh_token
      │
      ▼
saveAuth() → localStorage'ı güncelle
      │
      ▼
Devam et (kullanıcı oturumu kapatmaz)

Refresh token da süresi dolduysa:
  → clearAuth() → localStorage temizle
  → kullanıcı login ekranına yönlendirilir
```

---

## 12. ETag ve HTTP Cache

```
Upload anında: sha256 hesaplanır → objects.sha256'ya kaydedilir
Download anında: ETag = "sha256:<hex>" header olarak gönderilir

İkinci istek:
  Client → If-None-Match: "sha256:abc123..."
  FileServiceApi → etag == ifNoneMatch → 304 Not Modified
  → disk okunmaz, transfer yok, audit yazılmaz
```

Dosya içeriği değişmediği sürece (yani arşivlenip yeni yüklenmediği sürece)
aynı hash → aynı ETag → browser cache'den gelir.

---

## 13. Range Request (Partial Download)

Büyük dosyaları parça parça indirmek için — özellikle video/PDF sayfa atlama:

```
Client → GET /api/personnel/P001/cv/content
         Range: bytes=0-65535

YonetimApi → FileServiceApi'ye Range header'ı iletir
FileServiceApi → Results.Stream(enableRangeProcessing: true)
              → 206 Partial Content
              → Content-Range: bytes 0-65535/1048576
              → Accept-Ranges: bytes
```

---

## 14. Magic-Byte Kontrolü

Upload sırasında dosya uzantısıyla binary içeriğin eşleşip eşleşmediği kontrol edilir.
Biri `test.pdf` uzantısıyla aslında bir JPEG yükleyemez.

| Uzantı | Kontrol edilen byte'lar |
|--------|------------------------|
| pdf | `25 50 44 46` (%PDF) |
| jpg/jpeg | `FF D8 FF` |
| png | `89 50 4E 47 0D 0A 1A 0A` |
| webp | `52 49 46 46 .... 57 45 42 50` (RIFF....WEBP) |

Uyuşmazlık → 415 Unsupported Media Type.
Bu kontrol yalnızca **yüklemede** yapılır; indirmede yapılmaz (hash zaten doğrulanmış).

---

## 15. Content-Disposition ve Dosya Adı

İndirme yanıtında dosya adı RFC 5987 standardıyla encode edilir:

```
Content-Disposition: attachment; filename*=UTF-8''toplant%C4%B1%20notu.pdf
Content-Disposition: inline; filename*=UTF-8''profil-foto.jpg
```

- `attachment`: tarayıcı dosyayı indirir
- `inline`: tarayıcı dosyayı gösterir (resimler), kaydetmek istenince encode edilmiş isim kullanılır

Neden RFC 5987? HTTP header'larına non-ASCII karakter (Türkçe ı, ş, ğ vb.) doğrudan yazılamaz.
`Uri.EscapeDataString()` ile percent-encode edilen isim header'a güvenle girer.

Client tarafında çözme:
```js
const rfc5987Match = cd.match(/filename\*=UTF-8''([^;\s]+)/i)
const fileName = rfc5987Match ? decodeURIComponent(rfc5987Match[1]) : fallback
```

---

## 16. Bileşen Sorumluluk Özeti

| Katman | Sorumluluk | Sorumluluk dışı |
|--------|-----------|-----------------|
| **nginx** | Yönlendirme, boyut limiti, JSON hata yanıtları | Token doğrulama, iş mantığı |
| **YonetimApi** | User JWT doğrulama, RBAC, domain audit, FileService proxy | Dosya depolama, fiziksel path |
| **FlotaApi** | Aynı (fleet domain için) | — |
| **FileServiceApi** | Dosya katalog yönetimi, depolama, stream, teknik audit | Kullanıcı kimliği, RBAC |
| **PostgreSQL** | Veri kalıcılığı | — |
| **Files-01 (NFS)** | Binary depolama | Metadata, erişim kontrolü |
| **Keycloak** | Token imzalama, kullanıcı yönetimi | Uygulama iş mantığı |

---

## 17. Yaygın Hata Kodları ve Anlamları

| Kod | Kim döndürür | Anlam |
|-----|-------------|-------|
| 401 | YonetimApi / FileServiceApi | Token yok veya geçersiz |
| 403 `access_denied` | YonetimApi | RBAC: bu personele erişim yok |
| 403 `file_scope_denied` | YonetimApi | fileId bu personnelId'ye ait değil |
| 403 `policy_denied` | FileServiceApi | app_policy domain/tür izni yok |
| 404 | FileServiceApi | Dosya/referans bulunamadı |
| 409 | FileServiceApi | (eski) hash uyuşmazlığı — kaldırıldı |
| 413 | FileServiceApi | Dosya policy limitini aşıyor |
| 415 | FileServiceApi | Uzantı/magic-byte uyuşmazlığı |
| 500 | Herhangi | Yakalanmamış hata |
| 503 | FileServiceApi | Disk/NFS erişilemiyor |
| 502 | nginx | Upstream servis çökmüş |
| 504 | nginx | Upstream 120 saniyede cevap vermedi |
| 304 | FileServiceApi | ETag eşleşti, veri değişmemiş (cache) |
| 206 | FileServiceApi | Range isteği, kısmi içerik |
