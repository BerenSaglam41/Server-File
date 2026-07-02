# Proje Durumu ve Devam Planı

Bu dosya, File-Service API / YonetimAPI projesinin şu ana kadar nereye geldiğini ve
sırada ne olduğunu anlatır. Yeni bir Claude oturumuna bu dosyayı ve aşağıda listelenen
5 .md dosyasını verirsen, tam olarak nereden devam edeceğini bilir.

## Sistem mimarisini anlamak için

Tüm katmanları (auth, storage, RBAC, audit, dosya akışı) baştan sona açıklayan referans doküman:
→ **`MIMARI.md`**

Sıfırdan kurulum (Mac geliştirme, Linux üretim, Files-01 NFS):
→ **`KURULUM.md`**

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

- **files-01** (192.168.64.3): sadece binary depolama — NFS üzerinden `/srv/files` export eder.
- **DB-01 / `files.*` şeması**: merkezi dosya kataloğu. 4 tablo: `objects`, `references`, `app_policies`, `audit_events`.
- **File-Service API**: dosya kataloğunun tek sahibi. Policy kontrolü, audit, stream — hepsi burada.
- **YonetimAPI**: ilk consumer uygulama. Data-scope kontrolü yapar, FileService'i kendi servis kimliğiyle çağırır.

**Auth modeli:** OAuth2 client credentials (Keycloak `platform` realm). YonetimApi → FileServiceApi arası service JWT, Client → YonetimApi arası user JWT. Detay: "Auth mimarisi" bölümü.

## Güncel sınıflandırma — Production Candidate / Release Candidate

2026-06-30 itibarıyla proje artık "çalışıyor mu?" aşamasından çıkıp **Production Candidate
(Release Candidate)** seviyesine geldi. Mimari omurga, güvenlik kontrolleri, yetkilendirme,
dosya erişim modeli, audit, HTTPS gateway, Files-01 ayrımı ve tek giriş noktası uygulanmış
durumda.

Bu sınıflandırma "hiç eksik yok" anlamına gelmez. Kalan işler artık uygulamanın temel
çalışmasını değil, production ortamındaki operasyonel dayanıklılığı ve bakım kolaylığını
artıran hardening adımlarıdır:

- Let's Encrypt + gerçek domain ile gateway'in public 443'e alınması.
- Files-01 firewall/NFS export daraltması: TCP/2049 yalnız API/FileService sunucusuna açık olmalı.
- Demo şifreleri, Keycloak client secret'ları ve sertifika/CA rotasyon prosedürünün production'a göre yenilenmesi.
- Backup/restore'un tek seferlik testten düzenli otomasyona ve restore tatbikatına çevrilmesi.
- Katı production istenirse NFS `ro export + publisher/ops` modeline geçiş.
- Yük altında davranış, gözlemlenebilirlik, timeout/retry ve operasyon runbook'larının olgunlaştırılması.

Bu nedenle mevcut durum: **RC olarak değerlendirilebilir; production'a çıkmadan önce
operasyonel hardening checklist'i kapatılmalıdır.**

## Production hardening karar matrisi

Bu beş başlık artık "uygulama çalışıyor mu?" sorusundan çok production operasyonu sorusudur.
Mevcut karar: **minimum production profiliyle canlıya hazırlan, strict NFS publisher modelini V2
hardening olarak tut.**

| Başlık | Mevcut durum | Karar | Kapanış kanıtı |
|---|---|---|---|
| Firewall + NFS allowlist | **Tamamlandı/doğrulandı (2026-07-01)** | **Kapandı** | Files-01 `/srv/files` yalnız `192.168.64.5` için export; TCP/2049 yalnız API/FileService hostundan erişiliyor; Mac timeout aldı; FileService container staging→export probe geçti |
| Secret rotasyonu | Demo secret'lar compose/realm içinde duruyor | **Canlıya çıkmadan önce zorunlu** | Prod deploy gerçek secret'ları env/secret store'dan alıyor; demo kullanıcı/parola prod realm'de yok |
| Backup/restore otomasyonu | **Tamamlandı/doğrulandı (2026-07-01)** | **Kapandı** | `platform-backup.timer` günde 1 backup alır (02:00 UTC + randomized delay); başarılı backup sonrası restore doğrulaması çalışır; `platform-restore-test.timer` haftalık Pazar 03:00 UTC aktif; Ops UI “Son yedek” değerini gerçek en yeni backup klasöründen gösterir; `tools/restore-live.sh` uçtan uca test edildi ama yalnız Break Glass / Manual Recovery prosedürü |
| Let's Encrypt + gerçek domain | Self-signed HTTPS çalışıyor; kurulum notu var | **Public prod için zorunlu** | `https://domain/health` portsuz 443'ten geçiyor; sertifika zinciri tarayıcı/curl tarafından güvenilir |
| NFS strict ro/publisher modeli | 5 MD'deki en katı hedef; mevcut upload akışı rw NFS bekliyor | **V2 hardening / ops olgunlaştırma** | Mevcut minimum-prod modelde FileService runtime NFS'e yalnız allowlist üzerinden rw yazar; strict modelde publisher/ops süreci ayrıca tasarlanacak |
| Disk kapasitesi izleme | **Tamamlandı (2026-07-01)** | **Kapandı** | `platform-disk-check.timer` saatlik çalışır; WARN=%80, CRIT=%90; `.disk-status` yazar; `setup-server.sh` raporlar; Docker build cache temizliği ile API sunucusu %77→%57'ye düşürüldü |
| OpsApi V1 — Read-Only | **Tamamlandı/doğrulandı (2026-07-01)** | **Kapandı** | Ayrı .NET servisi; rol hiyerarşisi: ops.read < ops.execute < ops.admin; auth matrix: no-token→401, hr001(ops rolü yok)→**404** (indistinguishability), opsadmin→200, opsuser01→200 ✅; port dışarı publish edilmemiş; Docker socket mount yok; servis durumu host services snapshot dosyasından okunur (`platform-services-status.timer`, 5 dk); `ops.audit_events` PostgreSQL tablosu; X-Correlation-Id response header; `/ops/me`, `/ops/version`, `/ops/dashboard` hazır; logout sonrası `/ops/me`→401 doğrulandı |
| OpsApi V2 — Write Ops | Tasarlandı, henüz yok | **Observability Faz 1 sonrası** | POST /ops/backups/trigger (ops.execute), POST /ops/restore (ops.admin zorunlu); host systemctl erişim yöntemi belirlenmeli |
| Observability | Log + audit + health + Ops Dashboard V1 var; metrics/tracing/Grafana yok | **Prod hardening ile paralel Faz 1 başlatılabilir** | Request id/correlation standardı, structured logs, `/metrics`, Prometheus ve Grafana sıradaki faz |

Önerilen sıra:

1. ~~Backup/restore otomasyonunu systemd timer'a bağlamak.~~ **Tamamlandı (2026-07-01)**
2. Secret rotasyonu ve prod env ayrımı.
3. Gerçek domain + Let's Encrypt + public 443.
4. Resilience test V1: Gateway/OpsApi/FileService/Keycloak/PostgreSQL restart senaryoları.
5. Observability Faz 1: request id + structured logs + correlation standardı.
6. Metrics + Prometheus + Grafana.
7. Yük/izleme sonuçlarına göre strict NFS `ro export + publisher` tasarımı.

Not: strict NFS `ro export + publisher` modeli mimari olarak daha katıdır ama mevcut iki sunuculu çalışan model için ilk canlıya çıkışın
ön şartı değildir. İlk production adımı minimum profilde `rw` NFS'yi sadece API/FileService sunucusuna
daraltmak olmalıdır.

Observability detayı: `runbooks/observability-plan.md`. İlk teknik adım olarak doğrudan Grafana'dan
başlamak yerine request id/correlation/structured log standardı uygulanmalıdır; Prometheus, Grafana ve
distributed tracing bunun üstüne kurulmalıdır.

### 2026-07-01 Files-01 NFS allowlist doğrulaması

Minimum production NFS modeli canlı Files-01 üzerinde uygulandı. Mevcut upload/download/archive akışı
bozulmasın diye strict `ro export + publisher` modeline geçilmedi; `rw` yalnız API/FileService sunucusuna
daraltıldı.

Kanıtlar:

```text
Files-01 /etc/exports:
/srv/files 192.168.64.5(rw,sync,no_subtree_check,all_squash,anonuid=999,anongid=1003)

Files-01 exportfs -v:
/srv/files 192.168.64.5(sync,wdelay,hide,no_subtree_check,anonuid=999,anongid=1003,sec=sys,rw,secure,root_squash,all_squash)

Files-01 writer identity:
uid=999(files-writer) gid=1003(files-publishers)

Files-01 ufw:
Default: deny (incoming)
2049/tcp ALLOW IN 192.168.64.5

API sunucusu:
nc -vz 192.168.64.3 2049 -> succeeded
mount -> 192.168.64.3:/srv/files on /mnt/platform-files type nfs4 ... clientaddr=192.168.64.5
ls /mnt/platform-files/export/.probe -> OK
bash setup-server.sh -> [OK] Fileservice container staging -> export yazma/taşıma testi geçti

Mac / izinsiz makine:
nc -vz -G 3 192.168.64.3 2049 -> Operation timed out
```

Sonuç: Files-01 artık ağdaki herkese açık NFS storage değildir; yalnız API/FileService sunucusu mount
edebilir. Client/Mac/başka VM dosya katmanını bypass edemez.

### 2026-07-01 upload 503 kök nedeni ve düzeltme

NFS allowlist sonrası upload sırasında `POST /api/personnel/{id}/cv -> 503` görüldü. Loglarda YonetimApi'nin
FileService'e ulaştığı, FileService'in `storage_unavailable` döndüğü görüldü. Host üzerinde
`touch /mnt/platform-files/staging/write-test` başarılı olsa da bu yeterli değil; FileService container'ı
root olarak NFS'e yazarken `root_squash` nedeniyle Files-01 tarafında anonymous kullanıcıya map ediliyor.
Bu durumda staging/export yazma izni yoksa upload 503 verir.

