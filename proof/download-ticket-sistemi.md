# Kanıt: Opak, Tek Kullanımlık İndirme Ticket Sistemi

**Tarih:** 2026-07-02 (ilk sürüm), 2026-07-02 (FileServiceApi'ye taşıma)
**Kapsam:** Personel dosyaları — fileId bazlı indirme
**Durum:** ✅ 15+7 senaryo test edildi, 3 gerçek bug bulunup düzeltildi, hepsi geçiyor

---

## Mimari Geçmişi (2 aşama)

**Aşama 1 (ilk sürüm):** Ticket yaşam döngüsü (oluşturma+tüketme) tamamen YonetimApi'de,
`yonetim.download_tickets` tablosunda yaşıyordu.

**Aşama 2 (bu belgedeki "Taşıma Sonrası Testler" bölümü):** Kullanıcı, "düzeltme raporu"nun 9-13
maddelerini işaret edip ticket'ın hedef konuma (`/internal/download-tickets`,
`/internal/download-tickets/{ticket}/consume`) taşınmasını istedi. Aradaki fark netleştirildi ve kabul
edildi: **Gateway'in ticket'ı doğrudan tüketmesi ve X-Accel-Redirect ile byte'ın nginx'ten servis
edilmesi (madde 13) bilinçli olarak ayrı bir aşamaya bırakıldı** — bunlar Gateway'e Files-01 NFS erişimi
ve FileServiceApi'ye yeni bir ağ yolu açmayı gerektirir, mevcut doğrulanmış izolasyonu değiştirir.
Bu aşamada sadece ticket'ın **konumu** taşındı (YonetimApi → FileServiceApi), **çağıran hâlâ YonetimApi**
(servis token'ıyla, mevcut güven sınırı korunarak).

- Tablo: `yonetim.download_tickets` → `files.download_tickets` (FileServiceApi'nin kendi `AppDbContext`'i,
  `personnel_id` yerine domain-agnostik `file_id`+`app_code` — ileride FlotaApi de aynı endpoint'i
  kullanabilir).
- Endpoint'ler: `POST /internal/download-tickets`, `GET /internal/download-tickets/{ticket}/consume`
  (FileServiceApi üzerinde, `file-service-api-contract.md`'nin `/internal/files/*` desenine uygun).
- YonetimApi'nin `/api/personnel/.../download-ticket` ve `/api/personnel/download/{ticket}` endpoint'leri
  aynı kaldı (client/URL sözleşmesi değişmedi) — artık sadece ince bir proxy.
- Audit artık iki katmanlı: FileServiceApi kendi `files.audit_events`'ine teknik `ticket_create`/
  `ticket_consume` kaydı yazıyor (bu, `file-service-api-contract.md`'nin "iki katmanlı audit" ilkesine
  ilk sürümden daha uygun).

---

## Ne Test Edildi ve Neden

Amaç: uzun ömürlü oturum cookie'sine bağımlı olmadan, **tek bir dosyaya bağlı, kısa ömürlü, tek kullanımlık,
en az 256-bit entropili opak bir ticket** ile indirme yapılabildiğini kanıtlamak (S3 presigned URL /
Google Signed URL benzeri desen).

İlk 7 senaryodan sonra kullanıcı 8 ek, daha derin senaryo istedi (DB'de açık ticket var mı, ticket
tahmin/tamper edilebilir mi, gerçek eşzamanlı yük, Range davranışı, cleanup, archive etkileşimi, logout
etkileşimi, başarısız denemelerin audit'lenmesi). Bu ikinci tur **gerçek bir production bug'ı buldu**
(aşağıda "Bulunan ve Düzeltilen Bug" bölümü).

| # | Senaryo | Neyi kanıtlıyor |
|---|---|---|
| A | Ticket oluşturma | Yetkili kullanıcı gerçekten opak, rastgele bir ticket alabiliyor |
| B | Cookie'siz tüketim | Ticket, oturum kimliğinin yerine gerçekten geçebiliyor |
| C | Aynı ticket'ı tekrar kullanma | Tek-kullanımlık garantisi gerçekten çalışıyor |
| D | Uydurma ticket | Sistem var olmayan bir ticket için bilgi sızdırmıyor |
| E | Süresi dolmuş ticket | Kısa ömür (60 sn) gerçekten uygulanıyor |
| F | Yetkisiz kullanıcı ticket istemeye çalışıyor | RBAC, ticket oluşturmadan önce devrede |
| G | Yetkili kullanıcı kendi dosyası için ticket istiyor | Pozitif kontrol |
| H | DB'de ticket açık mı saklanıyor | Sadece hash saklandığı doğrulanıyor |
| I | Ticket tamper (son karakter değişimi) | Ticket path/fileId taşımıyor, tahmin edilemez |
| J | 10 eşzamanlı istek aynı ticket'a | Atomik tek-kullanım gerçek yarış koşulunda da tutuyor mu |
| K | Range isteğiyle tüketim + ikinci Range denemesi | Tek-kullanımın Range'i de kapsadığı netleşiyor |
| L | Ticket cleanup | Süresi dolmuş satırlar otomatik temizleniyor mu |
| M | Ticket alındıktan sonra dosya arşivlenirse | Tutarlı 404 davranışı |
| N | Logout sonrası ticket | Ticket, cookie oturumundan bağımsız mı |
| O | Geçersiz/süresi dolmuş/tekrar kullanılan denemelerin audit'i | Başarısız denemeler de iz bırakıyor mu |

## Ortam ve Test Yöntemi

- Sunucu: `192.168.64.5` (api sunucu, `docker compose -f docker-compose.yml`), tüm testler **sunucu hiç
  durdurulmadan, çalışır haldeyken**, SSH üzerinden gerçek HTTP istekleriyle yapıldı — mock yok, gerçek
  Keycloak login, gerçek Postgres, gerçek FileServiceApi çağrısı.
- Test kullanıcıları: `hr001` (read.all), `p001` (read.self)
- İki deploy turu oldu: ilk turda A-G test edildi ve geçti; H-O testleri sırasında J'de bir bug bulundu,
  kod düzeltilip **yeniden build+deploy edildi**, sonra bütün testler (özellikle J) tekrarlandı.

---

## Test A — Ticket Oluşturma

```json
{"ticket":"GAgB3stR2Ed3u1-NYAo4XnsqqHPwqHwe2co-8SSovT8","expiresInSeconds":60,"downloadUrl":"/api/personnel/download/..."}
```
256-bit (32 byte) rastgele veriden base64url — URL-safe, yeterli entropi.

**⚠️ Not:** `downloadUrl` formatı bu testin yapıldığı andaki gerçek çıktıydı. X-Accel-Redirect
eklendikten sonra format `/files/download/{ticket}` olarak değişti (bkz.
`proof/x-accel-redirect-gateway.md`); `/api/personnel/download/{ticket}` endpoint'i artık **hiç yok**.

## Test B — Cookie Olmadan Tüketim

`http:200`, dosya boyutu `110567` byte, **hiçbir cookie kullanılmadan**.

## Test C — Tekrar Kullanım Reddi

İkinci deneme → `http:404` (atomik `UPDATE ... WHERE used_at IS NULL` 0 satır etkiledi).

## Test D — Uydurma Ticket

`http:404` — bilgi sızdırmıyor.

## Test E — Süre Dolması

60 saniyelik ticket, 65 saniye bekleyip deneme → `http:404`.

## Test F — RBAC Reddi

p001, P002'nin path'i üzerinden ticket istedi → `{"error":"forbidden","reason":"access_denied"} http:403`,
ticket hiç oluşmadı (DB'de satır yok).

## Test G — Pozitif Kontrol

p001 kendi dosyası için ticket istedi → `http:200`.

---

## Test H — DB'de Açık Ticket Saklanıyor mu

```sql
\d yonetim.download_tickets
```

Sonuç: tabloda sadece `ticket_hash` (VARCHAR(64), `CHECK (ticket_hash ~ '^[a-f0-9]{64}$')`) var. Açık/ham
ticket değerini tutan hiçbir kolon yok — **doğrulandı, sadece hash saklanıyor.**

## Test I — Ticket Tamper

```
orijinal:      H1VJ9TS-JZDA4p6s1OuhmCgLOCD3IfAeWEYGV_lrIPg
degistirilmis: H1VJ9TS-JZDA4p6s1OuhmCgLOCD3IfAeWEYGV_lrIPX   (son karakter degisti)
```

- Değiştirilmiş ticket → `http:404`
- Orijinal ticket (henüz tüketilmemiş) → `http:200`

Ticket içinde `file_id`/path bilgisi taşınmıyor (SHA256 tabanlı arama), tek karakter değişimi hash'i
tamamen değiştirip eşleşmeyi bozuyor — **tahmin/tamper edilemez.**

## Test J — 10 Eşzamanlı İstek (Gerçek Bug Bulundu)

**İlk deneme (düzeltme öncesi kod):**
```
1 200
8 404
1 500
2 curl timeout (10sn)
```

Beklenen "1x200, 9x404" değil — **1 tane 500 ve 2 tane timeout var.** Bu, gerçek bir hataya işaret
ediyordu, görmezden gelinmedi.

### Bulunan ve Düzeltilen Bug

YonetimApi container loglarında:
```
Npgsql.NpgsqlOperationInProgressException: A command is already in progress:
UPDATE yonetim.download_tickets SET used_at = now() WHERE ticket_hash = $1 ...
```

**Kök neden:** `ConsumeTicketAsync` içinde, atomik `UPDATE` sorgusunun `reader`'ı bir `await using` bloğu
içindeyken **elle bir kez daha** `reader.DisposeAsync()` çağrılıyordu — blok kapanışında **otomatik ikinci
kez** dispose ediliyordu. Bu çift-dispose, eşzamanlı (concurrent) isteklerde bağlantı durumunu bozup aynı
bağlantı üzerinde "zaten devam eden bir komut var" hatasına yol açıyordu. Tek istekle test edildiğinde
zamanlama farklı olduğu için bug görünmüyordu — **sadece gerçek eşzamanlı yükte ortaya çıktı.**

**Düzeltme:** `UPDATE` komutu ve okuyucusu kendi `using` bloğunda elle müdahale edilmeden tam kapatıldı;
teşhis amaçlı ikinci `SELECT` sorgusu (hangi sebeple reddedildiğini bulmak için) yalnızca ilk komut
tamamen kapandıktan sonra, tamamen ayrı bir `using` bloğunda açılacak şekilde yeniden yazıldı. Kod
`YonetimApi/Endpoints/DownloadTicketEndpoints.cs`'de `ConsumeTicketAsync` metodunda.

**Düzeltme sonrası tekrar test:**
```
1 200
9 404
```
Tam beklenen sonuç. Container logları temiz (`grep -i exception` → sıfır sonuç).

## Test K — Range İsteğiyle Tüketim

**⚠️ GÜNCELLEME (2026-07-02, sonradan):** Bu testin sonucu ve aşağıdaki "V1, bilinçli" kararı **artık
geçerli değil** — lease modeli eklendikten sonra aynı ticket'la ikinci bir Range isteği artık `404`
DEĞİL, başarılı oluyor (lease penceresi + max-uses sınırı içinde). Güncel, doğru davranış için:
`proof/download-ticket-lease-model.md` → "Test B — Çoklu Range İsteği". Aşağıdaki orijinal test sonucu,
**o zamanki (lease öncesi) gerçek durumu** yansıtıyor, tarihsel kayıt olarak bırakıldı.

```
1. istek: Range: bytes=0-9  → HTTP/1.1 206 Partial Content
2. istek (aynı ticket, farklı Range): → http:404   [ARTIK GEÇERSİZ, bkz. yukarıdaki not]
```

**O zamanki karar (V1, artık aşılmış):** Ticket tek kullanımlıktı ve bu, Range header'ı olsa bile tek bir
HTTP isteğiyle sınırlıydı. Çok parçalı Range/seeking senaryosu için bir "lease" modeli gerektiği not
edilmişti — bu ihtiyaç sonradan tam olarak karşılandı.

## Test L — Ticket Cleanup

```sql
select count(*) toplam, count(*) filter (where used_at is not null) tuketilmis,
       count(*) filter (where expires_at < now() and used_at is null) suresi_dolmus_temizlenmemis
from yonetim.download_tickets;
```
```
 toplam | tuketilmis | suresi_dolmus_temizlenmemis
--------+------------+------------------------------
     11 |          9 |                            2
```

**Doğrulanan gerçek (test anında):** Süresi dolmuş ticket satırları **otomatik temizlenmiyordu**.

**Sonradan eklendi ve test edildi (2026-07-02):** `tools/cleanup-download-tickets.sh` +
`platform-download-ticket-cleanup.service`/`.timer` (günlük `04:00:00 UTC`, `Persistent=true`, diğer 4
platform timer'ıyla aynı desende). Sunucuda canlı test edildi:

```
$ RETAIN_DAYS=0 bash tools/cleanup-download-tickets.sh
[OK] 12 satir silindi (expires_at < now() - 0 gun)
```
Tablo `select count(*) from yonetim.download_tickets;` ile `0`'a düştüğü doğrulandı. Ayrıca
`systemctl start platform-download-ticket-cleanup.service` ile systemd üzerinden de tetiklenip
`journalctl -u platform-download-ticket-cleanup` çıktısı temiz şekilde göründü. `systemctl list-timers
'platform-*'` diğer 4 timer'ın (backup/restore-test/disk-check/services-status) etkilenmediğini gösterdi.

## Test M — Ticket Alındıktan Sonra Dosya Arşivlenirse

1. Yeni test dosyası yüklendi (P006/cv), ticket alındı.
2. Dosya `POST /api/personnel/P006/cv/archive` ile arşivlendi (ticket henüz tüketilmeden).
3. Ticket ile indirme denendi → **`HTTP 404`**.

**Davranış:** Ticket tablosundaki atomik `UPDATE` başarıyla `used_at`'i işaretliyor (ticket "harcanıyor"),
ama FileServiceApi kendi `status != active` kontrolünde reddediyor, sonuç `404`. Yani: aynı ticket'la
ikinci bir deneme de mümkün değil (zaten tüketildi) — kullanıcı yeni bir ticket almak zorunda, bu da
tutarlı ve güvenli bir davranış.

## Test N — Logout Sonrası Ticket

```
logout sonrası normal cookie isteği → http:401  (oturum düzgün sonlanıyor)
logout sonrası TICKET ile indirme    → http:200  (ticket cookie'e bağlı değil)
```

**Karar (bilinçli):** Ticket, oturum cookie'sinden tamamen bağımsız çalışır; logout ticket'ı geçersiz
kılmaz. Kabul edilebilir çünkü ömür zaten çok kısa (60 sn) — pratikte kullanıcı logout olup 60 saniye
içinde eski ticket'ı tekrar kullanmaya çalışmaz. Bilerek alınmış bir V1 kararı, gelecekte "logout tüm
açık ticket'ları da iptal etsin" istenirse `used_at`'i logout anında toplu güncelleyen bir sorgu eklenir.

## Test O — Başarısız Denemelerin Audit'i

Düzeltme öncesi kodda **başarısız ticket tüketim denemeleri hiç audit'e yazılmıyordu** — bu da testler
sırasında fark edilip düzeltildi (`ConsumeTicketAsync`'e, ret nedenini ayrı bir teşhis sorgusuyla bulan ve
`audit.WriteAsync` ile kaydeden mantık eklendi).

```sql
select reason_code, count(*) from yonetim.audit_events
where action='PersonnelDownloadTicketConsumed' and result='denied' group by reason_code;
```
```
     reason_code     | count
----------------------+-------
 ticket_already_used |    27
 ticket_expired      |     1
 ticket_not_found    |     1
```

Üç ret nedeninin üçü de (`ticket_already_used`, `ticket_expired`, `ticket_not_found`) doğru şekilde
audit'e yazıldığı doğrulandı.

---

## Regresyon Kontrolü

Her iki deploy turunda da (düzeltme öncesi ve sonrası) `tools/server-smoke-test.sh` baştan sona tekrar
çalıştırıldı — 23/23 adım `[OK]`, hiçbir regresyon yok.

## Deploy ve Senkronizasyon Süreci

Kod önce yerelde `dotnet build` ile derlendi (0 hata), sonra `scp` ile sunucuya kopyalanıp
`docker compose up -d --build yonetimapi` ile canlıya alındı — **testlerin hepsi bu çalışan, canlı
container üzerinde** yapıldı (sunucu hiç kapatılmadan). Değişiklikler yerelde `git commit` edildi,
`git push origin main` ile GitHub'a gönderildi, sunucuda `git pull` ile senkronize edildi — üç tarafın
(local/origin/server) `git rev-parse HEAD` değeri birebir aynı olduğu doğrulandı.

## Mimari Not (Aşama 1)

`FileServiceApi` bu değişiklikten etkilenmedi — hâlâ dışa hiç açık değil, mTLS + servis token'ı
zorunluluğu aynen duruyor. Ticket sistemi tamamen `YonetimApi`'nin kendi proxy katmanında, mevcut
güvenlik sınırlarını koruyarak eklendi.

---

## Taşıma Sonrası Testler (Aşama 2 — Ticket FileServiceApi'de)

### Bulunan ve Düzeltilen Bug #3: `files.audit_events` CHECK constraint

İlk deploy'da ticket oluşturma `{"error":"upstream_error"}` döndü, ticket tüketme `500` verdi.
FileServiceApi logları kök nedeni gösterdi:

```
Npgsql.PostgresException: 23514: new row for relation "audit_events" violates check constraint "chk_action"
```

**Kök neden:** `files.audit_events.chk_action` sadece `create, read, archive, delete_attempt`
değerlerine izin veriyordu (bu oturumun çok daha önceki bir aşamasında "sadece files.audit_events'te
CHECK constraint var" diye tespit edilmişti — bu kez o constraint'in kapsamı dışına çıkan yeni bir
action değeri eklerken bunu gözden kaçırdım). Yeni `ticket_create`/`ticket_consume` action'ları bu
listede yoktu.

**Düzeltme:**
```sql
ALTER TABLE files.audit_events DROP CONSTRAINT IF EXISTS chk_action;
ALTER TABLE files.audit_events ADD CONSTRAINT chk_action
  CHECK (action::text = ANY (ARRAY['create','read','archive','delete_attempt','ticket_create','ticket_consume']::text[]));
```
`db/docker-init/05-download-tickets-fileservice.sql`'e eklendi, sunucuda uygulandı. Düzeltme sonrası
tüm testler geçti.

### A-D, I — Temel Davranış ve Tamper (Yeniden Test)

Aynı sonuçlar, yeni mimaride de doğrulandı: ticket oluşturma `200`, cookie'siz tüketim `200` (110567
byte), tekrar kullanım `404`, uydurma ticket `404`, tamper edilmiş ticket `404` (orijinal hâlâ `200`,
henüz tüketilmemiş).

### F, G — RBAC (Yeniden Test)

p001 → P002 path'i: `403 access_denied`, ticket hiç oluşmadı. p001 → kendi dosyası: `200`.

### H — DB Şeması (Yeniden Test, Yeni Konum)

```
Table "files.download_tickets"
   Column    |           Type           | Nullable
-------------+--------------------------+----------
 ticket_hash | character varying(64)    | not null
 file_id     | uuid                     | not null
 app_code    | character varying(100)   | not null
 actor       | character varying(200)   |
 expires_at  | timestamp with time zone | not null
 used_at     | timestamp with time zone |
 created_at  | timestamp with time zone | not null
```
Açık ticket saklayan kolon yok, sadece hash — doğrulandı.

### J — 10 Eşzamanlı İstek (Yeniden Test)

```
1 200
9 404
```
Tam beklenen sonuç, **hiç regresyon yok** — bu kez `ConsumeTicketAsync` en baştan (ilk seferde bulduğum
double-dispose bug'ından ders çıkarılarak) her komutu kendi temiz `using` bloğunda kapatacak şekilde
yazıldı. FileServiceApi logları temiz (`grep -i exception` → sıfır sonuç).

### K, M — Range ve Archive Etkileşimi (Yeniden Test)

Range ile ilk istek → `206 Partial Content`; ikinci Range denemesi (aynı ticket) → `404` (o zaman
değişmemişti). **⚠️ Lease modeli sonradan eklenince bu davranış değişti** — bkz.
`proof/download-ticket-lease-model.md`. Ticket alındıktan sonra dosya arşivlenirse → `404` (bu davranış
hâlâ geçerli, lease modelinden etkilenmedi — arşivlenmiş dosya her koşulda `404` döner).

### Audit — İki Katman Ayrı Ayrı Doğrulandı

```sql
select action, result, reason_code, app_code, count(*) from files.audit_events
where action like 'ticket%' group by action, result, reason_code, app_code order by action;
```
```
     action     | result  |     reason_code     |  app_code  | count
----------------+---------+---------------------+------------+-------
 ticket_consume | denied  | ticket_already_used | yonetimapi |    10
 ticket_consume | denied  | ticket_not_found    | unknown    |     2
 ticket_consume | success |                     | yonetimapi |     3
 ticket_create  | success |                     | yonetimapi |     4
```
`yonetim.audit_events`'te de `PersonnelDownloadTicketCreated`/`Consumed` kayıtları birikmeye devam
ediyor — iki katman da çalışıyor.

**Bilinen, kabul edilen tradeoff:** YonetimApi artık ticket tüketimi sırasında `personnelId`'yi bilmiyor
(bu bilgi artık sadece FileServiceApi'nin ticket satırında `file_id`/`app_code` olarak var, `personnelId`
FileServiceApi'nin hiç bilmediği bir domain kavramı). `yonetim.audit_events`'teki tüketim kaydı artık
personel kimliği yerine `"UNKNOWN"` yazıyor. Teknik detay (hangi dosya, hangi app) `files.audit_events`'te
tam olarak var; personel bazlı sorgulama isteniyorsa `file_id` üzerinden `files.references` ile join
gerekir.

### Regresyon Kontrolü

`tools/server-smoke-test.sh` 23/23 `[OK]`. `tools/cleanup-download-tickets.sh` yeni tablo konumuna
(`files.download_tickets`) karşı da doğru çalıştığı doğrulandı.

### Gateway Hâlâ `/internal/download-tickets` Yolunu Engelliyor mu

```bash
curl -k -X POST 'https://localhost:5090/internal/download-tickets?fileId=...'
```
Sonuç: `http:404`. `nginx/nginx.conf`'taki `location /internal/ { return 404; }` kuralı toptan tüm
`/internal/*` yollarını kapsadığı için yeni endpoint de otomatik korunuyor — nginx config'inde hiçbir
değişiklik gerekmedi.

### Deploy ve Senkronizasyon

Her iki servis (`FileServiceApi`, `YonetimApi`) yerelde ayrı ayrı `dotnet build` ile derlendi, `scp` ile
sunucuya kopyalanıp `docker compose up -d --build fileservice yonetimapi` ile canlıya alındı, tüm testler
bu çalışan container'larda yapıldı.

## Mimari Not (Aşama 2 — O Zamanki Durum, Artık Aşama 3/4 ile Aşıldı)

**⚠️ GÜNCELLEME (2026-07-02, sonradan):** Aşağıdaki paragraf, bu belgenin Aşama 2 turunda yazıldığı
andaki gerçek durumu yansıtıyordu. Sonradan **hem Gateway/X-Accel hem lease modeli de yapıldı** — aşağıdaki
"bilinçli olarak yapılmadı" ifadesi artık geçerli değil. Güncel mimari için:
- Gateway/X-Accel-Redirect: `proof/x-accel-redirect-gateway.md`
- Lease modeli: `proof/download-ticket-lease-model.md`

Tarihsel kayıt (o zamanki gerçek durum): `FileServiceApi` `/internal/download-tickets*` endpoint'lerini
sunuyordu ama dışa hiç açık değildi — Gateway/istemci FileServiceApi'yi hiç görmüyordu, tek çağıran
YonetimApi'ydi (servis token'ıyla). Madde 9 (Gateway ticket consume) ve madde 13 (X-Accel-Redirect)
o an bilinçli olarak yapılmamıştı; madde 12 (lease modeli) de aynı nedenle ayrı bırakılmıştı.
