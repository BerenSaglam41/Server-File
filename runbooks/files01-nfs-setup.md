# Files-01 NFS Kurulum Runbook

Bu runbook Files-01 NFS export kurulumu, File-Service runtime mount ve doğrulama adımlarını kapsar.
Gerçek IP, hostname, parola veya PII bu dosyaya yazılmaz. VLAN/IP bilgileri kurum içi kontrollu kanalda tutulur.

## Ön Koşullar

- Files-01 sunucusu private network'te erişilebilir durumda
- File-Service runtime host'u (APP01 veya benzeri) Files-01 ile aynı VLAN'da veya allowlist'te
- NFSv4.2 kernel modülü her iki tarafta aktif
- `files-nfs-ro` grubu ve üyeliği yapılandırılmış

---

## 1. Files-01 — Dizin Yapısı

Hedef dizin yapısı (`files01-nfs-model.md` ile birebir):

```text
/srv/files
  /export          ← NFS read-only export (ReadPath)
    /personnel
    /fleet
    .probe
  /staging         ← Upload yazma alanı (StagingPath) — NFS export'a dahil değil
    /personnel
    /fleet
  /manifests       ← Migration/publish manifestleri (PII yok)
    /personnel
  /restore-tests   ← Restore test çıktıları (PII yazılmaz)
    /personnel
```

```bash
# Root dizinler
sudo mkdir -p /srv/files/export/personnel
sudo mkdir -p /srv/files/staging/personnel
sudo mkdir -p /srv/files/manifests/personnel
sudo mkdir -p /srv/files/restore-tests/personnel

# Sahiplik: export read-only grup, staging write grubu
sudo chown -R root:files-nfs-ro   /srv/files/export
sudo chown -R root:files-publishers /srv/files/staging
sudo chown -R root:files-publishers /srv/files/manifests
sudo chown -R root:files-publishers /srv/files/restore-tests

# İzinler
sudo chmod -R 750 /srv/files/export
sudo chmod -R 750 /srv/files/staging
sudo chmod -R 750 /srv/files/manifests
sudo chmod -R 750 /srv/files/restore-tests

# Dosya izni (export altındaki tüm dosyalar)
sudo find /srv/files/export -type f -exec chmod 640 {} \;
```

## 2. Files-01 — Probe Dosyası

Health check'in storage'ı doğrulaması için probe dosyası:

```bash
echo "probe" | sudo tee /srv/files/export/.probe > /dev/null
sudo chown root:files-nfs-ro /srv/files/export/.probe
sudo chmod 640 /srv/files/export/.probe
```

## 3. Files-01 — NFS Export Yapılandırması

Yalnız `/srv/files/export` export edilir. `staging`, `manifests`, `restore-tests` NFS dışında kalır.

`/etc/exports` dosyasına eklenecek satır (IP alanı VLAN/IP netleşince doldurulur):

```
/srv/files/export  <FILE-SERVICE-RUNTIME-IP>(ro,sync,no_subtree_check,all_squash,anonuid=<files-nfs-ro-uid>,anongid=<files-nfs-ro-gid>,fsid=100)
```

Kurallar:
- `ro` — read-only export; runtime host yazamaz
- `sync` — yazma onayı disk commit sonrası
- `no_subtree_check` — inode değişimlerinde yanlış red riskini azaltır
- `all_squash` — tüm istemci kullanıcıları `files-nfs-ro` kimliğine map edilir
- NFSv3/NFSv2 açılmaz; `rpcbind` bağımlılığı oluşturulmaz
- `staging/` asla export edilmez — geçici alan, doğrulanmamış dosyalar içerebilir

Export'u aktif et:

```bash
sudo exportfs -ra
sudo systemctl enable --now nfs-server
```

Export'u doğrula:

```bash
sudo exportfs -v
# Çıktıda /srv/files/export ve izinleri görünmeli
```

## 4. File-Service Runtime Host — NFS Mount

Mount noktası:

```bash
sudo mkdir -p /mnt/platform-files
sudo chown root:files-nfs-ro /mnt/platform-files
sudo chmod 750 /mnt/platform-files
```

`/etc/fstab` satırı (kalıcı mount):

```
<FILES-01-PRIVATE-ALIAS>:/srv/files/export  /mnt/platform-files  nfs4  ro,nfsvers=4.2,hard,timeo=600,retrans=3,_netdev  0  0
```

Mount et:

```bash
sudo mount -a
# veya tek seferlik:
sudo mount -t nfs4 -o ro,nfsvers=4.2 <FILES-01-PRIVATE-ALIAS>:/srv/files/export /mnt/platform-files
```

