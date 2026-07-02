-- Download ticket yaşam döngüsü YonetimApi'den FileServiceApi'ye taşındı
-- (files.* şeması, FileServiceApi'nin kendi AppDbContext'i). Amaç: ticket
-- oluşturma + tüketme mantığının, dosya kataloğunun kendisiyle aynı serviste
-- yaşaması — ileride Gateway'in doğrudan tüketebileceği bir endpoint
-- konumuna (`/internal/download-tickets/{ticket}/consume`) hazırlık.
--
-- Açık ticket asla saklanmaz — sadece SHA256 hash'i tutulur.
CREATE TABLE IF NOT EXISTS files.download_tickets (
  ticket_hash  VARCHAR(64)  PRIMARY KEY,      -- SHA256(raw ticket), hex
  file_id      UUID         NOT NULL,
  app_code     VARCHAR(100) NOT NULL,         -- ticket'ı hangi uygulama oluşturdu
  actor        VARCHAR(200),
  expires_at   TIMESTAMPTZ  NOT NULL,
  used_at      TIMESTAMPTZ,                   -- NULL = henüz tüketilmedi
  created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
  CONSTRAINT chk_ticket_hash_format CHECK (ticket_hash ~ '^[a-f0-9]{64}$')
);

CREATE INDEX IF NOT EXISTS idx_download_tickets_expires ON files.download_tickets(expires_at);
CREATE INDEX IF NOT EXISTS idx_download_tickets_file_id ON files.download_tickets(file_id);

-- Eski YonetimApi tarafı yerini alıyor — dead code/dead table bırakılmıyor.
DROP TABLE IF EXISTS yonetim.download_tickets;

-- files.audit_events.chk_action, ticket_create/ticket_consume'u kapsayacak şekilde genişletiliyor.
ALTER TABLE files.audit_events DROP CONSTRAINT IF EXISTS chk_action;
ALTER TABLE files.audit_events ADD CONSTRAINT chk_action
  CHECK (action::text = ANY (ARRAY['create', 'read', 'archive', 'delete_attempt', 'ticket_create', 'ticket_consume']::text[]));
