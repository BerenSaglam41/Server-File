# "Stajyer File-Service Düzeltme Raporu" Doğrulama Sonucu

## Yöntem

`stajyer-file-service-duzeltme-raporu.md` dosyasındaki her "Doğrusu" iddiası, gösterdiği kaynak dosyada (`PROJE/` klasöründeki 5 gerçek dosya) gerçekten yazıp yazmadığı kontrol edilerek doğrulandı:

1. `PROJE/` klasöründeki 5 dosya (`file-service-api-contract.md`, `file-catalog-model.md`, `files01-nfs-model.md`, `file-service-intern-brief.md`, `20-files01-nfs-personel-dosya-plani.md`) baştan sona tam olarak okundu.
2. Dosyaların değişmediği sistem seviyesinde doğrulandı (dosya hash karşılaştırması — "unchanged since last read").
3. Kullanıcı dosyaları yeniden yükledi; yeni yüklenen dosyalar eskileriyle **byte-for-byte aynı** çıktı (`git diff` boş).
4. Raporun kullandığı 22 spesifik terim (`X-Accel`, `Ticket Sözleşmesi`, `Ticket Store`, `app_clients`, `ExternalHub`, `PKCE`, vb.) 5 dosyanın tamamında `grep` ile arandı — 21'i **sıfır sonuç**, 1'i (`APP01`) raporun iddiasının **tam tersini** kanıtladı.
5. Raporun referans verdiği dosya yolları (`docs/file-service/...`, `authentication-boundary.md`, `planning/issues/...`) projede aranıp **hiçbirinin var olmadığı** doğrulandı.

**Sonuç: Aşağıdaki 8 madde, raporun "Doğrusu" dediği şeyin bizim gerçek `PROJE/` dosyalarımızda ya hiç yazmadığını ya da yazılanın tam tersi olduğunu gösteriyor.**

---

## Madde 1 — Byte Delivery (X-Accel-Redirect)

**Rapor "Doğrusu" diyor:**
> "File-Service bir **control plane**'dir; dosya byte'ı **taşımaz**. Byte'ı **Gateway Nginx**, `X-Accel-Redirect` header'ı ile Files-01'in **read-only** mount'undan servis eder."

**Rapor kaynak gösteriyor:**
> "Kaynak: `file-service-api-contract.md` → 'Private Download Akışı / Ticket Tüketimi ve Internal Redirect' ve 'Fiziksel Erişim Sınırı'."

**Gerçek dosyada (`PROJE/file-service-api-contract.md`, "V1 Backend Proxy Akışı" bölümü) ne yazıyor:**
> "Bu modelde **byte iki backend katmanindan gecer**. Ilk faz icin sadelik ve guvenlik avantajlidir. Performans baskisi olusursa V2 download ticket modeli degerlendirilir."

**Neden hatalı:** Gösterilen başlıklar ("Private Download Akışı", "Ticket Tüketimi ve Internal Redirect", "Fiziksel Erişim Sınırı") dosyada **hiç yok**. `X-Accel-Redirect` kelimesi projenin hiçbir dosyasında (`.md`, `.cs`, `.conf`) geçmiyor. Gerçek dosya, byte'ın File-Service'ten geçmesini **bilinçli V1 kararı** olarak tanımlıyor, tam tersini değil.

**GÜNCELLEME (2026-07-02) — Madde 1 (ticket bazlı indirmeler için) uygulandı, ama uydurma kaynağa dayanarak değil, kullanıcının kendi kararıyla:**

Kaynak alıntısı hâlâ hatalı (yukarıdaki tespit değişmedi), ama kullanıcı X-Accel-Redirect konseptinin
kendi başına mantıklı olduğuna karar verip uygulanmasını istedi. Tasarım netleştirilip onaylandı: Gateway'e
yeni bir NFS bağlantısı açılmadı (mevcut host `ro` mount'u bind-mount edildi), FileServiceApi'nin
ticket-consume endpoint'ine dar kapsamlı bir mTLS-only istisna eklendi (sadece CN=gateway, sadece bu
endpoint, diğer tüm `/internal/*` hâlâ mTLS+JWT istiyor).

