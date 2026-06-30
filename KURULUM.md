# Kurulum Kılavuzu

Bu kılavuz sistemi sıfırdan kurmak için gereken adımları içerir.  
Üç senaryo var: **Mac geliştirme**, **Linux üretim sunucusu**, **Files-01 NFS sunucusu**.

---

## Mac'te Çalıştırma (Geliştirme)

### Ön koşullar
- Docker Desktop kurulu ve çalışıyor
- Git kurulu

### Adımlar

**1. Repoyu klonla**
```bash
git clone <repo-url>
cd dosya-sistemi-projesi
```

**2. mTLS sertifikalarını üret** (`.key` dosyaları gitignore'da — repoda yok)
```bash
bash certs/generate-certs.sh
```

**3. `.env` dosyası oluştur**
```bash
echo "STORAGE_PATH=./test-storage" > .env
```

**4. Sistemi başlat**
```bash
docker compose up --build -d
```

**5. Doğrula**
```bash
curl http://localhost:5090/health
# Beklenen: {"status":"healthy","service":"Gateway-Nginx"}

curl -c /tmp/ck.txt -X POST http://localhost:5090/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"hr001","password":"Demo1234!"}'
# Beklenen: 200 OK, at/rt cookie alınır
```

Tarayıcıda: `http://localhost:5090`

### Mac'te gitignore nedeniyle repoda olmayan dosyalar

| Dosya | Nasıl oluşturulur |
|---|---|
| `certs/*.key` | `bash certs/generate-certs.sh` |
| `.env` | Yukarıdaki `echo` komutu |
| DB verileri | Container ilk açılışta `db/docker-init/` SQL'lerini otomatik çalıştırır |

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

### Files-01 NFS sunucusunun hazır olması gerekir (aşağıdaki bölüme bak)

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

Files-01 ayrı bir Ubuntu VM'dir. API sunucusundan bağımsız kurulur.

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
/srv/files/export  <API-SUNUCU-IP>(ro,sync,no_subtree_check)
```

Aktif et:
```bash
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

**5. API sunucusundan doğrula**
```bash
# API sunucusunda çalıştır:
showmount -e <FILES-01-IP>
# Beklenen: /srv/files/export   <API-SUNUCU-IP>

mount | grep platform-files
# Beklenen: <ip>:/srv/files/export on /mnt/platform-files type nfs ...
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

**NFS dosyaları görünmüyor (Linux):**  
```bash
mountpoint -q /mnt/platform-files && echo "mount OK" || echo "MOUNT YOK"
# Mount yoksa:
sudo mount -t nfs <FILES-01-IP>:/srv/files/export /mnt/platform-files
docker compose restart fileservice
```
