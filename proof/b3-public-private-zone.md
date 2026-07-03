# Kanıt: B3 — Public/Private Storage Zone

**Tarih:** 2026-07-03
**Kapsam:** `platform-mimarisi-stajyer-rehberi.txt` bölüm 9.3/9.6 — public/private güvenlik zone'u
**Durum:** ✅ Tamamlandı, 6 senaryo gerçek HTTP istekleriyle test edildi, tam regresyon temiz

---

## Bağlam

Faz B2 tartışmasında bu özellik "şu an hiçbir gerçek public-dosya ihtiyacı yok" gerekçesiyle YAGNI olarak
işaretlenmişti. Kullanıcı, gerçek bir iş ihtiyacı olmasa da bunu şimdi altyapı seviyesinde inşa etmeye
karar verdi — önce planlanıp (plan modu), sonra gerçek testlerle kanıtlanarak.

## Mimari Karar (risk minimize eden tasarım)

- **`relative_path` şeması DEĞİŞMEDİ** (`{domain}/{shard1}/{shard2}/{uuid}.{ext}`, mevcut
  `chk_relative_path_format` aynen kaldı) — sadece hangi **fiziksel kök dizine** yazıldığı zone'a göre
  değişiyor. Mevcut veri migrasyonu veya şema regex değişikliği hiç gerekmedi.
- **Public ve private dosyalar TAMAMEN AYRI fiziksel kök dizinlerde**: `/srv/files/export` (private,
  değişmedi) vs yeni `/srv/files/export-public` — savunma derinliği, path-traversal hatası bile iki ağacı
  karıştıramaz.
