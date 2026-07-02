# Kanıt: NFS rw → FilesPublisher Modeli (FileServiceApi Artık Yazmıyor)

**Tarih:** 2026-07-02
**Kapsam:** Personel dosya yükleme (upload) — fiziksel yazma yolu
**Durum:** ✅ Yeni servis kuruldu, uçtan uca test edildi, FileServiceApi container'ı gerçekten salt-okunur

---

## Sorun ve Karar

`PROJE/files01-nfs-model.md`'nin hedef modeli FileServiceApi'nin NFS export'unu **salt-okunur** mount
etmesini öngörüyordu, ama yazma (dosya yükleme) nasıl yapılacağını sadece şöyle tarif ediyor:
*"Dosya publish, migrate veya düzeltme işlemleri Files-01 üzerinde **kontrollü operasyon kullanıcısıyla**
yapılır."* — bu, bir kerelik migration senaryosu için yazılmış, bizim **canlı, sürekli** personel dosya
yükleme özelliğimizi kapsamıyor.

**Karar:** Planın "kontrollü operasyon kullanıcısı" dediği şeyi gerçekten inşa ettik — Files-01 üzerinde
çalışan, mTLS korumalı, minimal bir **FilesPublisher** servisi. FileServiceApi artık dosya yüklerken kendi
NFS mount'una yazmıyor, bu servise HTTP(S) ile içerik gönderiyor.

## Ne Eklendi

- **`FilesPublisher/publisher.py`** — Files-01 üzerinde `files-writer` kullanıcısıyla systemd servisi
  olarak çalışan, sadece Python stdlib kullanan (yeni runtime kurulumu gerekmedi) minimal HTTPS servisi:
  - `POST /publish?relativePath=...` — body'yi staging'e yazar, SHA256 hesaplar, export'a atomik taşır.
  - `DELETE /publish?relativePath=...` — rollback (duplicate/DB hatası durumunda).
  - mTLS zorunlu (`ssl.CERT_REQUIRED`), CN allowlist (`fileservice`), path traversal koruması.
