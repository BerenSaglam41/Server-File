# Kanıt: Faz A — Güvenlik Sertleştirme (Platform Mimarisi Rehberi Hizalaması)

**Tarih:** 2026-07-03
**Kapsam:** `PROJE/platform-mimarisi-stajyer-rehberi.txt` belgesiyle hizalamanın ilk fazı — 4 madde
**Durum:** ✅ Hepsi tamamlandı, gerçek testlerle doğrulandı, tam regresyon temiz

---

## Bağlam

Kullanıcı, organizasyonundan gelen bir hedef mimari belgesini (`PROJE/platform-mimarisi-stajyer-rehberi.txt`)
mevcut kodla karşılaştırmamı istedi. Belge, 5 sunuculu/LDAP'lı/ExternalHub'lı çok daha büyük bir kurumsal
platform tanımlıyor — kullanıcı mevcut 2 UTM sunuculu altyapıda kalmaya, ama belgedeki **özellik seviyesindeki**
gereksinimleri tamamlamaya karar verdi. Bir Plan agent'ı belgeyi (662 satır) gerçek kodla satır satır
karşılaştırdı; ben de kritik bulguları (özellikle `ArchiveFileAsync`) doğrudan okuyarak teyit ettim.

Ortaya çıkan aşamalı plan: Faz A (hızlı, düşük riskli, yüksek değerli — bu belge), Faz B (orta düzey,
kullanıcı onayı gerektiren YAGNI kararları), Faz C (büyük, çekirdek auth/RBAC reworkları — ayrı tasarım
konuşması gerektirir, henüz başlanmadı).

---

## A1 — ClamAV ile Fail-Closed Virus/Malware Tarama

### Neden

