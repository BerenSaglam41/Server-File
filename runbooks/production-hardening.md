# Production Hardening Runbook

Bu runbook mevcut UTM/test kurulumunu bozmadan production'a giderken kapatılması gereken altyapı açıklarını tanımlar.

Ana proje kararları değişmez:

```text
Client -> Gateway -> Uygulama API -> FileServiceApi -> DB files.* -> Files-01
```

Files-01 public servis değildir. Client, YonetimApi ve FlotaApi Files-01'e doğrudan gitmez.

## 1. NFS Export ve Firewall

Neden: Files-01 storage-only bileşendir ve istemciye public servis sunmaz. NFS export
`/srv/files *` olarak kaldığında aynı ağdaki herhangi bir makine FileService, Gateway,
auth ve audit katmanlarını bypass ederek Files-01'i mount edebilir. Bu durum hedef mimariyi bozar:

```text
Client -> Gateway -> Uygulama API -> FileServiceApi -> Files-01
```

Minimum production hedefi, strict read-only publisher modeline geçmeden önce bu bypass yolunu
kapatmaktır. Upload/download/archive kodu değişmeden çalışmaya devam eder; yalnız NFS'e erişebilen
host listesi daraltılır.

### Test / UTM profili

UTM ve lokal testte pratiklik için tüm `/srv/files` dizini rw export edilebilir:

```exports
/srv/files *(rw,sync,no_subtree_check)
```

Bu profil production için uygun değildir. Sadece kapalı test ağı ve geçici geliştirme ortamı içindir.

### Production minimum profil

Mevcut FileService upload akışı staging'e yazıp export'a `File.Move` yaptığı için API sunucusunun NFS üzerinde yazma yetkisi gerekir. Bu yüzden minimum production profili geniş `*` yerine yalnız API sunucusu IP'sini allowlist eder:

```exports
/srv/files <API_SERVER_IP>(rw,sync,no_subtree_check,root_squash)
```

Firewall sadece API sunucusundan TCP/2049 kabul etmelidir:

```bash
sudo ufw default deny incoming
sudo ufw allow from <API_SERVER_IP> to any port 2049 proto tcp
sudo ufw enable
```

Repo içindeki yardımcı script Files-01 üzerinde aynı ayarı uygular:

```bash
sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 ./tools/configure-files01-nfs.sh
```

UTM/test kolaylığı bilinçli olarak korunacaksa:

```bash
sudo NFS_MODE=test ./tools/configure-files01-nfs.sh
```

Kural: `NFS_MODE=production` iken `/etc/exports` içinde `/srv/files *` kesinlikle yazılmamalıdır.

Doğrulama:

```bash
cat /etc/exports
sudo exportfs -v
showmount -e localhost
```

API sunucusunda:

```bash
mount | grep platform-files
nc -vz <FILES_01_IP> 2049       # başarılı olmalı
```

Mac veya izinsiz makinede:

```bash
sudo mkdir -p /tmp/files01-test
sudo mount -t nfs -o resvport <FILES_01_IP>:/srv/files /tmp/files01-test
# Beklenen: access denied veya timeout
```

### 2026-07-01 canlı doğrulama sonucu

Files-01 production minimum profile alındı:

```text
/etc/exports:
/srv/files 192.168.64.5(rw,sync,no_subtree_check,root_squash)

exportfs -v:
/srv/files 192.168.64.5(sync,wdelay,hide,no_subtree_check,sec=sys,rw,secure,root_squash,no_all_squash)

ufw:
Default: deny (incoming)
2049/tcp ALLOW IN 192.168.64.5
```

API sunucusu doğrulaması:

```text
nc -vz 192.168.64.3 2049 -> succeeded
mount -> 192.168.64.3:/srv/files on /mnt/platform-files type nfs4 ... clientaddr=192.168.64.5
ls /mnt/platform-files/export/.probe -> OK
```

Mac/izinsiz makine doğrulaması:

```text
nc -vz -G 3 192.168.64.3 2049 -> Operation timed out
```

Sonuç: `*` export kapandı, NFS TCP/2049 yalnız API/FileService sunucusuna açık.

## 2. Katı Production Modeli

En katı modelde FileService runtime host'u export'u read-only görür. Yazma/publish işi ayrı bir kontrollü publisher sürecine taşınır.

Hedef:

```text
FileServiceApi runtime -> /srv/files/export: ro
Publisher/ops process  -> /srv/files/staging + /srv/files/export: kontrollü rw
```

