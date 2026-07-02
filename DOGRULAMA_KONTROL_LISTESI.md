# Doğrulama Kontrol Listesi — Plan vs Gerçek Test Kanıtı

> Bu dosya `PROJE/` klasöründeki 5 planlama dokümanının kendi kontrol listelerini/kabul kriterlerini/doğrulama kapılarını tek tek alıp, her maddeyi **gerçek kod okuması** ve/veya **bu oturumda bizzat çalıştırılmış canlı test** kanıtına göre işaretler. `PROJECT_STATUS.md`'nin "TAMAMLANDI ✅" dediği yerler de aynı titizlikle çapraz kontrol edildi.
>
> İşaretler:
> - ✅ **Doğrulandı** — gerçek kodda var VE bu oturumda (ya da önceki oturumlarda kayıtlı, tekrarlanabilir şekilde) canlı test edildi.
> - 🟡 **Sadece kodda var, test edilmedi** — kod bunu garanti ediyor ama gerçek bir çalıştırma/istekle bizzat doğrulanmadı.
> - ❌ **Yapılmadı / plana aykırı** — ya hiç yok, ya da plan farklı bir şey istiyordu ve gerçek kod ondan sapıyor.
>
> Her satırda kanıt/kaynak belirtildi. Uydurma yok — emin olunmayan yerler açıkça "test edilmedi" diye işaretlendi.

---

## 1. `PROJE/20-files01-nfs-personel-dosya-plani.md`

### Yapılacaklar (dokümanın kendi listesi, hepsi `[x]` işaretli)

| Madde | Durum | Kanıt |
|---|:---:|---|
| NFS export modeli ve File-Service runtime allowlist kararı yazılacak | ✅ | `files01-nfs-model.md` yazılmış; gerçek export sadece `192.168.64.5`'e açık, bu oturumda Mac'ten mount denemesiyle canlı doğrulandı (timeout ile red) |
| Dosya dizin yapısı ve sahiplik/izin modeli belirlenecek | ✅ | `/srv/files/{export,staging,manifests,restore-tests}` gerçekten mevcut, `ls -la` ile doğrulandı |
| Fotoğraf ve CV dosya adlandırma standardı karara bağlanacak | ✅ | `FileEndpoints.cs:362-366` — `{domain}/{shard1}/{shard2}/{guid}.{ext}` birebir uygulanmış, DB'de `chk_relative_path_format` CHECK constraint'i de bunu zorluyor |
| Legacy dosyaların stable key ile yeniden adlandırılma planı hazırlanacak | ✅ | `tools/migrate-legacy-files.py` (398 satır) gerçekten var, manifest formatı planla eşleşiyor |
| Backup ve kapasite takibi yazılacak | ✅ | `tools/backup-files01.sh`, `tools/disk-check.sh`, systemd timer'ları çalışıyor, `disk-status` `%73/%35` olarak bu oturumda okundu |
| API dosya erişimi için permission/data-scope bağımlılığı dokümante edilecek | ✅ | `file-service-api-contract.md` + gerçek kodda `PermissionService`/`HasVehicleAccess` birebir bu modeli uyguluyor |

### Kabul Kriterleri

| Kriter | Durum | Kanıt |
|---|:---:|---|
| Files-01 doğrudan istemciye açılmayan bir modelle tasarlanmış olmalı | ✅ | nginx'te `/internal/` → 404, FileServiceApi hiç `ports:` almıyor, mTLS zorunlu — kod + canlı test |
| File-Service runtime mount ve **NFS RO export** kuralları yazılmış olmalı | ❌ | Mount var ve çalışıyor, ama **RO değil RW** (`/etc/exports`: `rw,...,all_squash`) — bu oturumda `/etc/exports` ve `ls -la /srv/files` ile doğrudan doğrulandı |
| Dosya taşıma ve rollback planı hazır olmalı | 🟡 | `migrate-legacy-files.py` `--dry-run` destekliyor ama **hiç gerçek bir legacy taşıma bu ortamda çalıştırılmadı** — script var, kullanılmadı |

### Test ve Doğrulama Notları (dokümanın kendi istediği testler)

