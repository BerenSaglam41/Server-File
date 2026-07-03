# Kanıt: Faz C1 — Aşama 1 (Şema + Backfill, Davranış Değişmedi)

**Tarih:** 2026-07-03
**Kapsam:** `platform-mimarisi-stajyer-rehberi.txt` bölüm 6.2 — "Keycloak realm/client role'leri nihai
uygulama yetki kaynağı değildir" hedefine kademeli göçün İLK aşaması.
**Durum:** ✅ Tamamlandı. Kod hiç dokunulmadı, davranış hiç değişmedi — sadece yeni bir tablo eklendi ve
Keycloak'taki mevcut rol atamaları bu tabloya bire bir kopyalandı.

---

## Bağlam

C1'in hedefi: yetki (permission/rol) kararının kaynağını Keycloak realm role'lerinden kendi PostgreSQL
DB'mize taşımak. Kullanıcı açıkça **kademeli, her adımda gerçek testlerle doğrulanan** bir göç istedi —
bu yüzden Aşama 1, kod DEĞİŞTİRMEDEN sadece hedef şemayı kurup mevcut Keycloak verisini oraya
kopyalıyor. `PermissionService.cs`/`OpsApi` hâlâ tamamen eskisi gibi JWT `roles` claim'ine bakıyor.

## Şema

`db/docker-init/08-role-assignments.sql`:
```sql
CREATE TABLE yonetim.role_assignments (
  id, principal_id, permission, action, scope, granted_by, granted_at, revoked_at
);
CREATE UNIQUE INDEX uq_role_assignments_identity
  ON yonetim.role_assignments(principal_id, permission, action, COALESCE(scope, ''));
```

## Bulunan ve Düzeltilen Bug: NULL Scope Idempotency Kırılması

İlk tasarımda `UNIQUE (principal_id, permission, action, scope)` sıradan constraint kullanılmıştı.
**Canlı idempotency testinde** (`backfill-role-assignments.sh` ikinci kez çalıştırıldığında) beklenen
34 satır yerine **37** satır çıktı. Kök neden: SQL standardında `NULL != NULL` — sıradan `UNIQUE`
constraint, `scope IS NULL` olan satırları (ops rolleri, `scope` kullanılmıyor) asla çakışma olarak
görmüyor, `ON CONFLICT DO NOTHING` bu satırlarda hiç tetiklenmiyor, her çalıştırmada 3 ops satırı
(OPSADMIN×2, OPSUSER01×1) yeniden ekleniyordu. Doğrulama: `GROUP BY ... HAVING count(*)>1` ile tam 3
mükerrer grup (hepsi `scope IS NULL`) bulundu.

**Düzeltme:** `COALESCE(scope, '')` expression'ı üzerinden bir `UNIQUE INDEX` tanımlandı — NULL'lar da
artık tekilliğe dahil oluyor. Tablo `TRUNCATE` edilip backfill **3 kez üst üste** çalıştırıldı, üçünde
de tam **34 satır**, sıfır mükerrer doğrulandı.

## Backfill

`tools/backfill-role-assignments.sh` — `keycloak/realm-platform.json`'daki 32 kullanıcının mevcut
`realmRoles` atamaları (bir Python script ile realm JSON'undan çıkarılıp) bire bir INSERT edildi.

**Sonuç dağılımı (canlı DB'den, gerçek sorgu):**
```
    permission    | action | scope | count
-----------------+--------+-------+-------
 ops             | admin  |       |     1
 ops             | read   |       |     2
 personnel.files | read   | all   |     2
 personnel.files | read   | self  |    24
 personnel.files | read   | team  |     3
 personnel.files | write  | all   |     2
```
Toplam 34 — Keycloak realm export'undaki toplam rol atama sayısıyla birebir eşleşiyor.

## Test (gerçek, davranış değişmediğini kanıtlayan)

- `tools/server-smoke-test.sh` → **23/23 `[OK]`** (şema+backfill öncesiyle birebir aynı sonuç).
- `tools/server-safe-test-suite.sh` → **36/36 `[OK]`**, 0 `[HATA]`.
- İdempotency: backfill script'i 3 kez üst üste çalıştırıldı, her seferinde tam 34 satır.

Kod hiçbir yere dokunulmadığı için (henüz `PermissionService`/`OpsApi` DB'yi okumuyor) davranışın
değişmemesi zaten beklenen bir sonuçtu — ama gerçek smoke/safe-test-suite ile TEYİT edildi, varsayılmadı.

## Sıradaki (Aşama 2 — Shadow Mode)

`DbRoleSource` eklenir, JWT-tabanlı VE DB-tabanlı sonuç paralel hesaplanıp karşılaştırılır — karar hâlâ
JWT'den verilir, sadece uyuşmazlıklar loglanır. 32 kullanıcının hepsiyle gerçek login + istek testi
yapılıp sıfır mismatch kanıtlanacak. Bir sonraki aşamaya geçmeden önce kullanıcı onayı alınacak.
