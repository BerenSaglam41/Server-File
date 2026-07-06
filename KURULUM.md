# Kurulum Kılavuzu

Sistemde iki ayrı sunucu var:

| Sunucu | IP | Görev |
|---|---|---|
| **files-01** | 192.168.64.3 | NFS depolama — `/srv/files` export eder |
| **API sunucusu** | 192.168.64.5 | Docker Compose — tüm servisler burada çalışır |

**Kurulum sırası:** files-01 → API sunucusu. Mac yalnız UTM/test profilinde files-01'e bağlanır;
production minimum profilinde Mac/başka makine NFS mount edememelidir.

---

## 1. files-01 Kurulumu

files-01, NFS depolama sunucusudur. Test/UTM profilinde Mac ve API sunucusu bağlanabilir.
Production minimum profilinde yalnız API/FileService sunucusu bağlanmalıdır.
Sıfırdan kurulumu için bkz. `runbooks/files01-nfs-setup.md`

> Aşağıdaki özet UTM/test profilidir. Production'da `*` ile NFS export açma; yalnız API sunucusu IP'sini allowlist et ve TCP/2049'u firewall ile sınırla. Ayrıntı: `runbooks/production-hardening.md`.

### Özet adımlar

```bash
# NFS sunucu kurulumu
sudo apt install -y nfs-kernel-server

# Dizin yapısı
sudo mkdir -p /srv/files/export/personnel /srv/files/export/fleet
sudo mkdir -p /srv/files/staging/personnel
sudo mkdir -p /srv/files/manifests/personnel
sudo mkdir -p /srv/files/restore-tests/personnel

# Health check probe dosyası (FileServiceApi için zorunlu)
echo "probe" | sudo tee /srv/files/export/.probe > /dev/null

# NFS export — UTM/test profili
echo "/srv/files  *(rw,sync,no_subtree_check)" | sudo tee /etc/exports
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

Production minimum örnek:

```bash
API_SERVER_IP="<API_SERVER_IP>"
sudo NFS_MODE=production API_SERVER_IP="$API_SERVER_IP" ./tools/configure-files01-nfs.sh
```

Bu script `files-writer:files-publishers` kimliğini oluşturur, storage dizin sahipliğini ayarlar ve
NFS export'u yalnız API sunucusuna açar. Elle `root_squash` export yazmak container upload akışında
`503 storage_unavailable` hatasına yol açabilir.

Scriptin yazdığı export modeli:

```exports
/srv/files <API_SERVER_IP>(rw,sync,no_subtree_check,all_squash,anonuid=<FILES_WRITER_UID>,anongid=<FILES_WRITER_GID>)
```

### Doğrula

```bash
cat /etc/exports
sudo exportfs -v
id files-writer

# Production beklenen export modeli:
# /srv/files <API_SERVER_IP>(rw,sync,no_subtree_check,all_squash,anonuid=...,anongid=...)
```

API sunucusunda:

```bash
mount | grep platform-files
nc -vz <FILES_01_IP> 2049
bash setup-server.sh
# Beklenen: [OK] Fileservice container staging -> export yazma/taşıma testi geçti
```

Mac/izinsiz makinede production beklenen:

```bash
sudo mount -t nfs -o resvport <FILES_01_IP>:/srv/files /tmp/files01-test
# access denied veya timeout
```

### Dizin yapısı

```
/srv/files/
  export/            ← okunur (ReadPath + ExportPath) — NFS üzerinden erişilir
    personnel/
    fleet/
    .probe           ← health check
  staging/           ← upload geçici alan (StagingPath)
    personnel/
  manifests/
  restore-tests/
