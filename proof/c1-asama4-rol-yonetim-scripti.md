# Kanıt: Faz C1 — Aşama 4 (Rol Yönetim Scripti)

**Tarih:** 2026-07-06
**Kapsam:** Rol atamasını Keycloak admin panelinden değil, kendi DB'mizden yönetebilecek minimal bir
araç — C1'in vaat ettiği asıl faydanın (yetki değişikliği için Keycloak realm JSON güncelleme/reimport
gerekmemesi, kullanıcının yeniden login olmasına gerek kalmaması) somut kanıtı.
**Durum:** ✅ Tamamlandı — Faz C1'in TÜM aşamaları (1: şema+backfill, 2: shadow mode, 3: cutover,
4: yönetim scripti) tamamlandı.

---

## Araç

`tools/manage-role-assignment.sh` — kapsam bilinçli olarak minimal (kullanıcı kararı: tam bir CRUD/
yönetim UI'ı değil, basit bir CLI script):
```
bash tools/manage-role-assignment.sh grant  <principal_id> <permission> <action> [scope]
bash tools/manage-role-assignment.sh revoke <principal_id> <permission> <action> [scope]
bash tools/manage-role-assignment.sh list   [principal_id]
```

## Bulunan ve Düzeltilen Bug: `psql -c` Variable Substitution Çalışmıyor

İlk tasarım, SQL injection riskini ortadan kaldırmak için `psql -v var=... -c "... :'var' ..."`
kullanıyordu. Canlı testte `ERROR: syntax error at or near ":"` alındı — `psql`'in `:'var'`
substitution mekanizması **sadece STDIN/script modunda çalışıyor, `-c` (tek komut) modunda
ÇALIŞMIYOR** (PostgreSQL 16.14'te doğrulanan gerçek davranış). Düzeltme: SQL, `-c` yerine heredoc ile
STDIN'e gönderilecek şekilde yeniden yazıldı — güvenlik (parametrize sorgu, string interpolation yok)
korunarak.

## Test — C1'in Asıl Vaat Ettiği Faydanın Kanıtı

Test kullanıcısı P022 (sadece `personnel.files.read.self` yetkisine sahip) ile gerçek bir login
yapılıp **TEK BİR cookie/session** alındı, bu cookie'nin TAMAMI test boyunca **yeniden login OLMADAN**
tekrar kullanıldı:

1. **Başlangıç:** P022 → `GET /api/personnel/P001/files` → `403` (henüz izin yok).
2. **Script ile yeni izin verildi** (Keycloak'a HİÇ dokunmadan):
   ```
   bash tools/manage-role-assignment.sh grant P022 personnel.files read all
   [OK] P022 -> personnel.files.read.all verildi.
   ```
3. **AYNI cookie ile (yeniden login yok)** → `GET /api/personnel/P001/files` → **`200`** — izin
   ANINDA etkili oldu, kullanıcının tekrar giriş yapmasına gerek kalmadı.
4. **Script ile izin geri alındı:**
   ```
   bash tools/manage-role-assignment.sh revoke P022 personnel.files read all
   [OK] P022 -> personnel.files.read.all iptal edildi.
   ```
5. **AYNI cookie ile tekrar** → `GET /api/personnel/P001/files` → **`403`** — erişim ANINDA kesildi.
6. **Kontrol:** P022'nin kendi kaydına erişimi (`self` scope) hâlâ `200` — sadece verilen/geri alınan
   izin etkilendi, diğer yetkiler bozulmadı.

Bu, rehberin C1 için vaat ettiği ana kazanımı somut olarak kanıtlıyor: **yetki değişikliği için ne
Keycloak realm JSON güncellemesi/reimport'u, ne de kullanıcının oturumunu yenilemesi gerekiyor.**

DB son durumu (`list P022`):
```
 principal_id |   permission    | action | scope |        granted_by         | revoked_at
--------------+-----------------+--------+-------+----------------------------+------------
 P022         | personnel.files | read   | all   | manage-role-assignment.sh  | (dolu — revoke edildi)
 P022         | personnel.files | read   | self  | backfill-c1-asama1         | (boş — hâlâ aktif)
```

## Tam Regresyon

`tools/server-smoke-test.sh` → 23/23. `tools/server-safe-test-suite.sh` → 36/36, 0 hata.
`tools/shadow-parity-test.sh` (31 kullanıcı, 60 senaryo) → 60/60, temiz durum doğrulandı.
`platform-backup.service` (+ restore-test) → `ExecMainStatus=0`.

## Faz C1 — Genel Özet (Tüm Aşamalar Tamamlandı)

| Aşama | İçerik | Kanıt |
|---|---|---|
| 1 | Şema + Keycloak'tan backfill, kod değişmedi | `proof/c1-asama1-sema-backfill.md` |
| 2 | Shadow mode — DB paralel hesaplanır, JWT karar verir | `proof/c1-asama2-shadow-mode.md` |
| 3 | Cutover — DB karar verir, Keycloak rolleri silinmedi | `proof/c1-asama3-cutover.md` |
| 4 | Rol yönetim scripti — Keycloak'a dokunmadan grant/revoke | bu dosya |

**Bilinçli kalan sınır:** Keycloak'tan `realmRoles`/rol mapper'ının tamamen kaldırılması bu planın
kapsamı DIŞINDA bırakıldı (kullanıcının açık kararı) — cutover birkaç test turu boyunca sorunsuz
kaldıktan sonra ayrı bir karar noktası olarak ele alınacak.
