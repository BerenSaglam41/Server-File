# Kanıt: Ticket Lease Modeli (Süre + Sayı Sınırlı Çoklu Kullanım)

**Tarih:** 2026-07-02
**Kapsam:** Ticket tüketimi — tek-kullanımlık modelden, S3 presigned URL benzeri süre+sayı sınırlı "lease" modeline geçiş
**Durum:** ✅ 6 senaryo (eşzamanlılık dahil) test edildi, hepsi geçiyor, sıfır hata

---

## Neden

Önceki model kesin tek-kullanımlıktı: ticket bir kez tüketilince (tek bir HTTP isteği, Range dahil)
kalıcı olarak ölüyordu. Bu, tarayıcının video/büyük PDF için doğal olarak yaptığı **birden fazla Range
isteğini** (aynı URL'e tekrar tekrar gitme) desteklemiyordu — ikinci istek her zaman `404` alıyordu. Bu
oturumda, S3 presigned URL / Google Signed URL'lerin kullandığı modele benzer bir **lease** (kiralama)
modeli kuruldu: ticket ilk kullanımdan sonra hemen ölmüyor, sınırlı bir süre + sınırlı bir sayı kadar daha
kullanılabiliyor.

## Tasarım

- **`TicketLifetime` (60 sn, değişmedi):** Ticket'ın **ilk** kullanılması gereken pencere.
- **`LeaseDuration` (30 sn, yeni):** İlk kullanımdan sonra, **ek** isteklere izin verilen pencere.
- **`MaxUsesPerTicket` (20, yeni):** Süre sınırından bağımsız, sert bir üst sınır — lease süresi dolmasa
  bile 20. kullanımdan sonra ticket ölür.
- Şema değişikliği minimal: mevcut `used_at` kolonu artık "ilk tüketim zamanı / lease başlangıcı" anlamına
  geliyor (yeni kolon gerekmedi), sadece `use_count INT NOT NULL DEFAULT 0` eklendi.
- Tek bir atomik `UPDATE ... RETURNING` sorgusu: `(used_at IS NULL AND expires_at > now()) OR (used_at IS
  NOT NULL AND now() < used_at + lease_saniye)`, ayrıca `use_count < max_uses` şartı. Eşzamanlı "ilk
  kullanım" denemeleri Postgres'in satır kilidiyle doğal olarak sıraya girer; kaybeden istek `used_at`'i
  zaten set görüp otomatik olarak lease koluna düşer — özel concurrency kodu gerekmedi.

## Testler — 6 Senaryo, Hepsi Geçti

### Test A — Hemen Tekrar Kullanım (Ana Davranış Değişikliği)

```
ilk tüketim              → http:200
aynı ticket, hemen tekrar → http:200   (ÖNCEKİ MODELDE: 404 olurdu)
```

### Test B — Çoklu Range İsteği (Gerçek Kullanım Senaryosu)

```
Range: bytes=0-99    → http:206
Range: bytes=100-199 → http:206   (aynı ticket, ikinci istek — artık başarılı)
```

### Test C — Max Uses Sınırı (20)

22 ardışık istek: **1-20 arası `200`, 21 ve 22 kesin `404`.** Audit'te `ticket_max_uses_reached`
kaydedildi. Ayrıca her tekrar kullanım `lease_use_2`, `lease_use_3`, ..., `lease_use_20` reason
code'larıyla ayrı ayrı izlenebiliyor (doğrulandı, 19 farklı sayaç değeri audit'te görüldü).

### Test D — Lease Süresi Dolması (30 sn)

```
ilk kullanım                        → http:200
35 sn bekle (lease=30sn), tekrar dene → http:404
```
Audit: `ticket_lease_expired`.

### Test E — İlk Kullanım Süresi Dolması (Değişmedi, 60 sn)

Ticket hiç kullanılmadan 65 saniye bekletildi → `404`, audit: `ticket_expired` (eski davranışla birebir
aynı — lease modeli bu durumu etkilemiyor).

### Test F — Eşzamanlılık (Kritik)

Aynı taze ticket'a **25 eşzamanlı istek** atıldı:
```
20 200
 5 404
```
Tam olarak `min(MaxUsesPerTicket, N)` kadar başarı — atomik SQL, gerçek concurrent yükte de doğru
sınırlıyor. FileServiceApi logları temiz (`grep -i exception` → sıfır sonuç), container crash olmadı.

## Regresyon

- `tools/server-smoke-test.sh` → 23/23 `[OK]`.
- `tools/server-safe-test-suite.sh` → tüm senaryolar `[OK]` (403 matrisi, 3.8MB indirme, 20 eşzamanlı
  login dahil).
