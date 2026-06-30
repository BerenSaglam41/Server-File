# File-Service Stajyer Calisma Yonergesi

Bu yonerge File-Service API arastirma, tasarim ve ilk prototype calismasina katilacak stajyerler icindir.

Sunucu ve dosyalama tarafinda temel kararlar alinmistir: Files-01 storage-only kalir, dosyalar NFS uzerinden File-Service runtime tarafindan okunur, metadata DB-01 uzerindeki merkezi `files.*` katalogunda tutulur. Stajyer calismasi bu kararlari bozmayacak sekilde API ve dogrulama tarafina odaklanir.

## Okuma Listesi

Once asagidaki dokumanlar okunur:

- [file-service-api-contract.md](file-service-api-contract.md)
- [file-catalog-model.md](file-catalog-model.md)
- [files01-nfs-model.md](files01-nfs-model.md)
- [../runbooks/files01-nfs-setup.md](../runbooks/files01-nfs-setup.md)
- [../../planning/issues/20-files01-nfs-personel-dosya-plani.md](../../planning/issues/20-files01-nfs-personel-dosya-plani.md)

## Ana Hedef

File-Service API icin asagidaki sorulari cevaplayan kucuk ama ciddi bir tasarim/prototype paketi hazirlanir:

- File-Service API hangi endpoint'leri sunmali?
- Uygulama API'leri File-Service'e nasil authenticate olacak?
- `app_code` ile `client_id` nasil eslestirilecek?
- App policy, object status, reference ve audit kontrol sirasi nasil olmali?
- Stream endpoint `ETag`, `Range`, `Content-Type`, `Content-Disposition` davranislarini nasil desteklemeli?
- V1 backend proxy modeli ile V2 download ticket modeli arasindaki farklar nelerdir?

## Yapilacak Isler

### 1. API Sozlesmesi Arastirmasi

`docs/file-service/file-service-api-contract.md` uzerinden endpoint listesini incelersiniz.

Beklenen cikti:

- Endpoint listesi ve amaclari.
- Request/response ornekleri.
- Hata kodlari.
- `404` ile varlik sizdirmama kurali.
- `403` ve `404` ayriminin hangi katmanda yapilacagi.

### 2. Merkezi Katalog Tasarimi

`files.objects`, `files.references`, `files.app_policies`, `files.audit_events` tablolarini arastirirsiniz.

Beklenen cikti:

- PostgreSQL tablo taslagi.
- Primary key ve foreign key onerileri.
- Index onerileri.
- Status ve classification enum/deger listesi.
- Ornek seed: `app_code = yonetimapi`, `domain = personnel`, `file_type = photo/cv`.

### 3. Auth ve App Isolation

File-Service API kullanici token'ina dogrudan guvenmez. Uygulama API servis token'i ile gelir.

Beklenen cikti:

- OAuth2 client credentials akisi icin kisa aciklama.
- `client_id`, `app_code`, `scope` eslesme kurali.
- Header'lardan gelen `app_code` degerine neden tek basina guvenilmeyecegi.
- Deny-by-default policy ornekleri.

### 4. Prototype API

Kucuk bir skeleton/prototype hazirlanabilir. Dil ve framework repo standardina gore secilir; hedef .NET minimal API olabilir.

Minimum endpointler:

- `GET /internal/files/resolve`
- `GET /internal/files/{fileId}`
- `GET /internal/files/{fileId}/content`
- `POST /internal/files`
- `POST /internal/files/{fileId}/archive`

Prototype gercek secret, gercek IP veya gercek personel dosyasi icermemelidir.

### 5. Stream ve Dosya Guvenligi

Arastirilacak basliklar:

- Large file streaming.
- `Range` / `206 Partial Content`.
- `ETag` ve `If-None-Match`.
- MIME/content-type ve magic-byte kontrolu.
- Max file size.
- Path traversal engelleme.
- Hash mismatch durumunda ne yapilacagi.

### 6. Sunucu Tarafi Kalan Kontroller

VLAN degisimi sonrasi operasyon ekibiyle dogrulanacak konular listelenir:

- File-Service runtime host'u -> Files-01 TCP/2049 erisimi.
- NFS export yalniz File-Service runtime private alias/IP icin acik mi?
- Mount read-only mi?
- Runtime kullanicisi probe dosyasini okuyabiliyor mu?
- Runtime kullanicisi yazma/silme yapamiyor mu?
- NFS down durumunda File-Service health check ne donuyor?

## Yapilmayacaklar

- Frontend'i dogrudan Files-01 veya NFS'e baglamayin.
- Frontend'i V1'de dogrudan File-Service API'ye baglamayin.
- Uygulama API'lerinin `files.*` tablolarina dogrudan yazmasini tasarlamayin.
- Path'e ad, soyad, sicil, TCKN, app adi veya legacy dosya adi koymayin.
- Gercek IP, parola, token, connection string, personel dosyasi veya PII commit etmeyin.
- Hard delete'i ilk faz davranisi olarak tasarlamayin.

## Teslim Ciktilari

Stajyer calismasi sonunda asagidaki ciktilar beklenir:

- Kisa arastirma notu.
- Endpoint sozlesmesi uzerine yorumlar ve eksikler.
- PostgreSQL `files.*` tablo taslagi.
- Prototype API veya endpoint skeleton'i.
- Test senaryolari listesi.
- Acik sorular listesi.

## Test Senaryolari

Minimum test listesi:

- App policy izinliyken metadata resolve basarili.
- App policy izinsizken `403`.
- Scope miss uygulama tarafinda `404`.
- File not found `404`.
- Archived/revoked dosya icin karar.
- Unsupported media type `415`.
- File too large `413`.
- Storage unavailable `503`.
- Stream `200`, `206`, `304`.
- Audit event create/read/denied icin yaziliyor.

## Baslangic Gorev Dagilimi

| Gorev | Odak |
| --- | --- |
| API sozlesmesi | Endpoint, hata kodu, request/response |
| DB katalog | `files.*` tablo, index, seed |
| Auth/policy | client credentials, app policy, deny-by-default |
| Streaming | Range, ETag, content type, hash |
| Operasyon | NFS mount, health check, read-only probe |

Her kisi bulgularini secretsiz ve kisa kanitlarla paylasir. Karar degisikligi gerekiyorsa once dokumanda onerilir, sonra kod/prototype guncellenir.
