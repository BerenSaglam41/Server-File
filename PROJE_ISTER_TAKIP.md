# Sistemde Gerçekten Var Olan Her Şey — Doğrulanmış Envanter

Her madde ya kod okunarak ya da bu oturumda sunucuda gerçek bir istek/komutla kanıtlandı. Hiçbiri varsayım değil. `PROJECT_STATUS.md`'deki 31 "TAMAMLANDI" başlığının tamamı taranarak hazırlandı.

---

## 1. Ağ ve Erişim Kontrolü (en temelden başlayarak)

✅ Dışarıdan sisteme açık **tek bir port** var: gateway'in `5090` portu (HTTPS, container içi 443)
✅ Postgres, Keycloak, FileServiceApi, OpsApi, YonetimApi, FlotaApi, client — **hiçbiri** production'da doğrudan host'a açık değil (`ports:` tanımı yok)
✅ `docker-compose.override.yml` (dev portları: Keycloak 8080, FileService 5205, YonetimApi 5076, FlotaApi 5077) production'da bilinçli hariç tutuluyor (`docker compose -f docker-compose.yml`)
✅ `/internal/*` path'i nginx'te **404** — FileServiceApi'nin iç endpoint'leri dışarıya hiç açılmıyor
✅ ufw api sunucusunda aktif, sadece `22` (SSH) ve `5090` (gateway) izinli — **canlı test edildi**
✅ ufw files01'de aktif, `2049` (NFS) portu **sadece** `192.168.64.5`'ten kabul ediliyor — **canlı test edildi**: Mac'ten (`192.168.64.1`) gerçek NFS mount denemesi 4 dakika sonra "Operation timed out" ile reddedildi
✅ Tek Docker network (`platform-net`), servisler arası iletişim bunun üzerinden
✅ nginx Docker'ın kendi iç DNS'ini (`127.0.0.11`) kullanıyor — container restart sonrası IP değişse bile gateway yeniden çözebiliyor
✅ `.gitignore` ile `certs/*.key`, `certs/ca.srl`, `*.p12`, `*.pfx` commit'lenmekten korunuyor

## 2. Gateway (nginx)

✅ HTTPS/TLS aktif, `listen 443 ssl`, `TLSv1.2`/`TLSv1.3`, `HIGH:!aNULL:!MD5` cipher seti
✅ HTTP→HTTPS otomatik yönlendirme (`listen 80` → `301`)
✅ Güvenlik header'ları: `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy`, `Content-Security-Policy`
✅ `/api/auth/*`, `/api/personnel/*` → `yonetimapi:8080`
✅ `/api/vehicles/*` → `flotaapi:8080`
✅ `/ops/*` → `opsapi:8080`
✅ `/` → `client:80` (React SPA, SPA routing için `try_files`)
✅ `client_max_body_size` ayrı ayrı (personel 20m, filo 25m)
✅ `proxy_request_buffering off` / `proxy_buffering off` — büyük dosyalar tamponlanmadan stream ediliyor
✅ 502/504 için özel JSON hata sayfaları (upstream çökmesi / 120sn timeout)
✅ `= /health` → statik JSON (`{"status":"healthy","service":"Gateway-Nginx"}`)
✅ nginx image build tabanlı (`nginx/Dockerfile`), config değişince rebuild zorunlu — statik image değil

## 3. mTLS (Servis Kimliği)

✅ Kendi kendine imzalı CA (`platform-ca`, 10 yıl geçerli) — `openssl x509 -text` ile bizzat kontrol edildi
✅ `fileservice.crt` — sunucu sertifikası, CN=fileservice, SAN=fileservice+localhost
✅ `gateway.crt` — CN=gateway, SAN=gateway+localhost+127.0.0.1+192.168.64.5
✅ `yonetimapi.crt`, `filoapi.crt` — istemci sertifikaları, `clientAuth` extended key usage
✅ FileServiceApi Kestrel: `ClientCertificateMode.RequireCertificate` — sertifikasız bağlantı **kabul edilmiyor**
✅ CN allowlist: sadece `yonetimapi` ve `filoapi` isimli sertifikalar kabul ediliyor
✅ Sertifika zinciri doğrulaması `CustomRootTrust` ile sadece kendi CA'mıza güveniyor (sistemin genel CA listesine değil)
✅ YonetimApi ve FlotaApi, FileServiceApi'ye bağlanırken kendi istemci sertifikalarını sunuyor — **karşılıklı** doğrulama gerçekten çalışıyor
✅ `certs/generate-certs.sh` idempotent — sertifika zaten varsa yeniden üretmiyor (`FORCE_REGENERATE_CERTS=1` ile bilinçli rotasyon)
✅ Sertifika/anahtar uyuşmazlığında otomatik yedekleme (`backup-mismatch-<timestamp>` klasörüne taşıma)
✅ Gateway'in kendisi mTLS istemiyor (CA mount edilmemiş) — mTLS çemberi bilerek sadece `FileServiceApi ↔ {YonetimApi, FlotaApi}` arasında

