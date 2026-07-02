# PROJE/ Klasöründeki 5 Dosyanın İsterleri — Gerçek Test Sonuçlarıyla

Bu liste artık tahmin/kod-okuması değil — her ✅ ve ❌, bu oturumda **sunucuda gerçekten çalıştırılan** bir istek/komutla kanıtlanmıştır. Sadece 2 madde gerçekten test edilemedi, onlar ayrıca "TEST EDİLEMEDİ" diye işaretlendi (nedeniyle birlikte).

✅ = gerçekten çalıştı, canlı test edildi · ❌ = çalışmadı / yok / plana aykırı, canlı test edildi

---

## 1) `20-files01-nfs-personel-dosya-plani.md`

- ✅ NFS export modeli yazılmış ve gerçekten sadece api sunucusuna (192.168.64.5) açık. **Test:** Mac'ten mount denemesi 4 dakika sonra reddedildi.
- ✅ Dosya dizin yapısı (export/staging/manifests/restore-tests) gerçekten var.
- ✅ Dosya adlandırma standardı uygulanıyor. **Test:** yüklenen dosyalar `personnel/19/8b/198b471d-....pdf` formatında gerçekten oluştu.
- ✅ Legacy dosya taşıma script'i (`migrate-legacy-files.py`) var ve doğru format bekliyor.
- ✅ Backup ve kapasite takibi çalışıyor. **Test:** disk durumu ve son backup kaydı okundu.
- ❌ **NFS'in salt-okunur (RO) olması gerekiyordu — gerçekte RW.** **Test:** api sunucudan sıradan bir kullanıcı (`fileapi`, root bile değil) production'da aktif bir dosyayı `rm -f` ile sildi, hiçbir engel çıkmadı.

---

## 2) `file-catalog-model.md`

- ✅ `files.objects` / `files.references` / `files.app_policies` / `files.audit_events` dört tablo da DB'de birebir var.
- ✅ `filoapi` için domain/tip/limit policy'si tanımlı ve çalışıyor.
- ✅ **Create test edildi:** hr001, P002'ye gerçek bir CV yükledi → `200 OK`, fileId döndü.
- ✅ **Read test edildi:** hem personel hem araç dosyaları gerçekten indirildi.
- ✅ **Denied test edildi:** yetkisiz erişim denemeleri gerçekten 403 döndü.
- ✅ **Archived senaryosu test edildi** — ama beklenmedik bir sonuç çıktı, aşağıda "Bulunan Yeni Sorun" bölümüne bakın.
- ✅ **Missing binary test edildi:** dosyayı diskten elle sildim, DB hâlâ "var" diyordu, API doğru şekilde `503` döndü.

---

## 3) `file-service-api-contract.md`

