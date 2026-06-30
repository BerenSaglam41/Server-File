# Files-01 NFS Dosya Servisi Modeli

Bu dokuman Files-01 uzerindeki personel fotograf ve CV dosyalarinin nasil tutulacagini, File-Service API runtime'i tarafindan nasil okunacagini ve uygulamalarin dosyaya nasil yetkili sekilde erisecegini tanimlar.

Gercek IP, kullanici adi, parola, token, personel verisi, dosya adi veya kaynak path bu dokumana yazilmaz. Nihai VLAN/IP bilgileri kurum ici kontrollu kanalda tutulur; burada rol, alias, dizin, izin ve dogrulama sozlesmesi yer alir.

## Hedef Model

| Bilesen | Karar |
| --- | --- |
| Dosya sunucusu | `Files-01` / `files-01` |
| Dosya tuketicisi | File-Service API runtime host'u, ilk fazda APP01 uzerinde konumlanabilir |
| Paylasim protokolu | NFSv4.2, TCP/2049, private network |
| Export modeli | File-Service API runtime host'u icin read-only NFS export |
| Istemci erisimi | Yok; istemci dosyayi dogrudan Files-01'den almaz |
| API erisimi | `GET /files/{fileId}` benzeri fileId tabanli yetkili akis |
| Metadata | DB-01 uzerindeki merkezi File Catalog (`files.*`) modelinde tutulur |
| Binary icerik | Files-01 private storage alaninda tutulur |
| Nihai allowlist | VLAN/IP kesinlesince #3 firewall matrisiyle doldurulur |

## Sinirlar

- Files-01 public HTTP/HTTPS, SMB veya dogrudan istemci erisimi sunmaz.
- File-Service API runtime host'u, Files-01 uzerindeki export'u read-only mount eder.
- Dosya yetki karari NFS tarafinda degil merkezi File Catalog/File-Service sinirinda verilir.
- YonetimAPI ilk tuketici uygulamadir; dosya metadata'sinin sahibi degildir.
- YonetimAPI, Keycloak token'ini dogruladiktan sonra merkezi file permission ve kendi data-scope modelini birlikte kullanir.
- Yetkisiz veya scope disi dosya taleplerinde dosya varligi sizdirilmemelidir; API 404 veya genel yetki hatasi donebilir.
- Dosya metadata'si DB-01'de; dosya byte'lari Files-01'de kalir.
- Files-01 kendi API'sini veya kendi PostgreSQL veritabanini calistirmaz; storage-only rolunde kalir.
- Ikinci uygulama file storage tuketmeye baslamadan once merkezi File-Service API veya esdeger platform servis katmani devreye alinmalidir.

## Dizin Sozlesmesi

Files-01 uzerindeki onerilen ana kok:

```text
/srv/files
  /export
    /personnel
      /<shard1>
        /<shard2>
          /<file_id>.<ext>
  /staging
    /personnel
      /<shard1>
        /<shard2>
          /<file_id>.<ext>
  /manifests
    /personnel
  /restore-tests
    /personnel
```

File-Service API runtime host'u uzerindeki onerilen mount noktasi:

```text
/mnt/platform-files
```

File-Service API dosya okuma islemlerinde local mount path'ini dogrudan istemciye veya uygulama API'lerine gostermez. Uygulamalar yalniz `fileId`, entity referansi ve metadata sozlesmesi uzerinden dosya ister.

## Dosya Adlandirma

Disk uzerindeki dosya adlari PII icermemelidir.

| Domain | Onerilen path |
| --- | --- |
| Personel dosyalari | `/srv/files/export/personnel/<shard1>/<shard2>/<file_id>.<ext>` |

Kurallar:

