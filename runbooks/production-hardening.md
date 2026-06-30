# Production Hardening Runbook

Bu runbook mevcut UTM/test kurulumunu bozmadan production'a giderken kapatılması gereken altyapı açıklarını tanımlar.

Ana proje kararları değişmez:

```text
Client -> Gateway -> Uygulama API -> FileServiceApi -> DB files.* -> Files-01
```

Files-01 public servis değildir. Client, YonetimApi ve FlotaApi Files-01'e doğrudan gitmez.

## 1. NFS Export ve Firewall

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

Doğrulama:

```bash
sudo exportfs -v
showmount -e localhost
nc -vz <FILES_01_IP> 2049       # API sunucusundan başarılı olmalı
nc -vz <FILES_01_IP> 2049       # başka hosttan başarısız olmalı
```

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

Restore testi canlı `export/` alanına geri yazmaz; yedeği `restore-tests/<timestamp>/export` altına açar ve `export.sha256` manifestini doğrular.

Kuru storage testi veya CI benzeri DB'siz kontrolde `SKIP_DB_DUMP=1` kullanılabilir. Production backup'ta kullanılmamalıdır:

```bash
STORAGE_ROOT=/tmp/fake-storage BACKUP_ROOT=/tmp/fake-backup SKIP_DB_DUMP=1 tools/backup-files01.sh
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

## 6. Appsettings / Environment Sınırı

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

## 7. Kabul Kapıları

Production'a geçmeden önce şu kapılar tamamlanır:

| Kapı | Beklenen |
|---|---|
| NFS export | `*` yok, yalnız API sunucusu IP allowlist |
| Firewall | TCP/2049 yalnız API sunucusuna açık |
| Gateway | Public'te gerçek TLS veya doğru SAN'lı internal cert |
| İç mTLS | CA korunmuş, servis sertifikaları doğrulanıyor |
| Env config | Compose config production değerleriyle çalışıyor |
| Storage health | `.probe` okunuyor |
| Write path | Upload 200, archive 200, DB rollback testi temiz |
| Backup | `tools/backup-files01.sh` export/manifests/db dump üretir |
| Restore test | `tools/restore-test.sh` restore-tests altında hash doğrular |
| Denial test | API dışı host NFS mount/2049 erişimi alamıyor |