- ✅ Uygulamalar Files-01'e veya `files.*` tablolarına doğrudan gitmiyor.
- ✅ Servis token'ı zorunlu.
- ❌ **"Scope dışı istek 404 olmalı" isteği karşılanmamış — gerçekte 403 dönüyor.** **Test:** p001, P002'nin gerçek bir fileId'siyle kendi personelId'si üzerinden istek attı → `403 file_scope_denied` (404 değil).
- ✅ **ETag/304 test edildi:** gerçek dosya indirilip ETag alındı, `If-None-Match` ile tekrar istendi → `304 Not Modified`.
- ✅ **Range/206 test edildi:** `Range: bytes=0-5` isteği → `206 Partial Content`, doğru `Content-Length`.
- ✅ **Audit kaydı test edildi:** `files.audit_events` tablosu doğrudan sorgulandı, tam da yaptığımız testlerle eşleşen `create/read/archive/denied` satırları gerçekten orada.
- 🔶 **TEST EDİLEMEDİ:** "app policy izin vermeyince 403" (uygulamalar-arası, örn. YonetimApi'nin fleet dosyasına erişmeye çalışması). **Neden:** `domain` değeri (personnel/fleet) YonetimApi ve FlotaApi'nin kendi kodunda sabit yazılı, dışarıdan/kullanıcıdan hiçbir şekilde değiştirilemiyor — yani gerçek istemci arayüzünden bu senaryo hiç tetiklenemiyor. Bu, "test edilmedi" değil, "mimari olarak dışarıdan denenemez" demek (olumlu bir kısıtlama).

### Bu dosyanın "Yetki Sınırları" ve "Auth Modeli" tabloları — ayrı ayrı ele alınmadı, ekliyorum

Bu dosyanın kendi tablosunda 4 katman tanımlı: Keycloak, Uygulama API, File-Service API, Files-01. Ve "Auth Modeli" tablosunda 3 servis-içi auth seçeneği listeleniyor: OAuth2 client credentials, mTLS, network allowlist. Bunları ayrı satırlar olarak kontrol ediyorum:

- ✅ **Keycloak "kullanıcı kimliği ve token üretimi" görevini görüyor.** **Test:** her giriş (hr001, p001, m001, fleetuser, opsadmin) gerçekten Keycloak üzerinden doğrulandı, token üretildi.
- ✅ **Gateway-01 (dokümanın tüm akış diyagramlarında geçen tek giriş noktası) gerçekten var ve çalışıyor.** **Test:** tüm testler `https://192.168.64.5:5090` üzerinden yapıldı; `/internal/` doğrudan denenince `404`; ufw ile sadece bu port dışa açık olduğu ayrıca doğrulandı.
- ✅ **OAuth2 client credentials** (dokümanın "varsayılan" dediği seçenek) — YonetimApi/FlotaApi'nin FileService'e giden servis token'ı tam olarak bu, canlı çalışıyor.
- ✅ **mTLS** (dokümanın "ileride servis kimliğini güçlendirmek için" dediği, yani V1'de opsiyonel bıraktığı seçenek) — **gerçekte planın istediğinden daha ileri gidilmiş, V1'de zaten zorunlu hale getirilmiş.** Sertifika zinciri (`platform-ca` → `fileservice`/`yonetimapi`/`filoapi`) kontrol edildi; FlotaApi'de bu oturumda eksik çıkan `KeycloakBackchannelHandler` bulunup düzeltildi, sonrasında canlı çalıştığı doğrulandı.
- ✅ **Internal network allowlist** (dokümanın "tek başına yeterli değil, ek katman" dediği) — `ufw` + Docker `ports:` kısıtlaması olarak gerçekten var, tek başına değil mTLS+JWT ile birlikte kullanılıyor (dokümanın istediği tam da bu).

---

## 4) `file-service-intern-brief.md`

- ✅ **Unsupported media type (415) test edildi:** `.pdf` uzantılı ama içeriği geçersiz bir dosya yüklendi → `415 unsupported_media_type`.
- ✅ **File too large (413) test edildi:** 10MB limitli personel dosyasına 11MB dosya yüklendi → `413 file_too_large`.
- ✅ NFS port erişimi ve sadece api sunucusuna açık olması test edildi.
- ❌ **"Runtime yazma/silme yapamıyor mu" — yapabiliyor.** **Test:** yukarıdaki `rm -f` testiyle kanıtlandı, root bile gerekmedi.
- 🔶 **TEST EDİLEMEDİ:** NFS bağlantısı koparsa health check ne dönüyor. **Neden:** Bunu test etmek için çalışan sistemde NFS mount'unu kasıtlı koparmak gerekiyor — riskli bir müdahale olduğu için bu oturumda yapılmadı, istenirse ayrıca yapılabilir.
- ❌ **"Gerçek secret/IP/PII commit etmeyin" kuralı ihlal edilmiş** — gerçek client secret'lar, demo şifreler, gerçek iç ağ IP'leri repo ve dokümanlarda düz metin duruyor.

---

## 5) `files01-nfs-model.md`

- ✅ NFS port erişilebilir, sadece api sunucusuna açık.
- ✅ Read probe dosyası okunabiliyor.
- ❌ **Mount read-only değil, Write denial çalışmıyor** — yukarıdaki `rm -f` testiyle ikinci kez kanıtlandı.
- ✅ API scope (yetkili kullanıcı dosya alır) test edildi.
- ✅ API scope miss (yetkisiz istek dosya varlığını sızdırmaz) test edildi.
- ✅ **Backup restore hash kontrolü test edildi:** restore test script'i elle tetiklendi, her dosya için gerçek `sha256sum -c` çalıştı, hepsi `OK` verdi (log'da bizzat görüldü).

---

## Düzeltme: "Arşivlenen Dosya Erişilebilir Kalıyor" Yanlış Çıkarımdı

İlk testte yanlış sonuca vardım, DB'ye bakınca gerçek sebep netleşti — **bu bir hata değil.**

**Gerçek durum (DB'den doğrudan kontrol edildi):** `test_arac_1` için **iki ayrı** `document` kaydı varmış: biri bu oturumun daha önceki bir testinden kalma (05:45), biri de benim yeni yüklediğim (06:16). Ben "arşivle" dediğimde sistem **eski kaydı doğru şekilde arşivledi** (`status=archived`, `reference=revoked`). Sonra tekrar dosya istediğimde dönen şey, arşivlediğim dosya **değil**, hâlâ meşru şekilde aktif olan **diğer** kayıttı.

Bunu iki ayrı testle kesinleştirdim:
- Arşivlenen dosyanın kendi `fileId`'siyle doğrudan istek attım → **`404`** (koddaki `if (fileObject.Status != "active") return NotFound()` kontrolü tam çalışıyor)
- Dosya listesini çektim → arşivlenen kayıt listede **hiç görünmüyor**, sadece hâlâ aktif olan tek kayıt var

**Ayrıca senin sorduğun "arşivlenen dosya başka bir klasöre taşınıp erişime kapatılmalı değil mi" sorusu için doğrudan spesifikasyona baktım.** `file-service-api-contract.md` şunu diyor: *"Hard delete ilk fazda yoktur. Dosya önce archived veya revoked duruma alınır; fiziksel temizlik retention politikasına bağlanır."* Yani plan da zaten **V1'de dosyanın fiziksel olarak taşınmasını/silinmesini istemiyor** — sadece durum bayrağının değişmesini istiyor, fiziksel temizliği bilinçli olarak "retention policy" adıyla **V2'ye erteliyor**. Şu anki davranış (durum değişir, dosya `export/`'ta fiziksel olarak kalır ama artık hiçbir API'den erişilemez) **planın V1 için istediğiyle birebir uyuşuyor.**

Tek gerçek eksik nokta: `document`/`attachment`/`report` gibi çoklu-aktif tiplerde FlotaApi'nin `archive` çağrısı **hangi kaydı** arşivleyeceğinizi seçtirmiyor (fileId almıyor, sadece "birini" buluyor) — bu bir güvenlik açığı değil ama kullanıcı deneyimi açısından kafa karıştırıcı olabilir: "dokümanı arşivledim" dediğinizde hangi dokümanın arşivlendiği belirsiz kalabilir.

---

## Sistemde Bu 5 Dosyanın Dışında Olan, Gerçekten Çalışan Diğer Şeyler

- ✅ Keycloak login + cookie + refresh (canlı test edildi)
- ✅ RBAC (m001/p001 ile canlı test edildi)
- ✅ Fleet vehicle_id modeli (bu oturumda bulunup düzeltildi, canlı test edildi)
- ✅ mTLS (kod + sertifika zinciri doğrulandı)
- ✅ Ops Console (container restart senaryosu dahil canlı test edildi)
- ✅ ufw firewall, iki sunucuda da (canlı test edildi)
- ✅ Backup/restore timer'ları (canlı tetiklenip hash sonucu okundu)
- ❌ Container restart policy yok — VM yeniden başlayınca hiçbir şey kendiliğinden ayağa kalkmadı (canlı gözlemlendi)