```

---

## 2. Mac Kurulumu

Mac, yalnız UTM/test profilinde files-01'i `/Volumes/platform-files`'a mount eder.

> Bu bölüm test/UTM içindir. Production minimum modunda Mac, Files-01'i mount edememelidir;
> dosya erişimi yalnız Gateway → Uygulama API → FileService akışından yapılır.

### Ön koşullar
- Docker Desktop kurulu ve çalışıyor
- Git kurulu
- files-01 (192.168.64.3) erişilebilir: `ping 192.168.64.3`

### Adımlar

**1. Repoyu klonla**
```bash
git clone <repo-url>
cd dosya-sistemi-projesi
```

**2. Kurulum scriptini çalıştır**
```bash
bash setup-mac.sh
```

Bu script otomatik olarak şunları yapar:
- `.env` dosyasını `.env.mac`'tan oluşturur → `STORAGE_PATH=/Volumes/platform-files`
- files-01'i `/Volumes/platform-files`'a mount eder
- `.key` sertifika dosyaları eksikse `generate-certs.sh` çalıştırır
- `docker compose up --build -d`
- `fileservice` container'ını yeniden başlatır (NFS timing)
- DB schema ve seed SQL'lerini çalıştırır (tablolar yoksa)

**3. Doğrula**
```bash
curl -k https://localhost:5090/health
# {"status":"healthy","service":"Gateway-Nginx"}
```

Tarayıcıda: `https://localhost:5090` (self-signed uyarısı → ileri / accept)

### Mac yeniden başlatıldığında

macOS'ta NFS mount kalıcı değildir. Yeniden başlatma sonrası:

```bash
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
docker compose restart fileservice
```

### Güncelleme

```bash
git pull && bash setup-mac.sh
```

---

## 3. API Sunucusu Kurulumu (192.168.64.5)

API sunucusu, files-01'i `/mnt/platform-files`'a mount eder.

### Ön koşullar

```bash
# Docker
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# Çıkıp tekrar gir

# NFS client ve git
sudo apt install -y nfs-common git
```

### Adımlar

**1. Repoyu klonla**
```bash
git clone <repo-url> ~/Server-File
cd ~/Server-File
```

**2. Kurulum scriptini çalıştır**
```bash
# Production minimum varsayılan: Files-01'in '*' export olmadığını kontrol eder
bash setup-server.sh

# UTM/test kolaylığı gerekiyorsa:
# NFS_MODE=test bash setup-server.sh
```

Bu script otomatik olarak şunları yapar:
- `.env` dosyasını `.env.linux`'tan oluşturur → `STORAGE_PATH=/mnt/platform-files`
- `NFS_MODE=production` ise Files-01'in `/srv/files *` olarak açık olmadığını kontrol eder
- files-01'i `/mnt/platform-files`'a mount eder + `/etc/fstab`'a ekler
- `.key` sertifika dosyaları eksikse `generate-certs.sh` çalıştırır
- Production modda `docker compose -f docker-compose.yml up --build -d`
- `fileservice` container'ını yeniden başlatır (NFS timing)
- DB schema ve seed SQL'lerini çalıştırır (tablolar yoksa)

**3. Doğrula**
```bash
docker compose ps
curl -k https://localhost:5090/health
# {"status":"healthy","service":"Gateway-Nginx"}

bash tools/server-smoke-test.sh
```