- `platform-backup.service` (+ otomatik restore-test) → `ExecMainStatus=0`.
- Frontend (Playwright ile daha önce test edilen ticket akışı) **tekrar test edilmedi** — client kodu bu
  değişiklikte hiç dokunulmadı, sadece backend tüketim mantığı değişti; client zaten aynı
  `/files/download/{ticket}` URL'ini aynı şekilde çağırıyor.

## Kapsam Dışı / Bilinen Sınırlamalar

- Lease parametreleri (`LeaseDuration=30sn`, `MaxUsesPerTicket=20`) hardcoded sabitler, `TicketLifetime`
  ile aynı desende — V1 için config'e taşınmadı.
- FlotaApi'de hâlâ ticket sistemi yok, bu yüzden lease de sadece personel dosyaları için geçerli.

---

## Ek Doğrulama Turu — Kullanıcının 7 Sorusu (2026-07-02)

### 1. DB'de `use_count` gerçekten 20'de kalıyor mu?

22 ardışık istek sonrası doğrudan DB sorgusu:
```
 use_count |            used_at
-----------+-------------------------------
        20 | 2026-07-02 13:04:15.608917+00
```
**Evet — tam 20'de kalıyor**, 22 değil (fazladan 2 deneme reddedildiği için hiç artmadı).

### 2. 21. istek audit'te `ticket_max_uses_reached` mı?

```sql
select action, result, reason_code from files.audit_events where reason_code='ticket_max_uses_reached' order by created_at desc limit 5;
```
```
     action     | result |       reason_code
----------------+--------+-------------------------
 ticket_consume | denied | ticket_max_uses_reached   (x5, en yeni ikisi bu testin 21. ve 22. denemeleri)
```
**Evet.**

### 3. Lease süresi dolduktan sonra `use_count` artmıyor mu?

Ticket 1 kez kullanıldı, 35 sn (lease=30sn) beklendi, 3 kez daha denendi (hepsi `404`). DB:
```
 use_count |            used_at
-----------+-------------------------------
         1 | 2026-07-02 13:04:15.863153+00
```
**Evet — `use_count=1` kaldı**, reddedilen 3 deneme sayacı artırmadı (atomik `UPDATE`'in `WHERE` şartı
sağlanmadığı için satır hiç güncellenmedi).

### 4. Ticket hiç kullanılmadan 60 sn geçince `used_at` null kalıyor mu?

Ticket oluşturulup hiç tüketilmeden 60+ sn beklendi. DB:
```
 use_count | used_at |          expires_at           | suresi_dolmus_mu
-----------+---------+-------------------------------+------------------
         0 |  (null) | 2026-07-02 13:05:50.962082+00 | t
```
**Evet — `used_at` gerçekten `NULL`**, `use_count=0`, `expires_at` geçmiş.

### 5. X-Accel yolunda Range ile 206 + Content-Range doğru mu?

`Range: bytes=100-299` isteğiyle:
```
HTTP/1.1 206 Partial Content
Content-Length: 200
Content-Range: bytes 100-299/110567
```
İndirilen 200 byte'lık parça, dosyanın gerçek 100-299 aralığıyla (`dd` ile çıkarılıp `diff` ile
karşılaştırıldı) **birebir aynı** çıktı. **Evet, doğru.**

### 6. `/files/download` için rate limit var mı?

`nginx.conf`'ta `limit_req`/`limit_conn` **hiç yoktu** — ne `/files/download/` için ne genel olarak.
**⚠️ SONRADAN DÜZELTİLDİ (2026-07-02) — bkz. aşağıdaki "Rate Limit ve Log Maskeleme Düzeltmeleri" bölümü.**

### 7. Gateway access log'da ticket açık yazılıyor mu?