- `POST /files/download/{ticket}` yerine hâlâ `POST .../download-ticket` ile ticket alınıyor, ama
  `downloadUrl` artık Gateway'in yeni `/files/download/{ticket}` yoluna işaret ediyor.
- FileServiceApi ticket-consume'da artık `X-Accel-Redirect` header'ı dönüyor, byte'ı kendisi okumuyor.
- Uçtan uca test edildi: içerik birebir aynı, tek-kullanım korunuyor, Range çalışıyor, `/protected-download/`
  dışarıdan erişilemiyor, yanlış CN'ler (TLS seviyesinde ve uygulama seviyesinde) reddediliyor, diğer
  `/internal/*` endpoint'ler hâlâ JWT istiyor (doğrulandı).
- Tam kanıt: `proof/x-accel-redirect-gateway.md`. Karar gerekçesi: `PROJECT_STATUS.md` → "X-Accel-Redirect
  ile Byte Delivery Gateway'e Taşındı" bölümü.

**Hâlâ yapılmayan kısım:** Ticket dışı, normal (`/api/personnel/{id}/cv/content` gibi) indirme
endpoint'leri hâlâ eski V1 backend proxy akışını kullanıyor — bu zaten `file-service-api-contract.md`'nin
kendi ayrımına göre beklenen (X-Accel/ticket modeli sadece "performans baskısı" senaryoları için V2
opsiyonu).

---

## Madde 2 — Opaque Ticket + Lease Modeli

**Rapor "Doğrusu" diyor:**
> "Private indirmenin tüm güvenlik modeli **tek dosyaya bağlı, kısa ömürlü, en az 256-bit entropy'li opaque ticket** üzerine kurulu... Store'da açık ticket değil **hash'i** tutulur."

**Rapor kaynak gösteriyor:**
> "Kaynak: `file-service-api-contract.md` → 'Ticket Sözleşmesi' ve `file-catalog-model.md` → 'Ticket Store'."

**Gerçek dosyada ne yazıyor:** `file-service-api-contract.md`'de "Ticket Sözleşmesi" diye bir başlık yok — sadece "V2 Download Ticket Opsiyonu" var, ve içeriği sadece şu: "Kisa omurlu olur... Tek kullanim veya sinirli kullanim olabilir." Hash saklama, 256-bit entropy gibi hiçbir detay yok. `file-catalog-model.md`'de "ticket" kelimesi **sıfır kez** geçiyor, "Ticket Store" başlığı yok.

**Neden hatalı:** İki kaynak başlığı da (`Ticket Sözleşmesi`, `Ticket Store`) uydurma; gerçek doküman bu kadar detaylı bir ticket modeli tanımlamıyor.

**GÜNCELLEME (2026-07-02) — Madde 2 artık uygulandı, ama uydurma kaynağa dayanarak değil, bilinçli bir mimari kararla:**

Kaynak alıntısı hâlâ hatalı (yukarıdaki tespit değişmedi), ama kullanıcı bu ticket **konseptinin** (opak,
256-bit, hash olarak saklanan, kısa ömürlü, tek kullanımlık) kendi başına mantıklı bir güvenlik deseni
olduğuna karar verdi ve uygulanmasını istedi. Bu, `PROJE/file-service-api-contract.md`'nin kendi (gerçek,
kısa) "V2 Download Ticket Opsiyonu" notu temel alınarak, **madde 1 ve 3'ün önerdiği gibi FileServiceApi'yi
dışa açmadan** (mevcut, doğrulanmış mTLS/internal-only sınırı korunarak) `YonetimApi` içinde uygulandı:

- `YonetimApi/Endpoints/DownloadTicketEndpoints.cs` — `POST .../download-ticket` (RBAC sonrası 256-bit
  opak ticket üretir, sadece hash'i DB'ye yazar) ve `GET /api/personnel/download/{ticket}` (cookie'siz,
  atomik tek-kullanım).
