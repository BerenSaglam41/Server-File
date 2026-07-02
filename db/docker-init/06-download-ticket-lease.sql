-- Ticket "lease" modeli: tek-kullanımlık yerine, ilk tüketimden sonra sınırlı
-- bir süre (lease) ve sınırlı sayıda (max_uses) ek isteğe izin verir. Bu,
-- büyük PDF/video gibi dosyaların birden fazla Range isteğiyle (tarayıcının
-- doğal davranışı) okunabilmesi için gerekli — S3 presigned URL / Google
-- Signed URL'lerin de yaptığı gibi süre bazlı, sayı sınırlı bir model.
--
-- used_at artık "ilk tüketim zamanı / lease başlangıcı" anlamına geliyor
-- (önceki "tek kullanım zamanı" anlamının doğal genişlemesi, yeni kolon
-- gerektirmiyor). use_count her başarılı tüketimde artar.
ALTER TABLE files.download_tickets ADD COLUMN IF NOT EXISTS use_count INT NOT NULL DEFAULT 0;