Upload akışında hiçbir virus/malware tarama mekanizması yoktu (`grep -rn "virus|clamav|scan|malware"`
FileServiceApi'de sıfır sonuç veriyordu) — bu, gerçek, somut, doğrulanmış tek güvenlik açığıydı.

### Tasarım

- `docker-compose.yml`'e `clamav` servisi eklendi. **Önemli bulgu:** resmi `clamav/clamav:stable` imajı
  sadece `amd64` mimarisini destekliyor (`docker manifest inspect` ile doğrulandı) — bu sunucular ARM64
  (Apple Silicon UTM VM'leri, `aarch64`) olduğu için bu imaj çalışmıyordu. QEMU emülasyonu da kurulu
  değildi (`docker run --platform linux/amd64 hello-world` → `exec format error`) ve zaten CPU-yoğun bir
  servis için pratik olmazdı. Çözüm: ClamAV'ın kendi resmi Debian varyantı `clamav/clamav-debian:stable`
  kullanıldı — bu imaj `amd64`, `arm64` VE `ppc64le` için manifest sağlıyor (doğrulandı).
- `FileServiceApi/Services/VirusScanService.cs` (yeni): clamd'in kendi düz metin `INSTREAM` protokolüyle,
  ham `TcpClient` soketi üzerinden konuşur — 3. parti NuGet paketi gerekmez (kütüphanesiz tercih).
  4-byte big-endian uzunluk-prefixli chunk'lar + `\0\0\0\0` bitiş işareti; yanıt `"...OK"` (temiz),
  `"...FOUND"` (virüslü) veya bağlantı hatası olarak yorumlanır.
- `FileEndpoints.cs::CreateFileAsync`'e, magic-byte kontrolünden hemen sonra, `publisher.PublishAsync`
  çağrısından önce entegre edildi. **Fail-closed:** clamd'e ulaşılamıyorsa veya yanıt belirsizse,
  `VirusScanOutcome.Unavailable` → `503 scan_unavailable`, dosya YAYINLANMAZ. Virüs tespit edilirse
  `422 virus_detected`.

### Testler

**Test 1 — EICAR algılama zorluğu ve çözümü:** İlk denemelerde saf EICAR string'ini PDF/PNG magic-byte'ı
ile birleştirip (`%PDF-1.4` + EICAR, ya da geçerli bir PNG + trailing EICAR) `clamdscan` ile doğrudan
test edildiğinde **algılanmadı** (`OK` döndü) — birden fazla varyasyon denendi (ayraçsız birleştirme,
gerçek/minimal geçerli PNG + trailing data). Kök neden: ClamAV, dosya PDF/PNG olarak tanınınca
format-farkındalıklı bir ayrıştırıcı kullanıyor, ham "eklenmiş" trailing byte'ları generic imza
taramasına dahil etmiyor. Saf, ayraçsız EICAR string'i (68 byte, hiçbir sarmalayıcı olmadan) doğrudan
test edildiğinde **doğru şekilde algılandı** (`Eicar-Test-Signature FOUND`) — bu, ClamAV'ın kendisinin
çalıştığını doğruladı. Çözüm: EICAR'ı gerçek bir PDF **FlateDecode stream nesnesi** içine (zlib ile
sıkıştırılmış, geçerli PDF syntax'ıyla) gömmek — bu, ClamAV'ın PDF stream-ayrıştırıcısının gerçekten
baktığı yapı, ve bu şekilde **algılandı**.

**Test 2 — Gerçek endpoint üzerinden uçtan uca:** Yukarıdaki FlateDecode-gömülü EICAR PDF'i, gerçek
`/api/personnel/P010/cv` upload endpoint'i üzerinden yüklendi:
```json
{"error":"virus_detected"}
```
`http:422`. Audit: `action=create, result=denied, reason_code=virus_detected`. `files.references`
tablosunda P010 için EICAR yüklemesine ait **hiçbir yeni kayıt oluşmadı** (mevcut tek kayıt, farklı bir
tarihte yapılmış önceki bir teste aitti) — reddedilen dosya DB'ye hiç yazılmadı, doğrulandı.

**Test 3 — Fail-closed (clamd kapalı):** `docker stop server-file-clamav-1` sonrası temiz bir dosya
yüklenmeye çalışıldı:
```json
{"error":"scan_unavailable"}
```
`http:503` — "muhtemelen temizdir" varsayılmadı, upload güvenli şekilde reddedildi. ClamAV tekrar
başlatılıp (`docker start`) normal duruma dönüldü, `PING`→`PONG` ile hazır olduğu doğrulandı.

**Test 4 — Normal (temiz dosya) akış bozulmadı:** Tarama entegrasyonu sonrası sıradan bir PDF yüklemesi
`http:200` ile başarıyla tamamlandı — tarama adımı normal akışa gecikme dışında engel getirmiyor.

### Regresyon

`tools/server-smoke-test.sh` (23/23), `tools/server-safe-test-suite.sh` (36/36, 0 hata),
`platform-backup.service` (`ExecMainStatus=0`) — hepsi ClamAV entegrasyonu sonrası tekrar çalıştırıldı.

---

## A2 — Archive Endpoint'indeki Cross-App Yetki Boşluğu

### Bulunan Gerçek Zayıflık

`ArchiveFileAsync`'te (`FileEndpoints.cs`) tek sahiplik kontrolü `policy.AllowedDomains.Contains
(fileObject.Domain)` idi — çağıran `appCode`'un o `fileId`'ye ait **aktif bir referansı** olup olmadığı
hiç kontrol edilmiyordu. Referans-iptal sorgusu da `app_code` ile filtrelenmiyordu. Mevcut policy
verisinde (`yonetimapi`→`{personnel}`, `filoapi`→`{fleet}`, ayrık) bu **pratikte** istismar edilebilir
değildi, ama yapısal olarak, herhangi bir gelecek policy'de domain örtüşmesi olursa gerçek bir açık
olurdu.

### Düzeltme

Referans-iptal sorgusuna `r.AppCode == callerAppCode` filtresi eklendi; çağıran app'in o `fileId`'ye ait
aktif bir referansı yoksa no-leak `404` dönülür, audit'e `reason_code=reference_ownership_denied`
yazılır.

### Test — Kullanıcı İzniyle Canlı Cross-App Denemesi

Kullanıcının açık izniyle (`filoapi`'nin `allowed_domains`'i geçici olarak `personnel` ile genişletildi,
test hemen ardından yapıldı, policy hemen geri alındı):

1. Yeni, henüz arşivlenmemiş bir test dosyası yüklendi (`P007/cv`, `fileId=1533f800-...`).
2. `filoapi`'nin service token'ı + mTLS sertifikasıyla (gerçek container network'ü üzerinden, geçici
   `curlimages/curl` container'ı) `POST /internal/files/1533f800-.../archive` denendi:
   ```
   HTTPKOD:404
   ```
   **Engellendi** — `filoapi`'nin domain izni genişletilmiş olsa bile (eski kontrolü geçmiş olsa bile),
   yeni referans-sahiplik kontrolü doğru şekilde reddetti.
3. Audit doğrulandı: `action=archive, result=denied, reason_code=reference_ownership_denied, app_code=filoapi`.
4. DB'de dosyanın durumu kontrol edildi: `status=active` (hâlâ arşivlenmemiş, saldırı denemesi hiçbir
   etki bırakmadı).
5. `filoapi`'nin policy'si hemen orijinal haline (`{fleet}`) geri alındı, doğrulandı.
6. **Regresyon:** Gerçek sahibi (`yonetimapi`) aynı dosyayı normal akışla arşivledi → `http:200`,
   sorunsuz.

---

## A3 — `azp`/`client_id` Tutarlılık Kontrolü

### Neden

Belge bölüm 6.3: "azp ve client_id birlikte bulunup farklıysa token reddedilir" — token confusion'a
karşı ucuz bir ek katman.

### Doğrulama ve Uygulama

Gerçek bir `yonetimapi` service token'ı (Keycloak'tan `client_credentials` grant ile) alınıp decode
edildi (**sadece claim isimleri/yapısı incelendi, token'ın kendisi hiçbir yerde loglanmadı/yazdırılmadı**)
— `azp`, `client_id`, `app_code` claim'lerinin **gerçekten hepsinin mevcut ve tutarlı** olduğu doğrulandı
(`azp=client_id=app_code="yonetimapi"`). `ExtractAppCode` (`FileEndpoints.cs`) güncellendi: `azp` ve
`client_id` ikisi de mevcutsa ve **farklıysa**, appCode `null` döner (mevcut null-check deseniyle
otomatik `401` tetiklenir). `DownloadTicketEndpoints.cs` bu paylaşılan fonksiyonu zaten kullandığı için
düzeltme oraya da otomatik uygulandı.

---

## A4 — No-Leak 404 Gözden Geçirmesi + Read Endpoint'lerindeki Aynı Zayıflık

### Ek Bulgu (review sırasında keşfedildi)

A2'deki AYNI yapısal zayıflık (domain kontrolü var, referans-sahiplik kontrolü yok) `ResolveAsync`,
`GetMetadataAsync` ve `GetContentAsync`'te de bulundu. Kullanıcıya soruldu, **"şimdi bunları da düzelt"**
kararı alındı (sadece A2'yle sınırlı kalınmadı).

### Düzeltme

- `ResolveAsync`: referans sorgusuna `r.AppCode == appCode` filtresi eklendi.
- `GetMetadataAsync`: domain kontrolünden sonra, `db.References.AnyAsync(r => r.FileId == fileId &&
  r.AppCode == appCode && r.Status == "active")` kontrolü eklendi — yoksa no-leak 404,
  `reason_code=reference_ownership_denied`.
- `GetContentAsync`: aynı kontrol, `StreamContentAsync` çağrısından önce eklendi.

### Test — Kullanıcı İzniyle Canlı Cross-App Denemesi (Read Endpoint'leri)

Aynı yöntemle (`filoapi` policy'si geçici genişletildi, test edildi, hemen geri alındı):
```
=== metadata (GET /internal/files/{fileId}) ===
HTTPKOD:404
=== content (GET /internal/files/{fileId}/content) ===
HTTPKOD:404
```
Her ikisi de doğru şekilde engellendi. Policy geri alındı, doğrulandı
(`filoapi → {fleet}`). Regresyon: `tools/server-smoke-test.sh` 23/23 (ticket tabanlı indirme, doğrudan
`/content` stream'i dahil) — normal erişim bozulmadı.

---

## Genel Regresyon (Faz A Sonrası, Tüm Değişiklikler Birlikte)

- `tools/server-smoke-test.sh` → 23/23 `[OK]`.
- `tools/server-safe-test-suite.sh` → 36/36 `[OK]`, 0 `[HATA]` (403 yetkilendirme matrisi, 20 eşzamanlı
  login, 3.8MB dosya indirme dahil).
- `platform-backup.service` (+ otomatik restore-test) → `ExecMainStatus=0`.
- Ticket tabanlı indirme (X-Accel-Redirect + lease modeli) → `http:200`, bozulmadı.

## Deploy ve Senkronizasyon

Değişiklikler yerelde `dotnet build` ile derlendi (0 hata), `scp` ile sunucuya kopyalanıp
`docker compose up -d --build fileservice` (+ yeni `clamav` servisi için `docker compose up -d clamav`)
ile canlıya alındı. Tüm testler bu çalışan container'larda, sunucu hiç durdurulmadan yapıldı (ClamAV'ın
kendi fail-closed testi hariç, ki bu bilinçli bir `docker stop`/`start` döngüsüydü).

## Yan Not: VM Reboot Tekrarı

Bu Faz A çalışması sırasında sunucunun **ikinci kez** bağımsız olarak yeniden başladığı gözlemlendi
(uptime ~4 dakika, tüm container'lar `Exited (255)`) — `PROJECT_STATUS.md`'ye daha önce eklenen
`restart:` politikası eksikliği bulgusunun tekrar canlı bir örneği. Stack `docker compose up -d` ile
tekrar ayağa kaldırıldı, çalışmaya kaldığı yerden devam edildi.