## 5. File-Service API Yapılandırması

`FileServiceApi/appsettings.json` — üç ayrı path:

```json
{
  "FileStorage": {
    "ReadPath":    "/mnt/platform-files",
    "StagingPath": "/srv/files/staging",
    "ExportPath":  "/srv/files/export"
  }
}
```

| Anahtar | Açıklama | Production değeri |
|---|---|---|
| `ReadPath` | NFS read-only mount — okuma ve health check probe | `/mnt/platform-files` |
| `StagingPath` | Upload ilk yazma alanı — NFS'i bypass eder | `/srv/files/staging` |
| `ExportPath` | Doğrulanmış dosyaların kalıcı alanı — atomic rename hedefi | `/srv/files/export` |

**Upload akışı:**
1. Binary `StagingPath`'e yazılır.
2. Disk yazma bütünlüğü için SHA256 staging dosyasından hesaplanır.
3. `File.Move` ile staging → export atomic rename yapılır (aynı FS → rename, NFS bypass).
4. DB `files.objects` + `files.references` kayıtları oluşur.
5. DB insert başarısız olursa export dosyası silinir (rollback).

**Önemli:** `StagingPath` ve `ExportPath` NFS mount'una yönlendirilmez. V1 co-location'da File-Service API ve Files-01 aynı hostta olduğu için her ikisi de yerel `/srv/files` dizinindedir.

`GET /health` endpoint'i `ReadPath`'teki `.probe` dosyasını okur. Probe okunamazsa 503 döner.

## 6. Doğrulama Kapıları

### NFS Port Erişimi

File-Service runtime host'undan:

```bash
nc -zv <FILES-01-PRIVATE-ALIAS> 2049
# Beklenen: Connection succeeded
```

### Mount Başarısı

```bash
mount | grep platform-files
# Beklenen: <alias>:/srv/files/export on /mnt/platform-files type nfs4 (ro,...)
```

### Probe Dosyası Okuma

```bash
cat /mnt/platform-files/.probe
# Beklenen: probe
```

### Yazma Reddi

```bash
touch /mnt/platform-files/test-write 2>&1
# Beklenen: touch: cannot touch '/mnt/platform-files/test-write': Read-only file system
```

### API Health Check

```bash
curl http://localhost:5205/health
# Beklenen:
# {"status":"healthy","service":"FileServiceApi","checks":{"storage":{"status":"healthy"},"database":{"status":"healthy"}}}
```

### NFS Down Senaryosu

Files-01'de export geçici olarak kaldırıldığında:

```bash
curl http://localhost:5205/health
# Beklenen:
# {"status":"unhealthy","service":"FileServiceApi","checks":{"storage":{"status":"unhealthy","reason":"probe_read_failed"},"database":{"status":"healthy"}}}
# HTTP 503
```

### Backup Restore Probe

```bash
# Restore test alanına örnek dosya geri yükle (PII yazma)
cp /srv/files/export/personnel/<shard1>/<shard2>/<file_id>.pdf \
   /srv/files/restore-tests/personnel/<file_id>-restore-test.pdf

# SHA256 doğrula
sha256sum /srv/files/restore-tests/personnel/<file_id>-restore-test.pdf
# Beklenen: DB'deki sha256 ile eşleşmeli
```

## 7. Sorun Giderme

| Belirti | Olası Neden | Kontrol |
|---|---|---|
| `mount: No route to host` | VLAN/firewall | `nc -zv <alias> 2049` |
| `mount: access denied` | Export allowlist'te IP yok | Files-01'de `exportfs -v` |
| `probe_read_failed` health | NFS bağlandı ama probe yok | `.probe` dosyasını oluştur |
| `probe_not_found` health | Mount noktası boş | `mount | grep platform-files` |
| `Permission denied` (okuma) | `all_squash` uid/gid yanlış | Files-01'de `anonuid`/`anongid` kontrol et |

## 8. Güvenlik Notları

- Files-01 public HTTP/HTTPS sunmaz.
- NFS allowlist yalnız File-Service runtime host'unu kapsar; geniş subnet açılmaz.
- NFSv3/NFSv2 ve rpcbind açılmaz.
- Kerberos/NFS sec=krb5p ileride değerlendirilebilir; ilk kurulumda private network + read-only export yeterli.
- Runtime host üzerinden yazma, silme veya rename beklenmez. Publish işlemleri Files-01'de kontrollu operasyon kullanıcısıyla yapılır.