Bu modele geçmek için FileService upload path'i iki parçaya ayrılmalıdır:

1. FileService upload isteğini alır, policy/magic-byte/hash doğrular.
2. Binary staging'e değil local temp veya publisher queue'ya yazılır.
3. Publisher Files-01 üzerinde dosyayı export path'e atomik publish eder.
4. FileService DB kaydını publish sonucuna göre tamamlar veya rollback eder.

Bu V2 hardening işidir. Mevcut iki sunuculu modelde şimdilik minimum production profili yeterlidir.

## 3. Staging / Export Ayrımı

Mevcut çalışan model:

```text
/srv/files/export   -> ReadPath + ExportPath
/srv/files/staging  -> StagingPath
```

İkisi aynı NFS export altında olduğu için `File.Move` atomiktir ve testte pratiktir.

Production minimum:

- `/srv/files` yalnız API sunucusuna rw export edilir.
- `staging` geçici kabul edilir; backup kapsamına alınmaz.
- `export`, `manifests` ve DB dump backup kapsamına alınır.
- `staging` için periyodik temizlik yapılır; aktif upload olmayan eski dosyalar silinir.
- Bu model güvenlikte `*` export'a göre büyük iyileştirmedir ama FileService runtime hâlâ NFS yazma yetkisine sahiptir.

Katı production:

- `export` runtime için ro olur.
- `staging` yalnız publisher/ops kimliğiyle rw olur.
- FileService runtime direkt NFS write yapmaz.
- Bu model için mevcut `CreateFileAsync` upload akışı değişmelidir; aksi halde runtime export'u read-only olduğunda upload çalışmaz.

### Staging temizliği

Minimum production profilinde staging NFS altında kaldığı için orphan dosya temizliği gerekir. Örnek:

```bash
find /srv/files/staging -type f -mmin +120 -print -delete
find /srv/files/staging -type d -empty -delete
```

Bu komut cron/systemd timer ile çalıştırılmadan önce test ortamında kuru çalıştırılmalıdır:

```bash
find /srv/files/staging -type f -mmin +120 -print
```

Aktif upload süresi ve maksimum dosya boyutuna göre `+120` eşiği büyütülebilir.

## 4. Backup / Restore

Repoda iki operasyon scripti vardır:

```bash
tools/backup-files01.sh
tools/restore-test.sh
```

Backup kapsamı:

- `export/`
- `manifests/`
- PostgreSQL `platformdb` dump

Backup dışı:

- `staging/` geçici alandır ve bilinçli olarak yedeğe girmez.
- `restore-tests/` test çıktısıdır, canlı veri kaynağı değildir.

Örnek production kullanımı:

```bash
STORAGE_ROOT=/mnt/platform-files \
BACKUP_ROOT=/backup/platform-files \
tools/backup-files01.sh

STORAGE_ROOT=/mnt/platform-files \
BACKUP_ROOT=/backup/platform-files \
tools/restore-test.sh
```

Canlı doğrulama sırası API/FileService sunucusunda çalıştırılır:

```bash
cd ~/Server-File
git pull
bash setup-server.sh

docker compose ps
mount | grep platform-files
ls /mnt/platform-files/export/.probe

sudo mkdir -p /backup/platform-files
sudo chown -R "$USER:$USER" /backup/platform-files

STORAGE_ROOT=/mnt/platform-files \
BACKUP_ROOT=/backup/platform-files \
./tools/backup-files01.sh

ls -lh /backup/platform-files/*/platformdb.dump
ls -lh /backup/platform-files/*/export.sha256

STORAGE_ROOT=/mnt/platform-files \
BACKUP_ROOT=/backup/platform-files \
./tools/restore-test.sh

find /mnt/platform-files/restore-tests -maxdepth 3 -type f | tail
```

Kabul:

- Backup klasöründe `export/`, `manifests/`, `export.sha256`, `platformdb.dump`, `backup-info.txt` oluşur.
- `platformdb.dump` boş değildir.
- Restore testi canlı `export/` alanına yazmaz; yalnız `restore-tests/<timestamp>/export` altını kullanır.
- Hash doğrulama `OK` döner.
- `SKIP_DB_DUMP=1` production doğrulamada kullanılmaz.

### 2026-07-01 canlı backup/restore sonucu

API/FileService sunucusunda gerçek PostgreSQL dump ve Files-01 export backup'ı alındı:

