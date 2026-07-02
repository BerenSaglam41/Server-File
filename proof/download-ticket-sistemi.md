# Kanıt: Opak, Tek Kullanımlık İndirme Ticket Sistemi

**Tarih:** 2026-07-02
**Kapsam:** Personel dosyaları (YonetimApi) — fileId bazlı indirme
**Durum:** ✅ Tüm testler geçti, canlı sunucuda doğrulandı

---

## Ne Test Edildi ve Neden

Amaç: uzun ömürlü oturum cookie'sine bağımlı olmadan, **tek bir dosyaya bağlı, kısa ömürlü, tek kullanımlık,
en az 256-bit entropili opak bir ticket** ile indirme yapılabildiğini kanıtlamak. Bu, S3 presigned URL /
Google Signed URL gibi endüstride yaygın bir "imzalı tek kullanımlık link" deseni.

Test edilen 7 senaryo ve gerekçeleri:

| # | Senaryo | Neyi kanıtlıyor |
|---|---|---|
| A | Ticket oluşturma | Yetkili kullanıcı gerçekten opak, rastgele bir ticket alabiliyor |
| B | Cookie'siz tüketim | Ticket, oturum kimliğinin yerine gerçekten geçebiliyor |
| C | Aynı ticket'ı tekrar kullanma | Tek-kullanımlık garantisi gerçekten çalışıyor |
| D | Uydurma ticket | Sistem var olmayan bir ticket için bilgi sızdırmıyor |
| E | Süresi dolmuş ticket | Kısa ömür (60 sn) gerçekten uygulanıyor |
| F | Yetkisiz kullanıcı ticket istemeye çalışıyor | RBAC, ticket oluşturmadan önce devrede |
| G | Yetkili kullanıcı kendi dosyası için ticket istiyor | Pozitif kontrol — sistem normal durumda çalışıyor |

## Ortam

- Sunucu: `192.168.64.5` (api sunucu), `docker compose -f docker-compose.yml`
- Test kullanıcıları: `hr001` (read.all), `p001` (read.self)
- Endpoint'ler: `POST /api/personnel/{personnelId}/files/{fileId}/download-ticket`, `GET /api/personnel/download/{ticket}`

## Test A — Ticket Oluşturma

```bash
curl -k -sS -b "$HR" -X POST "https://localhost:5090/api/personnel/P001/files/$FILE_ID/download-ticket"
```

**Sonuç:**
```json
{"ticket":"GAgB3stR2Ed3u1-NYAo4XnsqqHPwqHwe2co-8SSovT8","expiresInSeconds":60,"downloadUrl":"/api/personnel/download/GAgB3stR2Ed3u1-NYAo4XnsqqHPwqHwe2co-8SSovT8"}
```

Ticket 32 byte (256 bit) rastgele veriden üretilip base64url ile encode edilmiş — URL-safe, entropi yeterli.

## Test B — Cookie Olmadan Tüketim

```bash
curl -k -sS -o /tmp/ticket-download.bin -w "http:%{http_code}\n" "https://localhost:5090/api/personnel/download/$TICKET"
```

**Sonuç:** `http:200`, dosya boyutu `110567` byte — normal cookie tabanlı indirmeyle aynı dosya, **hiçbir
`-b` (cookie) parametresi kullanılmadan.**

## Test C — Tekrar Kullanım Reddi

```bash
curl -k -sS -o /dev/null -w "http:%{http_code}\n" "https://localhost:5090/api/personnel/download/$TICKET"
```

**Sonuç:** `http:404` — DB'de `UPDATE ... WHERE used_at IS NULL` sorgusu ikinci denemede 0 satır etkiledi,
atomik tek-kullanım garantisi çalışıyor.

## Test D — Uydurma Ticket

```bash
curl -k -sS -o /dev/null -w "http:%{http_code}\n" "https://localhost:5090/api/personnel/download/uydurma-ticket-123"
```

**Sonuç:** `http:404` — var olmayan/uydurma bir ticket için de aynı, bilgi sızdırmayan cevap.

## Test E — Süre Dolması

```bash
TICKET=$(...)  # 60 saniyelik ticket
sleep 65
curl -k -sS -o /dev/null -w "http:%{http_code}\n" "https://localhost:5090/api/personnel/download/$TICKET"
```

**Sonuç:** `http:404` — 65 saniye sonra (60 saniyelik ömrü aşmış) ticket reddedildi.

## Test F — RBAC Reddi

```bash
# p001, P002'nin path'i uzerinden ticket istiyor
curl -k -sS -b "$P1" -X POST "https://localhost:5090/api/personnel/P002/files/$FILE_ID/download-ticket"
```

**Sonuç:** `{"error":"forbidden","reason":"access_denied"} http:403` — ticket **hiç oluşturulmadı**
(aşağıdaki DB sorgusuyla doğrulandı, bu deneme için satır yok).

## Test G — Pozitif Kontrol

```bash
curl -k -sS -b "$P1" -X POST "https://localhost:5090/api/personnel/P001/files/$FILE_ID_P1/download-ticket"
```

**Sonuç:** `http:200`, ticket başarıyla üretildi — p001 kendi dosyası için sorunsuz çalışıyor.

## DB Doğrulaması — Audit

```sql
select action, result, reason_code, created_at from yonetim.audit_events
where action ilike '%Ticket%' order by created_at desc limit 10;
```

```
             action              | result  |  reason_code  |          created_at
----------------------------------+---------+---------------+-------------------------------
 PersonnelDownloadTicketCreated  | success |               | 2026-07-02 08:46:15.767991+00
 PersonnelDownloadTicketCreated  | denied  | access_denied | 2026-07-02 08:46:15.739745+00
 PersonnelDownloadTicketCreated  | success |               | 2026-07-02 08:44:36.781948+00
 PersonnelDownloadTicketConsumed | success |               | 2026-07-02 08:44:17.643128+00
 PersonnelDownloadTicketCreated  | success |               | 2026-07-02 08:44:17.601354+00
(5 rows)
```

## DB Doğrulaması — Ticket Tablosu

```sql
select personnel_id, expires_at, used_at, created_at from yonetim.download_tickets
order by created_at desc limit 10;
```

```
 personnel_id |          expires_at           |            used_at            |          created_at
--------------+-------------------------------+-------------------------------+-------------------------------
 P001         | 2026-07-02 08:47:15.767178+00 |                               | 2026-07-02 08:46:15.767346+00
 P001         | 2026-07-02 08:45:36.780786+00 |                               | 2026-07-02 08:44:36.781112+00
 P001         | 2026-07-02 08:45:17.588391+00 | 2026-07-02 08:44:17.624749+00 | 2026-07-02 08:44:17.591768+00
```

**Doğrulanan:** Toplam 3 satır — Test F'nin (403 reddi) hiç satır oluşturmadığı, Test C'de tüketilen
ticket'ın `used_at` alanının dolu olduğu, Test E'nin süresi geçmiş ticket'ının `used_at` alanının boş
kaldığı (hiç tüketilmedi, sadece süresi doldu) doğrulandı.

## Regresyon Kontrolü

Değişiklik sonrası `tools/server-smoke-test.sh` baştan sona tekrar çalıştırıldı — 23/23 adım `[OK]`,
hiçbir regresyon yok.

## Mimari Not

`FileServiceApi` bu değişiklikten **etkilenmedi** — hâlâ dışa hiç açık değil, mTLS + servis token'ı
zorunluluğu aynen duruyor. Ticket sistemi tamamen `YonetimApi`'nin kendi proxy katmanında, mevcut
güvenlik sınırlarını koruyarak eklendi.
