-- Faz C1 (Aşama 1) — yerel yetkilendirme modeline kademeli göç.
-- platform-mimarisi-stajyer-rehberi.txt bölüm 6.2: "Keycloak realm/client role'leri nihai
-- uygulama yetki kaynağı değildir." Bu tablo, Keycloak realm role'lerinin (personnel.files.*,
-- ops.*) yerini alacak kaynak — bu aşamada SADECE şema + backfill, kod hiç dokunulmuyor,
-- davranış hiç değişmiyor (bkz. proof/c1-asama1-sema-backfill.md).
CREATE TABLE IF NOT EXISTS yonetim.role_assignments (
  id           BIGSERIAL    PRIMARY KEY,
  principal_id VARCHAR(100) NOT NULL,  -- personnel_id/username — PermissionService.GetPersonnelId
                                        -- ile AYNI normalize kuralı (ToUpperInvariant)
  permission   VARCHAR(50)  NOT NULL,  -- 'personnel.files' | 'ops'
  action       VARCHAR(20)  NOT NULL,  -- read/write/execute/admin
  scope        VARCHAR(20),            -- self/team/all — ops rollerinde kullanılmaz, NULL
  granted_by   VARCHAR(100),
  granted_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
  revoked_at   TIMESTAMPTZ
);

-- ÖNEMLİ: sıradan bir UNIQUE(...) constraint NULL scope'u tekilliğe dahil etmez (SQL standardı,
-- NULL != NULL) — ops rollerinde scope her zaman NULL olduğu için sıradan UNIQUE burada ON
-- CONFLICT'i tetiklemez ve backfill'i tekrar çalıştırmak mükerrer satır üretir (canlı testte
-- yakalandı: idempotency testinde 34 yerine 37 satır çıktı). COALESCE(scope,'') expression
-- index'i ile NULL'lar da tekilliğe dahil edilir.
CREATE UNIQUE INDEX IF NOT EXISTS uq_role_assignments_identity
  ON yonetim.role_assignments(principal_id, permission, action, COALESCE(scope, ''));

CREATE INDEX IF NOT EXISTS idx_role_assignments_active
  ON yonetim.role_assignments(principal_id) WHERE revoked_at IS NULL;