Düzeltme: `tools/configure-files01-nfs.sh` production minimum modeli `root_squash` yerine
`all_squash + files-writer` modeline çevrildi. Script `files-writer:files-publishers` kimliğini oluşturur,
storage dizin sahipliğini ayarlar ve export'u şu modele yazar:

```exports
/srv/files <API_SERVER_IP>(rw,sync,no_subtree_check,all_squash,anonuid=<FILES_WRITER_UID>,anongid=<FILES_WRITER_GID>)
```

Canlı kök neden:

```text
/srv/files/staging/personnel -> UNKNOWN:dialout 755 501:20
FileService logu -> Access to the path '/app/storage/staging/personnel/<shard>' is denied.
```

Yani `staging/personnel` eski sahibinde kaldığı ve `root_squash` kullanıldığı için container yeni shard
dizini açamadı. Güncel script `chown -R files-writer:files-publishers` ve `chmod u+rwX,g+rwX,o-rwx`
uygular.

`setup-server.sh` içine gerçek FileService upload probe'u eklendi: `staging/personnel/...` yazma,
SHA256 okuma, `export/personnel/...` altına `mv`, export dosyasını doğrulama ve temizleme. Artık API
hosttan yazma değil, container içinden gerçek staging→export akışı setup sırasında doğrulanır.

### 2026-07-01 canlı backup/restore doğrulaması

API/FileService sunucusunda gerçek storage ve PostgreSQL dump ile backup alındı, ardından canlı `export/`
alanına dokunmadan restore testi çalıştırıldı.

Kanıtlar:

```text
Backup:
STORAGE_ROOT=/mnt/platform-files BACKUP_ROOT=/backup/platform-files ./tools/backup-files01.sh
[OK] Backup completed: /backup/platform-files/20260701T071527Z

Backup içeriği:
export/
manifests/
export.sha256
platformdb.dump
backup-info.txt

Restore:
STORAGE_ROOT=/mnt/platform-files BACKUP_ROOT=/backup/platform-files ./tools/restore-test.sh
[OK] Restore test completed: /mnt/platform-files/restore-tests/20260701T071530Z

Hash doğrulama:
fleet/personnel altındaki tüm dosyalar ve .probe için OK döndü.
```

Sonuç: Export dosyaları, manifestler ve PostgreSQL catalog dump alınabiliyor; restore testi hash
doğrulamasını geçiyor.

### 2026-07-01 canlı restore (restore-live.sh) uçtan uca doğrulaması

`tools/restore-live.sh` ile belirtilen backup noktasına canlı sistemi geri sarma end-to-end test edildi.

Senaryo:
- File A (`b58bdb32-...`) → P001'e `attachment` olarak yüklendi
- Backup alındı: `/backup/platform-files/20260701T085837Z` (47 dosya, File A dahil)
- File B (`8b936232-...`) → backup'tan sonra yüklendi
- `FORCE=1 bash tools/restore-live.sh /backup/platform-files/20260701T085837Z` çalıştırıldı

Restore adımları ve sonuçları:
```text
[OK] fileservice, yonetimapi, flotaapi durduruldu
[OK] rsync -rl --delete --no-o --no-g → storage/export restore tamamlandı
[OK] PostgreSQL restore tamamlandı (pg_restore --clean --if-exists --no-privileges --no-owner)
[OK] Gateway yeniden başlatıldı (nginx DNS cache temizlendi)
[OK] Gateway sağlıklı
```

Doğrulama sonuçları:
```text
File A (backup'ta vardı)  : HTTP 200 — beklenen: 200 ✅
File B (sonra eklendi)    : HTTP 403 — beklenen: erişilemez ✅
  (403: API güvenlik paterni — dosya files.references'ta sıfır kayıt, 404 yerine 403 döner)

files.objects: yalnız File A var (status=active)
files.references: File B için 0 kayıt
Fiziksel storage: File B rsync --delete ile silindi
```

Tespit edilen ve düzeltilen sorun:
- `docker compose stop/start` sonrası nginx DNS cache stale kalıyor → `/api/auth/login` ve `/api/personnel/**` 404 dönüyor
- Fix: restore-live.sh sonuna `docker compose restart gateway` eklendi
- Commit: `fix(restore-live): gateway restart after service stop/start`

Sonuç: `tools/restore-live.sh` production-ready. NFS all_squash ortamında `rsync --no-o --no-g` + `pg_restore --clean --if-exists` + gateway restart zinciri doğrulandı.

### 2026-07-01 deploy smoke + safe test doğrulaması

`setup-server.sh`, `tools/server-smoke-test.sh` ve `tools/server-safe-test-suite.sh` FileAPI sunucusunda
uçtan uca çalıştırıldı.

Smoke test sonuçları:

```text
Gateway health                         -> 200
HR login                               -> 200
Personnel list                         -> 200 (29 kayıt)
P001 files                             -> 200 (3 dosya)
Download                               -> 200
p001 -> P002 erişimi                   -> 403
Ops login                              -> 200
/ops/me no-token                       -> 401
HR kullanıcısı /ops/dashboard          -> 404
/ops/me, /ops/health, /ops/services    -> 200
/ops/disk, /ops/backups, /ops/version  -> 200
/ops/dashboard                         -> 200
Ops logout sonrası /ops/me             -> 401
```

Safe test sonuçları:

```text
/ops/dashboard JSON bütünlüğü          -> OK
opsuser01 read-only                    -> OK (/ops/dashboard 200, execute/admin false)
BFF refresh endpoint                   -> OK
Ops denied audit kayıtları             -> OK (no_token, ops_role_missing)
X-Correlation-Id response header       -> OK
En büyük P001 dosyası download         -> 3,816,264 bytes; metadata ile uyumlu
20 eşzamanlı HR login                  -> OK
Son 10 dk ops audit kayıt sayısı       -> 42
```

Sonuç: Temel auth, yetki izolasyonu, ops endpoint koruması, logout, correlation id, audit yazımı,
download bütünlüğü ve eşzamanlı login davranışı doğrulandı. Kalan testler daha kontrollü resilience
senaryolarıdır: servis restart, NFS kopma, disk %90 simülasyonu, backup/restore failure.

Ek test otomasyonu hazırlandı:

```text
tools/server-safe-test-suite.sh
  - opsuser01 read-only kontrolü: /ops/dashboard 200, execute/admin false
  - BFF refresh endpoint kontrolü
  - ops.audit_events denied kayıtları: no_token ve ops_role_missing

tools/server-alert-simulation-test.sh
  - WARN_PCT düşük verilerek disk warning alert doğrulaması
  - .backup-status geçici failed yapılarak backup critical alert doğrulaması
  - Çıkışta status dosyalarını geri yükler

tools/server-resilience-test.sh
  - opsapi, gateway, keycloak restart sonrası /health, login, personnel list ve /ops/dashboard toparlanma testi
```

Not: `restore-live.sh` hâlâ yalnız Break Glass / Manual Recovery aracıdır. UI/OpsApi üzerinden tetiklenmez;
pre-restore backup + çift onay tamamlanmadan write ops kapsamına alınmayacak.

### 2026-07-01 ops bilgi sızıntısı / hardening değerlendirmesi

Ops ekranındaki ayrıntılar yalnız `ops.read` ve üstü rollerle döner; token yoksa 401, authenticated ama ops
rolü yoksa 404 ile gizlenir. Docker socket OpsApi container'ına mount edilmez; servis/CPU/RAM bilgileri host
tarafında üretilen snapshot dosyasından salt-okunur okunur.

Ek hardening:
- Gateway `server_tokens off` ve temel browser güvenlik header'ları ile çalışır.
- Public `/health` minimum bilgi döndürür.
- Ops health reason alanları exception/stack/connection string yerine kontrollü kodlar döndürür
  (`health_unreachable`, `health_timeout`, `db_unreachable`).
- HSTS gerçek domain + güvenilir sertifika sonrası açılacak; self-signed/IP test ortamında bilerek kapalı.
- `tools/server-security-headers-test.sh` Gateway header/CSP smoke testi için eklendi.
- Ops Console refresh hatasında mevcut veriyi silmez; 401 durumunda BFF refresh dener, kısa kesintide bir kez retry eder
  ve eski veriyi koruyarak uyarı gösterir.
- Backup snapshot ile services snapshot ayrıldı: backup günde 1 alınır, services snapshot 5 dakikada bir container
  CPU/RAM/restart/uptime bilgisini yeniler. Ops UI “Ölçüm” etiketi services snapshot zamanını gösterir.
- Ops UI container `Uptime` değeri son `git pull` zamanı değil Docker container'ın son start zamanıdır;
  compose değişmeyen container'ları recreate etmeyebilir.
