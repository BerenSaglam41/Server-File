# File-Service API Sozlesmesi

Bu dokuman merkezi File-Service API'nin uygulamalar, DB-01 uzerindeki `files.*` katalogu ve Files-01 binary storage arasinda nasil konumlanacagini tanimlar.

Files-01 public servis degildir. File-Service API, dosya metadata, app isolation, policy, audit ve stream kararlarinin merkezi servis siniridir.

## Temel Karar

```text
Client -> Gateway-01 -> Uygulama API -> File-Service API -> DB-01 files.* -> Files-01 storage
```

V1 karar:

- Frontend veya mobil istemci File-Service API'ye dogrudan gitmez.
- Uygulama API'leri Files-01'e veya `files.*` tablolarina dogrudan gitmez.
- Uygulama API'leri dosya icin File-Service API'yi servis-ici auth ile cagirir.
- File-Service API, Files-01 uzerinden binary okur/yazar ve `files.*` katalogunu gunceller.

## Yetki Sinirlari

| Katman | Sorumluluk |
| --- | --- |
| Keycloak | Kullanici kimligi ve token uretimi |
| Uygulama API | Business permission, entity data-scope, domain kararlar |
| File-Service API | App policy, dosya status/classification, katalog, audit, stream |
| Files-01 | Binary storage |

Uygulama API, kullanicinin ilgili is varligini gorup goremeyecegini bilir. File-Service API, uygulamanin ilgili dosya domain/type/action icin yetkili olup olmadigini ve dosyanin katalog durumunu bilir.

Bu nedenle File-Service API diger uygulamalara tamamen guvenmez:

- Servis kimligini dogrular.
- `app_code` degerini token/client identity ile eslestirir.
- `files.app_policies` ile domain, file type ve action kontrolu yapar.
- `files.objects.status`, `classification`, `retention_policy` ve `sha256` bilgisini kontrol eder.
- `files.references` ile dosya-entity iliskisini dogrular.
- Her create/read/archive/denied sonucunu `files.audit_events` icine yazar.

File-Service API domain data-scope'u tek basina cozmez. Ornek: "bu kullanici bu personeli gorebilir mi?" karari YonetimAPI'nin sorumlulugundadir. YonetimAPI bu karari verdikten sonra File-Service API'ye yetkili servis istegi yapar.

## Auth Modeli

V1 servis-ici auth secenekleri:

| Secenek | Kullanim |
| --- | --- |
| OAuth2 client credentials | Uygulama API -> File-Service API cagrilarinda varsayilan |
| mTLS | Ileride servis kimligini guclendirmek icin |
| Internal network allowlist | Tek basina yeterli degil; ek guvenlik katmani |

Token/claim beklentisi:

| Claim | Aciklama |
| --- | --- |
| `client_id` | Cagiran servis kimligi |
| `app_code` | `yonetimapi`, `filoapi` gibi uygulama kodu |
| `scope` | `files.read`, `files.create`, `files.archive` gibi servis yetkileri |

Header beklentisi:

| Header | Aciklama |
| --- | --- |
| `X-Correlation-Id` | Uygulamalar arasi izleme |
| `X-Actor-User-Id` | Uygulama tarafinda dogrulanmis kullanici/actor referansi |
| `X-Actor-Display` | Opsiyonel, secretsiz audit gosterimi |

File-Service API `app_code` degerini yalniz header'dan kabul etmez; servis token'i ile eslestirir.

## V1 Backend Proxy Akisi

```text
Client
  -> Gateway-01
  -> YonetimAPI
      1. JWT dogrular.
      2. Permission ve data-scope kontrol eder.
      3. File-Service API'yi servis token'i ile cagirir.
  -> File-Service API
      4. app policy, object/reference/status kontrol eder.
      5. Files-01'den stream eder.
      6. files.audit_events yazar.
  -> YonetimAPI
  -> Client
```

Bu modelde byte iki backend katmanindan gecer. Ilk faz icin sadelik ve guvenlik avantajlidir. Performans baskisi olusursa V2 download ticket modeli degerlendirilir.

## V2 Download Ticket Opsiyonu

Frontend'in File-Service API'ye dogrudan gitmesi gerekiyorsa raw Keycloak token ile genel dosya erisimi acilmaz.

Onerilen model:

```text
Client -> Uygulama API: dosya istegi
Uygulama API -> File-Service API: download ticket olustur
Client -> File-Service API: kisa omurlu ticket ile stream
```

Ticket kurallari:

- Kisa omurlu olur.
- `file_id`, `app_code`, `actor`, `action`, `expires_at` ve correlation bilgisini icerir.
- Tek kullanim veya sinirli kullanim olabilir.
- Ticket dosya varligini scope disi kullaniciya sizdirmaz.
- File-Service API yine object status ve audit kontrolu yapar.

V2, v1'in yerine ancak performans veya buyuk dosya ihtiyaci kanitlanirsa gecmelidir.

## Endpoint Taslagi

### Resolve

Uygulama domain entity uzerinden dosya metadata'si bulur.

```http
GET /internal/files/resolve?domain=personnel&entityType=personnel&entityId=<id>&relationType=photo
Authorization: Bearer <service-token>
X-Correlation-Id: <id>
X-Actor-User-Id: <actor>
```

Basarili yanit:

```json
{
  "fileId": "<uuid>",
  "domain": "personnel",
  "relationType": "photo",
  "contentType": "image/jpeg",
  "extension": "jpg",
  "sizeBytes": 12345,
  "sha256": "<hash>",
  "classification": "internal",
  "status": "active",
  "etag": "\"sha256:<hash>\""
}
```

### Stream

```http
GET /internal/files/{fileId}/content
Authorization: Bearer <service-token>
Range: bytes=0-
If-None-Match: "sha256:<hash>"
X-Correlation-Id: <id>
X-Actor-User-Id: <actor>
```

Beklenen:

- `200 OK` veya `206 Partial Content`
- `304 Not Modified`
- `ETag` sha256 tabanli olur.
- `Accept-Ranges: bytes` desteklenir.
- `Content-Disposition` file type policy'ye gore belirlenir.

### Metadata

```http
GET /internal/files/{fileId}
Authorization: Bearer <service-token>
```

Path, Files-01 hostname veya mount detayi donmez. Relative path yalniz servis ici ihtiyac varsa doner; client-facing API response'una tasinmaz.

### Create

```http
POST /internal/files
Authorization: Bearer <service-token>
Content-Type: multipart/form-data
```

Minimum metadata:

```json
{
  "domain": "personnel",
  "entityType": "personnel",
  "entityId": "<id>",
  "relationType": "cv",
  "classification": "restricted",
  "originalFileName": "redacted.pdf"
}
```

Create akisi:

1. App policy create yetkisi kontrol edilir.
2. Extension, content type, magic-byte, max size kontrol edilir.
3. `file_id` uretilir.
4. Shard path uretilir.
5. Binary Files-01 staging/export akisiyle yazilir.
6. `files.objects`, `files.references`, `files.audit_events` kayitlari olusur.

### Archive

```http
POST /internal/files/{fileId}/archive
Authorization: Bearer <service-token>
```

Hard delete ilk fazda yoktur. Dosya once `archived` veya `revoked` duruma alinir; fiziksel temizlik retention politikasina baglanir.

## Hata Sozlesmesi

| Durum | HTTP | Not |
| --- | --- | --- |
| App policy denied | `403` | Uygulama bu domain/action icin yetkisiz |
| Scope miss app tarafinda | `404` | Uygulama client'a dosya varligini sizdirmez |
| File not found | `404` | Katalog veya binary yok |
| Archived/revoked | `404` veya `410` | Client-facing tercih app sozlesmesine gore |
| Unsupported media type | `415` | Extension/content-type/magic-byte uyumsuz |
| File too large | `413` | Policy limit asildi |
| Hash mismatch | `409` | Binary ve metadata tutarsiz |
| Storage unavailable | `503` | Files-01/NFS erisilemiyor |

Problem response secretsiz olmalidir; path, host, mount veya credential bilgisi donmez.

## Audit

Iki katmanli audit kasitlidir:

- File-Service API: merkezi `files.audit_events` icine teknik dosya olayi yazar.
- Uygulama API: kendi domain audit'ini yazar. Ornek: `PersonnelCvDownloaded`.

Bu sayede platform seviyesinde "hangi app hangi dosyayi istedi" ve domain seviyesinde "hangi kullanici hangi is anlaminda eristi" ayrilir.

## V1 Kabul Kriterleri

- Uygulamalar Files-01'e dogrudan baglanmaz.
- Uygulamalar `files.*` tablolarina dogrudan yazmaz.
- File-Service API servis token'i olmadan cevap vermez.
- App policy izin vermedigi domain/action icin istek `403` olur.
- Scope disi dosyalar client'a varlik sizdirmeden `404` olur.
- Stream endpoint `ETag`, `Range`, `Content-Type` ve `Content-Disposition` davranisini destekler.
- `files.audit_events` her read/create/archive/denied sonucu icin kayit uretir.
