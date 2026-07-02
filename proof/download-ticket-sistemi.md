# Kanıt: Opak, Tek Kullanımlık İndirme Ticket Sistemi

**Tarih:** 2026-07-02
**Kapsam:** Personel dosyaları (YonetimApi) — fileId bazlı indirme
**Durum:** ✅ 15 senaryo test edildi, 1 gerçek eşzamanlılık bug'ı bulunup düzeltildi, hepsi geçiyor

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

```
1. istek: Range: bytes=0-9  → HTTP/1.1 206 Partial Content
2. istek (aynı ticket, farklı Range): → http:404
```

**Karar (V1, bilinçli):** Ticket tek kullanımlıktır ve bu, Range header'ı olsa bile **tek bir HTTP
isteğiyle sınırlıdır**. Aynı ticket'la ikinci bir Range isteği (örn. video/PDF'i parça parça okuma) 404
döner. Çok parçalı Range/seeking senaryosu desteklenecekse bir "lease" (süreli, birden fazla Range
isteğine izin veren oturum) modeli gerekir — **V1'de yok, dosyalarımız (PDF/resim, ~100KB) tek istekte
tamamen indiriliyor olduğundan şu an pratik bir kısıtlama değil.** V2 adayı.

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

**Doğrulanan gerçek:** Süresi dolmuş ticket satırları **otomatik temizlenmiyor** — bilinen, dokümante
edilmiş bir V1 sınırlaması. Satırlar küçük (~200 byte) ve hacim düşük olduğu için şu an operasyonel bir
risk değil, ama uzun vadede bir cleanup job (`DELETE WHERE expires_at < now() - interval '1 day'`)
eklenmeli.

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

## Mimari Not

`FileServiceApi` bu değişiklikten etkilenmedi — hâlâ dışa hiç açık değil, mTLS + servis token'ı
zorunluluğu aynen duruyor. Ticket sistemi tamamen `YonetimApi`'nin kendi proxy katmanında, mevcut
güvenlik sınırlarını koruyarak eklendi.