- Ops UI “Son yedek” değeri `.backup-status` yerine en yeni backup klasöründen hesaplanır; `.backup-status`
  son backup job sonucunu izlemek için kullanılır.

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
  ├── docker-compose.yml               <- servisler: postgres, keycloak, fileservice, yonetimapi, flotaapi, opsapi, client, gateway(nginx)
  ├── docker-compose.override.yml      <- Dev-only port mapping (5205, 5076, 5077)
  ├── nginx/
  │     └── nginx.conf                 <- Gateway routing: /api/personnel→yonetimapi, /api/vehicles→flotaapi
  ├── runbooks/
  │     ├── files01-nfs-setup.md               <- NFS kurulum runbook (üretim adımları)
  │     ├── production-hardening.md            <- Production hardening, NFS allowlist, backup/restore
  │     └── observability-plan.md              <- Request id, metrics, tracing, dashboard planı
  ├── tools/
  │     ├── migrate-legacy-files.py    <- Legacy dosya migration aracı
  │     ├── backup-files01.sh          <- Files-01 export + PostgreSQL dump backup
  │     ├── restore-test.sh            <- Backup hash doğrulama / restore probe
  │     ├── restore-live.sh            <- Canlı sistemi belirtilen backup noktasına geri sarar
  │     ├── install-backup-timers.sh   <- Systemd backup/restore/disk/services timer'larını kurar (root)
  │     ├── services-status.sh         <- Docker socket'i OpsApi'ye vermeden servis snapshot yazar
  │     └── server-smoke-test.sh       <- Deploy sonrası gateway/login/list/download/403/ops/audit smoke test
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
  └── test-storage/                    <- (eski — artık kullanılmıyor, NFS'e geçildi)
```

### Başlatma

**API sunucusunda (192.168.64.5):**
```bash
git pull
bash setup-server.sh
bash tools/server-smoke-test.sh
```
Tarayıcı: `http://192.168.64.5:5090/`

**Mac'te:**
```bash
git pull && bash setup-mac.sh
```
Tarayıcı: `http://localhost:5090/`

Detaylı kurulum adımları: `KURULUM.md`

### Test için token alma

```bash
# BFF üzerinden login (cookie tabanlı — tarayıcıda oturum)
# http://192.168.64.5:5090/ → hr001 / Demo1234!

# Curl ile direkt test (Linux sunucudan):
curl -s -X POST http://192.168.64.5:5090/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"hr001","password":"Demo1234!"}' \
  -c /tmp/ck.txt
curl http://192.168.64.5:5090/api/personnel?search= -b /tmp/ck.txt

# Gateway health check
curl http://192.168.64.5:5090/health
```

## FileServiceApi — 7 endpoint (tümü test edildi)

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
→ If-None-Match == ETag ise **304 (audit yazmadan)** → disk'te dosya var mı (503) → header'ları set et → stream et.

Özellikler:
- SHA256 tabanlı ETag: `"sha256:<hash>"`
- If-None-Match → 304 Not Modified (audit log yazılmaz — cache hit)
- Content-Disposition: resimler `inline`, dökümanlar `attachment; filename="<originalFileName>"`
- Path traversal koruması: `Path.GetFullPath()` ile root boundary kontrolü
- Range / 206 Partial Content: `enableRangeProcessing: true`
- **SHA256 re-hesaplama kaldırıldı** — hash upload anında staging'den hesaplanıp DB'ye kaydedilir; indirmede disk iki kez okunmaz

Test: 200, 206, 304, 401, 403, 404, 503.

### `GET /internal/files/ownership` ✅
Lightweight ownership check. Query: `fileId`, `entityId`. Tek DB sorgusu: `References.AnyAsync`.
YonetimApi'nin `FileBelongsToPersonnelAsync` helper'ı tarafından kullanılır — tüm dosya listesi yerine tek satır sorgu.

Test: 200 `{owned:true}`, 200 `{owned:false}`, 401, 403.

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

Başarılı liste dönüşünde `files.audit_events`'e `action=read, result=success` yazılır (2026-06-30 eklendi).

Test: 200 (liste), 401, 403.

### Kritik not — AuditService fileId kuralı

`fileId` parametresi, sadece nesnenin DB'de GERÇEKTEN var olduğu kesinleştikten **sonra** geçilmeli.
Öncesinde `null` geçilmeli. Sebep: `audit_events.file_id` üzerinde FK var (`files.objects`), var olmayan
UUID geçmek `DbUpdateException` fırlatır. Bu hata bir kez yaşandı, düzeltildi.

### Audit uyumluluk durumu (2026-06-30 kontrol edildi)

MD gereksinimleri (`file-catalog-model.md` + `file-service-api-contract.md`) ile karşılaştırıldı:

| Kontrol | Durum |
|---|---|
| Tüm `action` değerleri CHECK constraint'e uyuyor (`create/read/archive/delete_attempt`) | ✅ |
| Tüm `result` değerleri CHECK constraint'e uyuyor (`success/denied/not_found/error`) | ✅ |
| `resolve` → her sonuçta audit yazıyor | ✅ |
| `metadata` → her sonuçta audit yazıyor | ✅ |
| `content` → her sonuçta audit yazıyor (304 hariç) | ✅ |
| `create` → her sonuçta audit yazıyor | ✅ |
| `list` → başarılı sonuçta audit yazıyor | ✅ (düzeltildi) |
| `archive` → her sonuçta audit yazıyor | ✅ |
| `ownership` → iç endpoint, audit gereksiz | ✅ (bilinçli) |
| `actor_ip` / `user_agent` sütunları — DB şemasında var | ✅ doldurulur (2026-06-30 tamamlandı; IP zinciri: nginx → YonetimApi → FileServiceApi → AuditService) |

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

files-01 (192.168.64.3), NFS sunucusu olarak yapılandırıldı. Production minimum profilde
yalnız API/FileService sunucusu files-01'e bağlanır; Mac/başka makineler NFS mount edemez.
UTM/test profilinde Mac mount kolaylığı bilinçli olarak açılabilir.

**Topoloji:**
- **files-01** (192.168.64.3): NFS sunucusu — `/srv/files` export eder
- **API sunucusu** (192.168.64.5): `/mnt/platform-files` → NFS → `/srv/files`
- Container içi: `/app/storage` → host mount noktası → `/srv/files`
- **Mac / browser**: production minimumda NFS'e bağlanmaz; yalnız Gateway (`https://192.168.64.5:5090`) üzerinden erişir

**Production export modeli:**

```exports
/srv/files 192.168.64.5(rw,sync,no_subtree_check,all_squash,anonuid=<files-writer>,anongid=<files-publishers>)
```

**Bilinen kısıt:**
- Mac NFS mount yalnız `NFS_MODE=test` profilinde geçerlidir. Production minimumda Mac mount denemesi
  `access denied` veya timeout ile başarısız olmalıdır.

Detay: `runbooks/files01-nfs-setup.md` ve `runbooks/production-hardening.md`

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

## Gateway — Nginx (TAMAMLANDI ✅)

Gateway, `nginx:alpine` imaj tabanlı build olarak çalışır (`nginx/Dockerfile`). `nginx.conf` container içine kopyalanır.

- Port `5090` — client entry point
- `location /api/personnel` → `yonetimapi:8080`
- `location /api/vehicles` → `flotaapi:8080`
- `location = /health` → `{"status":"healthy","service":"Gateway-Nginx"}`
- `location /internal/` → 404 (FileServiceApi iç endpoint'leri engellenir)
- `location /` → React SPA (client container nginx:80 proxy)
- `proxy_request_buffering off` + `proxy_buffering off` — dosya upload/download stream için
- `client_max_body_size 20m/25m` — app policy limitine uygun
- `proxy_http_version 1.1` — chunked transfer ve Range için

`Gateway/` .NET projesi kaldırıldı.

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

## Hash Verification ve İndirme Optimizasyonları (GÜNCELLENDİ)

SHA256 hash yükleme anında staging dosyasından hesaplanır ve `objects.sha256` sütununa kaydedilir.
İndirme sırasında hash **yeniden hesaplanmaz** — disk iki kez okunmaz.

**Kaldırılan:** `GetContentAsync` içindeki per-download SHA256 re-hash bloğu.
- Neden: Hash upload akışında staging→export zincirinde zaten doğrulanıyor; indirmede tekrar okumak dosya başına disk yükünü iki katına çıkarıyor.
- Güvence: `File.Exists()` kontrolü ve `Results.Stream()` IOException koruması korunuyor.
- ETag (`"sha256:<hash>"`) hâlâ yanıtta gönderiliyor — client tarafı bütünlük doğrulaması için kullanılabilir.

**Diğer optimizasyonlar aynı commit'te:**
- 304 yanıtlarında audit log yazılmıyor (cache hit = gerçek veri erişimi yok)
- `FileBelongsToPersonnelAsync` tüm dosya listesi yerine `GET /internal/files/ownership` endpoint'ini kullanıyor (tek satır DB sorgusu)

Test sonuçları (BIN-1/2/3):
- Normal indirme: ✅ 200
- Binary missing (File.Exists false): ✅ 503
- Hash mismatch artık test edilmiyor (per-download check kaldırıldı)

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

NFS entegrasyonu tamamlandı — bkz. "Files-01 NFS Entegrasyonu" bölümü,
`runbooks/files01-nfs-setup.md` ve `runbooks/production-hardening.md`.

Test: H1 (normal → 200) ✅, H2 (storage down → 503) ✅, H3 (restore → 200) ✅

## Staging → Export Akışı (TAMAMLANDI ✅)

`file-service-api-contract.md` adım 5 — *"Binary Files-01 staging/export akışıyla yazılır"* tam olarak uygulandı.

**Upload akışı (`FileEndpoints.cs → CreateFileAsync`):**
1. Binary `StagingPath`'e yazılır (`/app/storage/staging` → files-01 `/srv/files/staging`).
2. SHA256, staging dosyasından hesaplanır (disk write bütünlüğü de doğrulanır).
3. `File.Move` ile staging → export atomic rename (aynı FS → rename).
4. DB kayıtları oluşur (`files.objects` + `files.references`).
5. DB insert başarısız olursa export dosyası silinir (rollback).

**Config anahtarları:**

| Anahtar | Dev (UTM NFS) değeri | Production hedefi |
|---|---|---|
| `FileStorage:ReadPath` | `/app/storage/export` | `/app/storage/export` |
| `FileStorage:StagingPath` | `/app/storage/staging` | `/app/storage/staging` |
| `FileStorage:ExportPath` | `/app/storage/export` | `/app/storage/export` |

Production minimumda container `/app/storage` → API sunucusunda `/mnt/platform-files` → NFS → files-01 `/srv/files`.
UTM/test profilinde Mac `/Volumes/platform-files` ile aynı export'u mount edebilir.

**Dizin yapısı** (`files01-nfs-model.md` ile birebir uyumlu):
```
/srv/files/
  export/            ← NFS export / ReadPath
  staging/           ← Upload yazma alanı — üretimde NFS dışı, Files-01 yerel
  manifests/         ← Migration manifestleri (PII yok)
  restore-tests/     ← Restore test çıktıları (PII yazılmaz)
```

**Staging notu:** Mevcut UTM/test kurulumunda `/srv/files` tamamı NFS üzerinden mount ediliyor (`192.168.64.3:/srv/files`), dolayısıyla `staging/` de NFS üzerinden erişilebilir. Production minimum profilde bu model yalnız API sunucusu IP allowlist + firewall ile daraltılır. Katı production profilinde ise FileService runtime export'u read-only görür; staging/publish işi ayrı kontrollü publisher/ops sürecine taşınır. Detay: `runbooks/production-hardening.md`.

## ReadPath / StagingPath / ExportPath Ayrımı (TAMAMLANDI ✅)

MD'lerin NFS read-only sınırına ve staging akışına uygun olarak `FileStorage:RootPath` üçe bölündü:

| Anahtar | Kullanım | Production değeri |
|---|---|---|
| `FileStorage:ReadPath` | Dosya okuma + health check probe | Minimum prod: `/app/storage/export`; katı prod: NFS ro mount |
| `FileStorage:StagingPath` | Upload ilk yazma | Minimum prod: `/app/storage/staging`; katı prod: publisher/ops süreci |
| `FileStorage:ExportPath` | Atomic rename hedefi — kalıcı alan | Minimum prod: `/app/storage/export`; katı prod: publisher/ops süreci |

`FileEndpoints.cs`:
- `GetContentAsync` → `ReadPath`
- `CreateFileAsync` → staging yazma + SHA256 → atomic `File.Move` → `ExportPath`

`Program.cs` health check → `ReadPath`

Migration tool (`tools/migrate-legacy-files.py`):
- `--export-path` ile doğrudan export'a yazar (migration tool staging adımını atlar; Files-01 üzerinde doğrudan çalışır)

## NFS Runbook (TAMAMLANDI ✅)

`runbooks/files01-nfs-setup.md` ve `runbooks/production-hardening.md` staging akışını iki seviyede anlatır. İçerik:
- Files-01 tam dizin yapısı: `export/`, `staging/`, `manifests/`, `restore-tests/`
- UTM/test profili: `/srv/files *(rw,...)`
- Production minimum profili: `/srv/files <API_SERVER_IP>(rw,...)` + firewall TCP/2049 only API host
- Katı production profili: runtime export read-only, staging/publish ayrı controlled process
- File-Service runtime mount (`/mnt/platform-files`, `/etc/fstab`)
- Config: `ReadPath` / `StagingPath` / `ExportPath` production değerleri
- Doğrulama kapıları: NFS port, mount, probe okuma, yazma reddi, API health check, NFS down senaryosu, backup restore
- Sorun giderme tablosu

NFS'e geçişte container config compose env ile verilir. `appsettings.json` local fallback kabul edilir.

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
| Dev | `docker compose up` (override otomatik yüklenir) | 5090, **8080**, 5205, 5076, 5077 (test kolaylığı) |
| Production | `docker compose -f docker-compose.yml up` | **Yalnız 5090 (Gateway)** |

**DİKKAT:** `docker-compose.override.yml` yanlışlıkla production'da kullanılmamalı. Production deploy'da her zaman `-f docker-compose.yml` ile açıkça belirt.

**Keycloak 8080 kararı (2026-06-30 güncellendi):**

Keycloak port `8080:8080` `docker-compose.override.yml`'e taşındı. Production'da Keycloak dışa açılmaz. BFF pattern sayesinde:
- Token alımı `/api/auth/login` → YonetimApi BFF üzerinden yapılır
- JWKS/MetadataAddress servislerin içinden `keycloak:8080` Docker ağı üzerinden erişilir
- 8080 dış erişime kapalı olsa da sistem tamamen çalışır

Dev'de `docker compose up` otomatik olarak `override.yml`'i de yükler, Keycloak 8080'den erişilebilir (admin arayüzü için gerekli).

**Doğrulanan kontroller:**

| Test | Sonuç |
|---|---|
| Production config: yalnız 5090 published | ✅ |
| Gateway `/internal/files/**` → 404 | ✅ |
| Gateway `/api/personnel/**` → YonetimApi | ✅ |
| Gateway `/api/vehicles/**` → FlotaApi | ✅ |
| Gateway `/api/fleet/**` (tanımsız route) → 404 | ✅ |
| Host'tan 5205/5076/5077 → bağlantı reddedildi (production modunda) | ✅ |
| yonetimapi container → `fileservice:8080` DNS erişimi | ✅ (uygulama logu + uçtan uca 200) |
| flotaapi container → `fileservice:8080` DNS erişimi | ✅ |

## Docker Konteynerizasyonu (TAMAMLANDI ✅)

Tüm sistem docker compose ile çalışıyor: `postgres`, `keycloak`, `fileservice`, `yonetimapi`, `flotaapi`, `gateway`.

**Dockerfile'lar:** `FileServiceApi/Dockerfile`, `YonetimApi/Dockerfile`, `FlotaApi/Dockerfile` — .NET 10, multi-stage build. Gateway artık `nginx:alpine` imajını kullanır; Dockerfile yoktur.

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
- Download tarafında `GetContentAsync` per-download SHA256 re-hash kaldırıldı (2026-06-30). Hash sadece upload anında hesaplanıyor.
- Docker dev ortamında staging ve export aynı bind volume altında çalışıyor. Gerçek Files-01 modelinde staging'in FileService runtime host'unda yerel, export'un Files-01 tarafında olması daha hızlı ve stale NFS riskini azaltır.

**Hızlandırma için güvenli V2 adayları:**

1. YonetimApi upload proxy'sini streaming multipart forward'a çevirmek.
2. Upload sırasında SHA256'i dosya yazılırken hesaplamak; staging dosyasını ikinci kez okumamak.
3. ~~Download'da her istekte tam hash yerine ETag/DB hash'e güvenmek~~ — **TAMAMLANDI (2026-06-30)**
4. Client upload progress bar ve timeout/error mesajlarını iyileştirmek; büyük dosyada "site çöktü" hissini azaltmak.
5. Dev compose'ta staging'i NFS/bind export'tan ayırmak; staging için container-local veya ayrı local volume kullanmak.

### Kapsam Dışı

- Production frontend hosting imajı henüz eklenmedi; client şu an Vite dev/build çıktısı olarak hazır (gateway nginx:alpine ayrı servis).

## Content-Disposition ve İndirme İsmi Düzeltmeleri (2026-06-30)

### Bug 1 — Türkçe dosya adı → 500

**Hata:** `GET /api/personnel/{id}/files/{fileId}/content` → 500 Internal Server Error.

**Kök neden:** `GetContentAsync` içinde `Content-Disposition` header'ına `originalFileName` doğrudan yazılıyordu. Türkçe karakter içeren dosya adlarında (ı, ş, ğ, ü, ö, ç, İ) .NET `InvalidOperationException: Invalid non-ASCII or control character in header: 0x0131` fırlatıyor. HTTP header'larına RFC 7230 gereği yalnızca ASCII karakterler yazılabilir.

**Düzeltme (FileServiceApi):** RFC 5987 `filename*=UTF-8''<percent-encoded>` formatı.
```csharp
var encodedName = Uri.EscapeDataString(rawName);
response.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{encodedName}";
```

### Bug 2 — Fotoğraf ve belge isimsiz iniyordu

**Hata:** CV kendi adıyla iniyordu; fotoğraf ve belgeler `dosya.uzantı` adıyla iniyordu.

**Kök neden (2 ayrı sebep):**

| Tür | Problem |
|---|---|
| Fotoğraf (jpg/png/webp) | `Content-Disposition: inline` yazılıyordu — filename hiç eklenmiyordu |
| Belge/PDF | RFC 5987 formatına (`filename*=UTF-8''...`) geçildi ama `api.ts` regex'i sadece eski `filename="..."` formatını anlıyordu |

**Düzeltme 1 (FileServiceApi):** Resimler dahil tüm türlere `filename*` eklendi; resimler yine `inline` (tarayıcıda görüntülenir) ama kaydedilirken doğru isim gelir.
```csharp
if (imageExtensions.Contains(fileObject.Extension))
    response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{encodedName}";
else
    response.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{encodedName}";
```

**Düzeltme 2 (client/src/api.ts):** `fetchFileBlob` RFC 5987 formatını önce deniyor, bulamazsa eski formata bakıyor.
```ts
const rfc5987Match = cd.match(/filename\*=UTF-8''([^;\s]+)/i)
const legacyMatch  = cd.match(/filename="([^"]+)"/)
const rawFileName  = rfc5987Match
  ? decodeURIComponent(rfc5987Match[1])
  : legacyMatch ? legacyMatch[1] : null
const fileName = rawFileName ?? `dosya.${contentType.split('/')[1] ?? 'bin'}`
```

**Sonuç:** CV, fotoğraf ve tüm belge türleri yüklendiği orijinal isimle iner. Türkçe karakterli isimler de doğru çalışır.

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

## Duplicate Dosya Kontrolü (TAMAMLANDI ✅ — 2026-06-30)

`CreateFileAsync` içinde, SHA256 hesaplandıktan ve staging→export taşındıktan **sonra**, DB yazımından **önce** duplicate kontrolü eklendi.

**Kural:** Aynı `(entityId, entityType, relationType, sha256)` dörtlüsü ve her ikisi `active` olan bir `reference + object` varsa upload reddedilir.

- HTTP `409 Conflict` + `{ "error": "duplicate_file", "existingFileId": "<guid>" }` döner.
- Export dosyası silinir (staging→export taşınmıştı; best-effort delete ile geri alınır).
- EF tracker'daki `prevObj/prevRef` değişiklikleri `SaveChangesAsync` çağrılmadan atılır — eski dosyaya dokunulmaz.
- Single-primary (cv, photo) ve multi-primary (document, attachment) için eşit biçimde uygulanır. Aynı binary'i iki kez yüklemek hiçbir senaryoda istenen davranış değil.
- Audit: `action=create, result=denied, reason_code=duplicate_file`.

Test senaryoları:
- Aynı PDF'i aynı personele iki kez yükle → ilki 200, ikincisi 409 + `existingFileId`
- `existingFileId` listedeki gerçek fileId ile eşleşiyor mu → kontrol et
- Farklı PDF'i aynı personele yükle → 200 (farklı SHA256)
- Aynı PDF'i farklı personele yükle → 200 (farklı entityId)

## HttpOnly Cookie BFF Auth (TAMAMLANDI ✅ — 2026-06-30)

localStorage'daki JWT token XSS ile çalınabiliyordu. BFF (Backend For Frontend) pattern ile token'lar artık JS'in göremeyeceği HttpOnly cookie'lerde tutuluyor.

### Mimari

```
Eski: Client → Keycloak (password grant) → JWT JSON → localStorage → Authorization: Bearer
Yeni: Client → /api/auth/login (YonetimApi BFF) → Keycloak → HttpOnly cookie "at" + "rt"
      Sonraki istekler → cookie otomatik gönderilir → Authorization header yok
```

### Değişen bileşenler

**YonetimApi:**
- `Endpoints/AuthEndpoints.cs` (yeni) — `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`
  - Login: `{username, password}` alır → Keycloak password grant → `at` + `rt` HttpOnly cookie → `{user, expiresAt}` JSON döner
  - Refresh: `rt` cookie'sini okur → Keycloak refresh grant → cookie günceller → `{user, expiresAt}` döner
  - Logout: cookie'leri siler
- `Program.cs` — JWT Bearer `OnMessageReceived`: `at` cookie'sinden token okunur (Authorization header da desteklenir — curl testleri için)
- `appsettings.json` + `docker-compose.yml` — `Keycloak:FrontendClientId: frontend-test` eklendi

**nginx:**
- `/api/auth/` → yonetimapi rotası eklendi

**Client:**
- `types.ts` — `AuthState`: `{user, expiresAt}` (token/refreshToken kaldırıldı). `AuthTokens` interface silindi. `AuthUser.exp` kaldırıldı.
- `auth.ts` — localStorage/sessionStorage kodu kaldırıldı. `isAccessTokenFresh` ve `canWrite` kaldı.
- `api.ts` — `login`/`refreshLogin` → `bffLogin`/`bffRefresh`/`bffLogout`. Tüm fonksiyonlardan `token` parametresi kaldırıldı. `apiFetch` → `credentials: 'include'`. `/realms` doğrudan çağrısı yok.
- `App.tsx` — Sayfa yüklenince `bffRefresh()` → oturum geri yükleme. Proaktif refresh `expiresAt`'e göre zamanlanır. Logout → `bffLogout()`.
- Tüm component'ler — `auth.token` pass'leri kaldırıldı.
- `vite.config.ts` — `/realms` proxy kaldırıldı (artık Keycloak'a doğrudan gidilmiyor).

### Cookie ayarları
- `HttpOnly: true` — JS okuyamaz (XSS koruması)
- `SameSite: Strict` — CSRF koruması
- `Secure: false` (dev); prod'da `true` yapılmalı (HTTPS gerektirir)
- `Path: /api` — yalnız API isteklerinde gönderilir
- `at` → access token ömrü kadar (Keycloak `expires_in`)
- `rt` → refresh token ömrü kadar (Keycloak `refresh_expires_in`)

### Neden güvenlik arttı
- Token JS tarafından **okunamaz** → XSS ile çalınamaz
- `localStorage.getItem('auth')` → artık boş
- Keycloak URL'i ve `frontend-test` client_id'si client bundle'a gömülmüyor

### Build doğrulama
| Kontrol | Sonuç |
|---|---|
| `npm run build` (TypeScript + Vite) | ✅ 0 hata |
| `dotnet build YonetimApi` | ✅ 0 hata |

### Test senaryoları
- `POST /api/auth/login` `{username: "hr001", password: "Demo1234!"}` → 200 + cookie set
- `POST /api/auth/refresh` (rt cookie mevcut) → 200 + cookie güncelleme
- `POST /api/auth/refresh` (rt cookie yok) → 401
- `POST /api/auth/logout` → cookie silindi, sonraki istek 401
- Browser DevTools → localStorage boş, `at` ve `rt` cookie'leri HttpOnly işaretli
- Sayfa yenilenince oturum korunuyor (rt cookie → refresh → session restore)

## actor_ip / user_agent IP Takip Zinciri (TAMAMLANDI ✅ — 2026-06-30)

`files.audit_events.actor_ip` ve `user_agent` sütunları artık doldurulmaktadır. V1'deki gap kapatıldı.

### Zincir

```
Telefon / Tarayıcı
   │
   ▼ (gerçek IP)
nginx → X-Real-IP: $remote_addr
   │
   ▼
YonetimApi (PersonnelEndpoints.cs)
  ExtractHeaders() → X-Real-IP veya X-Forwarded-For headers
  BuildResolveRequestAsync / FileBelongsToPersonnelAsync
  → tüm FileService HTTP isteklerine X-Client-IP header'ı eklenir
   │
   ▼
FileServiceApi (FileEndpoints.cs)
  Her method başında:
    clientIp  = request.Headers["X-Client-IP"].FirstOrDefault()
    userAgent = request.Headers["User-Agent"].FirstOrDefault()
  Tüm audit.WriteAsync çağrıları clientIp + userAgent parametreleriyle güncellendi
   │
   ▼
AuditService.WriteAsync(... actorIp, userAgent)
  → files.audit_events.actor_ip + user_agent INSERT
```

### Değiştirilen dosyalar

| Dosya | Değişiklik |
|---|---|
| `nginx/nginx.conf` | `/api/auth/` location'a `X-Real-IP $remote_addr` eklendi (zaten mevcuttu diğer location'larda) |
| `YonetimApi/Endpoints/PersonnelEndpoints.cs` | `ExtractHeaders()` 3-tuple döndürür: `(actor, correlationId, clientIp)`. Tüm FileService HTTP mesajlarına `X-Client-IP` header'ı eklendi |
| `FileServiceApi/Services/AuditService.cs` | `WriteAsync` imzası `actorIp` ve `userAgent` optional parametresi aldı |
| `FileServiceApi/Endpoints/FileEndpoints.cs` | Tüm 6 handler (Resolve, GetMetadata, GetContent, Create, List, Archive) başında `clientIp`/`userAgent` extraction eklendi; tüm `WriteAsync` çağrıları güncellendi |

### Doğrulama

Rebuild + telefondan farklı IP ile dosya yüklendikten sonra:
```sql
SELECT actor, actor_ip, user_agent, action, result, created_at
FROM files.audit_events
ORDER BY created_at DESC
LIMIT 5;
```
`actor_ip` sütununda telefonun gerçek IP adresi görülmeli.

## Çoklu Ortam Desteği — Mac + API Sunucusu (TAMAMLANDI ✅ — 2026-06-30)

Not: Bu bölüm geliştirme/test kolaylığını anlatır. Güncel minimum production profilde Mac Files-01'i
NFS ile mount etmez; Mac yalnız tarayıcı olarak Gateway'e gider. Files-01 NFS export'u yalnız
API/FileService sunucusuna açıktır.

Güncel çalışma modeli:

```text
Mac = development
FileAPI sunucusu = integration/prod-like doğrulama
Files-01 = storage-only, yalnız FileAPI erişir
```

Karar: Mac'i Files-01 allowlist'e eklemiyoruz. Aksi halde production minimum güvenlik modeli gevşer ve
container/NFS izin hataları geliştirici makinesinde gizlenebilir. Kod Mac'te geliştirilir, gerçek
Gateway/mTLS/NFS doğrulaması FileAPI sunucusunda yapılır.

### Topoloji

```
files-01 (192.168.64.3)
  /srv/files  ─NFS─▶  API sunucusu (192.168.64.5): /mnt/platform-files
              ─X─▶   Mac / başka VM (production minimumda timeout/access denied)

Mac (production minimum)
  tarayıcı → https://192.168.64.5:5090 → gateway → yonetimapi/fileservice

API sunucusu (192.168.64.5)
  docker compose → 192.168.64.5:5090
  → gateway → yonetimapi, fileservice, keycloak, postgres

Mac (yalnız UTM/test profili)
  NFS_MODE=test ile /Volumes/platform-files mount edilebilir
```

### Yapılan değişiklikler

**`client/vite.config.ts`** — `API_TARGET` env variable ile proxy hedefi değiştirilebilir:
```ts
const apiTarget = env.API_TARGET ?? 'http://localhost:5090'
```
- Yerel Docker: varsayılan (`localhost:5090`)
- API sunucusu üzerinden: `client/.env.local` dosyasına `API_TARGET=http://192.168.64.5:5090`

**`docker-compose.yml`** — iki fix:
1. Keycloak `KC_HOSTNAME=localhost` → Mac/Linux Docker fark etmeksizin token `iss` claim'i sabit `localhost:8080`
2. `STORAGE_PATH` env variable → Linux'ta NFS mount path farklı olabilir

**`client/.env.local`** (gitignore'da):
```
API_TARGET=http://192.168.64.5:5090
```

**`.env.linux`** (Linux sunucuda `cp .env.linux .env` ile kullanılır):
```
STORAGE_PATH=/mnt/platform-files
```

### Linux Docker sunucusunda ilk kurulum

```bash
# 1. NFS mount (files-01'den)
sudo mkdir -p /mnt/platform-files
sudo mount -t nfs 192.168.64.3:/srv/files /mnt/platform-files
# (kalıcı için /etc/fstab'a ekle)

# 2. Storage ortam değişkeni
cp .env.linux .env

# 3. Container'ları ayağa kaldır
docker compose up --build -d
docker compose ps   # tüm servisler healthy olana kadar
```

### Mac'te çalıştırma

```bash
bash setup-mac.sh
# Tarayıcı: http://localhost:5090
```

### Neden KC_HOSTNAME gerekti?

Mac Docker'da YonetimApi `keycloak:8080`'i çağırdığında token `iss=keycloak:8080` dönebiliyordu ama `ValidIssuers=["localhost:8080"]` bekleniyordu → 401. `KC_HOSTNAME=localhost` ile Keycloak her ortamda aynı issuer'ı üretir.

## Çoklu Ortam Kurulumu (TAMAMLANDI ✅ — 2026-06-30)

Sistem production minimum profilde API sunucusunda (192.168.64.5) çalışacak şekilde yapılandırıldı.
Mac yalnız tarayıcı olarak kullanılır; Files-01 NFS export'una doğrudan bağlanamaz.

### Topoloji

```
Mac (tarayıcı)  ──http://192.168.64.5:5090──▶  API sunucusu (192.168.64.5)
                                                    ├─ gateway (nginx) :5090
                                                    ├─ client (nginx SPA) :80 (iç)
                                                    ├─ yonetimapi :8080 (iç)
                                                    ├─ fileservice :8080 (iç, mTLS)
                                                    ├─ keycloak :8080
                                                    └─ postgres
                                                           │
                                                    NFS mount ▼
                                              UTM files-01 (192.168.64.3)
                                                    /srv/files → /mnt/platform-files
```

**Artık Mac'te hiçbir şey çalışmıyor.** Tarayıcı `http://192.168.64.5:5090/` ile doğrudan sunucuya bağlanır.

### Frontend Production Packaging (TAMAMLANDI ✅)

React SPA artık Docker container içinde çalışıyor:

- `client/Dockerfile` — multi-stage: `node:20-alpine` build → `nginx:alpine` serve
- `client/nginx-spa.conf` — SPA nginx config (`try_files $uri $uri/ /index.html`)
- `nginx/Dockerfile` — gateway nginx artık image değil build tabanlı (nginx.conf değişince rebuild zorunlu)
- `docker-compose.yml` — `client` servisi eklendi; gateway `location /` → client:80 proxy

### JWKS Backchannel Handler (TAMAMLANDI ✅)

`KC_HOSTNAME=localhost` nedeniyle OIDC discovery'deki `jwks_uri` `localhost:8080` döner. Container içinden `localhost:8080` Keycloak değildir → "signature key not found" → 401.

- `YonetimApi/Infrastructure/KeycloakBackchannelHandler.cs` — JWT Bearer middleware'in backchannel isteklerinde `localhost:8080 → keycloak:8080` yönlendirir
- `FileServiceApi/Infrastructure/KeycloakBackchannelHandler.cs` — aynı handler
- Her iki `Program.cs`'de `options.BackchannelHttpHandler = new KeycloakBackchannelHandler()` eklendi

### setup-server.sh Otomasyonu

`setup-server.sh` komutu git pull sonrasında şunları yapar. Production minimum artık varsayılandır;
UTM/test kolaylığı gerekiyorsa bilinçli olarak `NFS_MODE=test bash setup-server.sh` kullanılmalıdır.

1. `.env` yoksa `.env.linux`'tan kopyalar
2. Production modda Files-01'in `/srv/files *` olarak açık olmadığını kontrol eder
3. NFS mount yoksa `192.168.64.3:/srv/files → /mnt/platform-files` mount eder + `/etc/fstab`'a ekler
4. `certs/*.key` dosyaları eksikse/klasörse `generate-certs.sh` çalıştırır
5. Production modda `docker compose -f docker-compose.yml up --build -d`; test modda normal `docker compose up --build -d`
6. `docker compose ... restart fileservice` (NFS mount sırası için)
7. DB tablolar yoksa `01-schema.sql` + `02-seed.sql` çalıştırır
8. FileService container içinden gerçek `staging -> export` probe'u çalıştırır

Deploy sonrası önerilen doğrulama:

```bash
bash tools/server-smoke-test.sh
```

Smoke test kapsamı: Gateway health, login, personel listesi, `P001` files, varsa download, `p001`
başkasına `403`, audit son kayıtlar.

### Linux Sunucusu İlk Kurulum

```bash
# Production minimum:
bash setup-server.sh

# UTM/test:
# NFS_MODE=test bash setup-server.sh

# Kod güncellemesi:
git pull
bash setup-server.sh
bash tools/server-smoke-test.sh

# Ya da sadece container rebuild:
docker compose -f docker-compose.yml up --build -d
```

### Bilinen Kısıtlar

- **Telefon erişimi**: UTM sanal ağı (192.168.64.x) yalnızca Mac'ten erişilebilir. Telefon bu ağı göremez — bu UTM ağ mimarisi sınırlamasıdır. Telefondan erişim için ya UTM bridged networking (aynı WiFi) ya da ngrok/Tailscale gibi tünel servisi gerekir.
- **DB seed**: `docker compose down -v` sonrasında postgres volume sıfırlanır; `setup-server.sh` ile veya `docker exec -i server-file-postgres-1 psql ... < 01-schema.sql && 02-seed.sql` ile yeniden çalıştır.
- **NFS kalıcılığı**: Sunucu reboot'ta NFS mount kaybolabilir. `setup-server.sh` her çalışmada mount'u kontrol eder. fstab'a eklenmişse otomatik olur.

## Client UI — Filo (Fleet) ve Yükleme İlerlemesi (TAMAMLANDI ✅ — 2026-06-30)

### Fleet UI

`fleetuser` kullanıcısı (JWT `vehicle_id` claim'i olan) artık kendi aracının dosyalarını yönetebiliyor.

**Yeni bileşenler:**
- `client/src/components/VehicleFileView.tsx` — araç dosya görünümü (PersonnelFileView muadili)
- `client/src/components/Dashboard.tsx` — hem personel hem araç kullanıcısı için sekme navigasyonu

**Değiştirilen bileşenler:**
- `YonetimApi/Endpoints/AuthEndpoints.cs` — BFF artık `vehicle_id` JWT claim'ini `user` nesnesine ekliyor
- `client/src/types.ts` — `AuthUser.vehicle_id?: string` + `VEHICLE_UPLOAD_RELATION_TYPES` + `VEHICLE_SINGLE_PRIMARY_TYPES`
- `client/src/api.ts` — `getVehicleFiles`, `uploadVehicleFile`, `archiveVehiclePrimary`, `fetchVehicleFileContent`
- `client/src/auth.ts` — `canVehicleWrite(auth, vehicleId): boolean`
- `client/src/components/FileCard.tsx` — callback tabanlı yeniden yazıldı (`onDownload`, `onArchive`)
- `client/src/components/PersonnelFileView.tsx` — yeni FileCard ve UploadModal arayüzüne güncellendi
- `client/src/components/UploadModal.tsx` — generic yapıya alındı (`entityDisplayName`, `uploadFn`, `relationTypes`)

**Dashboard davranışı:**
- Kullanıcının hem `personnel` rolü hem `vehicle_id`'si varsa → "Personel" | "Filo" sekmeleri
- Yalnız `vehicle_id` varsa (`fleetuser` gibi) → doğrudan araç dosya görünümü
- Yalnız personel rolleri varsa → mevcut personel görünümü

**Fleet kısıtlamaları (V1):**
- Araç araması yok — her kullanıcı JWT'sindeki kendi `vehicle_id`'yi görür
- Multi-primary tipler (attachment, report): indirme ve arşivleme yok (FlotaApi V1 content/archive endpoint'i yalnız single-primary için)
- Single-primary tipler (photo, document, official_document): indirme + arşivleme mevcut

### Yükleme İlerleme Çubuğu

`fetch` → `XMLHttpRequest` geçişiyle dosya yükleme sırasında gerçek zamanlı ilerleme gösterimi eklendi.

- `api.ts` — `xhrUpload()` yardımcı fonksiyonu; `uploadFile` ve `uploadVehicleFile` bu ortak fonksiyonu kullanır
- `UploadModal.tsx` — yükleme sırasında `%0–%100` ilerleme çubuğu gösterir

### Keycloak Admin Kapatma

Keycloak port `8080:8080` `docker-compose.yml`'den `docker-compose.override.yml`'e taşındı.
Production'da `docker compose -f docker-compose.yml up` ile başlatılırsa Keycloak dışa açılmaz.
Dev'de (`docker compose up`) override otomatik yüklenir, 8080 erişilebilir kalır.

### TypeScript Doğrulama

`npx tsc --noEmit` — 0 hata.

## HTTPS — Gateway TLS (TAMAMLANDI ✅ — 2026-06-30)

Gateway artık HTTPS üzerinden çalışıyor. Self-signed sertifika (dev/iç ağ) ve Let's Encrypt (VPS/prod) destekleniyor.

### Değiştirilen dosyalar

| Dosya | Değişiklik |
|---|---|
| `certs/generate-certs.sh` | `sign_server_gateway()` eklendi — SAN: `DNS:gateway,DNS:localhost,IP:127.0.0.1,IP:192.168.64.5`. Çağrı: `sign_server_gateway "gateway" ...` |
| `nginx/nginx.conf` | HTTP→HTTPS redirect bloğu (listen 80) + HTTPS server bloğu (listen 443 ssl). `ssl_certificate /etc/nginx/certs/gateway.crt`, `ssl_protocols TLSv1.2 TLSv1.3` |
| `nginx/Dockerfile` | `EXPOSE 80 443` |
| `docker-compose.yml` | Gateway: `5090:443`; cert volume mount: `./certs/gateway.crt:/etc/nginx/certs/gateway.crt:ro` + key |
| `YonetimApi/Endpoints/AuthEndpoints.cs` | `IsSecureRequest(ctx)`: `ctx.Request.IsHttps \|\| X-Forwarded-Proto==https`. `MakeCookieOpts(maxAge, secure)` + `SetCookies(..., secure)` + `ClearCookies(..., secure)` — tüm çağrıcılar güncellendi |
| `KURULUM.md` | Bölüm 4: Self-signed + Let's Encrypt kurulum adımları; curl örnekleri `-k https://` olarak güncellendi |

### Cookie Secure davranışı

- `X-Forwarded-Proto: https` header'ı nginx tarafından `$scheme` ile set ediliyor (HTTPS → `https`)
- `IsSecureRequest` bunu okuyarak `Secure = true` ayarlıyor
- Dev (self-signed): tarayıcı uyarısını geç → cookie `Secure=true` gönderilir
- HTTP'de (`5090:80` kaldırıldı): `Secure=false` — production'da HTTP erişimi olmamalı

### Sertifika üretimi

```bash
bash certs/generate-certs.sh
# Yeni: certs/gateway.crt + certs/gateway.key
docker compose up --build -d gateway
```

### Doğrulama

```bash
curl -k https://localhost:5090/health
# {"status":"healthy","service":"Gateway-Nginx"}
```

Tarayıcıda: `https://localhost:5090` → uyarıyı kabul et → login çalışır.

---

## Ops Dashboard Boş Konteyner Bug Fix (TAMAMLANDI ✅ — 2026-07-01)

Prod sunucuda (192.168.64.5) Ops ekranının "Services" sekmesi tüm konteynerler çalışırken boş görünüyordu.

### Kök neden

`tools/services-status.sh`, `docker compose ps` stderr'ini sabit bir yola (`/tmp/platform-services-status.err`)
yönlendiriyordu (diğer geçici dosyalar `mktemp` kullanırken bu kullanmıyordu). Bu dosya bir noktada
`fileapi` kullanıcısıyla (root olmayan) oluşmuştu. `platform-services-status.timer` script'i `root` olarak
çalıştırdığında bu dosyaya yazamadı (sahiplik çakışması, tmpfs `usrquota` mount seçeneğiyle birleşince),
redirection `docker compose ps` çalışmadan başarısız oldu, script `else` (hata) dalına düştü ve
`/backup/platform-files/.services-status.json` dosyasına `status:"failed", count:0, services:[]` yazdı.
İlk hata anında henüz iyi bir önceki snapshot olmadığından fallback da boş kaldı ve her 5 dakikada bir
aynı boş sonuç kendini tekrarladı. OpsApi `/ops/services` ve `/ops/dashboard` bu dosyayı okuduğu için
Services sekmesi sürekli boş göründü — halbuki 8 konteynerin tamamı sağlıklı çalışıyordu.

### Fix

- `tools/services-status.sh`: stderr dosyası artık `err="$(mktemp)"` ile üretiliyor, `trap` temizliğine eklendi,
  sabit `/tmp/platform-services-status.err` yolu tamamen kaldırıldı. Artık kullanıcı/sahiplik farkı ne olursa
  olsun çakışma yaşanmaz.
- Sunucuda (192.168.64.5) düzeltilmiş script deploy edildi, `platform-services-status.service` manuel tetiklendi,
  `.services-status.json` `status:"success", count:8` üretti; timer normal 5 dakikalık döngüsüne devam ediyor.

### Doğrulama

- `bash tools/server-smoke-test.sh` sunucuda uçtan uca çalıştırıldı: gateway health, hr001/p001/opsadmin login,
  personel/dosya erişimi, 403 yetki reddi, tüm `/ops/*` endpoint'leri (200), audit son kayıtlar — hepsi geçti.
- `opsadmin` cookie'siyle `/ops/services` ve `/ops/dashboard` doğrudan sorgulandı: `services.count: 8`,
  `health.status: healthy`, 7 servisin health check'i (`yonetimapi/flotaapi/keycloak/gateway/postgres/fileservice/opsapi`)
  tek tek `healthy` döndü.
- files01 (192.168.64.3) NFS export ve api-server (192.168.64.5) NFS mount ayrıca kontrol edildi: export
  yalnız `192.168.64.5`'e açık (production-hardening modeliyle uyumlu), mount `nfs4` ile aktif, disk kullanımı
  api-server %72 / files01 %35 — ikisi de normal aralıkta.

### Kalan not

Bu, `services-status.sh`'ın kendi kendini iyileştiremediği bir senaryoydu (ilk hata → boş fallback → sonsuz
tekrar). Script artık bu spesifik çakışmayı yaşayamaz, ama script'in genel olarak "N kez üst üste failed
durumunda alerts'e critical düşür" gibi bir kendiliğinden kurtulma/alarm mekanizması yok — gelecekte benzer
farklı bir hata türü (ör. docker daemon geçici kopması) yine sessizce süresiz "failed" snapshot'ta takılı
kalabilir. V2 alerting iyileştirmesi olarak değerlendirilebilir.

### Ek doğrulama — Deploy senkronizasyonu ve tekli konteyner resilience testi (2026-07-01)

Fix'in sunucuya ulaşması sırasında ayrı bir sorun daha ortaya çıktı: sunucu 2 commit geride kalmıştı
(`ee733e3`), `git pull` bu yüzden `tools/services-status.sh` üzerinde "local changes would be overwritten"
hatası verdi. Sunucudaki dosyanın (daha önce `scp` ile taşınmış hali) yeni commit'le hash bazında birebir
aynı olduğu doğrulanıp yerel diff temizlendi, pull tamamlandı. Pull ile `OpsEndpoints.cs`, `OpsConsole.tsx`,
`types.ts` gibi gerçek uygulama kodu değişiklikleri de geldiği için `bash setup-server.sh` çalıştırılıp
tüm servisler rebuild edildi. `git push origin main` + sunucuda `git pull` + `setup-server.sh` + smoke test
ile uçtan uca doğrulandı; `/ops/dashboard` `version.commit` alanı push edilen commit'le eşleşti.

Ardından Ops ekranının gerçek zamanlı doğruluğunu kanıtlamak için canlı bir resilience testi yapıldı:
`client` container'ı bilinçli olarak durduruldu (`docker compose stop client`) → `/ops/services` anında
`exited` durumunu gösterdi, gateway kök path'i `502` döndü → `docker compose start client` ile geri
başlatıldı → `/ops/services` `running` durumuna döndü, `restart_count: 0` (Docker'ın kendi restart policy'si
tetiklenmedi, manuel start/stop olduğu için beklenen davranış), gateway `200`'e döndü, smoke test tekrar
tam geçti. Bu, Ops Dashboard'un statik/önbelleklenmiş değil gerçek container durumunu yansıttığını kanıtlar.

Ayrıca NFS'in yalnızca api sunucusuna açık olduğu iddiası ağ seviyesinde doğrulandı: files01'de (192.168.64.3)
`ufw` `default deny incoming` ile aktif, yalnızca `2049/tcp` `192.168.64.5`'ten kabul ediliyor; `rpcbind
(111)` `0.0.0.0`'da dinliyor ama ufw allow listesinde olmadığı için varsayılan deny ile bloklanıyor. Yani
koruma hem `/etc/exports` ACL seviyesinde hem firewall seviyesinde çift katmanlı ve gerçek.

**Bulunan eksik:** files01'in aksine api sunucusunda (192.168.64.5) `ufw` **inactive** — host firewall hiç
aktif değil. `ss -tlnp` çıktısı `22` (SSH), `111` (rpcbind — NFS client tarafı, kendi mount'u için), `5090`
(gateway) portlarının `0.0.0.0` üzerinden açık olduğunu, hiçbir OS seviyesi filtreleme olmadan gösteriyor.
Şu an tek public port zaten sadece `5090` (docker-compose diğer servisleri publish etmiyor) ama host
firewall'ın kapalı olması "Firewall + NFS allowlist" production kapısını yarım bırakıyor — NFS tarafı
(files01) kilitli, api-server tarafı değil. Öneri: api sunucusunda da `ufw` etkinleştirilip yalnızca
`22/tcp` (yönetim) ve `5090/tcp` (gateway) allow edilmeli; `111` dış ağdan erişilmemeli.

**Kapatıldı (2026-07-01):** api sunucusunda `ufw` etkinleştirildi. Kilitlenme riskine karşı sıralı uygulandı:
önce `ufw allow 22/tcp`, sonra `ufw allow 5090/tcp`, ardından `ufw --force enable`. Etkinleştirme sonrası
yeni bir SSH oturumu açılıp bağlantının kopmadığı doğrulandı, Mac'ten gateway'e `https://192.168.64.5:5090/health`
ile erişim test edildi (200), NFS mount'un (api-server'ın kendi outbound client bağlantısı, inbound kuralından
etkilenmez) hâlâ aktif olduğu kontrol edildi, tam smoke test tekrar çalıştırıldı — hepsi geçti. `111` (rpcbind)
için allow kuralı eklenmedi; artık dışarıdan erişilemiyor (varsayılan deny), api-server'ın kendi NFS client
mount'unu etkilemiyor çünkü bu outbound bir bağlantı. Artık her iki sunucuda da host firewall gerçek ve
doğrulanmış durumda — "Firewall + NFS allowlist" production kapısı tamamlandı.

### Ops Console "Ölçüm" zamanı arka plan sekmesinde donuyordu (TAMAMLANDI ✅ — 2026-07-01)

Kullanıcı Ops ekranını uzun süre arka planda bırakınca "Ölçüm" saati gerçek zamandan ~1 saat geride
kaldığını fark etti; "Uptime" sütunu da bununla tutarsız görünüyordu. Kök neden araştırıldı: backend
(`platform-services-status.timer`) tam 5 dakikada bir sorunsuz çalışıyor, snapshot her zaman tazeydi —
sorun backend'de değildi. Gerçek sebep: `client/src/components/OpsConsole.tsx` `setInterval(refresh, 30_000)`
ile pollüyor, ama tarayıcılar (özellikle Safari/macOS) arka plandaki/aktif olmayan sekmelerde JS timer'larını
yavaşlatıyor/duraklatıyor. Sekme uzun süre öne getirilmeyince `refresh()` gerçekte tetiklenmiyor, "Ölçüm"
donuk kalıyor; ama "Uptime" sütunu (`serviceAgeSeconds`) her render'da tarayıcının gerçek `Date.now()`'ıyla
canlı hesaplandığı için ikisi arasında tutarsızlık oluşuyor.

**Fix:** `OpsConsole.tsx`'e `document.visibilitychange` listener eklendi — sekme tekrar görünür olduğunda
anında `refresh()` tetikleniyor. Uptime hesaplama mantığına dokunulmadı (zaten doğruydu). Sunucuya `scp` +
`docker compose up --build -d client` + `restart gateway` ile deploy edildi (git push yapılmadı — sadece
local commit), derlenen JS bundle'da `visibilitychange` string'i doğrulandı, tam smoke test tekrar geçti.

---

## Opak, Tek Kullanımlık İndirme Ticket'ı — Personel Dosyaları (TAMAMLANDI ✅ — 2026-07-02)

Kullanıcı, endüstride yaygın "imzalı/tek kullanımlık indirme linki" (S3 presigned URL, Google Signed URL
benzeri) deseninin projeye eklenmesini istedi. `file-service-api-contract.md`'nin kendi "V2 Download Ticket
Opsiyonu" notundaki (kısa ömürlü, `file_id` bazlı, tek/sınırlı kullanım, varlık sızdırmayan) fikir esas
alınarak, **mevcut güvenlik mimarisini bozmadan** (FileServiceApi hâlâ hiç dışa açılmıyor, mTLS+servis token'ı
zorunluluğu aynen duruyor) YonetimApi'nin kendi proxy zinciri içinde uygulandı.

### Neden literal V2 modeli (Client → FileServiceApi doğrudan) değil

Bu oturumda projeye gönderilen bir "düzeltme raporu", ticket sisteminin Client'ın FileServiceApi'ye
doğrudan gitmesini, Gateway'in ticket'ı doğrulamasını ve X-Accel-Redirect kullanılmasını öneriyordu.
Kaynak olarak gösterdiği başlıklar (`Ticket Sözleşmesi`, `Ticket Store`, `Private Download Akışı`)
`PROJE/` klasöründeki 5 gerçek dosyada **doğrulanamadı** — detaylar `STAJYER-RAPORU-DOGRULAMA.md`'de.
Bu yüzden ticket konsepti alındı ama teslimat mekanizması bizim gerçek, zaten doğrulanmış güvenlik
sınırımıza (FileServiceApi internal-only, mTLS zorunlu) uyacak şekilde uyarlandı.

### Ne eklendi

- `db/docker-init/04-download-tickets.sql` — `yonetim.download_tickets` tablosu: `ticket_hash` (PK, SHA256
  hex, açık ticket asla saklanmaz), `personnel_id`, `file_id`, `actor`, `expires_at`, `used_at` (NULL =
  tüketilmedi).
- `YonetimApi/Endpoints/DownloadTicketEndpoints.cs`:
  - `POST /api/personnel/{personnelId}/files/{fileId}/download-ticket` — normal cookie auth + RBAC
    (`CanReadAsync`) + fileId-ownership kontrolü sonrası, `RandomNumberGenerator` ile **256-bit (32 byte)**
    rastgele opak ticket üretir, SHA256 hash'ini DB'ye yazar, ham ticket'ı **sadece bir kez** client'a döner.
    Varsayılan ömür: **60 saniye**.
  - `GET /api/personnel/download/{ticket}` — **`AllowAnonymous`**, hiçbir cookie/JWT gerektirmez. Gelen
    ticket hash'lenip DB'de aranır; **atomik** `UPDATE ... WHERE used_at IS NULL AND expires_at > now()`
    ile tek-kullanım garantisi sağlanır (aynı anda iki istek gelirse yalnızca biri kazanır). Bulunursa
    FileServiceApi'den mevcut mTLS+servis-token zinciriyle stream eder.
  - `PersonnelEndpoints.FileBelongsToPersonnelAsync` ve `OwnershipResult` `internal` yapılıp tekrar
    kullanıldı (kod tekrarı yok).

### Doğrulama — sunucuda 7 canlı test, hepsi geçti

1. Ticket oluşturma → `200`, 256-bit base64url ticket + `expiresInSeconds:60` döndü.
2. Ticket ile **hiçbir cookie olmadan** indirme → `200`, doğru dosya (110567 byte) indi.
3. Aynı ticket'ı tekrar kullanma → `404` (tek kullanımlık, DB'de ikinci `UPDATE` 0 satır etkiledi).
4. Uydurma/var olmayan ticket → `404` (varlık sızdırmıyor).
5. 60 saniyelik ticket'ı 65 saniye bekleyip kullanma → `404` (süre kontrolü çalışıyor).
6. p001, P002'nin path'i üzerinden ticket istemeye çalıştı → `403 access_denied` (RBAC, ticket satırı hiç
   oluşmadı — DB'den doğrulandı).
7. p001 kendi dosyası için ticket istedi → `200` (pozitif kontrol).

`yonetim.audit_events`'e `PersonnelDownloadTicketCreated`/`PersonnelDownloadTicketConsumed` olarak
yazıldığı doğrudan SQL sorgusuyla doğrulandı. Tam smoke test tekrar çalıştırıldı, hiçbir regresyon yok.

Test detayları ve kanıtları: `proof/download-ticket-sistemi.md`.

### Bilinen sınırlamalar (V1, bilerek)

- Sadece personel (`YonetimApi`) tarafında var; FlotaApi'ye henüz taşınmadı.
- Süresi dolmuş/tüketilmiş ticket satırları DB'de birikir — otomatik temizlik (cron/job) yok, düşük hacimde
  şu an sorun değil ama uzun vadede bir temizlik görevi eklenmeli.
- `relation_type` alanı ticket oluşturma sırasında bilinmediği için `"unknown"` olarak yazılıyor — fileId
  bazlı akışta işlevsel bir etkisi yok, sadece kozmetik.

---

## SIRADAKİ ADIM

- **Secret rotasyonu**: Demo parolalar/realm secret'ları prod deploy öncesi değiştirilmeli ve env/secret
  store üzerinden yönetilmeli.
- **Let's Encrypt + gerçek domain**: İç ağ self-signed HTTPS çalışıyor. Public prod için gerçek domain,
  443 ve güvenilir sertifika zinciri tamamlanmalı.
- **Observability Faz 1**: Request id/correlation standardı, structured logs ve temel metrics eklenmeli;
  sonra Prometheus/Grafana/tracing kurulmalı.
- **Deploy/test otomasyonu**: `tools/server-smoke-test.sh` ve `tools/server-safe-test-suite.sh` eklendi.
  Sonraki iyileştirme olarak branch deploy helper (`git fetch && checkout && setup-server && smoke && safe-test`)
  tek komuta indirilebilir.
- **Resilience test V1**: Safe test geçiyor; sıradaki kontrollü testler Gateway/OpsApi/FileService/Keycloak/PostgreSQL
  restart sonrası health, login, download ve ops dashboard toparlanma kontrolleri.
- **Ops Dashboard V1 polish**: Read-only console artık `/ops/dashboard` üzerinden System Health, Services,
  Disk, Alerts, Backups ve Version metadata alır. Docker socket OpsApi'ye mount edilmez; servis listesi
  `tools/services-status.sh` tarafından yazılan status-file üzerinden okunur. Kalan polish: UI metriklerinin
  canlı server'da doğrulanması ve ops audit son kayıtları için read endpoint.
- **restore-live guardrail**: `restore-live.sh` Break Glass / Manual Recovery olarak kalmalı; V2'de çalışmadan
  önce otomatik pre-restore backup alması eklenecek.
- **Strict NFS ro/publisher modeli**: Minimum production için şart değil; V2 hardening olarak tutuluyor.
  Bu modele geçilirse FileService runtime NFS'e yazmaz, staging/publish ayrı kontrollü sürece taşınır.
- **V2 Download**: `file-service-api-contract.md`'deki V2 model — performans baskısı oluşursa değerlendirilecek.
- **Sertifika rotasyonu**: `certs/generate-certs.sh` artık mevcut CA/sertifikaları varsayılan olarak ezmez; `FORCE_REGENERATE_CERTS=1` bilinçli rotasyon içindir. Gateway SAN değerleri `GATEWAY_DNS`/`GATEWAY_IPS` ile parametrelenir. Prod'da CA rotasyonu ayrı prosedürle yapılmalı.
- **Fleet vehicle araması**: FlotaApi'ye `GET /api/vehicles?search=` endpoint'i eklenirse Dashboard'a araç listesi sidebar'ı eklenebilir (şu an yoktur — V2 adayı).

## Bilinen tuzaklar

- `dotnet run` komutunu çalıştırırken yanlış dizinde olunursa yanlış servis başlar.
  Her zaman `pwd` ile doğrula veya `cd FileServiceApi && dotnet run` şeklinde zincirle.
- Arka planda çalışan eski bir servis `dotnet run` başlatmayı engeller (port already in use).
  `lsof -ti:<port> | xargs kill -9` ile temizle.
- `curl -I` HEAD isteği gönderir — GET endpoint'leri için 405 döner; içerik testlerinde
  `-X GET` veya `-v` kullan.
- `yeni-test-cv.pdf` gerçek PDF değil. Upload testinde magic-byte hatası alırsın.
- `runbooks/files01-nfs-setup.md` içindeki `*(rw)` NFS export örneği UTM/test içindir. Production'da `runbooks/production-hardening.md` kullanılmalı.
- `appsettings.json` dosyalarındaki localhost/Mac path değerleri local fallback'tir; production davranışı compose environment değişkenleriyle doğrulanmalı.