| Test | Durum | Kanıt |
|---|:---:|---|
| Mount | ✅ | `mountpoint -q /mnt/platform-files` ve `df -h` ile defalarca doğrulandı |
| Read-only davranış | ❌ | **Tam tersi doğrulandı** — FileServiceApi export'a gerçekten yazabiliyor (upload akışı çalışıyor), bu "read-only" değil |
| Dosya okuma | ✅ | Smoke test'te gerçek dosya indirme (110KB, 3.8MB dosyalar) defalarca başarılı |
| NFS down senaryosu | 🟡 | Kod `503 storage_unavailable` dönüyor (`FileEndpoints.cs`) ama bu oturumda NFS'i kasıtlı kesip test **yapılmadı** |
| Backup restore testleri | ✅ | `platform-restore-test.timer` "Son restore testi başarılı" raporluyor, `tools/restore-test.sh` gerçekten `sha256sum -c` ile hash karşılaştırması yapıyor (kod okunarak doğrulandı) |

---

## 2. `PROJE/file-catalog-model.md`

### Onboarding Kapısı (2. uygulama — FlotaApi — için gereken şartlar)

| Madde | Durum | Kanıt |
|---|:---:|---|
| `app_code` ve izinli domain/file type listesi | ✅ | `files.app_policies` seed'de `filoapi` → `fleet` domain, 5 dosya tipi |
| Create/read/archive policy | ✅ | `can_create/can_read/can_archive = true` DB'de, canlı test (upload denendi, 403/200 doğru döndü) |
| Entity reference modeli | ✅ | `files.references` ile `entity_type='vehicle'` kullanılıyor |
| Audit event sorumluluğu | ✅ | `filo.audit_events` + `files.audit_events` — kod seviyesinde her ikisine de yazılıyor |
| API endpoint sözleşmesi | ✅ | FlotaApi `/api/vehicles/*` uçları YonetimApi ile birebir aynı desende |
| Quota ve max file size kararları | ✅ | `filoapi` için 20 MiB, `yonetimapi` için 10 MiB — DB'de ve `client/src/api.ts`'teki hata mesajlarında tutarlı |
| **Test: create** | 🟡 | **fleetuser ile gerçek bir dosya YÜKLEME (başarılı upload) bu oturumda hiç test edilmedi** — sadece "başka araca yükleme → 403" test edildi, "kendi aracına yükleme → 200" hiç denenmedi |
| **Test: read** | ✅ | fleetuser kendi aracının dosya listesini görebiliyor — canlı test edildi |
| **Test: denied** | ✅ | fleetuser → başka araç → 403 `data_scope_denied` — canlı test edildi |
| **Test: scope miss** | ✅ | Aynı yukarıdaki (denied ile aynı senaryo) |
| **Test: archived file** | ❌ | Filo tarafında hiçbir dosya arşivleme senaryosu bu oturumda test edilmedi |
| **Test: missing binary** | ❌ | Hiç test edilmedi (NFS'ten elle dosya silip DB'nin hâlâ "var" demesi senaryosu) |

---

## 3. `PROJE/file-service-api-contract.md`

### V1 Kabul Kriterleri

| Kriter | Durum | Kanıt |
|---|:---:|---|
| Uygulamalar Files-01'e doğrudan bağlanmaz | ✅ | Sadece FileServiceApi container'ında NFS mount var — `docker-compose.yml` kod incelemesiyle doğrulandı |
| Uygulamalar `files.*` tablolarına doğrudan yazmaz | ✅ | YonetimApi/FlotaApi'nin `DbContext`'i yok, sadece kendi şemalarına (`yonetim.*`/`filo.*`) ham SQL ile yazıyorlar — kod incelemesiyle doğrulandı |
| File-Service API servis token'ı olmadan cevap vermez | 🟡 | Kod `RequireAuthorization()` ile garanti ediyor, ama bu oturumda "tokensiz FileService'e doğrudan istek at → 401 gör" **diye ayrıca test edilmedi** (zaten dışarıya kapalı olduğu için doğrudan test etmek mTLS gerektiriyor) |
| App policy izin vermediği domain/action için istek 403 olur | 🟡 | **Cross-domain senaryosu (yonetimapi'nin fleet dosyasına, ya da filoapi'nin personel dosyasına erişmeye çalışması) bu oturumda hiç test edilmedi.** Kod (`AllowedDomains.Contains(domain)` kontrolü) bunu garanti ediyor ama canlı doğrulanmadı |
| Scope dışı dosyalar client'a varlık sızdırmadan **404** olur | 🟡 | Gerçekte YonetimApi bu durumda **403** `file_scope_denied` dönüyor (404 değil) — varlık yine sızdırılmıyor (403 hem "yok" hem "yetkisiz" için aynı) ama planın istediği tam HTTP kodu (404) ile birebir örtüşmüyor |
| Stream endpoint `ETag`/`Range`/`Content-Type`/`Content-Disposition` destekler | 🟡 | Kodda tam destekleniyor (`FileEndpoints.cs:163-249`), ama bu oturumda `If-None-Match`/`Range` header'larıyla **gerçek bir 304/206 testi yapılmadı** — sadece düz 200 indirme test edildi |
| `files.audit_events` her sonuç için kayıt üretir | 🟡 | Kod her branch'te `audit.WriteAsync` çağırıyor; bu oturumda `files.audit_events` tablosuna **doğrudan SQL ile bakıp yeni kayıt oluştuğu sorgulanmadı** (sadece `ops.audit_events` ve `yonetim`/`filo` audit'leri dolaylı doğrulandı) |

