# Sistem Kontrol Listesi — Var / Yok

`+` = sistemde gerçekten var, çalışıyor (çoğu bu oturumda canlı test edildi) · `-` = yok, eksik, ya da bozuk

---

## Sunucular / Altyapı

+ api sunucu (192.168.64.5) ve files01 (192.168.64.3), iki ayrı UTM VM
+ Docker + docker-compose, tek `platform-net` ağı
+ 8 container: client, fileservice, flotaapi, gateway, keycloak, opsapi, postgres, yonetimapi
+ ufw firewall her iki sunucuda aktif (api sunucu: sadece 22+5090 açık; files01: sadece 2049 ve sadece api sunucusundan)
+ NFS mount (files01 → api sunucu) çalışıyor
- Container restart policy yok — VM/sunucu yeniden başlarsa hiçbir container kendiliğinden ayağa kalkmıyor
- NFS **read-only değil** — api sunucusu files01'e doğrudan yazabiliyor/silebiliyor (root gerekmeden)
- `export`/`staging` arasında izin ayrımı yok (aynı sahiplik, aynı rw izni)
- `docker-compose.override.yml` production'da bilinçli devre dışı ama biri elle `docker compose up` (dosya belirtmeden) yazarsa yanlışlıkla Keycloak/servis portlarını dışarı açabilir

## Gateway (nginx)

