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

## Bu Oturumda Bulunan Yeni Sorun (Testle Ortaya Çıktı)

**Arşivlenen dosya bazı durumlarda hâlâ erişilebilir kalabiliyor.**

`document`, `attachment`, `report` dosya tipleri veritabanında "multi-primary" olarak tanımlı (aynı anda birden fazla aktif dosya olabilir). Ama FlotaApi'nin `content`/`archive` uçları belirli bir `fileId` almıyor, sadece "bu ilişkinin aktif olanını getir" diyor. Test ettim: `test_arac_1` için bir `document` yükleyip arşivledim, sonra tekrar `.../document/content` istedim — **hâlâ `200 OK` ile bir dosya döndü** (muhtemelen daha önceden var olan başka bir aktif `document` kaydı). Yani multi-primary bir tipte "arşivle" dediğinizde, o tipin **tamamen** erişilemez hale geldiğinden emin olamıyorsunuz — hangi kaydın döneceği belirsiz.

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
