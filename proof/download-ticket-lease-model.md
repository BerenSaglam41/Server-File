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
