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

**Not:** Yukarıdaki diyagram, YonetimApi/FlotaApi üzerinden akan standart proxy zincirini gösterir
(dosya görüntüleme, upload, arşivleme). Personel dosyalarının **ticket tabanlı indirmesi** ("İndir"
butonu) bunun dışında, Gateway'in FileServiceApi'yi mTLS ile doğrudan çağırıp X-Accel-Redirect ile
byte'ı kendisi servis ettiği **paralel, daha kısa bir yol** kullanır — bkz. bölüm 6.3-6.5.

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
│     Claims: sub, preferred_username, personnel_id, vehicle_id, roles[]
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
  "vehicle_id": null,
  "roles": ["personnel.files.read.all", "personnel.files.write.all"],
  "iss": "http://localhost:8080/realms/platform",
  "exp": 1234567890
}
```

`fleetuser` için `vehicle_id: "test_arac_1"`, `personnel_id` ise yoktur.

Bu token **tarayıcıya gönderilmez**; YonetimApi BFF tarafından `at` HttpOnly cookie'sine yazılır.
- Tarayıcı `at` cookie'sini her istekte otomatik gönderir
- YonetimApi cookie'den token'ı okur, doğrular, `roles` claim'ini RBAC için kullanır
- FileServiceApi'ye **hiçbir zaman ulaşmaz**

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

### 3.1 Login (BFF Cookie Akışı)

```
Kullanıcı       Client         YonetimApi (BFF)       Keycloak
   │               │                  │                   │
   │─── giriş ────▶│                  │                   │
   │               │─ POST /api/auth/login ──────────────▶│
   │               │  {username, password}│               │
   │               │                  │── password grant ▶│
   │               │                  │◀─ {access, refresh}│
   │               │◀─ Set-Cookie: at=... rt=... (HttpOnly, SameSite=Strict)
   │               │   {user, expiresAt} JSON             │
   │               │                  │                   │
   │               │  Bellekte tutar: {user, expiresAt}   │
   │               │  Token YOK — cookie browser'da       │
```

`at` cookie: access token, `expires_in` süreli, `Path=/api`
`rt` cookie: refresh token, `refresh_expires_in` süreli, `Path=/api`
Her ikisi de `HttpOnly=true` — JS tarafından okunamaz (XSS koruması).

### 3.2 API İsteği (örnek: CV indirme)

```
Client            Gateway           YonetimApi        FileServiceApi
  │                  │                  │                   │
  │──GET /api/personnel/P001/cv/content▶│                   │
  │   Cookie: at=<token>  (otomatik)    │                   │
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
    → KARAR ARTIK yonetim.role_assignments PostgreSQL tablosundan verilir (Faz C1 cutover,
      2026-07-06). Keycloak JWT'nin "roles" claim'i yalnız kimlik doğrulama sonrası kullanıcıyı
      (personnel_id) belirlemek için okunur — yetki KARARI için değil. Bkz. bölüm 4.
    ┌─ role_assignments'ta {permission}.read.all aktifse   → tüm personele erişir
    ├─ role_assignments'ta {permission}.read.team aktifse  → DB'de (team_members) ekibindeki personele erişir
    └─ role_assignments'ta {permission}.read.self aktifse  → yalnız kendi ID'sine erişir
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