## 4. Kimlik Doğrulama (Auth) Zinciri

✅ Kullanıcı login'i Keycloak'a **doğrudan değil**, YonetimApi BFF üzerinden (ROPC grant, `frontend-test` public client)
✅ `at`/`rt` **HttpOnly** cookie — JavaScript token'ı hiçbir zaman göremiyor
✅ `SameSite=Strict` — CSRF koruması
✅ `Secure` bayrağı koşullu (`IsHttps || X-Forwarded-Proto==https`)
✅ Token refresh (`POST /api/auth/refresh`) — süre dolmadan ~60 sn önce frontend otomatik tetikliyor
✅ Logout cookie'leri hem `/` hem `/api` path'inde temizliyor
✅ Servis kimliği (YonetimApi/FlotaApi → FileServiceApi) **ayrı** token — `client_credentials` grant
✅ Servis token'ı bellekte cache'li (30 sn erken-expire toleranslı, thread-safe double-checked locking)
✅ `KeycloakBackchannelHandler` — YonetimApi, FileServiceApi **ve FlotaApi'de** (FlotaApi'ye bu oturumda eklendi) — `KC_HOSTNAME=localhost` JWKS çözümleme sorununu düzeltiyor
✅ FlotaApi'nin `at` cookie'sinden token okuma kodu (bu oturumda eklendi) — **canlı test edildi**
✅ `MapInboundClaims = false` — özel claim isimleri (`personnel_id`, `vehicle_id`, `roles`) bozulmadan korunuyor

## 5. Keycloak

✅ `platform` realm'i statik `realm-platform.json`'dan import ediliyor (`start-dev --import-realm`)
✅ 3 client: `frontend-test` (public), `yonetimapi`, `filoapi` (confidential, service account)
✅ `yonetimapi`/`filoapi` client'larında sabit `app_code` claim mapper'ı
✅ 8 rol: `personnel.files.{read,write}.{self,team,all}` (write.team hariç), `ops.{read,execute,admin}`
✅ 28 demo kullanıcı — hepsi bu oturumda gerçek login ile test edildi
✅ `accessTokenLifespan: 300`, `ssoSessionIdleTimeout: 1800`
✅ `vehicle_id`/`personnel_id`/`roles` claim mapper'ları (`oidc-usermodel-attribute-mapper`, `oidc-usermodel-realm-role-mapper`)

## 6. RBAC — Personel (YonetimApi)

✅ Rol formatı `{kaynak}.{eylem}.{kapsam}` — kod ve Keycloak realm'i birebir aynı isimlendirme
✅ `PermissionService.CanReadAsync`/`CanWriteAsync` — sıra: `all → team → self`
✅ `yonetim.team_members` tablosuna gerçek SQL sorgusu (`IsTeamMemberAsync`)
✅ Personel arama endpoint'inin kendi scope-filtreli SQL'i
✅ fileId-ownership çift kontrol (`FileBelongsToPersonnelAsync`)
✅ **Canlı test:** m001 kendi ekibini (P001) görür → 200; başka ekibi (P008) göremez → 403; hiç yazma rolü yok, kendi ekibine bile yükleyemez → 403
✅ **Canlı test:** p001 kendini görür, başkasını (P002) göremez → 403; kendi kaydına bile yükleme yapamaz → 403
✅ **Canlı test:** p001, kendi personelId'si üzerinden P002'nin gerçek bir fileId'sine erişemiyor → 403 `file_scope_denied`

## 7. Filo (FlotaApi)

✅ Rol yok — sadece JWT'deki `vehicle_id` claim'i ile path'teki araç ID'si birebir eşleşmesi (`HasVehicleAccess`)
✅ **Canlı test:** fleetuser kendi aracını (test_arac_1) görür → 200, gerçek dosya yükler → 200, arşivler → 200
✅ **Canlı test:** fleetuser başka aracı (test_arac_2) göremez/yükleyemez → 403 `data_scope_denied`
✅ photo/document/official_document için metadata+content+upload+archive route'ları; attachment/report için sadece upload (multi-primary)

