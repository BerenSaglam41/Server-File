-- Public/private güvenlik zone'u (B3 — platform-mimarisi-stajyer-rehberi.txt bölüm 9.3/9.6).
-- relative_path şeması değişmiyor; sadece hangi fiziksel kök dizine (export vs export-public)
-- yazıldığını belirten bir üst-düzey ayrım. Mevcut tüm satırlar 'private' olur (zaten öyleler,
-- fiziksel konumları değişmez, migrasyon riski yok).
ALTER TABLE files.objects ADD COLUMN IF NOT EXISTS storage_zone TEXT NOT NULL DEFAULT 'private';

ALTER TABLE files.objects DROP CONSTRAINT IF EXISTS chk_storage_zone;
ALTER TABLE files.objects ADD CONSTRAINT chk_storage_zone
  CHECK (storage_zone = ANY (ARRAY['public','private']::text[]));