- `<file_id>` uygulama tarafinda uretilen immutable UUID veya esdeger stable key olur.
- `<shard1>` ve `<shard2>`, `file_id` degerinden turetilen iki seviyeli dagitim klasorudur. Ornek: `a8f3...` icin `a8/f3`.
- Ad, soyad, sicil, TCKN, legacy dosya adi veya legacy path dosya adina yazilmaz.
- Orijinal dosya adi gerekiyorsa DB metadata alaninda tutulur; Files-01 path'ine yazilmaz.
- Uzanti allowlist ile sinirlanir: fotograf icin `jpg`, `jpeg`, `png`, `webp`; CV icin `pdf` onceliklidir.
- Ayni kisinin birden fazla dosyasi DB metadata ile versiyonlanir; path uzerinden anlam yuklenmez.
- Fotograf, CV, resmi evrak, hassasiyet sinifi ve retention kararlari path alt klasorlerine bolunmez; PostgreSQL metadata alanlariyla takip edilir.
- Tarih bazli klasorleme yapilmaz. `created_at`, `uploaded_at`, `source_migrated_at`, `retention_until` gibi tarihler DB metadata'sinda tutulur.

## Metadata ve Servis Sahipligi

Files-01, binary storage sunucusudur; dosya yetkisi veya katalog verisi icin kendi uzerinde API/PostgreSQL calistirmaz.

Metadata ve yetki kayitlari DB-01 uzerinde platform seviyesinde merkezi File Catalog olarak tasarlanir. Bu katalog YonetimAPI'ye ait app-specific tablo seti degildir; YonetimAPI ilk consumer olarak bu katalogdan yararlanir. Detayli katalog modeli [file-catalog-model.md](file-catalog-model.md) dosyasindadir.

Onerilen DB ownership siniri:

| Bilesen | Sorumluluk |
| --- | --- |
| `files.objects` | Fiziksel dosya, hash, boyut, path, status, classification |
| `files.references` | Dosyanin hangi uygulama/domain/entity ile iliskili oldugu |
| `files.app_policies` | Uygulama bazli create/read/archive izinleri, domain ve file type limitleri |
| `files.audit_events` | Dosya create/read/archive/delete denemeleri ve sonuc kayitlari |

Minimum `files.objects` alanlari:

| Alan | Aciklama |
| --- | --- |
| `file_id` | Immutable public olmayan dosya anahtari |
| `domain` | `personnel` gibi storage/domain sinifi |
| `entity_type` | `personnel` gibi is varligi tipi |
| `entity_id` | Ilgili personel veya is varligi anahtari |
| `file_type` | `photo`, `cv`, `official_document` gibi anlam |
| `classification` | `internal`, `confidential`, `restricted`, `official` gibi hassasiyet |
| `relative_path` | `personnel/a8/f3/<file_id>.<ext>` gibi PII icermeyen path |
| `content_type` | MIME tipi |
| `extension` | Normalize edilmis uzanti |
| `original_file_name` | Gerekirse DB'de tutulan orijinal ad |
| `size_bytes` | Dosya boyutu |
| `sha256` | Icerik hash'i |
| `owner_app` | Dosyayi olusturan veya yoneten uygulama kodu |
| `status` | `active`, `revoked`, `archived`, `deleted` |
| `retention_policy` | Saklama politikasi kodu |

Uygulamalarin karismamasini saglayan kurallar:

- Her uygulama `app_code` ile tanimlanir.
- Fiziksel path uygulama adi icermez; app ownership `files.references` ve `files.app_policies` ile takip edilir.
- Uygulamalar local path veya NFS mount detayini bilmez; `file_id` ile konusur.
- Dosya create/read/archive kararlarinda `app_code`, `domain`, `file_type`, `classification`, `entity_type` ve data-scope birlikte kontrol edilir.
- Ikinci uygulama onboard edilmeden once dosya operasyonlari File-Service API veya ortak platform servis katmani arkasina alinmis olmalidir.

## Sahiplik ve Izin Modeli

| Alan | Sahiplik | Izin | Not |
| --- | --- | --- | --- |
| `/srv/files/export` | `root:files-nfs-ro` | `0750` | NFS read-only export kokudur |
| Export alt dizinleri | `root:files-nfs-ro` | `0750` | File-Service API runtime'i NFS uzerinden okur |
| Export dosyalari | `root:files-nfs-ro` | `0640` | Dosyalar PII kabul edilir |
| `/srv/files/staging` | `root:files-publishers` | `0750` | Migration/publish hazirlik alani |
| `/srv/files/manifests` | `root:files-publishers` | `0750` | Secretsiz tasima manifestleri |
| `/srv/files/restore-tests` | `root:files-publishers` | `0750` | Restore test ciktilari, PII yazilmaz |

NFS export icin onerilen yaklasim:

