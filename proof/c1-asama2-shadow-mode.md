# Kanıt: Faz C1 — Aşama 2 (Shadow Mode)

**Tarih:** 2026-07-03
**Kapsam:** Yetki kararının kaynağını Keycloak'tan DB'ye taşımadan ÖNCE, DB-tabanlı hesaplamanın
JWT-tabanlı kararla birebir örtüştüğünü gerçek trafikte kanıtlamak.
**Durum:** ✅ Tamamlandı — karar hâlâ JWT'den, DB paralel hesaplanıyor, 31 kullanıcının hepsiyle
(pozitif+negatif senaryolarla) test edildi, sıfır gerçek mismatch, mekanizmanın kendisi de ayrıca
kanıtlandı (kasıtlı bozulma testiyle).

---

## Yapılan Değişiklikler

### YonetimApi — `PermissionService.cs`
`HasPermissionAsync`, KARAR için hâlâ `HasPermissionViaJwtAsync`'i (eski `HasRole`/JWT mantığı, değişmedi)
çağırıyor. Ayrıca `HasPermissionViaDbAsync` (aynı all→team→self önceliğiyle, `yonetim.role_assignments`
sorgulayan) paralel çalıştırılıyor; `jwtResult != dbResult` olursa `logger.LogWarning("ROLE_SHADOW_MISMATCH
...")` yazılıyor. Dönüş değeri HER ZAMAN `jwtResult` — davranış etkilenmiyor.

### OpsApi — statik `HasOpsRole`'dan `AuthorizationHandler`'a geçiş
`RequireAssertion` senkron çalıştığı için DB sorgusu (async) yapamıyordu — bu yüzden yeni
`OpsApi/Infrastructure/OpsRoleAuthorizationHandler.cs` (`AuthorizationHandler<OpsRoleRequirement>`)
eklendi, DI ile `NpgsqlDataSource`/`ILogger` alıyor. JWT mantığı (`realm_access.roles` parse) birebir
taşındı, DB sorgusu (`permission='ops'`) paralel çalıştırılıp aynı şekilde loglanıyor. `Program.cs`'teki
policy tanımları `RequireAssertion` yerine `AddRequirements(new OpsRoleRequirement(...))` kullanıyor.

## Test 1 — 31 Kullanıcının Hepsiyle Gerçek Login + İstek

`tools/shadow-parity-test.sh` — `keycloak/realm-platform.json`'daki TÜM 31 rol sahibi kullanıcı
(hr001, adm001, m001-m003, p001-p024, opsadmin, opsuser01 — fleetuser hariç, hiç rolü yok) için GERÇEK
Keycloak login yapıldı, hem pozitif (kendi/ekibi/herkes — 200 beklenir) hem negatif (başkası/ekip
dışı — 403 beklenir) istekler atıldı:

```
=== Sonuç: 60 OK, 0 HATA ===
```

Tüm senaryolar (all-scope×2×2, team-scope×3×2, self-scope×24×2, ops×2) doğru response kodunu döndürdü.

**Bulunan/düzeltilen script hatası (test yazarken):** İlk çalıştırmada `opsuser01` login'i `401` verdi —
script yanlış varsayılan parola (`ops123`, sadece `opsadmin` için doğru) kullanıyordu. `DEMO_HESAPLAR.md`
kontrol edilip `opsuser01`'in gerçek (dokümante edilmiş) parolası kullanılarak düzeltildi.

## Test 2 — Sıfır Mismatch Doğrulaması

```
docker compose logs yonetimapi opsapi --since 1m | grep -c ROLE_SHADOW_MISMATCH
0
```
31 kullanıcı, 60 istek — hiçbirinde JWT/DB kararı ayrışmadı.

## Test 3 — Mekanizmanın Gerçekten Çalıştığının Kanıtı (kullanıcı izniyle, kasıtlı bozulma)

"Sıfır mismatch" sonucunun "hiç mismatch yok" mu yoksa "shadow kontrolü hiç çalışmıyor" mu olduğunu
ayırt etmek için — kullanıcı onayıyla — P001'in DB'deki rolü GEÇİCİ olarak `revoked_at=now()` yapıldı
(JWT'de hâlâ geçerli):

1. P001 ile giriş yapılıp kendi kaydına istek atıldı: **`http:200`** (JWT kararına göre, davranış
   ETKİLENMEDİ — shadow mode'un garantisi bu).
2. Log kontrol edildi:
   ```
   ROLE_SHADOW_MISMATCH principal=P001 permission=personnel.files action=read target=P001 jwt=True db=False
   ```
   Mekanizma GERÇEKTEN çalışıyor — uyuşmazlığı doğru yakaladı ve logladı.
3. P001'in rolü hemen geri eklendi (`revoked_at=NULL`), DB'den doğrulandı.
4. `shadow-parity-test.sh` tekrar çalıştırıldı: **60/60 OK**, sıfır mismatch (temiz duruma dönüldüğü
   kanıtlandı).

## Tam Regresyon

`tools/server-smoke-test.sh` → 23/23. `tools/server-safe-test-suite.sh` → 36/36, 0 hata.
`platform-backup.service` (+ restore-test) → `ExecMainStatus=0`, `Result=success`.

## Deploy

`YonetimApi/Services/PermissionService.cs`, `OpsApi/Program.cs`,
`OpsApi/Infrastructure/OpsRoleAuthorizationHandler.cs` (yeni) — yerelde derlendi (0 hata), `scp` ile
sunucuya kopyalanıp `docker compose up -d --build yonetimapi opsapi` ile canlıya alındı, healthcheck
zinciri doğru sırayla 9/9 servis healthy.

## Sıradaki (kullanıcı onayı bekleyecek)

Aşama 3 — Cutover: `RoleSource` flag'i `Db`'ye çevrilir (önce YonetimApi, sonra ayrı test turuyla
OpsApi). Keycloak realm rolleri SİLİNMEZ (rollback güvenliği). Aynı 60 senaryo tekrar çalıştırılıp
sonuçların birebir aynı kaldığı doğrulanacak.
