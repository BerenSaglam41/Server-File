-- Platform DB şeması — Docker init
-- files.* + yonetim.* + filo.* şemaları

CREATE SCHEMA IF NOT EXISTS files;
CREATE SCHEMA IF NOT EXISTS yonetim;
CREATE SCHEMA IF NOT EXISTS filo;

-- ── Trigger fonksiyonu ──────────────────────────────────────────────────────
CREATE OR REPLACE FUNCTION files.set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- ── files.app_policies ──────────────────────────────────────────────────────
CREATE TABLE files.app_policies (
  app_code            VARCHAR(50)   PRIMARY KEY,
  allowed_domains     TEXT[]        NOT NULL,
  allowed_file_types  TEXT[]        NOT NULL,
  can_create          BOOLEAN       NOT NULL DEFAULT false,
  can_read            BOOLEAN       NOT NULL DEFAULT false,
  can_archive         BOOLEAN       NOT NULL DEFAULT false,
  max_file_size_bytes BIGINT        NOT NULL DEFAULT 10485760,
  created_at          TIMESTAMPTZ   NOT NULL DEFAULT now(),
  updated_at          TIMESTAMPTZ   NOT NULL DEFAULT now(),
  CONSTRAINT chk_allowed_domains_not_empty
    CHECK (COALESCE(array_length(allowed_domains, 1), 0) > 0),
  CONSTRAINT chk_allowed_file_types_not_empty
    CHECK (COALESCE(array_length(allowed_file_types, 1), 0) > 0),
  CONSTRAINT chk_max_file_size
    CHECK (max_file_size_bytes > 0)
);

CREATE TRIGGER trg_app_policies_set_updated_at
  BEFORE UPDATE ON files.app_policies
  FOR EACH ROW EXECUTE FUNCTION files.set_updated_at();

-- ── files.objects ───────────────────────────────────────────────────────────
CREATE TABLE files.objects (
  file_id           UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
  domain            VARCHAR(50)   NOT NULL,
  relative_path     VARCHAR(255)  NOT NULL,
  content_type      VARCHAR(100)  NOT NULL,
  extension         VARCHAR(10)   NOT NULL,
  original_file_name VARCHAR(255),
  size_bytes        BIGINT        NOT NULL CHECK (size_bytes > 0),
  sha256            VARCHAR(64)   NOT NULL,
  classification    VARCHAR(20)   NOT NULL DEFAULT 'internal',
  retention_policy  VARCHAR(50),
  status            VARCHAR(20)   NOT NULL DEFAULT 'active',
  created_by_app    VARCHAR(50)   NOT NULL REFERENCES files.app_policies(app_code),
  created_by_user   VARCHAR(100),
  created_at        TIMESTAMPTZ   NOT NULL DEFAULT now(),
  updated_at        TIMESTAMPTZ   NOT NULL DEFAULT now(),
  CONSTRAINT chk_classification
    CHECK (classification IN ('internal','confidential','restricted','official')),
  CONSTRAINT chk_status_objects
    CHECK (status IN ('active','revoked','archived','deleted')),
  CONSTRAINT chk_sha256_format
    CHECK (sha256 ~ '^[a-f0-9]{64}$'),
  CONSTRAINT chk_relative_path_format
    CHECK (relative_path ~ '^[a-z0-9_]+/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f-]{36}\.[a-z0-9]+$'),
  CONSTRAINT uq_relative_path UNIQUE (relative_path)
);

CREATE INDEX idx_objects_domain       ON files.objects(domain);
CREATE INDEX idx_objects_status       ON files.objects(status);
CREATE INDEX idx_objects_sha256       ON files.objects(sha256);
CREATE INDEX idx_objects_created_by_app ON files.objects(created_by_app);

CREATE TRIGGER trg_objects_set_updated_at
  BEFORE UPDATE ON files.objects
  FOR EACH ROW EXECUTE FUNCTION files.set_updated_at();

-- ── files.references ────────────────────────────────────────────────────────
CREATE TABLE files.references (
  id            BIGSERIAL     PRIMARY KEY,
  file_id       UUID          NOT NULL REFERENCES files.objects(file_id) ON DELETE RESTRICT,
  app_code      VARCHAR(50)   NOT NULL REFERENCES files.app_policies(app_code),
  entity_type   VARCHAR(50)   NOT NULL,
  entity_id     VARCHAR(100)  NOT NULL,
  relation_type VARCHAR(50)   NOT NULL,
  is_primary    BOOLEAN       NOT NULL DEFAULT false,
  status        VARCHAR(20)   NOT NULL DEFAULT 'active',
  created_at    TIMESTAMPTZ   NOT NULL DEFAULT now(),
  CONSTRAINT chk_status_references
    CHECK (status IN ('active','revoked'))
);

CREATE INDEX idx_references_file_id  ON files.references(file_id);
CREATE INDEX idx_references_entity   ON files.references(entity_type, entity_id);
CREATE INDEX idx_references_app_code ON files.references(app_code);

