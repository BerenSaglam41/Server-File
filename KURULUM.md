# Kurulum Kılavuzu

Bu kılavuz sistemi sıfırdan kurmak için gereken adımları içerir.  
İki senaryo var: **Mac**, **Linux üretim sunucusu**. Her ikisi de aynı Files-01 NFS sunucusunu kullanır.

> Files-01 NFS sunucusunun önce kurulmuş olması gerekir — bkz. [Files-01 Kurulumu](#files-01-nfs-sunucusu-kurulumu)

---

## Mac Kurulumu

### Ön koşullar
- Docker Desktop kurulu ve çalışıyor
- Git kurulu
- Files-01 VM (192.168.64.3) erişilebilir durumda

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
- `.env` dosyasını `.env.mac`'tan oluşturur (`STORAGE_PATH=/Volumes/platform-files`)
- Files-01 NFS sunucusunu `/Volumes/platform-files`'a mount eder
- `.key` sertifika dosyaları eksikse `generate-certs.sh` çalıştırır
- `docker compose up --build -d`
- `fileservice` container'ını yeniden başlatır (NFS timing)
- DB schema ve seed SQL'lerini çalıştırır (tablolar yoksa)

**3. Doğrula**
```bash
curl http://localhost:5090/health
# Beklenen: {"status":"healthy","service":"Gateway-Nginx"}
```

Tarayıcıda: `http://localhost:5090`

### İleride güncelleme yapmak

```bash
git pull
bash setup-mac.sh
```

### Mac yeniden başlatıldığında

macOS'ta NFS mount kalıcı değildir. Yeniden başlatma sonrası:

```bash
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
docker compose restart fileservice
```

Ardından sistem kaldığı yerden devam eder (`docker compose up -d` gerekmez — container'lar Docker Desktop ile otomatik başlar).

### Mac'te gitignore nedeniyle repoda olmayan dosyalar

| Dosya | Nasıl oluşturulur |
|---|---|
| `certs/*.key` | `setup-mac.sh` → `generate-certs.sh` |
| `.env` | `setup-mac.sh` → `.env.mac`'tan kopyalanır |
| DB verileri | `setup-mac.sh` → `01-schema.sql` + `02-seed.sql` |

---

## Linux Üretim Sunucusu Kurulumu

### Ön koşullar

Sunucuda şunların kurulu olması gerekir:

```bash
# Docker
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# Çıkıp tekrar gir (grup aktif olsun)

# NFS client
sudo apt install -y nfs-common

# Git
sudo apt install -y git
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
- `.env` dosyasını `.env.linux`'tan oluşturur (`STORAGE_PATH=/mnt/platform-files`)
- Files-01 NFS sunucusunu `/mnt/platform-files`'a mount eder
- `/etc/fstab`'a ekler (sunucu yeniden başladığında otomatik mount)
- `.key` sertifika dosyaları eksikse `generate-certs.sh` çalıştırır
- `docker compose up --build -d`
- `fileservice` container'ını yeniden başlatır (NFS timing)
- DB schema ve seed SQL'lerini çalıştırır (tablolar yoksa)

**3. Doğrula**
```bash
docker compose ps
# Tüm container'lar "Up" olmalı

curl http://localhost:5090/health
# Beklenen: {"status":"healthy","service":"Gateway-Nginx"}
```

Dışarıdan: `http://<sunucu-ip>:5090`

### İleride güncelleme yapmak

```bash
cd ~/Server-File
git pull
bash setup-server.sh
```

`setup-server.sh` idempotent'tir — zaten kurulu olan şeylere dokunmaz, sadece eksik olanları tamamlar.

### Linux'ta gitignore nedeniyle repoda olmayan dosyalar

| Dosya | Nasıl oluşturulur |
|---|---|
| `certs/*.key` | `setup-server.sh` → `generate-certs.sh` |
| `.env` | `setup-server.sh` → `.env.linux`'tan kopyalanır |
| DB verileri | `setup-server.sh` → `01-schema.sql` + `02-seed.sql` |

---

## Files-01 NFS Sunucusu Kurulumu

Files-01 ayrı bir Ubuntu VM'dir (192.168.64.3). Hem Mac hem Linux bu sunucuya bağlanır.

> Detaylı adımlar için: `runbooks/files01-nfs-setup.md`

### Özet

**1. Paket kur**
```bash
sudo apt install -y nfs-kernel-server
```

**2. Dizin yapısını oluştur**
```bash
sudo mkdir -p /srv/files/export/personnel
sudo mkdir -p /srv/files/export/fleet
sudo mkdir -p /srv/files/staging/personnel
sudo mkdir -p /srv/files/manifests/personnel
sudo mkdir -p /srv/files/restore-tests/personnel
```

**3. Probe dosyasını oluştur** (FileServiceApi health check için zorunlu)
```bash
echo "probe" | sudo tee /srv/files/export/.probe > /dev/null
```

**4. NFS export ayarla**

`/etc/exports` dosyasına ekle:
```
/srv/files  *(rw,sync,no_subtree_check)
```

Aktif et:
```bash
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

**5. Doğrula**
```bash
showmount -e localhost
# Beklenen: /srv/files  *
```

---

## Sistem Bileşenleri

| Container | Port | Görev |
|---|---|---|
| gateway (nginx) | 5090 | Tek giriş noktası — React SPA + API proxy |
| yonetimapi | — | Personel yönetimi, auth, BFF cookie |
| fileservice | — | Dosya listesi/indirme/yükleme (mTLS) |
| flotaapi | — | Filo yönetimi |
| client | — | React SPA |
| keycloak | — | Kimlik doğrulama (JWT, OIDC) |
| postgres | — | Veritabanı |

Tüm servisler Docker iç ağında birbirine bağlıdır. Dışarıya sadece gateway (5090) açıktır.

### Storage bağlantısı

```
Files-01 (192.168.64.3)
  /srv/files
      ├── export/        ← NFS üzerinden okunur (ReadPath + ExportPath)
      │     └── .probe   ← health check
      └── staging/       ← upload geçici alan (StagingPath)

Mac:   /Volumes/platform-files  → NFS → /srv/files
Linux: /mnt/platform-files      → NFS → /srv/files

Her iki ortamda container içi path: /app/storage
```

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

**401 Unauthorized (`/api/personnel` vb.):**  
Keycloak henüz hazır olmayabilir. 1-2 dk bekle, tekrar dene.  
Kalıcı ise: `docker compose logs yonetimapi | grep -i jwks`

**500 — dosyalar gelmiyor:**  
mTLS sertifikaları eksik veya dizin olarak oluşturulmuş olabilir.  
```bash
ls -la certs/*.key   # Bunlar dosya mı, dizin mi?
# Dizinse:
sudo rm -rf certs/yonetimapi.key certs/fileservice.key certs/filoapi.key
bash certs/generate-certs.sh
docker compose up --force-recreate -d fileservice yonetimapi flotaapi
```

**DB tablolar boş (kayıt gelmiyor):**  
Volume zaten varsa init SQL'leri çalışmaz. Elle çalıştır:
```bash
docker exec -i $(docker ps -qf name=postgres) psql -U platform -d platformdb < db/docker-init/01-schema.sql
docker exec -i $(docker ps -qf name=postgres) psql -U platform -d platformdb < db/docker-init/02-seed.sql
```

**NFS dosyaları görünmüyor:**
```bash
# Mac:
mount | grep platform-files || echo "MOUNT YOK"
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
docker compose restart fileservice

# Linux:
mountpoint -q /mnt/platform-files && echo "mount OK" || echo "MOUNT YOK"
sudo mount -t nfs 192.168.64.3:/srv/files /mnt/platform-files
docker compose restart fileservice
```

**Files-01'e erişilemiyor:**
```bash
ping 192.168.64.3        # VM açık mı?
showmount -e 192.168.64.3  # NFS server çalışıyor mu?
```