## 8. Veritabanı (`platformdb`, tek instance)

✅ 4 şema: `files`, `yonetim`, `filo`, `ops`
✅ `files.objects` — UUID PK, `relative_path` UNIQUE + format CHECK, `sha256` format CHECK, `status`/`classification` CHECK
✅ `files.references` — FK'li `file_id`/`app_code`, `is_primary`, `status` CHECK
✅ `files.app_policies` — `yonetimapi`→personnel (10MB), `filoapi`→fleet (20MB)
✅ `files.relation_type_config` — cv/photo/official_document=single, document/attachment/report=multi
✅ `files.audit_events` — CHECK constraint'li `action`/`result`, `actor_ip`/`user_agent` sütunları
✅ `trg_check_single_primary` — gerçek bir Postgres trigger, çift-primary'yi DB seviyesinde engelliyor
✅ `files.set_updated_at()` trigger fonksiyonu (`objects`/`app_policies` için)
✅ `yonetim.personnel` — 25 gerçek kayıt (HR001, ADM001, M001-3, P001-24)
✅ `yonetim.team_members` — 21 gerçek yönetici-personel ilişkisi
✅ `yonetim.audit_events`, `filo.audit_events`, `ops.audit_events` — üçü de ayrı, gerçek tablolar
✅ İndeksler: domain/status/sha256/created_by_app (`files.objects`), entity/app_code (`files.references`), personnel/actor/created (audit tabloları)
✅ Postgres healthcheck (`pg_isready`) tanımlı ve `service_healthy` koşuluyla diğer servislerin başlangıcını bekletiyor

## 9. Dosya Depolama (FileServiceApi) — Tek Tek Canlı Test Edildi