```text
Backup target:
/backup/platform-files/20260701T071527Z

Backup sonucu:
[OK] Backup completed: /backup/platform-files/20260701T071527Z

DB dump:
/backup/platform-files/20260701T071527Z/platformdb.dump
```

Restore testi canlı `export/` alanına yazmadan restore-tests altında çalıştı:

```text
Restore target:
/mnt/platform-files/restore-tests/20260701T071530Z

Restore sonucu:
[OK] Restore test completed: /mnt/platform-files/restore-tests/20260701T071530Z
```

`export.sha256` manifestindeki fleet/personnel dosyaları ve `.probe` için hash doğrulama `OK` döndü.
Sonraki adım bu komutları systemd timer ve log/alert takibine bağlamaktır.

Restore testi canlı `export/` alanına geri yazmaz; yedeği `restore-tests/<timestamp>/export` altına açar ve `export.sha256` manifestini doğrular.

Kuru storage testi veya CI benzeri DB'siz kontrolde `SKIP_DB_DUMP=1` kullanılabilir. Production backup'ta kullanılmamalıdır:

```bash
STORAGE_ROOT=/tmp/fake-storage BACKUP_ROOT=/tmp/fake-backup SKIP_DB_DUMP=1 tools/backup-files01.sh
```

Production kabulü için backup tek seferlik komut olarak kalmamalıdır. Hedef:

- Günlük backup: `export/`, `manifests/`, `platformdb.dump`.
- Haftalık restore testi: canlı `export/` alanına yazmadan `restore-tests/` altında hash doğrulama.
- Backup çıktısı Files-01 üstünde tek kopya olarak kalmamalı; ayrı disk, ayrı VM veya object storage'a kopyalanmalıdır.
- Her çalıştırma sonunda log ve exit code izlenmelidir. Başarısız backup production alarmı sayılır.

Basit systemd timer örneği:

```ini
# /etc/systemd/system/platform-files-backup.service
[Unit]
Description=Platform Files-01 backup

[Service]
Type=oneshot
WorkingDirectory=/opt/dosya-sistemi-projesi
Environment=STORAGE_ROOT=/mnt/platform-files
Environment=BACKUP_ROOT=/backup/platform-files
ExecStart=/opt/dosya-sistemi-projesi/tools/backup-files01.sh
```

```ini
# /etc/systemd/system/platform-files-backup.timer
[Unit]
Description=Daily Platform Files-01 backup

[Timer]
OnCalendar=*-*-* 03:00:00
Persistent=true

[Install]
WantedBy=timers.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now platform-files-backup.timer
systemctl list-timers platform-files-backup.timer
```

## 5. Sertifika Üretimi ve Rotasyon

`certs/generate-certs.sh` artık mevcut CA ve sertifikaları varsayılan olarak ezmez. Eksik dosyaları üretir, bilinçli yenileme için `FORCE_REGENERATE_CERTS=1` gerekir.

Gateway SAN değerleri ortama göre parametrelenebilir:

```bash
GATEWAY_DNS="gateway,localhost,platform.example.com" \
GATEWAY_IPS="127.0.0.1,<API_SERVER_IP>" \
bash certs/generate-certs.sh
```

Mevcut CA'yı yenilemek production'da tüm servislerin aynı güven zincirini değiştirmek demektir. Rotasyon prosedürü:

1. Yeni CA/sertifikalar ayrı dizinde üretilir.
2. Yeni CA önce trust bundle'a eklenir.
3. Servis client sertifikaları rolling olarak yenilenir.
4. FileService allowed client CN listesi doğrulanır.
5. Gateway cert'i yenilenir.
6. Eski CA tüm servisler yeni zincire geçtikten sonra kaldırılır.

VPS/public domain için Gateway'de Let's Encrypt tercih edilir; iç servis mTLS CA'sı public TLS'ten ayrı tutulur.

## 6. Gateway Public TLS / Let's Encrypt

Development ve UTM ortamında gateway'in host portu `5090` olabilir. Gerçek public production'da hedef
normal kullanıcı URL'sinin portsuz çalışmasıdır:

```text
https://platform.example.com
```

Bunun için production compose veya reverse proxy katmanı gateway'i public `443` üzerinden yayınlamalıdır:

```yaml
gateway:
  ports:
    - "443:443"
    # HTTP-01 challenge veya redirect kullanılacaksa:
    - "80:80"
```