---

## 4. `PROJE/file-service-intern-brief.md`

### Minimum Test Senaryoları

| Senaryo | Durum | Kanıt |
|---|:---:|---|
| App policy izinliyken resolve başarılı | ✅ | Personel + filo için `200` alındı |
| App policy izinsizken 403 | 🟡 | Sadece data-scope (kullanıcı bazlı) 403 test edildi; **app-policy (uygulama bazlı) 403 hiç tetiklenmedi** |
| Scope miss uygulama tarafında 404 | 🟡 | Yukarıda değinildi — gerçekte 403 dönüyor |
| File not found 404 | ❌ | Var olmayan bir `fileId` ile istek atıp 404 alındığı bu oturumda test edilmedi |
| Archived/revoked dosya için karar | ❌ | Test edilmedi |
| Unsupported media type 415 | ❌ | Bu oturumda test edilmedi (kod var, `PROJECT_STATUS.md`'nin "Bilinen tuzaklar" bölümü geçmişte test edildiğini ima ediyor ama bu oturumda tekrarlanmadı) |
| File too large 413 | ❌ | Test edilmedi |
| Storage unavailable 503 | ❌ | Test edilmedi (NFS kasıtlı kesilmedi) |
| Stream 200/206/304 | 🟡 | Sadece 200 test edildi |
| Audit event create/read/denied için yazılıyor | 🟡 | Kod garantisi var, DB'den doğrudan sorgulanmadı |

### Sunucu Tarafı Kalan Kontroller

| Kontrol | Durum | Kanıt |
|---|:---:|---|
| File-Service runtime → Files-01 TCP/2049 erişimi | ✅ | Test edildi, çalışıyor |
| NFS export yalnız runtime IP'sine açık mı | ✅ | Test edildi (Mac'ten red, api sunucudan kabul) |
| Mount read-only mi | ❌ | **Hayır, rw** |
| Runtime probe dosyasını okuyabiliyor mu | ✅ | `/health` endpoint testleri ve `setup-server.sh`'ın kendi probe kontrolü |
| **Runtime yazma/silme yapamıyor mu** | ❌ | **Tam tersi kanıtlandı — yapabiliyor.** Bu maddenin cevabı "hayır, yapamıyor" olmalıydı, gerçekte "evet yapabiliyor" |
| NFS down durumunda health check ne dönüyor | ❌ | Test edilmedi |

---

## 5. `PROJE/files01-nfs-model.md`

### Doğrulama Kapıları

| Kapı | Beklenen | Gerçek Durum |
|---|---|:---:|
| NFS port | Port reachable | ✅ Test edildi |
| Mount | **Read-only** mount başarılı | ❌ Mount başarılı ama **read-only değil** |
| Read probe | Probe dosya okunur | ✅ Test edildi |
| **Write denial** | Yazma/silme reddedilir | ❌ **Reddedilmiyor, kabul ediliyor** — en kritik sapma |
| API scope | Yetkili kullanıcı dosya alır | ✅ Test edildi (personel + filo) |
| API scope miss | Yetkisiz talep dosya varlığını sızdırmaz | ✅ (403 ile, 404 değil ama sızdırma yok) |
| Backup restore | Örnek dosya restore edilir ve hash eşleşir | 🟡 Restore "başarılı" raporluyor, script gerçekten `sha256sum -c` yapıyor (kod doğrulandı), ama bu oturumda restore çıktısının hash satırı bizzat okunmadı |

