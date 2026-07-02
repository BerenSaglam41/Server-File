# File-Service Çalışması — Düzeltme ve Yönlendirme Raporu

Bu rapor, stajyer çalışmasında (`PROJE_ISTER_TAKIP.md` envanteri) platformun karara bağlanmış mimarisiyle **çelişen** noktaları ve her biri için **doğru yaklaşımın ne olduğunu** özetler. Amaç suçlamak değil; nereye bakılacağını netleştirmek.

Yaptığın işin kapsamı ve titizliği güçlü — özellikle genel güvenlik hardening'i, YonetimAPI RBAC'ı ve katalog tarafı doğru yönde. Aşağıdaki maddeler ağırlıkla **File-Service teslim yönergesinde (`docs/file-service/file-service-intern-brief.md`) verilen ana kararlardan** sapan yerlerle ilgili.

## Önce okunacak referans dokümanlar

Aşağıdaki düzeltmeleri yapmadan önce bu dört doküman baştan sona okunmalı; hepsi çelişkileri açık cevaplıyor:

- `docs/file-service/file-service-intern-brief.md` — ana hedef ve "yapılmayacaklar" listesi
- `docs/file-service/file-service-api-contract.md` — endpoint, ticket, X-Accel, token modeli
- `docs/file-service/file-catalog-model.md` — `files.*` tablo şeması ve app isolation
- `docs/file-service/files01-nfs-model.md` — dizin/zone/shard ve mount modeli

---

## Kritik düzeltmeler (mimari kararla çelişiyor)

### 1. Byte delivery File-Service'ten geçmemeli

**Şu an:** Range/206, ETag/304, Content-Disposition ve dosya içeriği doğrudan FileServiceApi üzerinden akıyor.

**Doğrusu:** File-Service bir **control plane**'dir; dosya byte'ı **taşımaz**. Byte'ı **Gateway Nginx**, `X-Accel-Redirect` header'ı ile Files-01'in **read-only** mount'undan servis eder. File-Service sadece ticket'ı doğrulayıp `X-Accel-Redirect: /_protected/<backend-route-key>/<shard1>/<shard2>/<file_id>.<ext>` döner; Nginx `internal` location'ı bu header'ı tüketir.

> Kaynak: `file-service-api-contract.md` → "Private Download Akışı / Ticket Tüketimi ve Internal Redirect" ve "Fiziksel Erişim Sınırı". Yönergede net: *"File-Service download endpoint'inden dosya byte'ı stream etmeyin; byte delivery Gateway Nginx'e aittir."*

### 2. Opaque ticket + lease modeli eklenmeli

**Şu an:** Ticket sistemi hiç yok; indirme doğrudan dosya ID'siyle yapılıyor.

**Doğrusu:** Private indirmenin tüm güvenlik modeli **tek dosyaya bağlı, kısa ömürlü, en az 256-bit entropy'li opaque ticket** üzerine kurulu:

- Uygulama API business kontrolünden sonra `POST /internal/download-tickets` çağırır, client'a yalnız `https://<alias>/files/download/<ticket>` döner.
- Gateway ticket'ı `GET /internal/download-tickets/{ticket}/consume` ile doğrulatır.
- Store'da açık ticket değil **hash'i** tutulur; ticket **fiziksel path içermez**, tüketimde path yeniden çözülür.
- PDF/video için **atomik başlatılan, üst sınırlı kısa lease** (sonsuz sliding expiration yok); gerçek single-use yalnız Range/retry gerektirmeyen durumlarda.

> Kaynak: `file-service-api-contract.md` → "Ticket Sözleşmesi" ve `file-catalog-model.md` → "Ticket Store".

### 3. Servis auth: mTLS değil, audience + scope kontrolü

**Şu an:** Tüm `FileServiceApi ↔ {YonetimApi, FlotaApi}` halkasında mTLS + CN allowlist kullanılıyor.

