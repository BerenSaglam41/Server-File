# Files-01 NFS Kurulum — Oturum Notları

Bu dosya, UTM Ubuntu VM'i Files-01 olarak ayağa kaldırırken yapılan adımları, karşılaşılan sorunları ve
mevcut durumu özetler. Yeni bir oturumda referans noktası olarak kullan.

---

## Mevcut Durum

| Bileşen | Durum | Not |
|---|---|---|
| Ubuntu VM (UTM) | Çalışıyor | Files-01 sunucusu |
| NFS server | Kurulu ve aktif | `/srv/files` export ediliyor |
| Mac NFS mount | Çalışıyor | `/Volumes/platform-files` |
| Docker volume | NFS'e bağlı | `/Volumes/platform-files:/app/storage` |
| Upload testi | Geçti ✅ | Dosya Ubuntu'da oluştu |

---

## Topoloji

```
Mac (uygulama katmanı)              Ubuntu VM - Files-01 (depolama)
┌──────────────────────────────┐    ┌──────────────────────────────────┐
│ Docker Compose               │    │ IP: 192.168.64.3                 │
│  - postgres                  │    │                                  │
│  - keycloak                  │    │ /srv/files/                      │
│  - fileservice  ─────────────┼────▶  export/  ← ReadPath+ExportPath │
│  - yonetimapi                │NFS │  staging/ ← StagingPath         │
│  - gateway                   │    │  manifests/                      │
│                              │    │  restore-tests/                  │
│ /Volumes/platform-files      │    │                                  │
│   (NFS mount noktası)        │    │ NFS: rw, no_root_squash          │
└──────────────────────────────┘    └──────────────────────────────────┘
```

---

## Ubuntu'da Yapılanlar

### 1. NFS server kurulumu
```bash
sudo apt update && sudo apt install -y nfs-kernel-server
```

### 2. Dizin yapısı
```bash
sudo mkdir -p /srv/files/export/personnel /srv/files/export/fleet
sudo mkdir -p /srv/files/staging/personnel /srv/files/staging/fleet
sudo mkdir -p /srv/files/manifests/personnel
sudo mkdir -p /srv/files/restore-tests/personnel
echo "probe" | sudo tee /srv/files/export/.probe > /dev/null
```

### 3. İzinler (development — üretim için ayrı bakınız)
```bash
sudo chown -R nobody:nogroup /srv/files
sudo chmod -R 755 /srv/files

# Sorun: Docker container (root) NFS'e yazamıyordu → 777 ile çözüldü
sudo chmod -R 777 /srv/files
```

### 4. NFS export yapılandırması
```bash
# /etc/exports içeriği:
/srv/files  *(rw,sync,no_subtree_check,no_root_squash)
```
```bash
sudo systemctl enable --now nfs-kernel-server
sudo exportfs -ra
```

### 5. Probe dosyası
```bash
cat /srv/files/export/.probe   # "probe" yazıyor olmalı
```

---

## Mac'te Yapılanlar

### NFS mount
```bash
sudo mkdir -p /Volumes/platform-files
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files
```

**ÖNEMLİ:** Bu mount kalıcı değil — Mac her yeniden başladığında tekrar çalıştırılması gerekir.
Kalıcı yapmak için `/etc/fstab` veya launchd plist gerekir (henüz yapılmadı).

### docker-compose.yml değişikliği
```yaml
# ESKİ:
volumes:
  - ./test-storage:/app/storage

# YENİ:
volumes:
  - /Volumes/platform-files:/app/storage
```

### FileServiceApi appsettings.json değişikliği
```json
"FileStorage": {
  "ReadPath":    "/Volumes/platform-files/export",
  "StagingPath": "/Volumes/platform-files/staging",
  "ExportPath":  "/Volumes/platform-files/export"
}
```

---

## Karşılaşılan Sorunlar ve Çözümler

| Sorun | Sebep | Çözüm |
|---|---|---|
| `mkdir: /mnt: Read-only file system` | macOS `/mnt` write-protected | `/Volumes/platform-files` kullandık |
| `mount: invalid file system` | Mount noktası oluşmadan mount denendi | `/Volumes` altında mkdir + resvport seçeneği |
| `Permission denied` (container yazamıyor) | `/srv/files` izni `nobody:nogroup 755` | `sudo chmod -R 777 /srv/files` (dev ortamı) |

---

## Test Sonucu

Upload testi başarılı:
```
POST /internal/files → 200
fileId: 8e60ad76-e503-46c7-ab02-a3561f32a625
Ubuntu'da: /srv/files/export/personnel/8e/60/8e60ad76-....jpg
```

Health check:
```
GET /health → {"status":"healthy","checks":{"storage":{"status":"healthy"},"database":{"status":"healthy"}}}
```

---

## Eksik / Yapılmadı

- [ ] **Mount kalıcılığı**: `/Volumes/platform-files` Mac reboot'ta yok olur. `launchd` plist ile kalıcı hale getirmek gerekiyor.
- [ ] **Güvenli izin modeli**: `chmod 777` dev ortamı için kabul edilebilir. Üretimde `files-nfs-ro` / `files-publishers` grup modeli + `all_squash` ile doğru UID/GID mapping yapılmalı (bkz. `files01-nfs-setup.md`).
- [ ] **Firewall**: Ubuntu'da yalnız Mac IP'sinden NFS erişimi kısıtlanmadı (`*` kullanıldı). Üretimde IP bazlı kısıtlama.
- [ ] **Staging ayrımı**: Mevcut kurulumda staging ve export aynı NFS mount'u üzerinden erişiliyor. Üretimde staging yerel (Files-01 host'unda) olmalı, NFS yalnız export'u sunmalı.
- [ ] **Files-01 hostname**: `192.168.64.3` statik IP değil. UTM DHCP'den alınan bu IP değişebilir. Statik IP veya hostname yapılandırması önerilir.

---

## Mevcut Config Özeti

| Değişken | Değer |
|---|---|
| Ubuntu IP | `192.168.64.3` |
| NFS export | `/srv/files` |
| Mac mount noktası | `/Volumes/platform-files` |
| Docker volume | `/Volumes/platform-files:/app/storage` |
| ReadPath | `/Volumes/platform-files/export` (→ `/app/storage/export` container içinde) |
| StagingPath | `/Volumes/platform-files/staging` (→ `/app/storage/staging`) |
| ExportPath | `/Volumes/platform-files/export` (→ `/app/storage/export`) |

---

## Mac Reboot Sonrası Yapılacaklar

```bash
# 1. NFS'i yeniden mount et
sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files

# 2. Servisleri başlat
cd /Users/mustafaberen41/Desktop/dosya-sistemi-projesi
docker compose up -d

# 3. Doğrula
curl http://localhost:5205/health
```