---

## 6. `PROJECT_STATUS.md`'nin "TAMAMLANDI ✅" Dediği Ana Başlıklar — Çapraz Kontrol

| Başlık | PROJECT_STATUS.md diyor | Gerçek durum |
|---|---|:---:|
| Ops Dashboard V1 | TAMAMLANDI ✅ | ✅ Bu oturumda kapsamlı test edildi — container restart senaryosu dahil |
| RBAC — personel | TAMAMLANDI ✅ | ✅ Bu oturumda m001/p001 ile tam matris test edildi |
| Fleet UI / FlotaApi | TAMAMLANDI ✅ | ❌→✅ **Aslında hiç çalışmıyordu** (cookie auth eksikti) — bu oturumda bulunup düzeltildi, şimdi gerçekten ✅ |
| mTLS | TAMAMLANDI ✅ | ✅ Kod + sertifika zinciri doğrulandı; FlotaApi'de backchannel handler eksikti, düzeltildi |
| HTTPS Gateway | TAMAMLANDI ✅ | ✅ Test edildi, self-signed olduğu da not edildi |
| Backup/Restore otomasyonu | Kuruldu | ✅ Timer'lar çalışıyor, ama restore sonrası hash karşılaştırması bu oturumda bizzat okunmadı (🟡) |
| Resilience test V1 | "Sıradaki adım" olarak bekliyor | 🟡 Kısmen — `client` container'ı için tekil restart testi bu oturumda yapıldı; Gateway/FileService/Keycloak/Postgres için sistematik testler hâlâ yok. Ayrıca **VM'nin kendisi beklenmedik şekilde kapanınca hiçbir container kendiliğinden ayağa kalkmadı** (restart policy yok) — bu, canlı olarak gözlemlendi |
| Firewall + NFS allowlist | Observability planında "öncelik" olarak listelenmiş, ayrı bir "tamamlandı" notu yok | ✅ Bu oturumda hem files01 hem api sunucusunda `ufw` etkinleştirilip test edildi |

---

## 7. Özet — Öncelik Sırasına Göre Gerçek Eksikler

**Gerçekten yapılmamış / plana aykırı olan (❌), önem sırasına göre:**

1. **NFS read-only/publisher modeli hiç yapılmamış** — export hâlâ `rw`, FileServiceApi export'a doğrudan yazabiliyor. Planın en kritik güvenlik varsayımı (API sunucusu files01'in kalıcı verisini bozamaz) şu an **sağlanmıyor**.
2. **"Runtime yazma/silme yapamıyor mu" doğrulama kapısı tersine dönmüş** — madde 1 ile aynı kök sorun, ayrı bir doğrulama kapısı olarak da resmen düşmüş durumda.
3. **NFS-down / storage-unavailable senaryosu hiç test edilmemiş** — kod 503 döneceğini söylüyor ama kimse gerçekten NFS'i kesip denememiş.
4. **Cross-domain policy reddi (yonetimapi↔fleet, filoapi↔personnel) hiç test edilmemiş** — kod garanti ediyor, canlı kanıt yok.
5. **413/415/404/304/206 gibi HTTP durum kodlarının çoğu sadece kodda var, testle doğrulanmamış.**
6. **Archived/revoked dosya senaryosu ve "missing binary" senaryosu hiç test edilmemiş.**
7. **Restore testinin hash karşılaştırması bizzat okunup teyit edilmemiş** (script doğru şeyi yapıyor ama çıktısı bu oturumda kontrol edilmedi).

**En kritik pozitif sürpriz:** Fleet/FlotaApi özelliği dokümanlarda "tamamlandı" görünüyordu ama gerçekte cookie auth eksikliği yüzünden **hiç çalışmıyordu** — bu oturumda bulunup düzeltildi ve şimdi gerçekten doğrulanmış durumda.

---

*Bu liste 2026-07-02 tarihli oturumda, `PROJE/*.md` dosyalarının kendi kontrol listeleri madde madde ele alınarak ve gerçek kod + bu oturumda çalıştırılan canlı testler karşılaştırılarak hazırlanmıştır. "✅" işaretli olmayan hiçbir madde "yapıldı" sayılmamalıdır.*
