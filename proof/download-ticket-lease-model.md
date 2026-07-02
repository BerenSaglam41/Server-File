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

Yukarıdaki 2 bulgu (rate limit yok, ticket log'da açık) düzeltildi. Kullanıcının "bunu da ayrı test edip
kanıtlayalım" isteği üzerine bu iş kendi başına, kapsamlı bir belgeye taşındı — 3 ayrı sızıntı noktası
(X-Accel hedefi, rate-limit hedefi, nginx'in kendi `limit_req` error_log'u) bulunup kapatıldığı, ve her
biri ayrı ayrı test edildiği detaylarıyla orada:

**Tam kanıt: `proof/gateway-rate-limit-ve-ticket-log-maskeleme.md`**
