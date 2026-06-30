# Kurulum Kılavuzu

Sistemde iki ayrı sunucu var:

| Sunucu | IP | Görev |
|---|---|---|
| **files-01** | 192.168.64.3 | NFS depolama — `/srv/files` export eder |
| **API sunucusu** | 192.168.64.5 | Docker Compose — tüm servisler burada çalışır |

**Kurulum sırası:** files-01 → Mac veya API sunucusu (her ikisi de files-01'e bağlanır)

---

## 1. files-01 Kurulumu

files-01, hem Mac hem API sunucusunun bağlandığı NFS depolama sunucusudur.  
Sıfırdan kurulumu için bkz. `runbooks/files01-nfs-setup.md`

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

# NFS export — tüm /srv/files dizini
echo "/srv/files  *(rw,sync,no_subtree_check)" | sudo tee /etc/exports
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

### Doğrula

```bash
showmount -e localhost
# Beklenen: /srv/files  *
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

Mac, files-01'i `/Volumes/platform-files`'a mount eder.

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
bash setup-server.sh
```

Bu script otomatik olarak şunları yapar:
- `.env` dosyasını `.env.linux`'tan oluşturur → `STORAGE_PATH=/mnt/platform-files`
- files-01'i `/mnt/platform-files`'a mount eder + `/etc/fstab`'a ekler
- `.key` sertifika dosyaları eksikse `generate-certs.sh` çalıştırır
- `docker compose up --build -d`
- `fileservice` container'ını yeniden başlatır (NFS timing)
- DB schema ve seed SQL'lerini çalıştırır (tablolar yoksa)

**3. Doğrula**
```bash
docker compose ps
curl -k https://localhost:5090/health
# {"status":"healthy","service":"Gateway-Nginx"}
```

Dışarıdan: `https://192.168.64.5:5090` (self-signed) veya `https://domain:5090` (Let's Encrypt)

### Güncelleme

```bash
cd ~/Server-File && git pull && bash setup-server.sh
```

---

## 4. HTTPS Kurulumu

Gateway, HTTPS üzerinden çalışır. İki seçenek var:

### Seçenek A: Dahili / Geliştirme — Self-Signed Sertifika

`certs/generate-certs.sh` gateway için otomatik sertifika üretir (setup scriptleri zaten bunu çalıştırır):

```bash
bash certs/generate-certs.sh
```

Üretilen dosyalar: `certs/gateway.crt`, `certs/gateway.key`

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

Mac:            /Volumes/platform-files  → (NFS) → /srv/files
API sunucusu:   /mnt/platform-files      → (NFS) → /srv/files

Container içi:  /app/storage             = mount noktası
  /app/storage/export    = ReadPath + ExportPath
  /app/storage/staging   = StagingPath
```

---

## Sistem Bileşenleri (API sunucusunda çalışır)

| Container | Port | Görev |
|---|---|---|
| gateway (nginx) | 5090 | Tek giriş noktası — React SPA + API proxy |
| client (nginx) | — | React SPA |
| yonetimapi | — | Personel yönetimi, auth, BFF cookie |
| fileservice | — | Dosya listesi/indirme/yükleme (mTLS) |
| flotaapi | — | Filo yönetimi |
| keycloak | — | Kimlik doğrulama (JWT, OIDC) |
| postgres | — | Veritabanı |

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
docker exec -i $(docker ps -qf name=postgres) psql -U platform -d platformdb \
  < db/docker-init/01-schema.sql
docker exec -i $(docker ps -qf name=postgres) psql -U platform -d platformdb \
  < db/docker-init/02-seed.sql
```

**NFS mount yok:**
```bash
# Mac:
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
docker compose restart fileservice

# API sunucusu:
sudo mount -t nfs 192.168.64.3:/srv/files /mnt/platform-files
docker compose restart fileservice
```

**files-01 erişilemiyor:**
```bash
ping 192.168.64.3
showmount -e 192.168.64.3
```