-- ── files.relation_type_config ──────────────────────────────────────────────
-- Relation type başına kardinalite tanımı.
-- single: aynı entity+relationType için yalnız bir aktif primary olabilir (cv, photo).
-- multi: birden fazla aktif primary olabilir (document, attachment, report).
-- Tabloda bulunmayan tipler uygulama katmanında 'single' kabul edilir.
CREATE TABLE files.relation_type_config (
  relation_type VARCHAR(50) PRIMARY KEY,
  cardinality   VARCHAR(10) NOT NULL DEFAULT 'single',
  description   TEXT,
  CONSTRAINT chk_cardinality CHECK (cardinality IN ('single', 'multi'))
);

-- DB güvence katmanı: single-primary tipler için çift aktif primary engeli.
-- Uygulama zaten arşivleme yapıyor; bu trigger kod hatasına karşı korur.
CREATE OR REPLACE FUNCTION files.check_single_primary()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.is_primary = true AND NEW.status = 'active' THEN
        -- Tabloda 'multi' olarak kayıtlı değilse (bilinmeyen dahil) single-primary kuralı uygulanır
        IF NOT EXISTS (
            SELECT 1 FROM files.relation_type_config
            WHERE relation_type = NEW.relation_type AND cardinality = 'multi'
        ) THEN
            IF EXISTS (
                SELECT 1 FROM files.references r
                WHERE r.app_code    = NEW.app_code
                  AND r.entity_type = NEW.entity_type
                  AND r.entity_id   = NEW.entity_id
                  AND r.relation_type = NEW.relation_type
                  AND r.is_primary  = true
                  AND r.status      = 'active'
                  AND (TG_OP = 'INSERT' OR r.id != NEW.id)
            ) THEN
                RAISE EXCEPTION 'single_primary_violation: %/% app=% type=%',
                    NEW.entity_type, NEW.entity_id, NEW.app_code, NEW.relation_type;
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_check_single_primary
    BEFORE INSERT OR UPDATE ON files.references
    FOR EACH ROW EXECUTE FUNCTION files.check_single_primary();

-- ── files.audit_events ──────────────────────────────────────────────────────
CREATE TABLE files.audit_events (
  id             BIGSERIAL    PRIMARY KEY,
  file_id        UUID         REFERENCES files.objects(file_id) ON DELETE SET NULL,
  app_code       VARCHAR(50)  NOT NULL,
  actor          VARCHAR(100),
  action         VARCHAR(30)  NOT NULL,
  result         VARCHAR(20)  NOT NULL,
  reason_code    VARCHAR(50),
  correlation_id VARCHAR(100),
  actor_ip       VARCHAR(45),
  user_agent     VARCHAR(255),
  created_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
  CONSTRAINT chk_action
    CHECK (action IN ('create','read','archive','delete_attempt')),
  CONSTRAINT chk_result
    CHECK (result IN ('success','denied','not_found','error'))
);

CREATE INDEX idx_audit_file_id        ON files.audit_events(file_id);
CREATE INDEX idx_audit_app_code       ON files.audit_events(app_code);
CREATE INDEX idx_audit_created_at     ON files.audit_events(created_at);
CREATE INDEX idx_audit_correlation_id ON files.audit_events(correlation_id);

-- ── yonetim.audit_events ────────────────────────────────────────────────────
CREATE TABLE yonetim.audit_events (
  id             BIGSERIAL    PRIMARY KEY,
  personnel_id   VARCHAR(100) NOT NULL,
  actor          VARCHAR(200) NOT NULL,
  action         VARCHAR(100) NOT NULL,
  result         VARCHAR(50)  NOT NULL,
  reason_code    VARCHAR(100),
  correlation_id VARCHAR(100),
  created_at     TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX idx_yonetim_audit_personnel ON yonetim.audit_events(personnel_id);
CREATE INDEX idx_yonetim_audit_actor     ON yonetim.audit_events(actor);
CREATE INDEX idx_yonetim_audit_created   ON yonetim.audit_events(created_at);

-- ── yonetim.personnel ──────────────────────────────────────────────────────
CREATE TABLE yonetim.personnel (
  personnel_id  VARCHAR(100) PRIMARY KEY,
  display_name  VARCHAR(200) NOT NULL,
  department    VARCHAR(100),
  title         VARCHAR(100),
  created_at    TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX idx_personnel_display_name ON yonetim.personnel(display_name);

-- ── yonetim.team_members ────────────────────────────────────────────────────
CREATE TABLE yonetim.team_members (
  manager_id   VARCHAR(100) NOT NULL,
  personnel_id VARCHAR(100) NOT NULL,
  created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
  PRIMARY KEY (manager_id, personnel_id)
);

CREATE INDEX idx_team_members_manager ON yonetim.team_members(manager_id);

-- ── filo.audit_events ───────────────────────────────────────────────────────
CREATE TABLE filo.audit_events (
  id             BIGSERIAL    PRIMARY KEY,
  vehicle_id     VARCHAR(100) NOT NULL,
  actor          VARCHAR(200) NOT NULL,
  action         VARCHAR(100) NOT NULL,
  result         VARCHAR(50)  NOT NULL,
  reason_code    VARCHAR(100),
  correlation_id VARCHAR(100),
  created_at     TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX idx_filo_audit_vehicle ON filo.audit_events(vehicle_id);
CREATE INDEX idx_filo_audit_actor   ON filo.audit_events(actor);
CREATE INDEX idx_filo_audit_created ON filo.audit_events(created_at);