✅ Sharding: `{domain}/{ilk2hex}/{sonraki2hex}/{guid}.{ext}` — gerçek yüklenen dosyalarla doğrulandı
✅ staging → export atomik taşıma (`File.Move`, aynı dosya sistemi içinde rename)
✅ SHA256 hesaplama (diske yazılan gerçek içerikten) ve ETag olarak kullanılması
✅ Magic-byte kontrolü — **test edildi**: sahte içerikli `.pdf` → `415`
✅ Content-Type header kontrolü (uzantıyla eşleşmeli)
✅ Uzantı allow-list (relation type bazlı: cv/report=pdf, photo=jpg/jpeg/png/webp, diğerleri karışık)
✅ Boyut limiti — **test edildi**: 11MB dosya (10MB limitine karşı) → `413`
✅ Duplikasyon kontrolü (409, aynı SHA256 + aynı entity/relation)
✅ ETag/304 — **test edildi**: `If-None-Match` ile ikinci istek → `304`, disk okunmadı
✅ Range/206 — **test edildi**: `Range: bytes=0-5` → `206 Partial Content`, doğru `Content-Length`
✅ Path traversal koruması — normalize edilmiş path kök dizini aşamıyor
✅ Content-Disposition RFC 5987 (Türkçe karakter desteği), resimler `inline` diğerleri `attachment`
✅ Soft-archive — **test edildi**: arşivlenen dosya kendi fileId'siyle `404`, dosya listesinde hiç görünmüyor
✅ Eksik binary → 503 — **test edildi**: dosya NFS'ten elle silindi, DB "aktif" diyordu, API doğru şekilde `503` döndü
✅ Upload/DB tutarsızlığında rollback (DB kaydı başarısız olursa export'tan dosya silinir)
✅ `files.audit_events`'e her create/read/archive/denied yazılıyor — **doğrudan SQL ile sorgulanıp gerçek kayıtlar görüldü**
✅ `/health` endpoint'i — `.probe` dosyası + gerçek `SELECT 1` DB sorgusu
✅ **API cevaplarında `relative_path`/host/mount bilgisi hiç dönmüyor** — kod tarandı, `Results.Ok(new {...})` çağrılarının hiçbirinde `RelativePath` alanı yok (sadece `fileId/domain/relationType/contentType/extension/sizeBytes/classification/status/etag`); `file-catalog-model.md`'nin "API response Files-01 hostname, mount path veya relative path dönmez" isteği bire bir karşılanmış
✅ **Fiziksel path'te uygulama adı hiç geçmiyor** — path `{domain}/...` (personnel/fleet) ile başlıyor, `yonetimapi`/`filoapi` gibi bir uygulama ismi asla path'e yazılmıyor (`file-catalog-model.md`'nin "app isolation" kuralına uygun)

## 10. Ops Console / OpsApi

✅ Docker socket'e **hiç erişimi yok** — host'ta systemd timer'ının yazdığı JSON dosyasını okuyor (bilinçli güvenlik kararı)
✅ `/ops/health` — yonetimapi/flotaapi/keycloak/gateway/postgres'e **gerçek zamanlı** HTTP/DB sağlık kontrolü
✅ `/ops/services` — container/CPU/RAM/restart/uptime — **canlı restart testiyle doğrulandı**
✅ `/ops/disk`, `/ops/backups`, `/ops/version`, `/ops/dashboard`, `/ops/me`
✅ Rol bazlı erişim — `ops.read/execute/admin`, rolü olmayana **404** (403 değil, varlığı bile sızdırılmıyor)
✅ `opsuser01` (sadece read) yetkileri canlı test edildi: `read=true, execute=false, admin=false`
✅ Sekme arka plandan öne gelince anında yenileme (`visibilitychange` — bu oturumda eklenip test edildi)
✅ `X-Correlation-Id` header her `/ops/dashboard` cevabında var — canlı test edildi
✅ UI'da 6 kart: Servis Durumu, Disk Kullanımı, Uyarılar, Versiyon, Konteynerler, Yedekler

## 11. Audit Sistemi

✅ 4 bağımsız audit tablosu (`files`, `yonetim`, `filo`, `ops`) — hiçbiri diğerine bağımlı değil
✅ `actor_ip`/`user_agent` zinciri: nginx (`X-Real-IP`) → YonetimApi → FileServiceApi → audit tablosu
✅ Audit yazma hatası ana isteği bloklamıyor (try/catch + log, bilinçli tasarım)
✅ Ops audit'te `denied` kayıtları (`no_token`, `ops_role_missing`) canlı olarak DB'den sorgulanıp doğrulandı

## 12. Backup / Restore

✅ Günlük backup systemd timer'ı
✅ Haftalık restore-test timer'ı — **canlı tetiklendi**, gerçek `sha256sum -c` çalıştı, **her dosya için "OK"** çıktısı bizzat görüldü
✅ Saatlik disk doluluk kontrolü (`disk-check.sh`) — gerçek eşikler `WARN_PCT=80`, `CRIT_PCT=90` (koddan doğrulandı)
✅ `tools/install-backup-timers.sh` — 4 timer'ı (backup/restore-test/disk-check/services-status) tek seferde kuruyor
✅ `tools/configure-files01-nfs.sh` — files01 NFS export'unu production moduna göre yapılandırıyor

## 13. Client (React SPA)

✅ Login → Dashboard → rol/claim'e göre Personel/Filo/Ops sekmeleri (JWT claim'lerinden otomatik türetiliyor)
✅ Cookie tabanlı auth (`credentials:'include'`), hiçbir yerde token saklanmıyor (localStorage yok)
✅ XHR tabanlı upload ile gerçek zamanlı ilerleme çubuğu
✅ `PersonnelFileView`, `VehicleFileView`, `UploadModal`, `FileCard`, `OpsConsole` bileşenleri — hepsi gerçek, çalışan kod
✅ 401 alınca otomatik refresh+retry (sadece Ops Console'da, `opsFetch` içinde)
✅ RFC 5987 Content-Disposition'ı doğru parse edip Türkçe dosya adlarını gösteriyor

## 14. Deploy / Otomasyon Script'leri

✅ `setup-server.sh` — NFS production kontrolü, sertifika kontrolü, `docker compose up --build`, DB schema/seed, backup/disk sağlık raporu — hepsi tek komutta
✅ `server-smoke-test.sh` — temel uçtan uca akış, **defalarca canlı çalıştırıldı**
✅ `server-safe-test-suite.sh` — genişletilmiş kontroller + **10 senaryolu 403 yetkilendirme matrisi (bu oturumda eklendi)**, eşzamanlı 20 login testi dahil
✅ `services-status.sh`, `disk-check.sh`, `backup-files01.sh`, `restore-test.sh`, `restore-live.sh`, `migrate-legacy-files.py`
✅ `.env` / `.env.linux` / `.env.mac` — ortam bazlı config ayrımı

---

## Bu Turda Bulunan, Küçük Ama Gerçek Plan-Gerçek Farkları

- `files01-nfs-model.md`: "kapasite alarm eşikleri ilk hedef %70/%85/%95 (3 kademe)" diyor; gerçek kodda (`disk-check.sh`) sadece **2 kademe** var: `WARN_PCT=80`, `CRIT_PCT=90`.
- `files01-nfs-model.md`: "Files-01 diski 300 GB başlangıç kapasitesiyle izlenir" diyor; files01'in gerçek diski **9.8 GB** (`df -h` ile doğrudan doğrulandı) — bu bir test/demo VM'i olduğu için beklenen bir fark, ama plan metniyle birebir örtüşmüyor.
- `file-service-api-contract.md`: `X-Actor-Display` header'ından bahsediyor ("opsiyonel, secretsiz audit gösterimi"); kodda hiçbir yerde kullanılmıyor — sadece `X-Actor-User-Id` ve `X-Correlation-Id` var. Doküman bunu zaten "opsiyonel" dediği için bu bir eksik değil, sadece hiç kullanılmamış bir seçenek.

*Bu envanterin karşılığı olan diğer eksik/olmayan şeyler (`-` işaretliler) için: NFS read-only değil, container restart policy yok, `filo.vehicles` tablosu yok, secret rotasyonu yok, client router'ı yok — bunlar önceki oturumlarda ayrıca kayıt altına alınmıştı.*

---

## Güvenlik Taraması — Yeni Bulunan 6 Sorun (Henüz Düzeltilmedi, Kayıt Amaçlı)

Bu oturumda ek bir güvenlik taraması yapıldı: bağımlılık açıkları, container ayrıcalıkları, brute-force koruması, NFS mount seçenekleri, healthcheck kapsamı, log rotasyonu, SQL injection.

- ❌ **`Microsoft.OpenApi 2.0.0` paketinde bilinen YÜKSEK önem dereceli güvenlik açığı** (`GHSA-v5pm-xwqc-g5wc`) — hem `YonetimApi` hem `FlotaApi`'de. **Kanıt:** `dotnet list package --vulnerable --include-transitive` ile doğrudan tarandı.
- ❌ **6 container da (YonetimApi, FlotaApi, FileServiceApi, OpsApi, client, gateway) root olarak çalışıyor** — hiçbir Dockerfile'da `USER` direktifi yok. **Kanıt:** tüm Dockerfile'lar tek tek okundu.
- ❌ **Keycloak brute-force koruması tanımlı değil** — `realm-platform.json`'da `bruteForce` ayarı hiç yok, yanlış şifre denemelerine karşı kilitleme/gecikme mekanizması çalışmıyor.
- ❌ **NFS mount'ta `nosuid`/`nodev`/`noexec` seçenekleri yok** — zaten `rw` olan mount'a (bkz. yukarıdaki NFS read-only bulgusu) ek bir savunma katmanı da eksik. **Kanıt:** `mount` çıktısı sunucuda doğrudan okundu.
- ❌ **8 servisin sadece 2'sinde (postgres, keycloak) Docker healthcheck tanımlı** — YonetimApi/FlotaApi/FileServiceApi/OpsApi/client/gateway'in "process ayakta ama uygulama donmuş" durumunu Docker seviyesinde tespit eden hiçbir mekanizma yok.
- ❌ **Docker log rotasyonu hiç ayarlanmamış** (`logging:`/`max-size`/`max-file` yok) — container logları teorik olarak sınırsız büyüyüp disk doluluğuna katkıda bulunabilir (bu oturumda zaten bir disk doluluğu sorunu yaşandı, farklı bir sebepten).
- 🔶 (küçük, düşük öncelik) nginx CSP'de `style-src` için `'unsafe-inline'` kullanılıyor — CSP'yi hafifçe zayıflatıyor.

**Olumlu kontrol sonucu:** SQL injection taraması **temiz** — tüm veritabanı sorguları (YonetimApi, FlotaApi, FileServiceApi) parametreli (`$1,$2...` + `Parameters.AddWithValue`), dinamik SQL seçimi bile (personel arama endpoint'i) sabit literal string'ler arasından yapılıyor, kullanıcı girdisi hiçbir zaman sorgu metnine karışmıyor.