Let's Encrypt için iki kabul edilebilir model vardır:

1. `certbot --standalone`: sertifika yenileme sırasında 80 portu geçici olarak certbot'a verilir, sonra gateway restart edilir.
2. Reverse proxy / webroot modeli: 80/443 dış reverse proxy'de kalır, gateway iç ağa HTTPS veya HTTP ile bağlanır.

Mevcut repo self-signed gateway sertifikasıyla iç ağ/dev için çalışır. Public production'a çıkarken
`certs/gateway.crt` ve `certs/gateway.key` Let's Encrypt fullchain/privkey kopyalarıyla beslenmeli veya
gateway'in önüne ayrı TLS termination konmalıdır.

Doğrulama:

```bash
curl https://platform.example.com/health
openssl s_client -connect platform.example.com:443 -servername platform.example.com </dev/null
```

## 7. Secret Rotasyonu

Demo/test ortamındaki değerler production secret kabul edilmez:

- Keycloak client secret'ları: `yonetimapi-secret-v1`, `filoapi-secret`.
- PostgreSQL parolası: `platformpass`.
- Demo kullanıcı parolaları: `Demo1234!`.
- İç mTLS private key'leri ve CA key'i.

Production hedefi:

- `docker-compose.yml` demo default olarak kalabilir; production deploy ayrı `.env.prod` veya secret manager ile yapılır.
- Compose içinde gerçek secret literal yazılmaz; `${YONETIMAPI_CLIENT_SECRET}`, `${FILOAPI_CLIENT_SECRET}`, `${POSTGRES_PASSWORD}` gibi env değişkenleri kullanılır.
- Keycloak production realm import'u demo realm'den ayrılır veya deploy sonrası admin/API ile secret'lar rotate edilir.
- Demo kullanıcılar production realm'de bulunmaz; gerekiyorsa sadece kapalı UAT ortamında tutulur.
- CA/private key dosyaları repo dışında saklanır; dosya izinleri owner-only olmalıdır.

Önerilen rotasyon sırası:

1. Yeni Keycloak client secret üret.
2. Production env/secret store'u güncelle.
3. İlgili uygulama container'ını rolling restart et.
4. Eski secret'la token alınamadığını doğrula.
5. Audit/loglarda başarısız auth artışı olmadığını kontrol et.

## 8. Appsettings / Environment Sınırı

`appsettings.json` dosyalarındaki `localhost`, Mac path ve local connection string değerleri local fallback kabul edilir.

Production davranışı Docker environment değişkenleriyle belirlenir:

- `ConnectionStrings__PlatformDb`
- `FileStorage__ReadPath`
- `FileStorage__StagingPath`
- `FileStorage__ExportPath`
- `FileService__BaseUrl`
- `Keycloak__Authority`
- `Keycloak__MetadataAddress`
- `Keycloak__TokenUrl`
- `Mtls__*`

Production kontrolü:

```bash
docker compose -f docker-compose.yml config
docker compose exec fileservice printenv | grep -E 'FileStorage|Keycloak|ConnectionStrings|Mtls'
docker compose exec yonetimapi printenv | grep -E 'FileService|Keycloak|ConnectionStrings|Mtls'
```

Kural: production'da container içindeki aktif config `localhost` veya `/Volumes/...` göstermemelidir. İstisna: Keycloak issuer bilerek `localhost:8080` olarak sabitlenmiş dev/test senaryosu.

## 9. Kabul Kapıları

Production'a geçmeden önce şu kapılar tamamlanır:

| Kapı | Beklenen |
|---|---|
| NFS export | `*` yok, yalnız API sunucusu IP allowlist |
| Firewall | TCP/2049 yalnız API sunucusuna açık |
| Gateway | Public production'da gerçek domain + 443 + Let's Encrypt veya dış TLS termination |
| İç mTLS | CA korunmuş, servis sertifikaları doğrulanıyor |
| Secrets | Demo secret/parola yok; production env/secret store kullanılıyor |
| Env config | Compose config production değerleriyle çalışıyor |
| Storage health | `.probe` okunuyor |
| Write path | Upload 200, archive 200, DB rollback testi temiz |
| Backup | Otomasyon export/manifests/db dump üretir ve sonucu izlenir |
| Restore test | Periyodik restore testi restore-tests altında hash doğrular |
| Denial test | API dışı host NFS mount/2049 erişimi alamıyor |
