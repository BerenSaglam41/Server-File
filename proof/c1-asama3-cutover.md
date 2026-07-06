# Kanıt: Faz C1 — Aşama 3 (Cutover)

**Tarih:** 2026-07-06
**Kapsam:** Yetki kararının kaynağını GERÇEKTEN Keycloak JWT'den kendi PostgreSQL DB'mize (`yonetim.role_assignments`) çevirmek.
**Durum:** ✅ Tamamlandı — önce YonetimApi, ayrı test turuyla sonra OpsApi cutover edildi. Keycloak realm
rolleri SİLİNMEDİ (rollback güvenliği). Her iki cutover da hem "davranış değişmedi" hem "karar
gerçekten DB'den geliyor" açısından ayrı ayrı kanıtlandı.

---

## Mekanizma

`docker-compose.yml`'e her iki servis için `Authorization__RoleSource` env var'ı eklendi:
- `Jwt`: sadece JWT (Aşama 1 öncesi davranış, rollback için).
- `Shadow`: ikisi de hesaplanır, JWT karar verir (Aşama 2, önceki varsayılan).
- `Db`: ikisi de hesaplanır (gözlem kaybolmasın diye mismatch loglaması devam eder), ama artık **DB
  karar verir** (Aşama 3 cutover).

`YonetimApi/Services/PermissionService.cs` ve `OpsApi/Infrastructure/OpsRoleAuthorizationHandler.cs`,
`IConfiguration`'dan bu flag'i okuyup son kararı buna göre veriyor. DB sorgusu hata verirse ya da
`principalId` boşsa, `dbResult` JWT sonucuna düşüyor (fail-to-previous-known-good, fail-open değil).

## Deploy Sırası (kademeli, tek seferde değil)

1. **YonetimApi** — önce `Shadow` flag'iyle deploy edildi (davranış aynı kaldığı doğrulandı: smoke
   23/23), SONRA `Db`'ye çevrilip yeniden deploy edildi.
2. **OpsApi** — aynı desen: önce `Shadow` (smoke 23/23 aynı), sonra `Db`.

Bu sıralama, iki servisi aynı anda çevirmemek için — bir sorun çıkarsa hangi tarafta olduğunu anında
ayırt etmeyi sağladı (plan gereği).

## Test 1 — Davranış Değişmedi (60 senaryo, YonetimApi cutover sonrası)

`tools/shadow-parity-test.sh` tekrar çalıştırıldı: **60/60 OK, 0 HATA** — Aşama 2'deki sonuçla birebir
aynı (all/team/self scope'ların hepsi, pozitif+negatif senaryolar).

## Test 2 — YonetimApi: Kararın GERÇEKTEN DB'den Geldiğinin Kanıtı (kullanıcı izniyle)

"60/60 aynı sonuç" tek başına yeterli değildi — Shadow modda da aynı sonucu alırdım. Kullanıcı
izniyle P001'in DB rolü GEÇİCİ revoke edildi (JWT'de hâlâ geçerli, Keycloak'a dokunulmadı):
```
p001 -> GET /api/personnel/P001/files  ->  http:403
```
**Reddedildi** — JWT hâlâ izin verse bile, artık DB kararı geçerli (Aşama 2'de aynı test `200`
döndürmüştü, çünkü o zaman karar hâlâ JWT'denydi). Rol hemen geri eklendi, doğrulandı (`revoked_at`
NULL), tam test seti tekrar 60/60 temiz.

## Test 3 — OpsApi: Kararın GERÇEKTEN DB'den Geldiğinin Kanıtı (kullanıcı izniyle)

Aynı desen — `opsuser01`'in DB'deki `ops.read` rolü GEÇİCİ revoke edildi (JWT'de hâlâ geçerli):
```
opsuser01 -> GET /ops/health  ->  http:404
```
**Reddedildi** (mevcut 403→404 bilgi-sızdırmama davranışı — `OpsForbidToNotFoundHandler` — korunmuş
şekilde çalıştı). Rol hemen geri eklendi, tam test seti tekrar 60/60 temiz.

## Tam Regresyon (cutover sonrası, her iki servis Db modunda)

`tools/server-smoke-test.sh` → 23/23. `tools/server-safe-test-suite.sh` → 36/36, 0 hata.
`platform-backup.service` (+ restore-test) → `ExecMainStatus=0`, `Result=success`.

## Rollback Güvenliği

`keycloak/realm-platform.json`'daki realm rolleri ve kullanıcı atamaları HİÇ silinmedi/değiştirilmedi.
Bir sorun çıkarsa, `Authorization__RoleSource` env var'ı `Jwt` veya `Shadow`'a geri çevrilip servis
yeniden başlatılarak ANINDA eski davranışa dönülebilir — kod veya veri değişikliği gerekmez.

## Sıradaki (ayrı karar noktası — bu planın kapsamı dışında)

Aşama 4 — `tools/manage-role-assignment.sh` (basit grant/revoke script'i, DB'den rol yönetimi).
Keycloak'tan `realmRoles`/mapper'ın tamamen kaldırılması ise **planın açık kararı gereği** bu işin
parçası değil — cutover birkaç test turu boyunca sorunsuz kaldıktan sonra ayrı bir konuşmanın konusu.
