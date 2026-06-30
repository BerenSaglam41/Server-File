# Merkezi File Catalog Modeli

Bu dokuman Files-01 uzerindeki binary dosyalarin metadata, ownership, app isolation, permission ve audit bilgisinin nasil tutulacagini tanimlar.

Files-01 storage-only kalir. Bu katalog DB-01 uzerinde platform seviyesinde `files.*` semasi veya ayri `file_catalog` veritabani olarak konumlanir. YonetimAPI ilk consumer uygulamadir; dosya katalog modelinin sahibi degildir.

## Temel Karar

```text
Files-01 = binary storage
DB-01 / files.* = merkezi dosya katalogu
File-Service API = cok uygulamali erisim siniri
Uygulamalar = file_id ile konusan consumer'lar
```

Ilk fazda File-Service API fiziksel olarak ayri servis olmayabilir. Ancak tablo isimleri, app ownership ve API sozlesmesi merkezi file-service siniri gibi tasarlanir. Ikinci uygulama dosya storage tuketmeye baslamadan once ortak File-Service API veya esdeger platform servis katmani devreye alinmalidir.

API sozlesmesi [file-service-api-contract.md](file-service-api-contract.md) dosyasinda takip edilir.

## Neden Merkezi Katalog

- Her uygulama kendi dosya metadata tablosunu kurarsa ownership, extension, retention ve audit kararlari dagilir.
- Fiziksel path uygulama adi icerirse uygulama adlari storage sozlesmesine karisir.
- Dosya hassasiyeti ve yetkisi zamanla degisebilir; bunlar path ile degil metadata ile yonetilmelidir.
- Ortak File Catalog, dosya create/read/archive davranisini app bazli policy ile sinirlar.

## Onerilen Sema

### `files.objects`

Fiziksel binary objeyi tanimlar.

| Alan | Aciklama |
| --- | --- |
| `file_id` | Immutable UUID veya esdeger stable key |
| `domain` | `personnel`, `fleet`, `procurement` gibi storage/domain sinifi |
| `relative_path` | `personnel/a8/f3/<file_id>.<ext>` gibi PII icermeyen path |
| `content_type` | MIME tipi |
| `extension` | Normalize edilmis uzanti |
| `size_bytes` | Dosya boyutu |
| `sha256` | Icerik hash'i |
| `classification` | `internal`, `confidential`, `restricted`, `official` |
| `retention_policy` | Saklama politikasi kodu |
| `status` | `active`, `revoked`, `archived`, `deleted` |
| `created_by_app` | Dosyayi olusturan uygulama kodu |
| `created_by_user` | Uygulama kullanici/actor referansi |
| `created_at` | Olusturma zamani |

### `files.references`

Dosyanin hangi uygulama ve is varligi ile iliskili oldugunu tutar.

| Alan | Aciklama |
| --- | --- |
| `file_id` | `files.objects.file_id` |
| `app_code` | `yonetimapi` gibi uygulama kodu |
| `entity_type` | `personnel`, `vehicle`, `case` gibi varlik tipi |
| `entity_id` | Ilgili varlik anahtari |
| `relation_type` | `photo`, `cv`, `attachment`, `report` gibi anlam |
| `is_primary` | Birincil dosya mi |
| `status` | Referans durumu |

### `files.app_policies`

Uygulamalarin hangi domain ve dosya turlerinde ne yapabilecegini sinirlar.

| Alan | Aciklama |
| --- | --- |
| `app_code` | Uygulama kodu |
| `allowed_domains` | Izinli domain listesi |
| `allowed_file_types` | Izinli dosya turleri |
| `can_create` | Dosya olusturabilir mi |
| `can_read` | Dosya okuyabilir mi |
| `can_archive` | Dosya arsivleyebilir mi |
| `max_file_size_bytes` | Uygulama bazli boyut limiti |

### `files.audit_events`

Dosya islemleri icin secretsiz audit izi tutar.

| Alan | Aciklama |
| --- | --- |
| `file_id` | Ilgili dosya |
| `app_code` | Istegi yapan uygulama |
| `actor` | Kullanici veya servis actor'u |
| `action` | `create`, `read`, `archive`, `delete_attempt` |
| `result` | `success`, `denied`, `not_found`, `error` |
| `reason_code` | Secretsiz sonuc sinifi |
| `created_at` | Olay zamani |

## App Isolation Kurallari

- Her uygulama `app_code` ile kayit altina alinir.
- Fiziksel path uygulama adi icermez.
- Bir uygulama yalniz `files.app_policies` ile izin verilen domain ve file type uzerinde islem yapar.
- Entity ownership `files.references` ile tutulur; ayni binary obje birden fazla uygulama/entity tarafindan referanslanabilir.
- Uygulamalar local path veya NFS mount detayini bilmez.
- API response Files-01 hostname, mount path veya relative path donmez; yalniz file metadata ve stream doner.

## Erisim Akisi

V1 backend proxy akisi:

```text
Client -> Gateway-01 -> YonetimAPI -> File-Service API -> DB-01 files.* -> Files-01 storage
```

End-state cok uygulamali akis:

```text
Client -> Gateway-01 -> Uygulama -> File-Service API -> DB-01 files.* -> Files-01 storage
```

## Onboarding Kapisi

Ikinci uygulama dosya storage tuketmeden once asagidakiler netlesmelidir:

- `app_code` ve izinli domain/file type listesi.
- Create/read/archive policy.
- Entity reference modeli.
- Audit event sorumlulugu.
- API endpoint sozlesmesi.
- Quota ve max file size kararlari.
- Test senaryolari: create, read, denied, scope miss, archived file, missing binary.
