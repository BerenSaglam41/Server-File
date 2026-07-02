# Kanıt: Gateway Rate Limit + Ticket Log Maskeleme

**Tarih:** 2026-07-02
**Kapsam:** `/files/download/{ticket}` — IP başına rate limit + access/error log'da ticket'ın maskelenmesi
**Durum:** ✅ 3 ayrı sızıntı noktası bulunup kapatıldı, tam regresyon temiz

---

## Neden

`proof/download-ticket-lease-model.md`'deki "Ek Doğrulama Turu"nda (kullanıcının 7 sorusu) 2 gerçek,
düzeltilmemiş eksik bulunmuştu:

1. `/files/download/` için Gateway seviyesinde hiçbir rate limit yoktu.
2. Ham ticket değeri, Gateway'in access log'unda düz metin olarak görünüyordu.

Kullanıcı bu ikisinin **düzgün şekilde** düzeltilmesini ve **ayrıca kanıtlanmasını** istedi — bu belge o
kanıt. (Bu belge, önce `download-ticket-lease-model.md`'ye kısa bir ek olarak yazılmıştı; kullanıcının
"bunu da ayrı test edip ayrı prooflayalım" isteği üzerine kendi başına, daha kapsamlı bir belgeye taşındı.)

## Tasarım

```nginx
# http seviyesi
limit_req_zone $binary_remote_addr zone=download_limit:10m rate=30r/s;
limit_req_status 429;
limit_req_log_level notice;

map $ticket $ticket_masked {
    "~^(.{8})" "$1...";
    default    "(yok)";
}
log_format download_masked
    '$remote_addr - - [$time_local] "$request_method /files/download/$ticket_masked '
    '$server_protocol" $status $body_bytes_sent "$http_referer" "$http_user_agent"';
```

- **Rate limit:** IP başına (`$binary_remote_addr`), `rate=30r/s`, `burst=50 nodelay`. `burst` değeri
  bilinçli seçildi: gerçek testlerimizin en yoğunu (lease modelindeki 25 eşzamanlı istek,
  `proof/download-ticket-lease-model.md`) bunun altında kalıyor — test yaparken kendimizi
  engellemiyoruz, ama gerçek kötüye kullanım (saniyede yüzlerce istek) sınırlanıyor.
- **Log maskeleme:** `map` ile `$ticket`'ın ilk 8 karakteri dışı `"..."` ile değiştiriliyor. 8 karakter
  (256-bit'lik ticket'ın ~48 biti), brute-force/yeniden oluşturma riski taşımayacak kadar az, ama "bu
  ticket'a ait bir istek oldu mu" sorusuna temel bir ops-görünürlüğü sağlayacak kadar çok.

## Bulunan ve Düzeltilen 3 Sızıntı Noktası

nginx'in **internal redirect** mekanizması (`X-Accel-Redirect` ve `error_page`), bir isteğin YANITINI
ORİJİNAL location'dan FARKLI bir location'da bitirebiliyor — bu durumda ORİJİNAL location'a konan
`access_log` hiç uygulanmıyor. Bu, tek tek test edilerek 3 ayrı noktada bulundu:

| # | Yanıt türü | Yanıtı asıl bitiren yer | İlk denemede maskeli miydi? |
|---|---|---|---|
| 1 | 403 (yanlış CN) / 404 (geçersiz/süresi dolmuş ticket) | `/files/download/` (kendi context'i) | ✅ Evet, ilk denemede çalıştı |
| 2 | 200/206/304 (başarılı, X-Accel-Redirect) | `/protected-download/` (internal, X-Accel hedefi) | ❌ Hayır — bulunup düzeltildi |
| 3 | 429 (rate limit aşımı) | `@rate_limited` (internal, `error_page 429` hedefi) | ❌ Hayır — bulunup düzeltildi |

Ayrıca **4. bir sızıntı**: nginx'in `limit_req` modülünün kendi `error_log` diagnostik satırı, `log_format`
tarafından hiç etkilenmeyen, sabit formatlı, **her zaman tam ham request URI'sini** (ticket dahil) yazan
ayrı bir mekanizma. `limit_req_log_level notice;` ile bu mesajın seviyesi `error_log`'un varsayılan
`error` eşiğinin altına düşürülüp tamamen susturuldu (diğer gerçek hata logları etkilenmedi).

### Nokta 2 — X-Accel Hedefi (`/protected-download/`) Nasıl Bulundu

İlk denemede `access_log`'u sadece `/files/download/` location'ına eklemiştim; gerçek bir ticket ile
indirme yapıp log'a bakıldığında ticket **hâlâ tam hâliyle** görünüyordu. Kanıtlamak için geçici bir debug
header eklendi:
```nginx
# /files/download/ içinde:
add_header X-Debug-Ticket-Masked "here1-$ticket_masked" always;
# /protected-download/ içinde:
add_header X-Debug-Ticket-Masked "here2-$ticket_masked" always;
```
Gerçek isteğin yanıt header'ında **`here2-...`** çıktı — yani başarılı yanıtı asıl bitiren yer
`/protected-download/`'dı, `here1` (`/files/download/`) hiç devreye girmemişti. `access_log` de
`/protected-download/`'a taşınınca (debug header'lar kaldırılarak) maske gerçekten uygulandı.

### Nokta 3 — Rate Limit Hedefi (`@rate_limited`) Nasıl Bulundu