Dışarıdan: `https://192.168.64.5:5090` (self-signed) veya `https://domain:5090` (Let's Encrypt)

### Güncelleme

```bash
cd ~/Server-File
git pull
bash setup-server.sh
bash tools/server-smoke-test.sh
```

### Backup Timer Kurulumu

İlk kurulumdan sonra bir kere çalıştır (root gerektirir):

```bash
sudo mkdir -p /backup/platform-files
sudo bash tools/install-backup-timers.sh
```

Timer'ları doğrula:

```bash
systemctl list-timers 'platform-*'
# platform-backup.timer       → her gün 02:00 UTC
# platform-restore-test.timer → her Pazar 03:00 UTC
# platform-services-status.timer → her 5 dakikada servis snapshot
```

Not: `platform-backup.timer` gerçek yedek üretir; `platform-services-status.timer` yedek değildir,
Ops ekranındaki container CPU/RAM/restart/age ölçüm snapshot'ını üretir.

Manuel test (timer'ı beklemeden):

```bash
sudo systemctl start platform-backup.service
journalctl -u platform-backup --no-pager -n 30

sudo systemctl start platform-restore-test.service
journalctl -u platform-restore-test --no-pager -n 20

sudo systemctl start platform-services-status.service
journalctl -u platform-services-status --no-pager -n 20
```

---

## 3.1 Geliştirme ve Deploy Akışı

Günlük geliştirme Mac'te yapılır; Files-01 production minimum güvenlik modelinde Mac'e açılmaz.
FileAPI sunucusu integration/prod-like doğrulama ortamıdır.

```text
Mac = development
FileAPI (192.168.64.5) = integration/prod-like test
Files-01 (192.168.64.3) = storage-only; yalnız FileAPI erişir
```

### Mac'te geliştirme

```bash
git pull
# kod değişikliği
npm --prefix client run build
dotnet build FileServiceApi/FileServiceApi.csproj
dotnet build YonetimApi/YonetimApi.csproj
git push
```

Mac'te NFS allowlist açılmaz. Mac yalnız tarayıcı/geliştirici makinesidir.

### FileAPI sunucusunda doğrulama

```bash
cd ~/Server-File
git pull
bash setup-server.sh
bash tools/server-smoke-test.sh
```

Ek güvenli test paketi:

```bash
bash tools/server-safe-test-suite.sh
```

Kontrollü restart ve alert simülasyon testleri:

```bash
# Servis restart eder; integration/prod-like test ortamında bilinçli çalıştır
bash tools/server-resilience-test.sh

# Status dosyalarını geçici değiştirir, alert'i doğrular, çıkarken geri alır
bash tools/server-alert-simulation-test.sh

# Gateway security header/CSP smoke testi
bash tools/server-security-headers-test.sh
```

`tools/server-smoke-test.sh` şu kısa kontrolleri yapar:

- Gateway health
- `hr001` login
- personel listesi
- `P001` dosya listesi
- varsa ilk dosyayı download
- `p001` kullanıcısının başka personele erişemediğini `403` ile doğrulama
- `opsadmin` login
- `/ops/me`, `/ops/health`, `/ops/services`, `/ops/disk`, `/ops/backups`, `/ops/version`, `/ops/dashboard`
- `files.audit_events` son kayıtlarını gösterme

`tools/server-safe-test-suite.sh` sistemi bozmadan daha derin kontroller yapar:

- `/ops/dashboard` JSON alan bütünlüğü
- `opsuser01` read-only yetki doğrulaması
- BFF refresh endpoint kontrolü
- denied ops audit kayıtları (`no_token`, `ops_role_missing`)
- `X-Correlation-Id` response header
- en büyük mevcut personel dosyasını indirip boyut doğrulama
- 20 eşzamanlı login
- son ops audit kayıtlarının yazıldığını doğrulama

Varsayılanlar:

```bash
BASE_URL=https://localhost:5090
HR_USER=hr001
HR_PASS=Demo1234!
SELF_USER=p001
SELF_PASS=Demo1234!
PERSONNEL_ID=P001
OTHER_PERSONNEL_ID=P002
```

Farklı değerlerle çalıştırmak için:

```bash
BASE_URL=https://192.168.64.5:5090 PERSONNEL_ID=ADM001 bash tools/server-smoke-test.sh
```

Smoke test sistemi değiştirmemek için otomatik dosya yüklemez. `P001` dosya listesi boşsa download
kontrolünü uyarı vererek atlar.

### Ops Dashboard

Ops rolü olan kullanıcıyla giriş yapıldığında client içinde `Ops` sekmesi görünür.

Demo hesaplar:

```text
opsadmin / ops123
opsuser01 / ops456
```

Backend tek çağrılık dashboard endpoint'i:

```bash
curl -k -b cookies.txt https://localhost:5090/ops/dashboard
```

`/ops/dashboard` şu alanları döndürür: Gateway/Postgres/FileService/OpsApi health, container servisleri,
disk durumu, alert listesi, backup retention/toplam/doluluk özeti ve `commit/branch/build` metadata.

Ops ekranındaki `Ölçüm` zamanı container services snapshot zamanıdır. `Son yedek` ise en yeni backup
klasöründen hesaplanır; backup timer günde 1 çalışır.

Container tablosundaki `Uptime`, son `git pull` zamanı değil Docker container'ın son start zamanıdır.
`docker compose up --build -d` değişmeyen container'ları recreate etmeyebilir; bu durumda uptime devam eder.

Not: OpsApi container'ına Docker socket mount edilmez. Container durumları host tarafındaki
`tools/services-status.sh` ile `/backup/platform-files/.services-status.json` dosyasına yazılır;
OpsApi bu snapshot'ı salt-okunur okur.

---

## 4. HTTPS Kurulumu

Gateway, HTTPS üzerinden çalışır. İki seçenek var:

### Seçenek A: Dahili / Geliştirme — Self-Signed Sertifika

`certs/generate-certs.sh` gateway için otomatik sertifika üretir (setup scriptleri zaten bunu çalıştırır). Script mevcut CA ve sertifikaları varsayılan olarak ezmez; yeniden üretmek için `FORCE_REGENERATE_CERTS=1` verilir.

```bash
bash certs/generate-certs.sh
```

Üretilen dosyalar: `certs/gateway.crt`, `certs/gateway.key`

Gateway SAN değerleri ortam değişkenleriyle verilebilir:

```bash
GATEWAY_DNS="gateway,localhost,platform.sirket.com" \
GATEWAY_IPS="127.0.0.1,<API_SERVER_IP>" \
bash certs/generate-certs.sh
```

Container'lar başlatıldıktan sonra:

```bash
# Mac
curl -k https://localhost:5090/health
# {"status":"healthy","service":"Gateway-Nginx"}

# API sunucusu
curl -k https://192.168.64.5:5090/health
```

Tarayıcıda: `https://localhost:5090` (self-signed uyarısı → güvenli devam et / ileri / accept)

> `-k` / `--insecure` yalnız self-signed için gereklidir. Let's Encrypt sertifikasıyla gerek yok.

---

### Seçenek B: VPS — Let's Encrypt (Gerçek Sertifika)

**Ön koşullar:**
- Bir domain adı (örn. `platform.sirket.com`) → VPS IP'ye A kaydı
- VPS'in 80. portu dışarıya açık olmalı (sertifika doğrulaması için)

**1. certbot kur ve sertifika al:**

```bash
sudo apt install -y certbot

# Gateway container'ı durdur (80. port serbest olsun)
docker compose stop gateway

# Sertifika al
sudo certbot certonly --standalone -d platform.sirket.com

# Sertifikaları proje certs/ dizinine kopyala
DOMAIN=platform.sirket.com
PROJ=/home/kullanici/dosya-sistemi-projesi

sudo cp /etc/letsencrypt/live/$DOMAIN/fullchain.pem $PROJ/certs/gateway.crt
sudo cp /etc/letsencrypt/live/$DOMAIN/privkey.pem   $PROJ/certs/gateway.key
sudo chmod 644 $PROJ/certs/gateway.crt
sudo chmod 640 $PROJ/certs/gateway.key
sudo chown $(whoami) $PROJ/certs/gateway.key

# Gateway'i yeniden başlat
docker compose up -d gateway
```

**2. Otomatik yenileme (cron):**

```bash
sudo crontab -e
```

Ekle (alanları kendi değerlerinle değiştir):

```
0 3 1 * * certbot renew --quiet \
  && cp /etc/letsencrypt/live/platform.sirket.com/fullchain.pem /home/kullanici/dosya-sistemi-projesi/certs/gateway.crt \
  && cp /etc/letsencrypt/live/platform.sirket.com/privkey.pem   /home/kullanici/dosya-sistemi-projesi/certs/gateway.key \
  && docker compose -f /home/kullanici/dosya-sistemi-projesi/docker-compose.yml restart gateway
```

Let's Encrypt sertifikası 90 günde bir yenilenir; yukarıdaki cron her ay 1'inde kontrol eder.

İç servis mTLS CA'sı ile public Gateway TLS sertifikasını ayrı düşün. Gateway public domain'de Let's Encrypt kullanabilir; FileService/YonetimApi/FlotaApi arası mTLS için platform CA korunur ve rotasyon `runbooks/production-hardening.md` prosedürüyle yapılır.

**3. Doğrula:**

```bash
curl https://platform.sirket.com:5090/health
# -k gerekmez — gerçek sertifika
```

---

## Storage Bağlantısı

```
files-01 (192.168.64.3)
  /srv/files/
    export/     ← ReadPath + ExportPath
    staging/    ← StagingPath

API sunucusu:   /mnt/platform-files      → (NFS) → /srv/files
Mac:            production minimumda NFS'e bağlanmaz; yalnız Gateway'i kullanır
                UTM/test profilinde /Volumes/platform-files mount edilebilir

Container içi:  /app/storage             = mount noktası
  /app/storage/export    = ReadPath + ExportPath
  /app/storage/staging   = StagingPath
```

---

## Production Hardening Özeti

Production'a geçmeden önce şu kapılar tamamlanmalı:

- NFS export `*` içermemeli; yalnız API sunucusu IP'si allowlist edilmeli.
- Production minimum export `root_squash` yerine `all_squash + files-writer` kullanmalı; aksi halde
  container upload sırasında `Permission denied` / `503 storage_unavailable` görülebilir.
- Files-01 firewall TCP/2049'u yalnız API sunucusuna açmalı.
- Mac/başka VM üzerinden NFS mount denemesi `access denied` veya timeout ile başarısız olmalı.
- API sunucusunda `setup-server.sh` gerçek upload yolunu doğrulamalı:
  `staging/personnel/...` yazma → SHA256 okuma → `export/personnel/...` içine taşıma.
- `staging` geçici, `export` kalıcı/backup kapsamı olarak ayrılmalı.
- Backup/restore systemd timer ile otomatik: `sudo bash tools/install-backup-timers.sh` (bkz. aşağıdaki Backup Timer bölümü).
- `certs/generate-certs.sh` kazara CA yenilemeyecek şekilde kullanılmalı; CA rotasyonu planlı yapılmalı.
- `appsettings.json` dosyalarındaki local değerlerin fallback olduğu bilinmeli; production config `docker compose config` ve container env çıktısıyla doğrulanmalı.

Detaylı plan: `runbooks/production-hardening.md`

---

## Sistem Bileşenleri (API sunucusunda çalışır)

| Container | Port | Görev |
|---|---|---|
| gateway (nginx) | 5090 | Tek giriş noktası — React SPA + API proxy, rate limit, public zone servisi |
| client (nginx) | — | React SPA |
| yonetimapi | — | Personel yönetimi, auth, BFF cookie, RBAC kararı (DB'den) |
| fileservice | — | Dosya listesi/indirme/yükleme (mTLS) |
| flotaapi | — | Filo yönetimi |
| opsapi | — | Salt-okunur operasyon/gözlem API'si |
| clamav | — | Yüklenen dosyalar için fail-closed virüs taraması |
| keycloak | — | Kimlik doğrulama (JWT, OIDC) — yetki kararı DEĞİL |
| postgres | — | Veritabanı — `yonetim.role_assignments` yetki kaynağı dahil |

Dışarıya sadece **gateway (5090)** açıktır.

---

## Test Hesapları

| Kullanıcı | Şifre | Rol |
|---|---|---|
| `hr001` | `Demo1234!` | HR — tüm personeli görür, dosya yükler |
| `adm001` | `Demo1234!` | Admin |
| `m001` | `Demo1234!` | Manager — kendi ekibini görür |
| `p001` | `Demo1234!` | Self — sadece kendi kaydını görür |

Detaylı hesap listesi: `DEMO_HESAPLAR.md`

---

## Sorun Giderme

**Container ayağa kalkmıyor:**
```bash
docker compose logs <servis-adı>
```

**401 Unauthorized:**
Keycloak henüz hazır olmayabilir — 1-2 dk bekle.
```bash
docker compose logs yonetimapi | grep -i jwks
```

**500 — dosyalar gelmiyor (mTLS hatası):**
```bash
ls -la certs/*.key    # dosya mı, dizin mi?
# Dizinse:
rm -rf certs/yonetimapi.key certs/fileservice.key certs/filoapi.key
bash certs/generate-certs.sh
docker compose up --force-recreate -d fileservice yonetimapi flotaapi
```

**DB tablolar boş:**
```bash
# Sırayla TÜM migration dosyaları uygulanmalı (01'den 08'e, sıra önemli):
for f in db/docker-init/*.sql; do
  docker exec -i $(docker ps -qf name=postgres) psql -U platform -d platformdb < "$f"
done
```
`08-role-assignments.sql` sonrası, Keycloak'taki mevcut rol atamalarını DB'ye aktarmak için ayrıca:
```bash
bash tools/backfill-role-assignments.sh
```
(Rol yönetimi artık Keycloak admin panelinden değil, `tools/manage-role-assignment.sh` ile yapılır —
bkz. `MIMARI.md` bölüm 4.)

**NFS mount yok:**
```bash
# Mac (yalnız UTM/test profili):
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
docker compose restart fileservice

# API sunucusu:
sudo mount -t nfs 192.168.64.3:/srv/files /mnt/platform-files
bash setup-server.sh
```

**files-01 erişilemiyor:**
```bash
ping 192.168.64.3
nc -vz 192.168.64.3 2049
# showmount production/firewall/NFSv4 ortamında yanıltıcı olabilir; asıl kabul mount + probe testidir.
```