- `all_squash` ile File-Service API runtime host'undan gelen NFS islemleri server tarafinda `files-nfs-ro` kimligine map edilir.
- Export `ro` kalir; runtime host uzerinden yazma, silme veya rename beklenmez.
- Dosya publish, migrate veya duzeltme islemleri Files-01 uzerinde kontrollu operasyon kullanicisiyle yapilir.
- NFSv4.2 disinda NFSv3/NFSv2 acilmaz; rpcbind bagimliligi olusturulmaz.

Kerberos/NFS sec=krb5p ileride degerlendirilebilir; ilk kurulumda private network, File-Service runtime allowlist ve read-only export yeterli kabul edilir.

## API Erisim Sozlesmesi

V1 backend proxy akisi:

```text
Client -> Gateway-01 -> YonetimAPI -> File-Service API -> DB-01 File Catalog -> Files-01 storage
```

End-state cok uygulamali akista beklenen sinir:

```text
Client -> Gateway-01 -> Uygulama -> File-Service API -> DB-01 File Catalog -> Files-01 storage
```

Minimum API kontrolleri:

- Token gecerliligi.
- Servis kimligi ve `app_code` eslesmesi.
- Dosya metadata kaydi ve aktiflik durumu.
- App policy kontrolu.
- Domain uygulamasinin daha once yaptigi permission/data-scope kararinin servis istegiyle uyumu.
- Dosya tipi allowlist kontrolu.
- Diskte dosya varligi ve hash/size metadata uyumu.

API dosya path'ini, Files-01 hostname bilgisini veya NFS mount detayini response icinde donmez.

## Migration Manifesti

Legacy dosya tasimasi icin secretsiz manifest sablonu:

| Kolon | Aciklama |
| --- | --- |
| `file_id` | Yeni immutable dosya anahtari |
| `entity_type` | `personnel` gibi genel varlik tipi |
| `file_type` | `photo` veya `cv` |
| `target_relative_path` | `personnel/<shard1>/<shard2>/<file_id>.<ext>` gibi PII icermeyen path |
| `extension` | Normalize edilmis uzanti |
| `size_bytes` | Dosya boyutu |
| `sha256` | Icerik hash'i |
| `source_alias` | Legacy kaynak alias'i, gercek path degil |
| `migration_status` | `pending`, `copied`, `verified`, `failed`, `rolled_back` |
| `checked_at` | Test tarihi |
| `notes` | Secretsiz sonuc ozeti |

Legacy kaynaklar tasima tamamlanana ve rollback penceresi kapanana kadar silinmez.

## Backup ve Kapasite

- Files-01 diski 300 GB baslangic kapasitesiyle izlenir.
- `export`, `manifests` ve gerekli migration loglari backup kapsamina girer.
- `staging` gecici alan kabul edilir; uzun sureli saklama gerektiren dosya once `export` veya kontrollu arsive alinmalidir.
- Backup dosyalari repository'ye eklenmez.
- Restore testi, gercek PII yazmadan ornek/probe dosya setiyle veya kontrollu redakte edilmis sonucla kanitlanir.
- Kapasite alarm esikleri #8 monitoring calismasinda netlestirilir; ilk hedef `70%`, `85%`, `95%` uyarilaridir.

## Dogrulama Kapilari

| Kapi | Kaynak | Hedef | Beklenen sonuc |
| --- | --- | --- | --- |
| NFS port | File-Service runtime host'u | Files-01 TCP/2049 | Port reachable |
| Mount | File-Service runtime host'u | Files-01 export | Read-only mount basarili |
| Read probe | File-Service runtime kullanicisi | `/mnt/platform-files` | Probe dosya okunur |
| Write denial | File-Service runtime kullanicisi | `/mnt/platform-files` | Yazma/silme reddedilir |
| API scope | Client | `GET /files/{fileId}` | Yetkili kullanici dosya alir |
| API scope miss | Client | `GET /files/{fileId}` | Yetkisiz/scope disi talep dosya varligini sizdirmez |
| Backup restore | Files-01 | Restore test alani | Ornek dosya restore edilir ve hash eslesir |

Nihai VLAN/IP sonrasinda File-Service runtime host'u -> Files-01 TCP/2049 allowlist'i #3 kapsaminda dogrulanir.
