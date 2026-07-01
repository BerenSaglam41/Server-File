-- Ops operasyon audit tablosu — yonetim.audit_events'tan tamamen bağımsız şema
CREATE SCHEMA IF NOT EXISTS ops;

CREATE TABLE IF NOT EXISTS ops.audit_events (
    id             BIGSERIAL    PRIMARY KEY,
    actor          TEXT         NOT NULL,           -- kullanıcı adı ya da 'anonymous'
    action         TEXT         NOT NULL,           -- ops.health.read, ops.backups.list, ...
    result         TEXT         NOT NULL,           -- success | denied | error
    reason_code    TEXT,                            -- ops_role_missing | no_token | NULL
    correlation_id TEXT,
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    ip             TEXT,
    path           TEXT,
    method         TEXT,
    duration_ms    INTEGER
);

CREATE INDEX IF NOT EXISTS ops_audit_created_at_idx ON ops.audit_events (created_at DESC);
CREATE INDEX IF NOT EXISTS ops_audit_actor_idx      ON ops.audit_events (actor);
CREATE INDEX IF NOT EXISTS ops_audit_action_idx     ON ops.audit_events (action);