- **`certs/generate-certs.sh`** genişletildi: `filespublisher` (server) ve `fileservice-client` (client,
  CN=fileservice ama clientAuth EKU'lu) sertifikaları eklendi.
- **`FileServiceApi/Services/FilesPublisherClient.cs`** — Publisher'a mTLS ile bağlanan `IHttpClientFactory`
  client'ı.
- **`FileServiceApi/Endpoints/FileEndpoints.cs → CreateFileAsync`** — staging/export'a doğrudan dosya
  yazan kod tamamen kaldırıldı, yerine `publisher.PublishAsync(...)`/`publisher.DeleteAsync(...)` çağrıları
  geldi. Kod tabanında (`grep`) doğrulandı: FileServiceApi'de artık **hiçbir** `File.Move/Delete/Create`
  veya `Directory.CreateDirectory` çağrısı yok.
- **`docker-compose.yml`**: FileServiceApi'nin storage mount'u `:ro` yapıldı
  (`${STORAGE_PATH}:/app/storage:ro`), `FilesPublisher__*` env değişkenleri eklendi.
- **Files-01'de yeni ufw kuralı**: port 6060, sadece api-server IP'sinden.

## Test Süreci ve Bulunan Sorunlar (2 tanesi gerçek bug/yanlış varsayımdı)

### Sorun 1 — Mac/VM saat farkı, sertifika "henüz geçerli değil"

İlk üretilen `filespublisher.crt`/`fileservice-client.crt` Mac'in saatiyle imzalandı, ama Mac VM'lerden
~1 saat ileride olduğu için sertifikalar VM'lerin gözünde henüz geçerli değildi
(`certificate is not yet valid`). `openssl x509 -req ... -not_before` ile geriye tarihlenerek düzeltildi.

### Sorun 2 — GERÇEK BUG: systemd `ProtectSystem=strict` + ayrı `ReadWritePaths` → `EXDEV`

İlk testte `POST /publish` `"Invalid cross-device link"` hatası verdi. Kök neden: systemd unit dosyasında
`ReadWritePaths=/srv/files/staging /srv/files/export` (iki ayrı girdi) kullanılmıştı. systemd bu iki yolu
**ayrı bind-mount'lar** olarak sandbox'ladığı için, aynı fiziksel dosya sisteminde olsalar bile
`os.rename()` "farklı cihaz" hatası veriyordu. **Düzeltme:** ortak üst dizin (`/srv/files`) tek
`ReadWritePaths` girdisi olarak verildi.

### Sorun 3 — GERÇEK KEŞİF: server'ın kendi CA'sı local'den farklı

Yeni sertifikaları önce Mac'teki local CA ile imzalayıp deploy ettim; FileServiceApi, Publisher'ın
sertifikasını `RemoteCertificateValidationCallback` içinde reddetti (`certificate was rejected`).
Kök neden: sunucunun `certs/ca.crt`'si (hash `f2bcec7c...`) benim yerel `certs/ca.crt`'imden
(`35a9e0b2...`) **farklıydı** — sunucu kendi CA'sını `setup-server.sh` ile bağımsız üretmiş. **Düzeltme:**
yeni sertifikalar sunucunun **kendi** `generate-certs.sh`'i sunucuda çalıştırılarak, sunucunun gerçek
CA'sıyla yeniden imzalandı, sonra doğru `ca.crt`+`filespublisher.crt` Files-01'e aktarıldı.

## Testler — Hepsi Geçti

1. **mTLS zorunluluğu:** Sertifikasız istek → TLS handshake seviyesinde reddedildi (`certificate required`).
2. **Yanlış CN (yonetimapi sertifikasıyla):** Doğru CA'dan ama izin listesinde olmayan CN → `403
   client_cert_not_allowed`.
3. **Gerçek publish:** İçerik gönderildi → `200`, dönen SHA256 bağımsız hesaplanan hash'le birebir eşleşti.
4. **Dosya gerçekten diskte:** api-server'ın NFS mount'undan doğrulandı, içerik ve hash tutarlı.
5. **Duplicate reddi:** Aynı `relativePath`'e ikinci `POST` → `409 already_exists`.
6. **Path traversal:** `../../../etc/passwd-test` → `400 invalid_path`.
7. **DELETE rollback:** Dosya silindi, NFS mount'tan "yok" olduğu doğrulandı.
8. **Gerçek uygulama üzerinden uçtan uca upload:** `POST /api/personnel/{id}/cv` → FileServiceApi →
   Publisher → Files-01 diskine yazıldı → indirme ile içerik **birebir aynı** çıktı (`diff` temiz).
9. **Duplicate + rollback gerçek akışta:** Aynı dosyayı ikinci kez yüklemeye çalışma → `409`, ve bu
   reddedilen denemenin fiziksel dosyasının **orphan kalmadığı** (Publisher'ın `DELETE`'i çalıştığı)
   doğrulandı.
10. **Container seviyesinde yazma reddi (planın kendi "Doğrulama Kapıları" tablosundaki "Write denial"
    testi):**
    ```
    docker exec server-file-fileservice-1 sh -c 'touch /app/storage/export/personnel/x.txt'
    → touch: cannot touch '...': Read-only file system
    mount | grep app/storage → ... type nfs4 (ro,relatime,...)
    ```
11. **`:ro` sonrası tam regresyon:** `tools/server-smoke-test.sh` 23/23 `[OK]`.
12. **`:ro` sonrası gerçek upload + indirme:** Yeni dosya yüklendi, indirilen içerik orijinalle birebir
    aynı (`diff` temiz) — Publisher yolu, container gerçekten salt-okunurken de sorunsuz çalışıyor.

## Kapsam Dışı Bırakılan Kısım (Bilinçli Karar)

Host seviyesindeki NFS mount'u (`/mnt/platform-files`, api-server fstab) **`ro` yapılmadı** — sadece
FileServiceApi container'ının kendi bind-mount görünümü `:ro`. Sebep: `tools/restore-test.sh` (haftalık
otomatik systemd timer) ve `tools/restore-live.sh` (break-glass manuel recovery) host seviyesinde
`$STORAGE_ROOT/restore-tests/` ve `$STORAGE_ROOT/export/` altına yazıyor — bunlar güvenilir, root
tarafından çalıştırılan, ağa açık olmayan bakım araçları, network-facing FileServiceApi ile aynı tehdit
modeline sahip değiller. Host mount'u da `ro` yapmak bu iki aracı kırardı; bunun için
`files01-nfs-model.md`'nin kendi önerdiği gibi `export`/`staging`/`restore-tests`'i **ayrı NFS export'lara**
bölmek gerekir — bu, ayrı ve daha büyük bir hardening adımı, şimdilik yapılmadı, dokümante edildi.

**Asıl güvenlik hedefi karşılandı:** Ağdan gelen isteklerle çalışan, potansiyel olarak istismar edilebilir
FileServiceApi artık **hiçbir koşulda** dosya sistemine yazamıyor — container seviyesinde kanıtlandı.

## Mimari Not

- FileServiceApi hâlâ **okuma** için NFS mount'unu kullanıyor (`FileStorage:ReadPath`) — bu değişmedi.
- Byte delivery hâlâ FileServiceApi'den akıyor (X-Accel-Redirect/Gateway entegrasyonu yapılmadı — bu ayrı,
  bilinçli olarak ertelenmiş bir konu, bkz. `STAJYER-RAPORU-DOGRULAMA.md` madde 1).
- FlotaApi'nin araç dosyaları için de aynı Publisher kullanılabilir (kod zaten domain-agnostik, `fileId`
  bazlı) — ayrıca bir değişiklik gerekmedi çünkü `CreateFileAsync` tüm domain'ler için ortak.