**Önemli mimari değişiklik (Faz C1, 2026-07-03 — 2026-07-06):** Yetki kararının kaynağı Keycloak
realm role'lerinden `yonetim.role_assignments` PostgreSQL tablosuna taşındı. Keycloak artık **sadece
kimlik doğrular** ("bu kişi kim?", `personnel_id`/`sub` claim'i JWT'ye hâlâ Keycloak'tan gelir);
**"bu kişi ne yapabilir?"** sorusunun cevabı DB'den gelir. Bu, kademeli, 4 aşamalı bir göçle yapıldı
(şema+backfill → shadow mode → cutover → yönetim scripti) — detaylı gerekçe ve test kanıtları
`PROJECT_STATUS.md`'nin "Faz C1" bölümlerinde ve `proof/c1-*.md` dosyalarında. Keycloak realm
rolleri/atamaları **silinmedi** (rollback güvenliği, `Authorization__RoleSource` env var'ı ile anında
`Jwt`/`Shadow`/`Db` arasında geçiş yapılabilir) ama artık yetki kararı için OKUNMUYOR.

### 4.1 Rol Yapısı

Her rol üç boyutu tek isimde taşır (`{permission}.{action}.{scope}` formatı — bu format hem eski
Keycloak realm role adlarında hem yeni DB satırlarında aynı kaldı, sadece SAKLANDIĞI yer değişti):

```
personnel.files.read.self   → yalnız kendi personnel kaydını okur
personnel.files.read.team   → kendi + DB'deki ekibini okur
personnel.files.read.all    → tüm personeli okur
personnel.files.write.self  → kendi dosyasını yükler/arşivler
personnel.files.write.all   → herkese dosya yükler/arşivler
ops.read / ops.execute / ops.admin  → OpsApi rolleri (scope yok, NULL)
```

`yonetim.role_assignments` şeması:
```sql
role_assignments(id, principal_id, permission, action, scope, granted_by, granted_at, revoked_at)
-- principal_id: personnel_id (P001, HR001, OPSADMIN, ...) — GetPersonnelId ile aynı normalize kuralı
-- scope NULL olabilir (ops.* rolleri) — UNIQUE INDEX bunu COALESCE(scope,'') ile ele alır
-- revoked_at NULL ise rol aktif, dolu ise iptal edilmiş (satır silinmez, iz kalır)
```

`YonetimApi/Services/PermissionService.cs` (`HasPermissionViaDbAsync`) ve
`OpsApi/Infrastructure/OpsRoleAuthorizationHandler.cs`, all→team→self önceliğiyle bu tabloyu sorgular.
Rol atamak/kaldırmak için Keycloak admin paneli DEĞİL, `tools/manage-role-assignment.sh` kullanılır:
```bash
bash tools/manage-role-assignment.sh grant  P022 personnel.files read all
bash tools/manage-role-assignment.sh revoke P022 personnel.files read all
bash tools/manage-role-assignment.sh list   P022
```
Bu değişiklik anında etkilidir — kullanıcının yeniden login olmasına veya Keycloak realm JSON'unun
güncellenip yeniden import edilmesine gerek yoktur (kanıt: `proof/c1-asama4-rol-yonetim-scripti.md`).

### 4.2 Kapsam Belirleme

```
read.all  → role_assignments'ta {permission}.read.all aktif → herkese erişir
read.team → role_assignments'ta {permission}.read.team aktif VE
              (ownId == targetId OR EXISTS (SELECT 1 FROM team_members WHERE manager_id=$ownId AND personnel_id=$targetId))
read.self → role_assignments'ta {permission}.read.self aktif VE ownId == targetId
```

### 4.3 Test Kullanıcıları

| Kullanıcı | Şifre | Rolü | Erişim |
|-----------|-------|------|--------|
| hr001 | Demo1234! | read.all + write.all | Tüm personel, tüm işlemler |
| adm001 | Demo1234! | read.all + write.all | Tüm personel, tüm işlemler |
| m001/m002/m003 | Demo1234! | read.team | Kendi + ekibi (read-only) |
| p001..p024 | Demo1234! | read.self | Yalnız kendi kaydı (read-only) |
| fleetuser | Demo1234! | — (vehicle_id: test_arac_1) | Yalnız kendi aracını görür/yükler |

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

**⚠️ Not (2026-07-03 itibarıyla güncellendi):** Bu bölüm, aşağıda anlatılan "FilesPublisher" modeliyle
değiştirildi. FileServiceApi artık NFS'e **doğrudan yazmıyor** — kendi container mount'u salt-okunur
(`:ro`). Yazma işlemleri (upload/archive) Files-01 üzerinde ayrı, mTLS ile korunan bir `FilesPublisher`
servisine devredildi. Ayrıntılı kanıt: `proof/nfs-rw-to-publisher-model.md`.

### 6.1 Fiziksel Yapı (Files-01 / NFS / Publisher)

```
Files-01 (192.168.64.3)
  /srv/files/
    export/               ← kalıcı PRIVATE depolama (NFS export, host seviyesinde rw)
      personnel/ab/cd/abcd1234-....pdf
      fleet/ab/cd/abcd1234-....jpg
    export-public/        ← kalıcı PUBLIC depolama (Faz B3, 2026-07-03) — export/'ten TAMAMEN
                             ayrı fiziksel kök dizin (savunma derinliği), aynı shard şeması
    staging/              ← geçici yazma alanı (Publisher kullanır, hem public hem private için ortak)

API sunucusu (192.168.64.5) — FileServiceApi container'ı
  /app/storage/  → NFS mount, docker-compose'da **:ro** (salt-okunur)
    export/        ← FileServiceApi SADECE buradan OKUR (indirme/stream/content)
    export-public/ ← Gateway'in AYRI, kimlik-doğrulamasız salt-okunur mount'u (bkz. 6.6)
    staging/       ← FileServiceApi artık buraya YAZMIYOR

FilesPublisher (Files-01'de, systemd servisi, kullanıcı: files-writer)
  Python stdlib (http.server + ssl), mTLS zorunlu (CN=fileservice)
  POST/DELETE /publish?relativePath=...  ← FileServiceApi'nin TEK yazma yolu
```

Dosya adı formatı: `{domain}/{uuid[0:2]}/{uuid[2:4]}/{uuid}.{ext}` (değişmedi).

**Neden bu model?** FileServiceApi'nin kendi NFS mount'unu salt-okunur yapmak, container
seviyesinde "bu servis yazamaz, sadece okur" garantisini sağlıyor — bir güvenlik açığı/RCE
durumunda bile FileServiceApi üzerinden dosya sistemine yazma/silme mümkün olmuyor. Gerçek
yazma yetkisi sadece `files-writer` kullanıcısıyla çalışan, ayrı, dar yetkili `FilesPublisher`
servisinde.

### 6.2 Upload Akışı (Publisher Modeli)

```
1. Magic-byte kontrolü (ilk 12 byte okur, dosyayı ret etmeden önce kapat)
2. Fail-closed virüs taraması (Faz A1, 2026-07-03): IVirusScanService, ClamAV'e (clamd raw
   INSTREAM protokolü, TCP) dosyayı gönderir.
   → Infected  → 422 virus_detected, dosya YAYINLANMAZ
   → Unavailable (clamd'e ulaşılamıyor) → 503 scan_unavailable — "muhtemelen temizdir"
     VARSAYILMAZ, fail-closed. Kanıt: proof/faz-a-guvenlik-sertlestirme.md.
3. FileServiceApi, dosyayı KENDİ container'ında geçici bir yerel dosyaya yazar (NFS değil)
4. SHA256 hesaplanır (yerel diskten)
5. FilesPublisherClient.cs → mTLS ile FilesPublisher'a POST /publish?...&zone=public|private çağrısı
   (relativePath + dosya içeriği + zone gönderilir — bkz. 6.6, varsayılan zone=private)
6. FilesPublisher (Files-01, files-writer kullanıcısı):
   a. staging'e yazar
   b. aynı dosya sistemi içinde atomic rename → export/ veya export-public/'e taşır (zone'a göre)
7. DB: files.objects (storage_zone dahil) + files.references INSERT
   (DB fail → Publisher'a DELETE /publish çağrısıyla rollback)
8. Audit: create/success
```

Neden hâlâ staging (artık Publisher içinde)?
- Eksik yükleme (network kesildi) asla export'a karışmaz
- SHA256 tamamlanmış ve diske yazılmış byte'lardan hesaplanır
- `ProtectSystem=strict` + tek `ReadWritePaths=/srv/files` (staging+export aynı üst dizin) —
  ikisi ayrı `ReadWritePaths` girişi olsaydı systemd bunları ayrı bind-mount'lar yapardı,
  `os.rename()` `EXDEV` hatasıyla başarısız olurdu (bu, gerçek test sırasında bulunan bir bug'dı).

### 6.3 Download Akışı — İki Ayrı Yol

Sistemde artık **iki farklı** indirme yolu var, farklı amaçlar için:

**(A) Doğrudan stream (`/api/.../content`, uygulama içi görüntüleme/CV indirme için, cookie auth):**
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
Bu yol, FileServiceApi'nin `:ro` NFS mount'undan okuyarak byte'ı **kendisi** stream eder.

**(B) Ticket + X-Accel-Redirect (`/files/download/{ticket}`, "İndir" butonu için, cookie'siz):**
```
1. Client: POST /api/.../download-ticket (cookie auth) → opak, 256-bit ticket alır
2. Client: GET /files/download/{ticket}  (Gateway'e, cookie'siz)
3. Gateway → mTLS (CN=gateway) ile FileServiceApi'nin ticket-consume endpoint'ini çağırır
4. FileServiceApi: ticket'ı DB'de atomik UPDATE ile doğrular/tüketir (bkz. 6.4 — Lease Modeli)
   → geçerliyse X-Accel-Redirect: /protected-download/{relativePath} header'ı döner
   → FileServiceApi byte'ı KENDİSİ OKUMAZ
5. Gateway (nginx), X-Accel-Redirect'i görünce /protected-download/ (internal-only location)
   üzerinden dosyayı KENDİ read-only bind-mount'undan (host'un mevcut NFS export'u) servis eder
```
Bu yolda FileServiceApi hiç byte görmez — sadece ticket'ı doğrulayıp yönlendirme header'ı döner;
gerçek byte transferi tamamen Gateway'de olur. Detay: `proof/x-accel-redirect-gateway.md`.

SHA256 **hiçbir indirme yolunda yeniden hesaplanmaz** — yükleme anında doğrulanmış ve DB'ye
kaydedilmiştir. ETag = `"sha256:<hash>"` — (A) yolunda geçerli; (B) yolunda nginx kendi
mtime+size tabanlı ETag'ini kullanır (bilinen, kozmetik bir fark — bkz. `proof/x-accel-redirect-gateway.md`).

### 6.4 Ticket Lease Modeli (Süre + Sayı Sınırlı Çoklu Kullanım)

`POST .../download-ticket` ile alınan ticket, S3 presigned URL benzeri bir "lease" modeliyle çalışır:

```
TicketLifetime   = 60 saniye   ← ilk kullanım penceresi (hiç kullanılmadıysa)
LeaseDuration    = 30 saniye   ← ilk kullanımdan sonraki ek kullanım penceresi
MaxUsesPerTicket = 20          ← toplam sert üst sınır

Atomik SQL (tek UPDATE...RETURNING, Postgres row-level locking ile eşzamanlılık güvenli):
  used_at IS NULL AND expires_at > now()                              → ilk kullanım, izin ver
  used_at IS NOT NULL AND now() < used_at + lease_saniye               → lease içinde, izin ver
  use_count >= MaxUsesPerTicket                                       → reddet (ticket_max_uses_reached)
```

Bu, çoklu Range isteği gerektiren büyük dosya/video senaryolarını (tarayıcının doğal parça parça
okuma davranışı) tek bir HTTP isteğiyle sınırlı olmadan destekler. Tam kanıt (6 senaryo, 25
eşzamanlı istek testi dahil): `proof/download-ticket-lease-model.md`.

### 6.5 Gateway Seviyesi Koruma: Rate Limit + Log Maskeleme

`/files/download/{ticket}` için:
- **Rate limit**: IP başına `30r/s` + `burst=50`, aşılırsa `429`.
- **Log maskeleme**: Ticket'ın sadece ilk 8 karakteri access log'a yazılır (`map`+özel `log_format`),
  tam ticket hiçbir log dosyasında görünmez — hem başarılı (X-Accel) hem başarısız (404/429) yanıtlar
  için, nginx'in `limit_req` modülünün kendi diagnostik `error_log`'u dahil.

Tam kanıt: `proof/gateway-rate-limit-ve-ticket-log-maskeleme.md`.

### 6.6 Public/Private Storage Zone (Faz B3, 2026-07-03)

Bazı dosyalar (yalnız `classification=official` olanlar) kimlik doğrulaması OLMADAN erişilebilir
şekilde yayınlanabilir — bu, `files.objects.storage_zone` (`public`/`private`, varsayılan `private`)
kolonuyla kontrol edilir:

```
POST /internal/files  (zone=public form alanı, classification=official ZORUNLU eşleşme)
  → zone=public + classification != official  → 400 zone_classification_mismatch
  → geçerliyse: FilesPublisher'a zone=public parametresiyle yayınlanır (export-public/'e yazılır)
  → yanıt: { ..., zone: "public", publicUrl: "/public/{relativePath}" }

GET /public/{relativePath}  (Gateway, HİÇBİR kimlik doğrulaması yok — ticket/JWT/mTLS gerekmez)
  → nginx, export-public/'in salt-okunur mount'undan DOĞRUDAN servis eder
  → FileServiceApi ve ticket store bu okuma yolunda HİÇ yer almaz (rehberin açık isteği)
```

Private dosyalar `export-public/` dizininde FİZİKSEL OLARAK hiç bulunmadığı için `/public/` yolundan
asla erişilemez (savunma derinliği — path-traversal hatası bile iki ağacı karıştıramaz). Fail-closed
validasyon zinciri (magic-byte, ClamAV — bkz. 6.2) zone'dan bağımsız aynen uygulanır. Tam kanıt:
`proof/b3-public-private-zone.md`.

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
│     ├── download_tickets  opak indirme ticket'ları (sadece hash, lease modeli — bkz. 6.4)
│     └── audit_events     teknik audit (app bazlı)
│
├── yonetim.*         ← YonetimApi'nin sahibi olduğu tablolar
│     ├── personnel        personel listesi (ad, departman, unvan)
│     ├── team_members     yönetici-ekip ilişkisi (RBAC.team için)
│     ├── role_assignments yetki kaynağı (Faz C1, 2026-07-06) — bkz. bölüm 4
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
classification TEXT         -- "internal" | "confidential" | "restricted" | "official"
storage_zone  TEXT          -- "public" | "private" (varsayılan "private") — bkz. bölüm 6.6
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

**Referans-bazlı archive modeli (Faz B1, 2026-07-03):** Archive işlemi artık **dosya** (`fileId`)
seviyesinde değil, **referans** (`referenceId`) seviyesinde yapılır:
```
POST /internal/references/{referenceId}/archive
  1. Sahiplik kontrolü: reference.AppCode == callerAppCode (değilse no-leak 404)
  2. Bu referansı revoked yap
  3. Aynı file_id'ye ait BAŞKA aktif referans var mı kontrol et
     → yoksa: files.objects.status = archived (cascade)
     → varsa: obje active kalır (başka bir app/entity hâlâ bu dosyayı kullanıyor)
```
Bu, bir dosyanın birden fazla uygulama/varlık tarafından referanslanabildiği (`files.references`
şemasının zaten desteklediği ama eskiden kullanılmayan) senaryoda doğru davranışı sağlar — eski
`POST /internal/files/{fileId}/archive` (doğrudan obje arşivleme) tamamen kaldırıldı. Tam kanıt
(cascade davranışı gerçek DB testiyle doğrulandı): `proof/b1-referans-bazli-archive.md`.

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

/internal/                 → 404  (FileServiceApi iç endpoint'leri genel olarak dışarıya açılmaz —
                                    TEK istisna aşağıda: /files/download/ üzerinden mTLS ile)
/files/download/{ticket}   → FileServiceApi'ye mTLS (CN=gateway) ile, ticket tüketir,
                              X-Accel-Redirect ile /protected-download/'a yönlendirir (bkz. 6.3-6.5)
/protected-download/       → internal-only, sadece X-Accel-Redirect ile erişilebilir, dışarıdan
                              doğrudan istek 404 döner
/api/auth/*                → yonetimapi:8080  (BFF login/refresh/logout)
/api/personnel/*           → yonetimapi:8080
/api/vehicles/*            → flotaapi:8080
/ops/*                     → opsapi:8080
/health                    → {"status":"healthy","service":"Gateway-Nginx"}
/*                         → client:80  (React SPA — try_files ile SPA routing)

Hata yanıtları (JSON):
429 too_many_requests     → /files/download/ rate limit aşıldıysa (bkz. 6.5)
502 upstream_unavailable  → servis çöktüyse
504 upstream_timeout      → 120 saniye içinde cevap gelmediyse

Upload/download için:
client_max_body_size    20m  (personnel) / 25m (fleet)
proxy_request_buffering off  → büyük dosyalar tampona alınmaz, stream edilir
proxy_buffering         off  → download da stream edilir
```

nginx, `/api/*`/`/ops/*` gibi normal proxy yollarında JWT'yi okumaz, doğrulamaz, değiştirmez —
tüm kimlik/yetki kararları YonetimApi/FlotaApi/OpsApi ve FileServiceApi'de verilir. **Tek istisna**
`/files/download/{ticket}` — burada nginx, FileServiceApi'yi CN=gateway mTLS sertifikasıyla doğrudan
çağırır (JWT gerekmez, çünkü ticket'ın kendisi zaten RBAC'tan geçmiş, kısa ömürlü, lease modelli bir
yetkidir). Diğer tüm `/internal/*` endpoint'ler (ticket oluşturma dahil) hâlâ hem mTLS hem JWT ister.

---

## 11. Token Yenileme (BFF Refresh)

```
Access token süresi doluyor (5 dk)
      │
      ▼
Client: isAccessTokenFresh(auth, skewSeconds=30) → false
      │
      ▼
POST /api/auth/refresh
  (rt cookie otomatik gider — JS token görmez)
      │
      ▼
YonetimApi BFF → Keycloak refresh grant
Keycloak → yeni access_token + refresh_token
BFF → at + rt cookie'lerini günceller (Set-Cookie)
      │
      ▼
Client ← {user, expiresAt} JSON
auth state bellekte güncellenir
localStorage YOK — token hiçbir zaman JS'e ulaşmaz
      │
      ▼
Devam et (kullanıcı oturumu kapatmaz)

rt cookie süresi dolduysa:
  → POST /api/auth/refresh → 401
  → Client → bffLogout() → login ekranına
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

**⚠️ Önemli ayrım (2026-07-03):** `inline`/`attachment` seçimi **sadece doğrudan stream yolunda**
(`/api/.../content`, bölüm 6.3 (A)) geçerlidir — bu, uygulama içi önizleme amaçlıdır. **Ticket tabanlı
"İndir" akışında** (bölüm 6.3 (B), `/files/download/{ticket}`) disposition her zaman **koşulsuz
`attachment`**'tır, dosya türünden bağımsız — çünkü bu akışın tek amacı indirmedir, önizleme değil.
Bu ayrım, resim dosyalarının ticket ile indirilince tarayıcıda açılıp indirilmemesine neden olan gerçek
bir prod bug'ının düzeltmesiyle netleşti (bkz. `proof/ticket-download-content-disposition-fix.md`).

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
| **nginx (Gateway)** | Yönlendirme, boyut limiti, JSON hata yanıtları, rate limit, ticket X-Accel servis, public zone servisi | Token doğrulama, iş mantığı |
| **YonetimApi** | User JWT doğrulama, RBAC kararı (DB'den, bkz. bölüm 4), domain audit, FileService proxy | Dosya depolama, fiziksel path |
| **FlotaApi** | Aynı (fleet domain için) | — |
| **FileServiceApi** | Dosya katalog/metadata yönetimi, virüs tarama tetikleme, stream, teknik audit | Kullanıcı kimliği, RBAC kararı, fiziksel yazma (artık FilesPublisher'da) |
| **ClamAV** | Fail-closed virüs taraması (bkz. 6.2) | Dosya katalog/metadata |
| **FilesPublisher (Files-01)** | Tek fiziksel yazma noktası (mTLS korumalı) | Yetki/politika kararı |
| **PostgreSQL** | Veri kalıcılığı, yetki kaynağı (`yonetim.role_assignments`) | — |
| **Files-01 (NFS)** | Binary depolama (private + public) | Metadata, erişim kontrolü |
| **Keycloak** | Kimlik doğrulama (token imzalama, kullanıcı yönetimi) — yetki kararı DEĞİL | Uygulama iş mantığı, "ne yapabilir" kararı |

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
| 400 `zone_classification_mismatch` | FileServiceApi | `zone=public` ama `classification != official` (bkz. 6.6) |
| 422 `virus_detected` | FileServiceApi | ClamAV taraması dosyayı reddetti (bkz. 6.2) |
| 500 | Herhangi | Yakalanmamış hata |
| 503 | FileServiceApi | Disk/NFS erişilemiyor |
| 503 `scan_unavailable` | FileServiceApi | ClamAV'e ulaşılamadı — fail-closed, dosya yayınlanmadı |
| 502 | nginx | Upstream servis çökmüş |
| 504 | nginx | Upstream 120 saniyede cevap vermedi |
| 429 | nginx | `/files/download/` rate limit aşıldı (IP başına 30r/s, burst 50) |
| 304 | FileServiceApi / nginx | ETag eşleşti, veri değişmemiş (cache) |
| 206 | FileServiceApi / nginx | Range isteği, kısmi içerik |