Nokta 2 düzeltildikten sonra "artık hepsi tamam" diye düşünülüp ek bir doğrulama turu yapıldı (kullanıcının
"ilerde bunun da sıkıntısına düşmeyelim" isteği üzerine): 200 eşzamanlı sahte istekle rate limit'i bilerek
aşıp 429 yanıtlarının log'unu kontrol ettik — **tam ticket hâlâ görünüyordu**:
```
"GET /files/download/rate-limit-test-ticket-58 HTTP/1.1" 429 53 ...
```
Kök neden Nokta 2 ile birebir aynı desendeydi: `limit_req` bir isteği reddettiğinde, `error_page 429`
direktifi isteği `@rate_limited` adlı, `server` seviyesinde tanımlı, **ayrı** bir named location'a internal
olarak yönlendiriyor — bu, `/files/download/`'un access_log'unu yine atlıyor. Maskeli `access_log`
`@rate_limited`'a da eklenince düzeldi.

### Nokta 4 — `limit_req`'in Kendi `error_log`'u Nasıl Bulundu

Nokta 3'ü doğrularken `docker logs` çıktısında access_log satırlarının YANINDA şu satırlar da görüldü:
```
2026/07/02 13:34:55 [error] 22#22: *220 limiting requests, excess: 50.240 by zone "download_limit",
client: 172.18.0.1, server: _, request: "GET /files/download/rate-limit-test-ticket-65 HTTP/1.1", ...
```
Bu, `log_format`/`access_log` ile hiç ilgisi olmayan, `limit_req` modülünün **kendi** diagnostik mesajı —
tam request satırını (ticket dahil) `error_log`'a yazıyor. `limit_req_log_level notice;` ile susturuldu.

## Testler — Hepsi Geçti

### Test 1 — Rate limit gerçekten tetikleniyor mu

200 eşzamanlı istek (sahte ticket'larla, saf throttle testi) → **karışık `404`/`429`** sonuçlar, çok
sayıda `429` (rate limit gerçekten devrede). Daha küçük denemelerde (120 istek) `57×429, 63×404` net
görüldü.

### Test 2 — Rate limit, gerçek kullanım senaryomuzu engellemiyor mu

25 eşzamanlı istek (gerçek, geçerli TEK ticket'a, lease/max-uses testindeki gibi) →
**tam olarak `20×200, 5×404`** — rate limit hiç araya girmedi, lease modelinin kendi `max-uses=20`
sınırıyla birebir aynı sonuç. `burst=50` seçimi doğrulandı.

### Test 3 — Access log maskeleme, 3 farklı yanıt türünde de doğru mu

- **404 (geçersiz ticket, `/files/download/` kendi context'i):** `"GET /files/download/bu-gecer... HTTP/1.1" 404 0"` — maskeli. ✅
- **200 (başarılı, X-Accel → `/protected-download/`):** Gerçek ticket `2m5chPOWV13BGfAXeL86ALZEyQjcyqk9PhOptVjZVvk` ile indirme → log'da `"GET /files/download/2m5chPOW... HTTP/1.1" 200 110567"` — tam ticket **yok**. ✅
- **429 (rate limit, `@rate_limited`):** `"GET /files/download/error-lo... HTTP/1.1" 429 53"` — maskeli. ✅

### Test 4 — `error_log`'daki `limiting requests` satırı susturuldu mu

`limit_req_log_level notice;` eklendikten sonra 200 eşzamanlı istekle rate limit tekrar tetiklendi
(çok sayıda `429` alındı, doğrulandı), ama `docker logs ... | grep "limiting requests"` **boş** döndü —
mesaj artık hiç yazılmıyor. Düzeltme öncesi aynı test aynı mesajı defalarca üretiyordu.

### Test 5 — Temiz (cold) restart sonrası davranış değişmiyor mu

Config, iteratif debug turlarında `docker compose up -d --build gateway` ile defalarca deploy edildi —
bunun "sıcak" bir container state'ine bağlı bir yan etki olup olmadığını elemek için `docker compose
restart gateway` ile **tam bir restart** yapıldı, ardından `tools/server-smoke-test.sh` çalıştırıldı:
**23/23 `[OK]`**, davranış değişmedi.

### Regresyon

- `tools/server-smoke-test.sh` → 23/23 `[OK]` (düzeltmelerin her aşamasında + final cold-restart sonrası tekrar tekrar çalıştırıldı).
- `tools/server-safe-test-suite.sh` → 36/36 `[OK]`, 0 `[HATA]` (403 yetkilendirme matrisi, 3.8MB dosya indirme, 20 eşzamanlı login dahil).
- `platform-backup.service` (+ otomatik `restore-test`) → `ExecMainStatus=0`.

## Bilinçli Kalan Sınır

Log'da ticket'ın **ilk 8 karakteri** hâlâ görünüyor (tamamen `"(yok)"` değil) — temel ops-görünürlüğü
için bilinçli bir denge; 256-bit'in ~48 bitlik bir kısmı olduğundan pratik brute-force/tahmin riski
taşımıyor. Tamamen sıfırlamak istenirse `map`'in değeri `"(yok)"` olarak sabitlenebilir, ama bu durumda
"loglardan bu path'e hiç trafik oldu mu" sorusuna bile cevap veremeyiz — V1 için mevcut denge tercih
edildi.

## Deploy ve Senkronizasyon

`nginx/nginx.conf` üzerinde 4 ayrı iteratif düzeltme turu yapıldı (her turda `scp` ile sunucuya kopyalanıp
`docker compose up -d --build gateway` ile canlıya alındı, `docker logs` ile temiz başladığı doğrulandı).
Son hal `git commit` + `git push origin main` ile GitHub'a gönderildi, sunucuda `git pull` ile senkronize
edildi — üç tarafın (local/origin/server) `git rev-parse HEAD` değeri birebir aynı.