- 15 canlı senaryoyla test edildi, bu süreçte **2 gerçek bug bulunup düzeltildi** (eşzamanlı isteklerde
  Npgsql bağlantı hatası; başarısız denemelerin audit'lenmemesi).
- Tam kanıt: `proof/download-ticket-sistemi.md`. Karar gerekçesi ve kapsam: `PROJECT_STATUS.md` →
  "Opak, Tek Kullanımlık İndirme Ticket'ı" bölümü.

**Yapılmayan kısım:** Raporun madde 1'de istediği gibi Gateway'in `X-Accel-Redirect` ile ticket'ı tüketmesi
uygulanmadı — bu hâlâ gerçek dosyalarda doğrulanamayan, FileServiceApi'yi mTLS sınırının dışına çıkaracak
bir öneri olduğu için. Ticket, YonetimApi'nin kendi proxy'sinde tüketiliyor.

**GÜNCELLEME 2 (2026-07-02) — Madde 9/10/11 (endpoint konumu) uygulandı, madde 9/12/13 (Gateway/X-Accel/lease) o an için bilinçli olarak ertelendi (sonradan GÜNCELLEME 3 ve `proof/x-accel-redirect-gateway.md` ile tamamlandı):**

Kullanıcı raporun 9-13 maddelerini ("Gateway ticket consume: YOK", "POST /internal/download-tickets: YOK/
farklı", "GET /internal/download-tickets/{ticket}/consume: YOK", "Lease modeli: YOK", "X-Accel entegrasyonu:
YOK") işaret edip bunların da yapılmasını istedi. Önce mimari tradeoff netleştirildi
(`AskUserQuestion`): madde 9/13 (Gateway'in ticket'ı doğrudan tüketmesi + X-Accel-Redirect) Gateway
container'ına Files-01 NFS erişimi ve FileServiceApi'ye yeni bir ağ yolu açmayı gerektiriyor — kullanıcı
bunu **ayrı bir aşamaya bıraktı** ("x accel redirect'e sonra bakacağız"), önce sadece ticket'ın konumunun
taşınmasını istedi.

Yapılan: Ticket yaşam döngüsü `yonetim.download_tickets`'ten `files.download_tickets`'e, `YonetimApi`'den
`FileServiceApi`'ye taşındı; `POST /internal/download-tickets` ve `GET /internal/download-tickets/{ticket}/
consume` artık gerçekten var (madde 10/11'in hedeflediği konumda). Çağıran hâlâ YonetimApi — Gateway'in
doğrudan çağırması (madde 9) ve X-Accel-Redirect (madde 13) hâlâ yapılmadı, bilinçli olarak. Lease modeli
(madde 12) de aynı nedenle ayrı bırakıldı.

Bu taşıma sırasında **3. gerçek bug** bulundu: `files.audit_events.chk_action` CHECK constraint'i yeni
`ticket_create`/`ticket_consume` action değerlerini reddediyordu — düzeltildi. Detaylar:
`proof/download-ticket-sistemi.md` → "Taşıma Sonrası Testler", `PROJECT_STATUS.md` → "Ticket Yaşam
Döngüsü FileServiceApi'ye Taşındı".

**GÜNCELLEME 3 (2026-07-02) — Madde 12 (lease modeli) de artık uygulandı:**

X-Accel-Redirect (madde 13, ayrı bir turda) tamamlandıktan sonra kullanıcı lease modelinin de yapılmasını
istedi. Kaynak alıntısı yine uydurma ("Ticket Sözleşmesi" hâlâ yok), ama konsept (S3 presigned URL
benzeri süre+sayı sınırlı çoklu kullanım) mantıklı bulunup uygulandı: `TicketLifetime` (60sn, ilk kullanım
penceresi) + yeni `LeaseDuration` (30sn, ek kullanım penceresi) + yeni `MaxUsesPerTicket` (20, sert üst
sınır). 6 senaryo test edildi (hemen tekrar kullanım, çoklu Range, max-uses sınırı, lease süresi dolumu,
ilk kullanım süresi dolumu, 25 eşzamanlı istek) — hepsi geçti. Tam kanıt:
`proof/download-ticket-lease-model.md`.

Madde 9 (Gateway'in doğrudan tüketmesi) ve madde 13 (X-Accel) zaten daha önce ayrıca yapılmıştı (bkz.
`proof/x-accel-redirect-gateway.md`). Geriye sadece FlotaApi'ye taşıma (ticket sisteminin araç dosyalarına
genişletilmesi) kaldı — bu istenmedi, açık kapsam dışı.

---

## Madde 3 — Servis Auth: mTLS'in Kapsamı

**Rapor "Doğrusu" diyor:**
> "mTLS bizim modelde **yalnızca Gateway → File-Service ticket-consume kimliği** için opsiyonel... App halkasına mTLS koymak fazladan ve kararla uyuşmuyor."

**Rapor kaynak gösteriyor:**
> "Kaynak: `file-catalog-model.md` → 'Auth ve App Isolation', `authentication-boundary.md` → 'Backend Service Account Sınırı'."

**Gerçek dosyada (`file-service-api-contract.md`, "Auth Modeli" tablosu) ne yazıyor:**
> "mTLS | Ileride servis kimligini guclendirmek icin" — bu, **Uygulama API → File-Service API** çağrıları için genel bir seçenek olarak listelenmiş, "sadece Gateway'in ticket-consume'u için" diye bir kısıtlama yok.

**Neden hatalı:** `authentication-boundary.md` diye bir dosya **projede hiç yok**. Gerçek dosyada Gateway'in File-Service'e hiç konuştuğu bir akış tanımlı değil (Gateway sadece `/internal/*`'ı 404 döndürüyor); "Gateway ticket-consume kimliği" kavramı bizim dokümanımızda yok.

---

## Madde 4 — Endpoint Sözleşmesi

**Rapor "Doğrusu" diyor:**
> "`GET /internal/files/{fileId}` global lookup olarak **tasarlanmaz**; sadece aktif reference + policy kontrolüyle açık ihtiyaç varsa eklenir."

**Rapor kaynak gösteriyor:**
> "Kaynak: `file-service-intern-brief.md` → 'Prototype API', `file-service-api-contract.md` → 'Endpoint Taslağı'."

**Gerçek dosyada (`file-service-api-contract.md`, "Endpoint Taslağı" → "Metadata" bölümü) ne yazıyor:**
> ```
> ### Metadata
> GET /internal/files/{fileId}
> Authorization: Bearer <service-token>
> ```

**Neden hatalı:** Bu, raporun iddiasının **doğrudan tersi**. Gösterilen kaynağın kendisi (`file-service-api-contract.md` → "Endpoint Taslağı") bu endpoint'i açıkça tanımlıyor.

---

## Madde 5 — Storage Key ve Shard Mantığı

**Rapor "Doğrusu" diyor:**
> "Üst seviye **güvenlik zone'u**: `private/` ve `public/`... personnel/fleet/cv/photo gibi iş kavramları **path'e değil metadata/reference'a** yazılır. Shard, **`SHA-256(canonical file_id)`**'in ilk 2 + sonraki 2 karakteri."

**Rapor kaynak gösteriyor:**
> "Kaynak: `files01-nfs-model.md` → 'Fiziksel Dosya Adlandırma', `file-catalog-model.md` → 'Fiziksel Storage Key'."

**Gerçek dosyada (`files01-nfs-model.md`) ne yazıyor:**
> "`<shard1>` ve `<shard2>`, `file_id` degerinden turetilen iki seviyeli dagitim klasorudur. Ornek: `a8f3...` icin `a8/f3`." (SHA-256 değil, düz alt-string)

**Gerçek dosyada (`file-catalog-model.md`, "Önerilen Şema") ne yazıyor:**
> "`relative_path` | `personnel/a8/f3/<file_id>.<ext>` gibi PII icermeyen path" — **domain (personnel) açıkça path'in içinde.**

**Neden hatalı:** `private/`, `public/`, "güvenlik zone'u", `SHA-256(canonical file_id)` ifadelerinin hiçbiri bu dosyalarda yok. Gerçek dosyanın kendi örneği (`a8f3... için a8/f3`) tam olarak bizim kodumuzun yaptığı şey (düz alt-string), raporun iddia ettiği SHA-256 değil.

---

## Madde 6 — Katalog Şeması

**Rapor "Doğrusu" diyor:**
> "`files.app_clients` tablosu... `files.objects` içinde `storage_backend_id`, `storage_zone`, `storage_key_version`, `scan_provider`, `scan_result`... Zengin status lifecycle: `uploading, pending_scan, publishing, ready, published, revoked, archived, deleted`."

**Rapor kaynak gösteriyor:**
> "Kaynak: `file-catalog-model.md` → 'Önerilen Şema', `files01-nfs-model.md` → 'Upload ve Atomik Publish'."

**Gerçek dosyada (`file-catalog-model.md`, "Önerilen Şema") ne yazıyor:** Sadece 4 tablo tanımlı — `files.objects`, `files.references`, `files.app_policies`, `files.audit_events`. `status` alanı için tanımlı değerler: `active, revoked, archived, deleted` (4 değer, raporun iddia ettiği 8 değerli zengin lifecycle değil).

**Neden hatalı:** `files.app_clients`, `storage_backend_id`, `storage_zone`, `scan_provider` gibi hiçbir alan/tablo gerçek dosyada **hiç geçmiyor**. "Upload ve Atomik Publish" başlığı da `files01-nfs-model.md`'de yok.

---

## Madde 7 — FlotaApi'nin Varlığı

**Rapor "Doğrusu" diyor:**
> "Platformda standalone 'filo API' yok. 'Filo' bir veri domainidir... `vehicle_id` claim'li ayrı bir servis mimaride tanımlı değil."

**Rapor kaynak gösteriyor:**
> "Kaynak: `planning/issues/17-yonetimapi-external-hub-view-sozlesmeleri.md`, `docs/external-hub/migration-roadmap.md`."

**Gerçek dosyada (`file-catalog-model.md`, "Onboarding Kapısı") ne yazıyor:**
> "**İkinci uygulama** dosya storage tuketmeye baslamadan once ortak File-Service API veya esdeger platform servis katmani devreye alinmalidir."

**Neden hatalı:** `planning/issues/` diye bir klasör **projede hiç yok**. Gösterilen dosyalar mevcut değil. Gerçek dosyanın kendisi, ikinci bir tüketici uygulamanın (yani FlotaApi'nin) geleceğini öngörüp bunun için kural koyuyor — "yok, tanımlı değil" demiyor.

---

## Madde 9 — Sunucu Topolojisi

**Rapor "Doğrusu" diyor:**
> "Platform **5 sunucu**: Gateway-01, APP01 (YonetimAPI + File-Service birlikte), DB-01, **ExternalHub-01**, Files-01."

**Rapor kaynak gösteriyor:**
> "Kaynak: `docs/servers/server-inventory.md`, `docs/keycloak/authentication-boundary.md`."

**Gerçek durum:** "ExternalHub-01" kelimesi 5 dosyanın **hiçbirinde bir kez bile geçmiyor**. "APP01" sadece `files01-nfs-model.md`'de 1 kez, tek bir sunucu için geçici isim olarak geçiyor: *"Dosya tuketicisi | File-Service API runtime host'u, ilk fazda **APP01** uzerinde konumlanabilir"* — 5 sunuculu bir mimari tanımı yok, bizim gerçek 2 sunuculu modelimizle (api sunucu + files01) tutarlı.

**Neden hatalı:** `docs/servers/` ve `docs/keycloak/` klasörleri **projede hiç yok**. "5 sunucu" iddiası hiçbir kaynakta doğrulanamıyor.

---

## Genel Sonuç

Yukarıdaki 8 maddenin (1, 2, 3, 4, 5, 6, 7, 9) hepsinde rapor, ya **var olmayan bir dosya/başlığa** atıf yapıyor ya da gösterdiği gerçek dosyanın **söylediğinin tam tersini** iddia ediyor. Madde 8 (OpsApi) ve 10 (ROPC/PKCE) için gerçek dosyalarda doğrudan bir çelişki yok çünkü bu konular bizim 5 dosyamızda zaten hiç işlenmiyor — ama onlar için de gösterilen kaynaklar (`planning/issues/08-...`, `authentication-boundary.md`) yine mevcut değil.

**Bu rapora dayanarak kod/mimari değişikliği yapılması önerilmez** — böyle bir değişiklik projeyi kendi gerçek `PROJE/` planından uzaklaştırır, yakınlaştırmaz.