**Doğrusu:** Uygulama → File-Service kimliği **Keycloak service token** (`client_credentials`) ile taşınır ve File-Service şunları doğrular:

- `aud=file-service`
- `azp/client_id` eşleşmesi; `azp` ile `client_id` farklıysa **reject**
- `client_id -> app_code` eşlemesi **`files.app_clients`** (veya server-side eşdeğer config) üzerinden — header'daki `app_code`'a asla güvenilmez
- teknik scope (`files.ticket.create` vb.) yoksa `403`, token/audience geçersizse `401`

mTLS bizim modelde **yalnızca Gateway → File-Service ticket-consume kimliği** için opsiyonel (`GatewayCaller` + `files.ticket.consume`). App halkasına mTLS koymak fazladan ve kararla uyuşmuyor. İki policy net ayrılmalı: **`AppCaller`** (resolve/ticket/archive) ve **`GatewayCaller`** (yalnız consume).

> Kaynak: `file-catalog-model.md` → "Auth ve App Isolation", `authentication-boundary.md` → "Backend Service Account Sınırı".

### 4. Endpoint sözleşmesi yönergeye hizalanmalı

**Doğrusu:** Minimum endpoint seti:

- `GET /internal/files/resolve`
- `POST /internal/download-tickets`
- `GET /internal/download-tickets/{ticket}/consume` (yalnız `GatewayCaller`)
- `POST /internal/references/{referenceId}/archive`
- `POST /internal/upload-sessions`

`GET /internal/files/{fileId}` global lookup olarak **tasarlanmaz**; sadece aktif reference + policy kontrolüyle açık ihtiyaç varsa eklenir.

> Kaynak: `file-service-intern-brief.md` → "Prototype API", `file-service-api-contract.md` → "Endpoint Taslağı".

### 5. Storage key: iş domaini değil güvenlik zone'u + doğru shard

**Şu an:** Path `{domain}/{ilk2hex}/{sonraki2hex}/{guid}.{ext}` (domain = personnel/fleet), shard **içeriğin SHA256'sından**.

**Doğrusu:**

- Üst seviye **güvenlik zone'u**: `private/` ve `public/` — fiziksel olarak ayrı. personnel/fleet/cv/photo gibi iş kavramları **path'e değil metadata/reference'a** yazılır.
- Shard, **`SHA-256(canonical file_id)`** lowercase hex'in ilk 2 + sonraki 2 karakteri; içeriğin hash'inden değil.
- `storage_key_version=1` katalogda tutulur; algoritma değişirse eski path yeniden hesaplanmaz.
- `relative_path` publish sonrası immutable; extension server-side magic-byte/MIME allowlist'inden üretilir.

> Kaynak: `files01-nfs-model.md` → "Fiziksel Dosya Adlandırma", `file-catalog-model.md` → "Fiziksel Storage Key".

### 6. Katalog şeması ve lifecycle tamamlanmalı

**Eksik/farklı olanlar:**

- `files.app_clients` tablosu (`client_id -> app_code`) — auth zincirinin kaynağı.
- `files.objects` içinde `storage_backend_id`, `storage_zone`, `storage_key_version`, `scan_provider`, `scan_result`.
- **Storage backend registry** (`storage_backend_id -> supported_zones / publish_root / nginx_internal_route_key`). `fs01` gibi route key path'in parçası değil, registry'den gelir.
- Zengin status lifecycle: `uploading, pending_scan, publishing, ready, published, revoked, archived, deleted` (şu an sadece active/archived var).
- **Fail-closed virus scan**: validation/scan başarısız veya erişilemezken publish **fail-closed** olmalı. Upload akışı: local quarantine → scan → Files-01'e `.partial` → size/hash → aynı fs içinde atomic rename → `ready/published`.
- `classification` değerleri: `internal, confidential, restricted, official`.

> Kaynak: `file-catalog-model.md` → "Önerilen Şema", `files01-nfs-model.md` → "Upload ve Atomik Publish".

