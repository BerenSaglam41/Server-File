#!/usr/bin/env bash
# Faz C1 (Aşama 1) — keycloak/realm-platform.json'daki 32 kullanıcının mevcut realmRoles
# atamalarını yonetim.role_assignments tablosuna BİRE BİR aktarır. Kod hiç dokunulmaz,
# davranış hiç değişmez — bu, sadece göçün ilk adımı (bkz. proof/c1-asama1-sema-backfill.md).
#
# Idempotent: ON CONFLICT DO NOTHING sayesinde tekrar çalıştırmak güvenlidir.
#
# Kullanım (proje kök dizininden, docker-compose.yml'in bulunduğu yerden):
#   bash tools/backfill-role-assignments.sh
#
# NOT: Bu dosyadaki liste, keycloak/realm-platform.json'un 2026-07-03 tarihli halinden
# üretildi (32 kullanıcı, 34 rol ataması). Realm dosyası değişirse bu liste elle
# güncellenmeli ya da aynı çıkarım mantığıyla yeniden üretilmelidir.

set -euo pipefail

docker compose exec -T postgres psql -U platform -d platformdb <<'SQL'
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('HR001', 'personnel.files', 'read', 'all', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('HR001', 'personnel.files', 'write', 'all', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('ADM001', 'personnel.files', 'read', 'all', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('ADM001', 'personnel.files', 'write', 'all', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('M001', 'personnel.files', 'read', 'team', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('M002', 'personnel.files', 'read', 'team', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('M003', 'personnel.files', 'read', 'team', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P001', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P002', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P003', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P004', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P005', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P006', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P007', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P008', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P009', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P010', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P011', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P012', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P013', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P014', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P015', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P016', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P017', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P018', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P019', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P020', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P021', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P022', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P023', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('P024', 'personnel.files', 'read', 'self', 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('OPSADMIN', 'ops', 'read', NULL, 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('OPSADMIN', 'ops', 'admin', NULL, 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by) VALUES ('OPSUSER01', 'ops', 'read', NULL, 'backfill-c1-asama1') ON CONFLICT (principal_id, permission, action, COALESCE(scope, '')) DO NOTHING;

SELECT count(*) AS toplam_atama FROM yonetim.role_assignments;
SQL
