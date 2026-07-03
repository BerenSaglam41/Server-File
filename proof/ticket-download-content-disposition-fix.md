# Kanıt: Ticket İndirmesinde Fotoğraflar Tarayıcıda Açılıyordu, İndirilmiyordu (Bug Fix)

**Tarih:** 2026-07-03
**Kapsam:** `/internal/download-tickets/{ticket}/consume` (ticket tabanlı "İndir" akışı) — `Content-Disposition` header'ı
**Durum:** ✅ Kök neden bulundu, düzeltildi, gerçek dosyayla test edildi, tam regresyon temiz

---

## Bildirilen Sorun

Kullanıcı: HR olarak bir personelin fotoğrafını indirmeye çalışınca, tarayıcı dosyayı indirmek yerine
**yeni bir sekmede/aynı sekmede açtı** ve URL çubuğunda doğrudan
`https://192.168.64.5:5090/files/download/{ticket}` göründü — "İndir" butonuna basılmasına rağmen bir
indirme işlemi gerçekleşmedi, tarayıcı görsel dosyayı render etti.

## Kök Neden

`FileServiceApi/Endpoints/DownloadTicketEndpoints.cs`'de (ticket tüketme endpoint'i), `Content-Disposition`
header'ı dosya uzantısına göre koşullu belirleniyordu:

```csharp
var imageExtensions = new[] { "jpg", "jpeg", "png", "webp" };
var disposition = imageExtensions.Contains(fileObject.Extension) ? "inline" : "attachment";
```

Bu mantık, `FileEndpoints.cs`'deki `StreamContentAsync` yardımcı metodundan (uygulama içi
önizleme/görüntüleme amaçlı `/content` endpoint'i için) **kopyalanmıştı**. O endpoint için "resimler
inline görünsün" doğru bir karar — ama `DownloadTicketEndpoints.cs`'in **tek amacı** kullanıcının "İndir"
butonuna bastığında tetiklenen ticket tabanlı indirme akışıdır (bkz. `client/src/components/
FileCard.tsx`'teki `handleDownload` → `client/src/components/PersonnelFileView.tsx`'teki
`createDownloadTicket` çağrısı). Bu akışta **hiçbir önizleme senaryosu yok** — frontend'de hiçbir yerde
(`grep "<img"` → sıfır sonuç) ticket URL'i `<img>` etiketiyle kullanılmıyor, sadece bir `<a>` elementine
`href` olarak verilip `.click()` ile tetikleniyor (`download` attribute'u da yok, yani davranış tamamen
sunucunun `Content-Disposition` değerine bağlı).

Sonuç: resim dosyaları için sunucu `inline` dönünce, tarayıcı `.click()` sonrası bağlantıyı **navigasyon**
olarak işledi (indirme değil), fotoğrafı doğrudan render etti — kullanıcının gördüğü tam olarak buydu.

**Neden bu ana kadar fark edilmedi:** Bu oturumdaki tüm ticket/lease/X-Accel/rate-limit testleri **PDF
dosyasıyla** yapılmıştı (`33fa4cbb-...pdf`) — PDF, `imageExtensions` listesinde olmadığı için her zaman
doğru şekilde `attachment` alıyordu, bug hiç ortaya çıkmadı. Sorun sadece kullanıcı gerçek bir fotoğraf
indirmeyi denediğinde görüldü.

## Düzeltme

```csharp
// Bu endpoint SADECE ticket tabanlı "İndir" akışı için var (bkz. FileCard.tsx handleDownload) —
// önizleme/inline görüntüleme amacı yok. FileEndpoints.StreamContentAsync'teki resimler için
// "inline" istisnası (o, /content endpoint'inin önizleme amacı için doğru) buraya kopyalanmıştı,
// bu yüzden fotoğraf indirmeleri tarayıcıda açılıyor, indirilmiyordu — kaldırıldı.
var disposition = "attachment";
```

`FileEndpoints.cs`'deki `StreamContentAsync` (uygulama içi `/content` görüntüleme endpoint'i) **bilinçli
olarak dokunulmadı** — o, farklı bir amaç için var ve şu an için scope dışı; sadece bildirilen bug'ın
kaynağı olan ticket-consume endpoint'i düzeltildi.

## Testler

### Test 1 — Gerçek bir JPG dosyasıyla uçtan uca

DB'den gerçek bir resim dosyası bulundu (`075ac528-...`, P021'e ait, `.jpg`, `image/jpeg`). Gerçek ticket
akışıyla (`POST .../download-ticket` → `GET /files/download/{ticket}`) indirme yapıldı:
```
Content-Type: image/jpeg
Content-Disposition: attachment; filename="2021-concept-rendering-1.jpg"; filename*=UTF-8''2021-concept-rendering-1.jpg
```
**`attachment` — düzeltme doğrulandı.**

### Test 2 — PDF regresyonu (değişmemeli)

Aynı akış P001'in CV'siyle (PDF) tekrarlandı:
```
Content-Disposition: attachment; filename="33fa4cbb-8723-4753-b42c-be06d2bb8b12.pdf"; ...
```
Değişmedi — zaten `attachment`'tı, hâlâ öyle.

### Test 3 — Lease modeli resimlerle de bozulmadı mı

Aynı resim ticket'ıyla 3 ardışık istek (lease penceresi içinde) → **`200, 200, 200`** — lease/multi-use
mekanizması (yalnızca `Content-Disposition` satırı değişti, atomik `UPDATE`/lease SQL'i hiç dokunulmadı)
resimlerle de sorunsuz.

### Regresyon

- `tools/server-smoke-test.sh` → 23/23 `[OK]`.
- `tools/server-safe-test-suite.sh` → 36/36 `[OK]`, 0 `[HATA]`.
- `platform-backup.service` (+ otomatik restore-test) → `ExecMainStatus=0`.

## Yan Not: Deploy Sırasında Bulunan, İlgisiz Bir Operasyonel Durum

Deploy sırasında sunucunun VM'inin **11 dakika önce yeniden başladığı** fark edildi (`uptime` ile
doğrulandı) — bu, bu oturumdaki hiçbir işlemin sonucu değil, bağımsız bir olay (muhtemelen host/UTM
seviyesinde bir restart). `docker-compose.yml`'de **hiçbir servis için `restart:` politikası tanımlı
değil** (varsayılan: `"no"`) — bu yüzden VM reboot sonrası `postgres`/`keycloak` dışındaki tüm servisler
(`gateway`, `yonetimapi`, `flotaapi`, `opsapi`, `client`) durmuş kalmıştı, `docker compose up -d` ile
elle ayağa kaldırıldı. **Bu, bu bug fix'in kapsamı dışında, ayrı bir gerçek operasyonel bulgu** — üretimde
bir restart/crash sonrası platform kendiliğinden ayağa kalkmaz. Düzeltilmedi, kullanıcıya ayrıca
bildirildi, istenirse `restart: unless-stopped` eklenmesi V2 adayı olarak not edilebilir.

## Deploy ve Senkronizasyon

`FileServiceApi/Endpoints/DownloadTicketEndpoints.cs` yerelde `dotnet build` ile derlendi (0 hata), `scp`
ile sunucuya kopyalanıp `docker compose up -d --build fileservice` ile canlıya alındı. Değişiklik `git
commit` + `git push origin main` ile gönderildi, sunucuda `git pull` ile senkronize edildi.