- **`FilesPublisher/publisher.py`'ye minimal değişiklik**: sadece `zone` query param'ına göre
  `EXPORT_ROOT` vs `EXPORT_ROOT_PUBLIC` seçimi (~5 satır her `do_POST`/`do_DELETE`'te). NFS export
  (`/etc/exports`) hiç değişmedi — zaten `/srv/files`'in tamamını kapsıyordu, yeni alt dizin otomatik
  göründü. `ReadWritePaths=/srv/files` (systemd) tek, ortak üst dizin olduğu için EXDEV riski yok.
- **Public okuma File-Service/ticket-store'u hiç görmüyor** (rehber 9.6) — nginx `/public/` location'ı
  hiçbir backend'e gitmeden doğrudan salt-okunur mount'tan servis ediyor, X-Accel-Redirect YOK.
- **Yeni güvenlik kısıtı:** `zone=public` sadece `classification=official` ile kabul edilir —
  confidential/restricted/internal bir dosya asla public olamaz.
- Mevcut upload validasyon zinciri (extension, content-type, magic-byte, ClamAV tarama) zone'dan
  bağımsız olarak aynen uygulanıyor.

## Değişen Dosyalar

- `db/docker-init/07-storage-zone.sql` (yeni): `storage_zone TEXT NOT NULL DEFAULT 'private' CHECK
  (storage_zone IN ('public','private'))`.
- `FileServiceApi/Models/FileObject.cs`, `Data/AppDbContext.cs`: `StorageZone` property/mapping.
- `FileServiceApi/Services/FilesPublisherClient.cs`: `PublishAsync`/`DeleteAsync`'e `zone` parametresi.
- `FileServiceApi/Endpoints/FileEndpoints.cs::CreateFileAsync`: `zone` form alanı, zone/classification
  kontrolü, yanıta `zone`+`publicUrl` eklendi.
- `FilesPublisher/publisher.py`: `EXPORT_ROOT_PUBLIC` + zone'a göre kök seçimi.
- `FilesPublisher/platform-files-publisher.service`: `EXPORT_ROOT_PUBLIC` env var eklendi.
- `docker-compose.yml`: gateway'e yeni `export-public:/public-files:ro` bind-mount.
- `nginx/nginx.conf`: yeni kimlik-doğrulamasız `/public/` location + `mime.types` include (aşağıda
  açıklanan bug'ın düzeltmesi).
- Files-01: `/srv/files/export-public` dizini `files-writer:files-publishers` sahipliğinde (`0770`)
  oluşturuldu, `platform-files-publisher.service` yeni env var ile yeniden başlatıldı.

## Bulunan ve Düzeltilen Bug: Content-Type `text/plain`

İlk testte public dosya doğru servis edildi ama `Content-Type: text/plain` döndü (PDF olmasına rağmen).
Kök neden: `nginx.conf`'ta `include /etc/nginx/mime.types;` hiç yoktu — private/ticket akışı bu sorunu
hiç yaşamamıştı çünkü FileServiceApi `Content-Type`'ı zaten açıkça set edip X-Accel-Redirect öncesi
gönderiyordu (nginx sadece upstream header'ını koruyordu). Ama `/public/` location'ı hiçbir backend'e
gitmediği için, nginx KENDİ mime.types tablosundan tahmin etmesi gerekiyordu — tablo dahil edilmemiş
olduğu için varsayılan `text/plain`'e düşüyordu. Düzeltme: `http {}` bloğunun başına `include
/etc/nginx/mime.types; default_type application/octet-stream;` eklendi.

## Testler (6 senaryo, gerçek HTTP istekleriyle)

**Test 1 — Public dosya oluşturma:** `classification=official, zone=public` ile gerçek bir PDF, geçerli
bir service token + mTLS ile `POST /internal/files`'a yüklendi:
```json
{"fileId":"2a3b0912-...","zone":"public","publicUrl":"/public/personnel/2a/3b/2a3b0912-...pdf", ...}
```
`http:200`, `publicUrl` doğru döndü.

**Test 2 — Sıfır kimlik doğrulamayla erişim:** O `publicUrl`'e **hiçbir cookie/JWT/mTLS sertifikası
olmadan**, düz `curl` ile (kendi Mac'imden, doğrudan `https://192.168.64.5:5090/public/...`):
```
HTTP/1.1 200 OK
Content-Type: application/pdf   (mime.types düzeltmesi sonrası — önce text/plain'di)
Content-Length: 44
```
İçerik doğrulandı — yüklenen PDF'in tam metnini içeriyordu.

**Test 3 — İzolasyon:** Mevcut bir PRIVATE dosyanın gerçek `relative_path`'i (`personnel/e2/2f/e22f0c82-...pdf`)
`/public/` altında denendi:
```
http:404
```
Doğal izolasyon — private dosya fiziksel olarak `export-public` dizininde hiç yok.

**Test 4 — Zone/classification tutarlılık:** `zone=public` + `classification=confidential` ile yükleme
denendi:
```json
{"error":"zone_classification_mismatch"}
```
`http:400`, dosya hiç oluşturulmadı.

**Test 5 — Fail-closed virus tarama zone'dan bağımsız:** Gerçek bir EICAR (PDF FlateDecode stream'ine
gömülü, Faz A'daki teknikle aynı), `zone=public` + `classification=official` ile yüklenmeye çalışıldı:
```json
{"error":"virus_detected"}
```
`http:422`, hiç yayınlanmadı — virus tarama zinciri zone'dan tamamen bağımsız, aynı sıkılıkta çalışıyor.

**Test 6 — Tam regresyon (öncesi ve sonrası):** `tools/server-smoke-test.sh` → 23/23 `[OK]` (mime.types
düzeltmesi öncesi ve sonrası iki kez). `tools/server-safe-test-suite.sh` → 36/36 `[OK]`, 0 `[HATA]`.
`platform-backup.service` (+ otomatik restore-test) → `ExecMainStatus=0`.

## Deploy ve Senkronizasyon

FileServiceApi yerelde derlendi (0 hata), `scp` ile api-server'a kopyalanıp `docker compose up -d --build
fileservice gateway` ile canlıya alındı — healthcheck zinciri sayesinde bağımlı servisler (yonetimapi,
flotaapi) de otomatik doğru sırayla yeniden ayağa kalktı. Files-01'de yeni dizin + systemd servis
güncellemesi elle (SSH ile) yapıldı, `journalctl` ile temiz başladığı doğrulandı.

## Bilinçli Kalan Sınırlar

- `app_policies`'e ayrı bir "public oluşturabilir mi" bayrağı yok — her `can_create` app'i ikisini de
  yapabilir.
- Gerçek bir UI/iş akışı public dosya oluşturmayı TETİKLEMİYOR — bu, sadece altyapı/API seviyesinde inşa
  edildi (planın açık kapsamı), YonetimApi/FlotaApi'nin mevcut upload formlarına `zone` seçeneği
  eklenmedi.
