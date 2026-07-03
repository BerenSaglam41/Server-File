# Kanıt: B1 — Referans-Bazlı Archive Endpoint'i

**Tarih:** 2026-07-03
**Kapsam:** `POST /internal/references/{referenceId}/archive` — `platform-mimarisi-stajyer-rehberi.txt`
bölüm 9.2/9.9 hedef modeli
**Durum:** ✅ Tamamlandı, 3 servis (FileServiceApi/YonetimApi/FlotaApi) güncellendi, cascade davranışı
gerçek veriyle test edildi

---

## Bağlam

Faz A'daki A2/A4 düzeltmeleri, `ArchiveFileAsync`'teki (fileId-bazlı) cross-app yetki boşluğunu kapatmıştı
— ama bu, hâlâ **objeyi** (dosyayı) doğrudan arşivliyordu. Rehberin hedef modeli farklı: bir obje birden
fazla uygulama/varlık tarafından referanslanabilir (`files.references` şeması zaten bunu destekliyordu,
ama pratikte hiç kullanılmıyordu — daha önce doğrulanmıştı: her `file_id`'nin tam olarak 1 referansı var).
B1, archive işlemini **referans** seviyesine taşıyor: sadece çağıranın kendi referansı iptal edilir; obje
ancak **hiçbir aktif referans kalmadığında** `archived`'a düşer.

## Yapılan Değişiklikler

### FileServiceApi
- `ResolveAsync` ve `CheckOwnershipAsync` yanıtlarına `referenceId` eklendi (çağıranların yeni endpoint'i
  kullanabilmesi için).
- **Yeni endpoint:** `POST /internal/references/{referenceId}/archive` (yeni `/internal/references`
  grubu). Mantık: `reference.AppCode == callerAppCode` sahiplik kontrolü (yoksa no-leak 404) → referansı
  `revoked` yap → aynı `file_id`'ye ait BAŞKA aktif referans var mı kontrol et → yoksa obje de
  `archived`'a düşer, varsa obje `active` kalır.
- **Eski `POST /internal/files/{fileId}/archive` (`ArchiveFileAsync`) tamamen kaldırıldı** — plan
  gereği ("iki archive yolu olmasın"), tek bir doğru yol bırakıldı.
- **Ek bulgu:** `CheckOwnershipAsync`'te de A2/A4 ile AYNI desendeki bir zayıflık bulundu (domain/entity
  eşleşmesi var, ama `app_code` filtresi yoktu) — bu da düzeltildi, artık sadece çağıranın KENDİ
  referansı için "owned" dönüyor.

### YonetimApi
- `FileResolveResult`/`OwnershipResult` DTO'larına `ReferenceId` eklendi.
- `FileBelongsToPersonnelAsync`'in dönüş tipi `Task<bool>`'dan `Task<long?>`'a değişti (null = erişim
  yok, değer = referenceId).
- Hem resolve-tabanlı (`ProxyArchiveAsync` — cv/photo/official_document) hem fileId-tabanlı
  (`ProxyArchiveFileByIdAsync` — document/attachment/report) archive akışları yeni
  `/internal/references/{referenceId}/archive` endpoint'ini çağıracak şekilde güncellendi.
- `DownloadTicketEndpoints.cs`'teki paylaşılan `FileBelongsToPersonnelAsync` çağrısı da (sadece
  bool sonuç gerektiği için `is null` kontrolüne) güncellendi.

### FlotaApi
- `FileResolveResult` DTO'suna `ReferenceId` eklendi, `ProxyArchiveAsync` yeni endpoint'i çağırıyor.

## Testler

### Test 1 — Normal akışlar (regresyon)

- **Resolve-tabanlı archive** (`P012/cv/archive`): `{"referenceId":41,"fileId":"...","objectStatus":"archived"}` → `200`.
- **FileId-tabanlı archive** (`P012/files/{fileId}/archive`, document tipi): `{"referenceId":42,"fileId":"...","objectStatus":"archived"}` → `200`.
- **FlotaApi archive** (`test_arac_1/photo/archive`): `{"referenceId":45,"fileId":"...","objectStatus":"archived"}` → `200`.

Üçü de yeni referans-bazlı yanıt formatını doğru döndürdü, tek referans olduğu için (mevcut 1:1
gerçeklik) `objectStatus` doğru şekilde `archived`.

### Test 2 — Cross-app yetki engeli (kullanıcı izniyle, gerçek deneme)

`filoapi`'nin `allowed_domains`'i geçici olarak `personnel` ile genişletildi (izin alındı), yeni
yüklenen bir test dosyasının (`P013/cv`, `referenceId=43`, sahibi `yonetimapi`) referansı `filoapi`
kimliğiyle archive edilmeye çalışıldı:
```
HTTPKOD:404
```
Engellendi. Policy hemen orijinal haline (`{fleet}`) geri alındı, referansın hâlâ `active` kaldığı
doğrulandı.

### Test 3 — Cascade davranışı (B1'in asıl yeni özelliği, kullanıcı izniyle gerçek DB testi)

Mevcut 1:1 gerçeklik nedeniyle bu senaryoyu doğal olarak tetiklemek mümkün değildi — kullanıcı izniyle,
**geçici, test amaçlı** bir 2. referans satırı DB'ye eklendi (aynı `file_id`'ye işaret eden,
`entity_id='B1-CASCADE-TEST'`, `id=44`).

1. İlk referansı (43, `yonetimapi`'nin gerçek referansı) archive et:
   ```
   {"referenceId":43,"fileId":"765d013c-...","objectStatus":"active"}
   ```
   **Obje `active` kaldı** — diğer referans (44) hâlâ aktif olduğu için cascade tetiklenmedi. Bu,
   B1'in A2'den farkını kanıtlayan asıl test.
2. İkinci (son kalan aktif) referansı da archive et:
   ```
   {"referenceId":44,"fileId":"765d013c-...","objectStatus":"archived"}
   ```
   **Obje artık `archived`** — son aktif referans da iptal edilince cascade doğru tetiklendi.
3. Test referansı (`id=44`) temizlendi (`DELETE FROM files.references WHERE id=44`).

### Regresyon

`tools/server-smoke-test.sh` → 23/23 `[OK]`. `tools/server-safe-test-suite.sh` → 36/36 `[OK]`, 0 `[HATA]`.

## Deploy ve Senkronizasyon

`FileServiceApi/Endpoints/FileEndpoints.cs`, `YonetimApi/Endpoints/PersonnelEndpoints.cs`,
`YonetimApi/Endpoints/DownloadTicketEndpoints.cs`, `FlotaApi/Endpoints/VehicleEndpoints.cs` — yerelde
`dotnet build` ile derlendi (0 hata, her 3 servis için ayrı ayrı), `scp` ile sunucuya kopyalanıp
`docker compose up -d --build fileservice yonetimapi flotaapi` ile canlıya alındı — healthcheck zinciri
(fileservice→yonetimapi/flotaapi) doğru sırayla, 9/9 servis healthy.