---

## Kapsam sapmaları (platformda karşılığı yok)

### 7. FlotaApi — ayrı bir servis olarak yok

Platformda standalone "filo API" yok. **"Filo" bir veri domainidir**; Kentkart/Boys legacy kaynaklarından **ExternalHub-01 üzerindeki read-only view'larla** gelir ve **YonetimAPI** tüketir. `vehicle_id` claim'li ayrı bir servis mimaride tanımlı değil. Filo ihtiyacı, YonetimAPI + ExternalHub view sözleşmeleri üzerinden modellenmeli.

> Kaynak: `planning/issues/17-yonetimapi-external-hub-view-sozlesmeleri.md`, `docs/external-hub/migration-roadmap.md`.

### 8. OpsApi / Ops Console — spec'te yok

Dokümanlarda OpsApi, `ops.*` şeması veya `ops.{read,execute,admin}` rolleri tanımlı değil. Monitoring/logging bir plan olarak var (bkz. `planning/issues/08-loglama-monitoring-yaklasimi.md`) ama bu şekilde bir servis/konsol olarak kararlaştırılmadı. Bu tarafı eklemeden önce ilgili issue'daki yaklaşımla hizalanmalı.

### 9. Sunucu topolojisi ve eksik eksen

Platform **5 sunucu**: Gateway-01, **APP01 (YonetimAPI + File-Service birlikte)**, DB-01, **ExternalHub-01**, Files-01.

- Her şeyi tek host + tek `platformdb`'de toplamak yerine bu ayrım (özellikle File-Service'in APP01'de ayrı process/user olması) korunmalı.
- **ExternalHub-01 / FDW-ETL / legacy migration ekseni** ve **LDAP/LDAPS federasyonu** platformun ana temaları — çalışmada hiç yer almıyor. 28 kullanıcının realm import'tan gelmesi yerine Keycloak'ın kurumsal dizinden federe etmesi hedef.

> Kaynak: `docs/servers/server-inventory.md`, `docs/keycloak/authentication-boundary.md`.

### 10. Login akışı: ROPC yerine Authorization Code + PKCE

Browser tabanlı akışta tercih **Authorization Code + PKCE**'dir; password grant (ROPC) yalnız kontrollü test/servis senaryosu. `frontend-test` + ROPC üzerine kurulu gerçek login akışı buna göre revize edilmeli.

> Kaynak: `authentication-boundary.md` → "Smoke Test Örneği" sonundaki not.

---

## Zaten doğru olan yerler (korunmalı)

- Gateway tek giriş + TLS + güvenlik header'ları; Files-01'in dışa kapalı olması, NFS 2049 kısıtı.
- Keycloak merkezî auth, HttpOnly cookie/BFF, token'ın JWKS ile doğrulanması, yetkinin uygulama katmanında (all→team→self, DB tabanlı) verilmesi.
- Merkezî `files.*` kataloğu fikri, path'te app/PII olmaması, API cevabında `relative_path` dönmemesi.
- Davranış kuralları: magic-byte, boyut limiti, 415/413/409/503, no-leak 404, soft-archive, ETag/304, Range/206.
- Backup/restore + sha256 reconciliation, katman bazlı audit.

## Önerilen öncelik sırası

1. Byte delivery'yi Gateway `X-Accel-Redirect`'e taşı (madde 1).
2. Ticket + lease modelini kur (madde 2).
3. Servis auth'u audience+scope+`files.app_clients`'e çevir, `AppCaller`/`GatewayCaller` ayrımını yap (madde 3-4).
4. Storage zone/shard ve katalog şemasını düzelt (madde 5-6).
5. Kapsamı sadeleştir: FlotaApi/OpsApi'yi platform kararlarıyla yeniden değerlendir (madde 7-9).

Her karar değişikliğinde önce ilgili doküman güncellenir, sonra kod/prototype güncellenir (yönergedeki kural).
