# files-01 NFS Kurulum Runbook

files-01 (192.168.64.3), tüm sisteme dosya depolama sağlayan NFS sunucusudur.
Test/UTM modunda Mac ve API sunucusu bu sunucuya bağlanabilir. Production minimum modunda
yalnız API/FileService sunucusu (varsayılan: 192.168.64.5) mount edebilir.

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
sudo mkdir -p /srv/files/staging/fleet
sudo mkdir -p /srv/files/manifests/personnel
sudo mkdir -p /srv/files/restore-tests/personnel

# Test izinleri
sudo chmod -R 777 /srv/files
```

Production'da `777` kullanılmaz. Hedef izin modeli `files01-nfs-model.md` içindeki sahiplik/izin tablosudur.

**Public zone dizini (Faz B3, 2026-07-03):** `export/`'ten TAMAMEN ayrı, ikinci bir fiziksel kök dizin
— kimlik doğrulaması olmadan servis edilen dosyalar (`classification=official` + `zone=public`) için:
```bash
sudo mkdir -p /srv/files/export-public
sudo chown files-writer:files-publishers /srv/files/export-public
sudo chmod 0770 /srv/files/export-public
```
Bu dizin `export/`den ayrı tutulur (savunma derinliği — bir path-traversal hatası bile iki ağacı
karıştıramaz). Ayrı bir NFS export girişi GEREKMEZ — mevcut export zaten `/srv/files`'in tamamını
kapsar, yeni alt dizin otomatik görünür olur. `FilesPublisher`'ın `EXPORT_ROOT_PUBLIC` env var'ı
(`platform-files-publisher.service`) bu dizini gösterir; Gateway'de `/public/` location'ı (kimlik
doğrulaması yok) bu dizinin salt-okunur bir bind-mount'undan servis eder. Detay: `MIMARI.md` bölüm 6.6,
`proof/b3-public-private-zone.md`.

### 3. Health check probe

FileServiceApi bu dosyayı okuyarak storage'ın erişilebilir olduğunu doğrular.

```bash
echo "probe" | sudo tee /srv/files/export/.probe > /dev/null
```

### 4. NFS export

Bu repo Files-01 üzerinde çalıştırılabilecek yardımcı script içerir:

```bash
# Production minimum: yalnız API/FileService sunucusu mount edebilir
sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 ./tools/configure-files01-nfs.sh

# UTM/test: eski kolaylık, ağdaki makineler mount edebilir
sudo NFS_MODE=test ./tools/configure-files01-nfs.sh
```

Script kullanmadan elle yapmak gerekirse UTM/test profili:

```bash
echo "/srv/files  *(rw,sync,no_subtree_check)" | sudo tee /etc/exports
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

Production minimum profili:

```bash
API_SERVER_IP="<API_SERVER_IP>"
sudo NFS_MODE=production API_SERVER_IP="$API_SERVER_IP" ./tools/configure-files01-nfs.sh
```

Script `files-writer:files-publishers` kimliğini oluşturur ve production export'u şu modele çeker:

```exports
/srv/files <API_SERVER_IP>(rw,sync,no_subtree_check,all_squash,anonuid=<FILES_WRITER_UID>,anongid=<FILES_WRITER_GID>)
```

Elle `root_squash` kullanmak container içindeki FileService upload akışında `503 storage_unavailable`
üretebilir; production minimum için script kullanılmalıdır.

Katı production modelinde export runtime için read-only olacak şekilde ayrı tasarlanır; detaylar `runbooks/production-hardening.md` içindedir.

### 5. Doğrula

```bash
# Files-01 üzerinde — önce/sonra gerçek export satırını gör
cat /etc/exports

# Files-01 üzerinde — aktif NFS exportlarını gör
sudo exportfs -v

showmount -e localhost
# UTM/test beklenen:
# Export list for localhost:
# /srv/files  *
#
# Production beklenen (showmount firewall/NFSv4 ortamında yanıltıcı olabilir):
# /srv/files  <API_SERVER_IP>

id files-writer
sudo stat -c '%n -> %U:%G %a %u:%g' \
  /srv/files/staging \
  /srv/files/staging/personnel \
  /srv/files/export \
  /srv/files/export/personnel
```

API sunucusunda:

```bash
mount | grep platform-files
nc -vz <FILES_01_IP> 2049
# Beklenen: succeeded/open
bash setup-server.sh
# Beklenen: [OK] Fileservice container staging -> export yazma/taşıma testi geçti
```

Mac veya izinsiz başka makinede:

```bash
sudo mkdir -p /tmp/files01-test
sudo mount -t nfs -o resvport <FILES_01_IP>:/srv/files /tmp/files01-test
# Production beklenen: access denied veya timeout
```

---

## Dizin Yapısı

```
/srv/files/
  export/            ← API sunucusu + Mac bu dizini okur (ReadPath + ExportPath) — PRIVATE dosyalar
    personnel/
    fleet/
    .probe           ← FileServiceApi health check
  export-public/     ← Gateway'in ayrı, kimlik-doğrulamasız mount'u — PUBLIC dosyalar (Faz B3)
                        export/'ten TAMAMEN ayrı fiziksel kök, aynı shard şeması
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

Production minimum modunda Mac satırı geçerli değildir; Mac/başka VM mount edememelidir. Production'da
dosyalara yalnız Gateway → Uygulama API → FileService akışıyla erişilir.

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
| Upload `503 storage_unavailable` | `docker compose logs fileservice`; `Permission denied` varsa `/etc/exports` `all_squash,anonuid=...,anongid=...` mı ve `files-writer` var mı kontrol et |
| `Access to /app/storage/staging/personnel/<shard> is denied` | Files-01'de `sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 bash setup-files01.sh`; API sunucusunda remount + `bash setup-server.sh` |
| Host yazabiliyor ama upload patlıyor | Host testi yeterli değildir; container probe (`setup-server.sh`) geçmelidir |