+ Tek dışa açık kapı: `5090:443`
+ HTTPS/TLS aktif (self-signed sertifika)
+ `/api/auth`, `/api/personnel` → YonetimApi
+ `/api/vehicles` → FlotaApi
+ `/ops/` → OpsApi
+ `/internal/` → 404 (FileServiceApi'ye dışarıdan hiç yol yok)
+ `/` → React SPA
+ Güvenlik header'ları (CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy)
+ 502/504 için özel JSON hata sayfaları
- Sertifika gerçek/güvenilir değil (self-signed, tarayıcı uyarı veriyor)
- HSTS yok (bilinçli, gerçek sertifika olmadığı için)

## Keycloak

+ `platform` realm'i statik JSON'dan (`realm-platform.json`) import ediliyor
+ 3 client: `frontend-test` (public, password grant), `yonetimapi`, `filoapi` (ikisi de client_credentials)
+ Roller: `personnel.files.{read,write}.{self,team,all}`, `ops.{read,execute,admin}`
+ 28 demo kullanıcı (hr001, adm001, m001-m003, p001-p024, opsadmin, opsuser01, fleetuser)
- Kalıcı veritabanı yok — container yeniden oluşunca (fresh recreate) state sıfırlanıyor, sadece `realm-platform.json`'daki kalıcı kalıyor
- Client secret'lar ve demo şifreler düz metin, secret rotasyonu yok

## Kimlik Doğrulama (Auth)

+ ROPC login, YonetimApi BFF üzerinden (Keycloak'a doğrudan gitmiyor)
+ `at`/`rt` HttpOnly cookie (Secure koşullu, SameSite=Strict)
+ Token refresh (`/api/auth/refresh`)
+ Logout (sadece local cookie temizleme)
+ Servis token (client_credentials, önbellekli, 30 sn erken-expire toleranslı)
+ mTLS — YonetimApi/FlotaApi ↔ FileServiceApi (CN allowlist + CA zinciri doğrulama)
+ `KeycloakBackchannelHandler` — YonetimApi, FileServiceApi, **ve FlotaApi'de (bu oturumda eklendi)**
+ FlotaApi cookie okuma — **bu oturumda eklendi, önceden hiç yoktu**

## RBAC — Personel (YonetimApi)

+ `read.self` / `read.team` / `read.all`, `write.self` / `write.all` rolleri (write.team bilinçli olarak yok)
+ `PermissionService` — kontrol sırası `all → team → self`
+ `yonetim.team_members` tablosuyla gerçek ekip sorgusu
+ fileId-ownership çift kontrol (kendi personelId'n üzerinden başkasının fileId'sine erişememe)
+ Personel arama endpoint'inin kendi scope-filtreli SQL'i

## Filo (FlotaApi)

+ `vehicle_id` JWT claim'i ile tek-araç erişim modeli (rol yok)
+ `HasVehicleAccess` — path'teki araç ID'si ile claim birebir eşleşmeli
- Araç için ayrı bir veritabanı tablosu yok (`filo.vehicles` yok, araç hiç "kayıt" değil)

## Veritabanı (`platformdb`)

+ 4 şema: `files`, `yonetim`, `filo`, `ops`
+ `files.objects`, `files.references`, `files.app_policies`, `files.relation_type_config`, `files.audit_events`
+ `yonetim.personnel` (25 kayıt), `yonetim.team_members`, `yonetim.audit_events`
+ `filo.audit_events`
+ `ops.audit_events`
+ `trg_check_single_primary` trigger (DB seviyesinde çift-primary engeli)
- `filo.vehicles` yok
- Audit tablolarının sadece `files.audit_events`'inde CHECK constraint var; `yonetim`/`filo`/`ops` audit'lerinde yok
- `files.references.relation_type` foreign key değil, serbest metin

## Dosya Depolama (FileServiceApi)

+ Sharding: `{domain}/{2hex}/{2hex}/{guid}.{ext}`
+ staging → export atomik taşıma (`File.Move`)
+ Upload sırasında SHA256 hesaplama ve doğrulama
+ Magic-byte kontrolü (pdf/jpg/png/webp) — **canlı test edildi, 415 doğru döndü**
+ Content-Type header kontrolü
+ Uzantı allow-list (relation type bazlı)
+ Boyut limiti (personel 10MB, filo 20MB) — **canlı test edildi, 413 doğru döndü**
+ Duplikasyon kontrolü (409)
+ ETag / 304 Not Modified — **canlı test edildi**
+ Range / 206 Partial Content — **canlı test edildi**
+ Content-Disposition (RFC 5987, Türkçe karakter desteği)
+ Path traversal koruması
+ Single/multi-primary kardinalite sistemi + DB trigger
+ Soft-archive (durum bayrağı) — **canlı test edildi, arşivlenen dosya kendi fileId'siyle 404, listede görünmüyor**
+ Eksik binary → 503 — **canlı test edildi** (dosya NFS'ten elle silindi, DB "var" diyordu, API doğru 503 döndü)
- Hard delete özelliği hiç yok (bilinçli, plan da bunu V1'de istemiyor)
- NFS read-only/publisher modeli yok (§ yukarıda)
- Multi-primary tiplerde (`document`/`attachment`/`report`) `archive` çağrısı `fileId` almıyor — hangi kaydın arşivleneceği belirsiz olabilir (güvenlik açığı değil, UX belirsizliği)

## Audit

+ `files.audit_events` — teknik, app bazlı — **canlı sorgulandı, gerçek kayıtlar görüldü**
+ `yonetim.audit_events` — domain, personel bazlı
+ `filo.audit_events` — domain, araç bazlı
+ `ops.audit_events` — ops konsolu, diğer 3'ünden bağımsız
+ `actor_ip` / `user_agent` zinciri (nginx → YonetimApi → FileServiceApi → audit tablosu)
+ Audit yazma hatası ana akışı bloklamıyor (try/catch + log)

## Ops Console / OpsApi

+ Docker socket'e erişimi yok (bilinçli) — host'un yazdığı durum dosyasından okuyor
+ `/ops/health`, `/ops/services`, `/ops/disk`, `/ops/backups`, `/ops/version`, `/ops/dashboard`, `/ops/me`
+ Container adı/CPU/RAM/restart/uptime görünümü — **canlı restart testiyle doğrulandı**
+ 5 dakikalık systemd timer (`services-status.sh`)
+ Rol bazlı erişim (`ops.read/execute/admin`), rolü olmayana 404 (varlığı bile sızdırılmıyor)
+ Sekme arka plandan öne gelince anında yenileme (`visibilitychange`, bu oturumda eklendi)
- Ops audit geçmişi ekranda gösterilmiyor (DB'de var, arayüzde yok)

## Backup / Restore

+ Günlük backup timer'ı
+ Haftalık restore-test timer'ı — **canlı tetiklendi, gerçek `sha256sum -c` ile her dosya "OK" verdi**
+ Saatlik disk doluluk kontrolü
- `restore-live.sh` "break glass" — otomatik pre-restore backup alma özelliği yok (V2)

## Client (React SPA)

+ Login, Dashboard, Personel/Filo/Ops sekmeleri (JWT claim'lerine göre otomatik görünürlük)
+ Cookie tabanlı auth (`credentials:'include'`, hiçbir yerde token saklanmıyor)
+ Upload ilerleme çubuğu (XHR ile gerçek zamanlı yüzde)
- URL tabanlı router yok — sayfa yenilenince/URL'e link yazılınca state korunmuyor
- Client-side dosya validasyonu yok (uzantı/boyut kontrolü tamamen sunucuda)

## Test / Otomasyon Script'leri

+ `server-smoke-test.sh` — temel uçtan uca akış
+ `server-safe-test-suite.sh` — genişletilmiş kontroller + **10 senaryolu 403 yetkilendirme matrisi (bu oturumda eklendi)**
+ `services-status.sh`, `disk-check.sh`, `backup-files01.sh`, `restore-test.sh`
- Cross-domain policy reddi testi yok (mimari olarak dışarıdan tetiklenemiyor, sadece kodla garanti)
- NFS bağlantısı koparsa ne olur — hiç canlı test edilmedi (riskli müdahale, kasıtlı yapılmadı)
