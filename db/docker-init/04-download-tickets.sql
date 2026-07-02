-- Opak, kısa ömürlü, tek kullanımlık indirme ticket'ı (personel dosyaları için)
-- Açık ticket asla saklanmaz — sadece SHA256 hash'i tutulur.
CREATE SCHEMA IF NOT EXISTS yonetim;

CREATE TABLE IF NOT EXISTS yonetim.download_tickets (
  ticket_hash   VARCHAR(64)  PRIMARY KEY,      -- SHA256(raw ticket), hex
  personnel_id  VARCHAR(100) NOT NULL,
  file_id       UUID         NOT NULL,
  relation_type VARCHAR(50)  NOT NULL,
  actor         VARCHAR(200) NOT NULL,
  expires_at    TIMESTAMPTZ  NOT NULL,
  used_at       TIMESTAMPTZ,                   -- NULL = henüz tüketilmedi
  created_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
  CONSTRAINT chk_ticket_hash_format CHECK (ticket_hash ~ '^[a-f0-9]{64}$')
);

CREATE INDEX IF NOT EXISTS idx_download_tickets_expires ON yonetim.download_tickets(expires_at);
CREATE INDEX IF NOT EXISTS idx_download_tickets_personnel ON yonetim.download_tickets(personnel_id);
