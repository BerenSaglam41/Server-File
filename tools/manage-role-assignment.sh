#!/usr/bin/env bash
# Faz C1 Aşama 4 — yonetim.role_assignments üzerinde basit grant/revoke/list aracı.
# Rol ataması artık Keycloak admin panelinden DEĞİL, buradan yönetilir (C1'in cutover'ından
# sonra Keycloak realm rolleri artık yetki kaynağı değil — bkz. proof/c1-asama3-cutover.md).
# Kapsam bilinçli olarak minimal tutuldu: tam bir CRUD/yönetim UI'ı DEĞİL, sadece grant/revoke/list.
#
# Kullanım (proje kök dizininden, docker-compose.yml'in bulunduğu yerden):
#   bash tools/manage-role-assignment.sh grant  <principal_id> <permission> <action> [scope]
#   bash tools/manage-role-assignment.sh revoke <principal_id> <permission> <action> [scope]
#   bash tools/manage-role-assignment.sh list   [principal_id]
#
# scope opsiyoneldir — personnel.files.* rolleri için self/team/all verilmeli, ops.* rolleri
# için scope YOKTUR (boş bırakılır, NULL olarak saklanır).
#
# Örnekler:
#   bash tools/manage-role-assignment.sh grant P022 personnel.files read all
#   bash tools/manage-role-assignment.sh revoke P022 personnel.files read all
#   bash tools/manage-role-assignment.sh grant OPSUSER02 ops read
#   bash tools/manage-role-assignment.sh list P022
#
# Güvenlik: değerler psql'in -v (:'var') mekanizmasıyla iletilir — SQL string interpolation
# YAPILMAZ, injection riski yok. ÖNEMLİ: psql'in :'var' substitution'ı sadece STDIN/script
# modunda çalışır, -c (tek komut) modunda ÇALIŞMAZ (canlı testte bulunan davranış) — bu yüzden
# SQL, heredoc ile stdin'e gönderiliyor, -c KULLANILMIYOR.

set -euo pipefail

cmd="${1:-}"
principal="${2:-}"
permission="${3:-}"
action="${4:-}"
scope="${5:-}"

usage() {
  echo "Kullanım:"
  echo "  bash tools/manage-role-assignment.sh grant  <principal_id> <permission> <action> [scope]"
  echo "  bash tools/manage-role-assignment.sh revoke <principal_id> <permission> <action> [scope]"
  echo "  bash tools/manage-role-assignment.sh list   [principal_id]"
  exit 1
}

case "$cmd" in
  grant)
    [ -z "$principal" ] || [ -z "$permission" ] || [ -z "$action" ] && usage
    docker compose exec -T postgres psql -U platform -d platformdb -v ON_ERROR_STOP=1 \
      -v principal="$principal" -v permission="$permission" -v action="$action" -v scope="$scope" <<'SQL'
INSERT INTO yonetim.role_assignments (principal_id, permission, action, scope, granted_by)
VALUES (:'principal', :'permission', :'action', NULLIF(:'scope', ''), 'manage-role-assignment.sh')
ON CONFLICT (principal_id, permission, action, COALESCE(scope, ''))
DO UPDATE SET revoked_at = NULL, granted_at = now(), granted_by = 'manage-role-assignment.sh';
SQL
    echo "[OK] $principal -> $permission.$action${scope:+.$scope} verildi."
    ;;
  revoke)
    [ -z "$principal" ] || [ -z "$permission" ] || [ -z "$action" ] && usage
    docker compose exec -T postgres psql -U platform -d platformdb -v ON_ERROR_STOP=1 \
      -v principal="$principal" -v permission="$permission" -v action="$action" -v scope="$scope" <<'SQL'
UPDATE yonetim.role_assignments SET revoked_at = now()
WHERE principal_id = :'principal' AND permission = :'permission' AND action = :'action'
  AND scope IS NOT DISTINCT FROM NULLIF(:'scope', '');
SQL
    echo "[OK] $principal -> $permission.$action${scope:+.$scope} iptal edildi."
    ;;
  list)
    if [ -n "$principal" ]; then
      docker compose exec -T postgres psql -U platform -d platformdb -v ON_ERROR_STOP=1 \
        -v principal="$principal" <<'SQL'
SELECT principal_id, permission, action, scope, granted_by, granted_at, revoked_at
FROM yonetim.role_assignments WHERE principal_id = :'principal' ORDER BY permission, action, scope;
SQL
    else
      docker compose exec -T postgres psql -U platform -d platformdb -v ON_ERROR_STOP=1 <<'SQL'
SELECT principal_id, permission, action, scope, revoked_at
FROM yonetim.role_assignments ORDER BY principal_id, permission, action, scope;
SQL
    fi
    ;;
  *)
    usage
    ;;
esac
