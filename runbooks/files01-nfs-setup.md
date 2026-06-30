# files-01 NFS Kurulum Runbook

files-01 (192.168.64.3), tüm sisteme dosya depolama sağlayan NFS sunucusudur.
Hem Mac hem API sunucusu (192.168.64.5) bu sunucuya bağlanır.

> Bu runbook UTM/test kurulumunu anlatır. Production için geniş `*` export kullanılmaz;
> production-hardening adımları için `runbooks/production-hardening.md` dosyasını izle.

---

## Kurulum (sıfırdan)

### 1. NFS sunucu paketi

```bash
sudo apt install -y nfs-kernel-server
```

### 2. Dizin yapısı

```bash
sudo mkdir -p /srv/files/export/personnel
sudo mkdir -p /srv/files/export/fleet
sudo mkdir -p /srv/files/staging/personnel
sudo mkdir -p /srv/files/manifests/personnel
sudo mkdir -p /srv/files/restore-tests/personnel

# Test izinleri
sudo chmod -R 777 /srv/files
```

Production'da `777` kullanılmaz. Hedef izin modeli `files01-nfs-model.md` içindeki sahiplik/izin tablosudur.

### 3. Health check probe

FileServiceApi bu dosyayı okuyarak storage'ın erişilebilir olduğunu doğrular.

```bash
echo "probe" | sudo tee /srv/files/export/.probe > /dev/null
```

### 4. NFS export

UTM/test profili:

```bash
echo "/srv/files  *(rw,sync,no_subtree_check)" | sudo tee /etc/exports
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

Production minimum profili:

```bash
API_SERVER_IP="<API_SERVER_IP>"
echo "/srv/files  ${API_SERVER_IP}(rw,sync,no_subtree_check,root_squash)" | sudo tee /etc/exports
sudo exportfs -ra
sudo ufw allow from "$API_SERVER_IP" to any port 2049 proto tcp
```

Katı production modelinde export runtime için read-only olacak şekilde ayrı tasarlanır; detaylar `runbooks/production-hardening.md` içindedir.

### 5. Doğrula

```bash
showmount -e localhost
# UTM/test beklenen:
# Export list for localhost:
# /srv/files  *
#
# Production beklenen:
# /srv/files  <API_SERVER_IP>
```

---

## Dizin Yapısı

```
/srv/files/
  export/            ← API sunucusu + Mac bu dizini okur (ReadPath + ExportPath)
    personnel/
    fleet/
    .probe           ← FileServiceApi health check
  staging/           ← Upload geçici alan (StagingPath) — dosyalar burada yazılır, export'a taşınır
    personnel/
  manifests/         ← Migration manifestleri
    personnel/
  restore-tests/     ← Restore test çıktıları
    personnel/
```

---

## Bağlanan Sistemler

| Sistem | Mount noktası | Komut |
|---|---|---|
| Mac | `/Volumes/platform-files` | `sudo mount -t nfs -o resvport 192.168.64.3:/srv/files /Volumes/platform-files` |
| API sunucusu (192.168.64.5) | `/mnt/platform-files` | `sudo mount -t nfs 192.168.64.3:/srv/files /mnt/platform-files` |

---

## Upload Akışı

1. Dosya `staging/` altına yazılır (`/app/storage/staging/...`)
2. SHA256 hesaplanır
3. `File.Move` ile `export/` altına taşınır (atomic — aynı FS)
4. DB kaydı oluşur
5. DB insert başarısız olursa export dosyası silinir (rollback)

---

## Sorun Giderme

| Belirti | Kontrol |
|---|---|
| `showmount` yanıt vermiyor | `sudo systemctl status nfs-server` |
| Mount başarısız (access denied) | `/etc/exports`'u kontrol et, `sudo exportfs -ra` |
| `probe_not_found` health | `ls /srv/files/export/.probe` — yoksa oluştur |
| API sunucusu dosyaları görmüyor | Mount'tan sonra `docker compose restart fileservice` |