```
172.18.0.1 - - [02/Jul/2026:13:07:36 +0000] "GET /files/download/YSulm825tOZead6H5OoK8-7Y9jbOp8-hrdkPbCjqERs HTTP/1.1" 200 110567 "-" "curl/8.18.0"
```
**Evet — ham ticket değeri access log'da düz metin olarak görünüyordu** (`docker logs
server-file-gateway-1`). **⚠️ SONRADAN DÜZELTİLDİ (2026-07-02) — bkz. aşağıdaki "Rate Limit ve Log
Maskeleme Düzeltmeleri" bölümü.**

### Genel Sonuç (o anki durum)

Sorular 1-5: tümü doğrulandı, sistem tasarlandığı gibi çalışıyor. Sorular 6-7: iki gerçek, o an
düzeltilmemiş gözlem — rate limit yok, ticket access log'da açık yazılıyor. Kullanıcıya bildirildi, hemen
ardından ikisi de düzeltildi (aşağıya bakın).

---

## Rate Limit ve Log Maskeleme Düzeltmeleri (TAMAMLANDI ✅ — 2026-07-02)

### Rate Limit

`nginx.conf`'a `/files/download/` için IP başına rate limit eklendi:
```nginx
limit_req_zone $binary_remote_addr zone=download_limit:10m rate=30r/s;
limit_req_status 429;
...
location ~ ^/files/download/(?<ticket>[A-Za-z0-9_-]+)$ {
    limit_req zone=download_limit burst=50 nodelay;
    ...
}
```
`burst=50` seçildi çünkü gerçek testlerimizin en yoğunu (25 eşzamanlı istek) bunun rahatça altında kalıyor
— test yaparken kendimizi engellemeden, gerçek kötüye kullanımı (saniyede yüzlerce istek) sınırlıyor.
Aşılırsa `429` + `{"error":"too_many_requests","reason":"rate_limited"}` JSON body (mevcut
`error_page`/`@upstream_down` desenine uygun yeni bir `@rate_limited` handler'ı eklendi).

**Test 1 — Rate limit gerçekten devrede mi:** 120 eşzamanlı istek (sahte ticket'larla, saf throttle testi)
→ **57×429, 63×404** — limit gerçekten tetikleniyor.

**Test 2 — Gerçek kullanım senaryomuzu engellemiyor mu:** 25 eşzamanlı istek (gerçek, geçerli tek ticket'a,
lease/max-uses testindeki gibi) → **tam olarak `20×200, 5×404`** — rate limit hiç araya girmedi, sonuç
öncekiyle birebir aynı.

### Log Maskeleme

**Bulunan ek bir gerçek sorun (düzeltme sırasında keşfedildi):** İlk denemede `access_log` ve custom
`log_format`'ı `/files/download/{ticket}` location'ına eklememe rağmen ticket **hâlâ maskelenmeden**
log'a yazılıyordu. Kök neden: X-Accel-Redirect ile **başarılı (200/206/304)** yanıtlar aslında
`/protected-download/` (internal) location'ında finalize ediliyor — orijinal location'daki
`access_log`/`add_header` gibi response-seviyesi direktifler bu durumda **hiç uygulanmıyor**. Bu, bir
debug header (`add_header X-Debug-Ticket-Masked`) ile ampirik olarak doğrulandı: header sadece
`/protected-download/` location'ına eklendiğinde görünür oldu.

**Düzeltme:** Maskeli `access_log` her iki location'a da eklendi — `/files/download/` (X-Accel
tetiklenmeyen 403/404/429 yanıtları için) ve `/protected-download/` (başarılı X-Accel yanıtları için,
asıl önemli olan). `$ticket` named-capture'ının `/protected-download/`'a internal redirect sonrası da
erişilebilir kaldığı doğrulandı.

```nginx
map $ticket $ticket_masked {
    "~^(.{8})" "$1...";
    default    "(yok)";
}
log_format download_masked
    '$remote_addr - - [$time_local] "$request_method /files/download/$ticket_masked '
    '$server_protocol" $status $body_bytes_sent "$http_referer" "$http_user_agent"';
```

**Test — Maskeleme gerçekten çalışıyor mu:** Gerçek ticket `2m5chPOWV13BGfAXeL86ALZEyQjcyqk9PhOptVjZVvk`
ile indirme yapıldı. Log çıktısı:
```
"GET /files/download/2m5chPOW... HTTP/1.1" 200 110567 "-" "curl/8.18.0"
```
Sadece ilk 8 karakter (`2m5chPOW`) görünüyor, tam ticket log'da **hiç yok**. 20 eşzamanlı başarılı isteğin
tamamı da doğru maskelendiği doğrulandı (hepsi aynı ticket'ın ilk 8 karakteriyle, `yPaxrd3F...`).

**Küçük bir formatting hatası da bulunup düzeltildi:** İlk denemede log satırında `"HTTP/HTTP/1.1"`
(mükerrer yazım) görüldü — `log_format` string'inde `$server_protocol` zaten `"HTTP/1.1"` döndürdüğü
için önüne ayrıca `"HTTP/"` eklemek gereksizdi. Düzeltildi, tekrar test edildi: `"HTTP/1.1"` (doğru).

### Regresyon

`tools/server-smoke-test.sh` (23/23), `tools/server-safe-test-suite.sh` (tüm senaryolar), `platform-backup.service`
(+ otomatik restore-test) — hepsi düzeltmeler sonrası tekrar çalıştırıldı, regresyonsuz.

### Kalan, Bilinçli Kabul Edilen Sınır

Log'da ticket'ın **ilk 8 karakteri** hâlâ görünüyor (tamamen sıfır değil) — bu, temel ops
görünürlüğü/debugging için bilinçli bir denge (tamamen `"(yok)"` yazmak yerine, "bu ticket'a ait bir
istek oldu mu" sorusuna en azından kısmi cevap veriyor). 8 karakter, 256-bit'lik ticket'ın çok küçük bir
kısmı (~48 bit) olduğu için brute-force/yeniden oluşturma riski taşımıyor.
