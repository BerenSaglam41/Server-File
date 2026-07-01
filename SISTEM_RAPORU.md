# Sistem Raporu — Personel/Filo Dosya Yönetim Platformu

> Bu doküman koddan çıkarılmıştır. `PROJE/` klasöründeki 5 planlama dokümanı (design-time düşünceler) baz alınmış, ama her iddia gerçek kaynak koddan (dosya:satır referanslarıyla) doğrulanmıştır. Planla gerçek kodun **farklılaştığı** yerler ayrıca "Plan vs Gerçek" bölümünde açıkça işaretlenmiştir — hiçbir yerde "böyle olması gerekiyordu" ile "böyle çalışıyor" birbirine karıştırılmamıştır. Mock/sahte/eksik olan şeyler de açıkça "GERÇEK DEĞİL" ya da "EKSİK" diye işaretlendi.

---

## 0. Tek Cümlede Sistem

Bir React SPA, nginx gateway arkasında, iki iş uygulamasına (personel için `YonetimApi`, filo/araç için `FlotaApi`) HttpOnly-cookie tabanlı oturumla bağlanıyor; bu iki uygulama kullanıcının kimliğini ve yetkisini kendileri karara bağlayıp, dosya okuma/yazma işini kendi kimlikleriyle (mTLS + servis JWT) ayrı bir `FileServiceApi`'ye devrediyor; `FileServiceApi` dosyaları NFS (Files-01) üzerinde tutuyor ve tüm metadata'yı PostgreSQL'de 4 ayrı şemada (`files`, `yonetim`, `filo`, `ops`) saklıyor. Ayrıca salt-okunur bir `OpsApi` + Ops Console, container/disk/backup durumunu göstermek için var.

---

## ⚠️ KRİTİK BULGU (2026-07-02) — FlotaApi Hiçbir Zaman Tarayıcıdan Kullanılamıyordu, Düzeltildi

Bu oturumda personel/araç endpoint'leri için kapsamlı bir "yanlış kullanıcı → 403" test matrisi yazılırken, **FlotaApi'nin gerçek tarayıcı/SPA akışında (HttpOnly cookie ile) hiçbir zaman authenticate olamadığı** ortaya çıktı — mock veya varsayım değil, canlı sunucuda tekrar tekrar doğrulanmış, kod seviyesinde kanıtlanmış bir bug.

**Kök neden — iki ayrı eksik, aynı anda:**

1. **`FlotaApi/Program.cs`'te cookie okuma kodu hiç yoktu.** `YonetimApi/Program.cs`'te `options.Events.OnMessageReceived` içinde `ctx.Token = ctx.Request.Cookies["at"]` satırı var — bu, BFF cookie akışının temeli. FlotaApi'de bu blok **tamamen eksikti**. Sonuç: FlotaApi sadece `Authorization: Bearer <token>` header'ı arıyordu; ama gerçek client (`client/src/api.ts`) **hiçbir zaman** bu header'ı göndermiyor, sadece `credentials:'include'` ile cookie gönderiyor. Yani tarayıcıdan gelen her istek, token'ı taşısa bile FlotaApi tarafından "token yok" sayılıp 401 ile reddediliyordu.
2. **`KeycloakBackchannelHandler` de hiç yoktu.** `YonetimApi`/`FileServiceApi`'de Keycloak'ın `KC_HOSTNAME=localhost` yüzünden JWKS URI'sinin `localhost:8080` dönmesini `keycloak:8080`'e yönlendiren bir handler var; FlotaApi'de bu da eksikti. Bu tek başına ikinci bir potansiyel authentication kırılma noktasıydı (test sırasında asıl kırılma #1'den geldi, ama #2 de gerçek ve düzeltilmesi gerekiyordu).

**Neden şimdiye kadar fark edilmemiş olabilir:** `PROJECT_STATUS.md`/`MIMARI.md`, "Fleet UI TAMAMLANDI" ve "fleetuser test edildi" diyor. Muhtemel açıklama: geliştirme sırasında FlotaApi büyük ihtimalle Postman/curl ile elle alınan bir `Authorization: Bearer` header'ıyla test edilmiş (bu senaryoda cookie sorunu hiç ortaya çıkmaz), gerçek tarayıcı/SPA akışıyla (sadece cookie) hiç uçtan uca test edilmemiş. Ayrıca `fleetuser` demo hesabının kendisi de `keycloak/realm-platform.json`'da **hiç kayıtlı değildi** (sadece dokümanlarda geçiyordu) — bu da bu senaryonun gerçekten hiç tekrarlanabilir şekilde test edilmediğinin ayrı bir kanıtı.

**Yapılan düzeltmeler (hepsi canlı sunucuda test edildi):**
- `FlotaApi/Program.cs`: `options.Events.OnMessageReceived` ile `at` cookie okuma eklendi (YonetimApi ile birebir aynı desen).
- `FlotaApi/Infrastructure/KeycloakBackchannelHandler.cs`: yeni dosya, YonetimApi'dekiyle aynı JWKS yönlendirme mantığı.
- `FlotaApi/Program.cs`: `MapInboundClaims = false` eklendi (tutarlılık için).
- `keycloak/realm-platform.json`: `fleetuser` (vehicle_id=test_arac_1) demo hesabı kalıcı olarak eklendi; ayrıca `opsadmin`/`opsuser01`'de eksik olan `firstName`/`lastName`/`emailVerified`/`requiredActions` alanları eklendi (bunlar olmadan Keycloak'ın "VERIFY_PROFILE" dinamik kontrolü fresh import'ta login'i "Account is not fully set up" diyerek reddediyordu — bu da ayrı, gerçek bir gizli hataydı, sadece Keycloak container'ı hiç "gerçekten sıfırdan" yeniden oluşturulmadığı için şimdiye kadar tetiklenmemişti).

**Doğrulama:** `fleetuser` gerçek cookie login'i ile `GET /api/vehicles/test_arac_1/files` → `200`; `GET /api/vehicles/test_arac_2/photo` (başka araç) → `403 data_scope_denied`; upload denemesi de aynı şekilde `403`. Tam smoke test + genişletilmiş `server-safe-test-suite.sh` (10 senaryolu 403 matrisi dahil) baştan sona geçti.

**Ayrıca bu oturumda canlı olarak gözlemlendi (ayrı ama ilişkili bir gerçek):** VM'ler bir noktada beklenmedik şekilde kapanıp yeniden açıldığında, `docker-compose.yml`'de hiçbir `restart:` politikası olmadığı için **hiçbir container kendiliğinden ayağa kalkmadı** — hepsi "Exited" durumunda takılı kaldı, elle `docker compose up -d` gerekti. Bu, önceki bir bölümde ("Dayanıklılık" tartışması) zaten teorik olarak konuşulmuştu; bu kez gerçek bir VM kapanmasıyla **canlı olarak doğrulandı**.

---

## 1. Genel Mimari — Bileşenler ve Sorumluluklar

```
Tarayıcı (React SPA, client/)
   │  HTTPS, çerezle (at/rt cookie, HttpOnly)
   ▼
Gateway (nginx, port 5090 → 443, TEK dışa açık servis)
   │
   ├─ /api/auth/*, /api/personnel/*   → YonetimApi:8080
   ├─ /api/vehicles/*                 → FlotaApi:8080
   ├─ /ops/*                          → OpsApi:8080
   ├─ /internal/*                     → 404 (asla dışarı açılmaz)
   └─ /*                              → client:80 (React SPA statik dosyaları)
         │                    │
         ▼                    ▼
   YonetimApi              FlotaApi
   (personel BFF)           (filo BFF)
         │                    │
         │   mTLS + servis JWT (her ikisi de aynı anda zorunlu)
         └──────────┬─────────┘
                     ▼
              FileServiceApi  (dışa hiç açık değil, sadece platform-net'te)
                     │
          ┌──────────┴───────────┐
          ▼                      ▼
     PostgreSQL              Files-01 (NFS, 192.168.64.3)
     (files/yonetim/filo/ops şemaları)   /srv/files → /mnt/platform-files
```

Her katmanın **gerçek** sorumluluğu (kod bazlı, `MIMARI.md` §16 ile birebir eşleşiyor):

| Katman | Yapar | Yapmaz |
|---|---|---|
| nginx (gateway) | Yönlendirme, body-size limiti, JSON hata sayfaları, TLS terminasyonu | Token doğrulama, iş mantığı |
| YonetimApi | User JWT doğrulama, RBAC/data-scope, domain audit, FileService'e proxy | Dosya depolama, fiziksel path bilgisi |
| FlotaApi | Aynısı, filo/araç alanı için — ama RBAC yerine tek bir claim eşleşmesi (`vehicle_id`) | Rol bazlı kontrol (rol yok) |
| FileServiceApi | Dosya kataloğu, fiziksel depolama, stream, teknik audit, app-policy kontrolü | Kullanıcı kimliği, iş mantığı RBAC'ı |
| OpsApi | Salt-okunur container/disk/backup/versiyon görünürlüğü | Docker'a doğrudan erişim (socket mount YOK) |
| PostgreSQL | Kalıcı veri, 4 ayrı şema | — |
| Files-01 (NFS) | Binary depolama | Metadata, erişim kontrolü |
| Keycloak | Token imzalama, kullanıcı/rol tanımı (statik realm JSON'dan import) | Uygulama iş mantığı |

---

## 2. Ağ Topolojisi — Gerçek Durum

**Tek Docker network var**: `platform-net` (bridge). Katmanlar arası izolasyon **network segmentasyonuyla değil**, hangi container'ın `ports:` ile host'a açıldığıyla sağlanıyor (`docker-compose.yml`).

| Container | Host'a açık mı? | Not |
|---|---|---|
| `gateway` | **Evet**, `5090:443` | Tek dışa açık servis (prod compose) |
| `client`, `opsapi`, `fileservice`, `postgres`, `yonetimapi`, `flotaapi` | Hayır (prod compose) | Sadece `platform-net` içinden erişilir |
| `keycloak` | Hayır (prod) | `docker-compose.override.yml` ile dev'de `8080:8080` açılır |

`docker-compose.override.yml` sadece **dev kolaylığı** içindir — `docker compose up` çalıştırıldığında otomatik yüklenir ve `keycloak:8080`, `fileservice:5205`, `yonetimapi:5076`, `flotaapi:5077` portlarını host'a açar (Swagger/debug erişimi için). **Production'da bu dosya devre dışı bırakılmalı**: `docker compose -f docker-compose.yml up` (override'sız) komutu kullanılır — bunu `setup-server.sh` zaten böyle çalıştırıyor.

Sunucu seviyesinde (host firewall, `ufw`), bu oturumda kurulup doğrulanmış durum:
- **api sunucusu (192.168.64.5)**: `ufw` aktif, sadece `22/tcp` (SSH) ve `5090/tcp` (gateway) izinli.
- **files01 (192.168.64.3)**: `ufw` aktif, `2049/tcp` (NFS) sadece `192.168.64.5`'ten kabul ediliyor — bizzat test edildi (bu Mac'ten mount denemesi 4 dakika sonra timeout ile reddedildi).

---

## 3. Kimlik Doğrulama — Uçtan Uca Gerçek Akış

### 3.1 İki Ayrı Kimlik Zinciri

Sistemde **kullanıcı kimliği** ile **servis kimliği** birbirine hiç karışmıyor:

```
[Kullanıcı JWT]                          [Servis JWT]
Keycloak client: frontend-test           Keycloak client: yonetimapi / filoapi
grant_type=password (ROPC)               grant_type=client_credentials
Taşıyıcı: tarayıcı (HttpOnly cookie)     Taşıyıcı: YonetimApi/FlotaApi (bellekte, 30sn erken expire ile cache'li)
Süre: 300 sn (5 dk)                      Süre: 300 sn, otomatik yenilenir
Hedef: YonetimApi / FlotaApi             Hedef: FileServiceApi
```

FileServiceApi kullanıcı kimliğinden **tamamen habersizdir** — sadece "hangi app_code çağırıyor" ve o app_code'un policy'sini bilir. Kullanıcı bazlı karar (örn. "bu personeli görebilir mi") tamamen YonetimApi/FlotaApi katmanında verilir.

### 3.2 Login — Gerçek Kod (`YonetimApi/Endpoints/AuthEndpoints.cs:31-52`)

```csharp
var kcResp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "password",
    ["client_id"]  = clientId,        // "frontend-test"
    ["username"]   = body.Username,
    ["password"]   = body.Password,
}));
if (!kcResp.IsSuccessStatusCode)
    return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);
...
SetCookies(ctx.Response, kc, IsSecureRequest(ctx));
var user = DecodeJwtPayload(kc.AccessToken);
return Results.Ok(BuildResponse(user, kc.ExpiresIn));
```

Bu **gerçek bir ROPC (Resource Owner Password Credentials) grant** — kullanıcının parolası doğrudan YonetimApi üzerinden Keycloak'a gönderiliyor (public client `frontend-test`, `keycloak/realm-platform.json:108-161`: `publicClient: true`, `directAccessGrantsEnabled: true`). Access token **tarayıcıya asla JSON body'de dönmez** — sadece sunucu tarafında decode edilip küçük bir `user` objesi (username, personnel_id, vehicle_id, roles) JSON'a konur; ham token `at`/`rt` cookie'lerine yazılır:

```csharp
// AuthEndpoints.cs:104-119
private static CookieOptions MakeCookieOpts(TimeSpan maxAge, bool secure) => new()
{
    HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = maxAge,
};
response.Cookies.Append("at", kc.AccessToken!, MakeCookieOpts(atAge, secure));
response.Cookies.Append("rt", kc.RefreshToken ?? "", MakeCookieOpts(rtAge, secure));
```

`Secure` bayrağı koşullu: `ctx.Request.IsHttps || X-Forwarded-Proto == "https"` (`AuthEndpoints.cs:99-101`) — yani HTTPS gateway arkasında Secure=true, düz HTTP'de Secure=false (yerel test için). `HttpOnly` ve `SameSite=Strict` **her zaman** aktif — JS token'ı hiç göremez, XSS'e karşı korunur.

Client tarafında bu şu şekilde çağrılır (`client/src/api.ts:9-18`, `bffLogin`) — `fetch(..., {credentials: 'include'})` ile, hiçbir Authorization header yok, token JS tarafında hiç saklanmaz (localStorage bile yok).

### 3.3 Token Yenileme (BFF Refresh)

`POST /api/auth/refresh` — `rt` cookie'sini okuyup Keycloak'a `grant_type=refresh_token` ile gider, yeni `at`/`rt` çiftini set eder (`AuthEndpoints.cs:56-89`). Frontend, access token süresi dolmadan ~60 saniye önce bunu proaktif tetikler (`client/src/App.tsx:23-33`, `isAccessTokenFresh` kontrolüyle). `rt` süresi de dolmuşsa 401 döner, client `bffLogout()` çağırıp login ekranına düşer.

**Logout tamamen lokaldir** — Keycloak'ın end-session endpoint'i hiç çağrılmaz, sadece cookie'ler temizlenir (`AuthEndpoints.cs:92-96,121-129`). Yani Keycloak session'ı teknik olarak sunucu tarafında `ssoSessionIdleTimeout` (1800 sn) dolana kadar "aktif" kalabilir, sadece tarayıcı artık kullanamaz.

### 3.4 Backchannel DNS Sorunu ve Çözümü (gerçek, üretilmiş bir bug fix)

Keycloak `KC_HOSTNAME=localhost` ile çalışıyor (tarayıcı `localhost:8080`'a gidebilsin diye). Ama .NET'in JWT middleware'i JWKS/OIDC metadata'sını container **içinden** çekmeye çalışınca, discovery dökümanındaki `jwks_uri` de `localhost:8080` çıkıyor — container içinden bu adres Keycloak'a gitmiyor (kendi container'ına döner). Çözüm (`YonetimApi/Infrastructure/KeycloakBackchannelHandler.cs`, `FileServiceApi`'de birebir kopyası var):

```csharp
protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
{
    if (request.RequestUri?.Host == "localhost" && request.RequestUri.Port == 8080)
    {
        var ub = new UriBuilder(request.RequestUri) { Host = "keycloak" };
        request.RequestUri = ub.Uri;
    }
    return base.SendAsync(request, ct);
}
```
`options.BackchannelHttpHandler` olarak takılıyor — sadece JWKS/metadata çağrılarını `keycloak:8080`'e yönlendiriyor, login akışını (browser→Keycloak) etkilemiyor.

### 3.5 Servis Kimliği — YonetimApi/FlotaApi'nin Kendi Token'ı

`YonetimApi/Services/TokenService.cs:29-64` — `client_credentials` grant, client_id=`yonetimapi`, secret=`yonetimapi-secret-v1` (FlotaApi'de `filoapi`/`filoapi-secret`). Bu client'ların Keycloak'ta `oidc-hardcoded-claim-mapper` ile sabit bir `app_code` claim'i var (`keycloak/realm-platform.json`: yonetimapi client → `app_code: "yonetimapi"`). Token bellekte 30 saniye erken-expire toleransıyla cache'lenir (double-checked locking ile thread-safe).

### 3.6 mTLS — FileServiceApi'ye Erişimin İkinci Katmanı

JWT tek başına yetmiyor; **aynı anda mTLS de zorunlu**. Gerçek Kestrel kurulumu (`FileServiceApi/Program.cs:12-45`):

```csharp
kestrel.ListenAnyIP(8080, endpoint => endpoint.UseHttps(https =>
{
    https.ServerCertificate = X509Certificate2.CreateFromPemFile(serverCertPath, serverKeyPath!);
    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
    https.ClientCertificateValidation = (cert, chain, _) =>
    {
        var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
        if (!allowedCNs.Contains(cn)) return false;   // sadece "yonetimapi" veya "filoapi"
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        return chain.Build(cert);
    };
}));
```

YonetimApi/FlotaApi tarafında, FileService'e giden `HttpClient` kendi client sertifikasını sunuyor (`YonetimApi/Program.cs:18-49`, birebir aynı kod FlotaApi'de de var):
```csharp
var raw = X509Certificate2.CreateFromPemFile(clientCertPath!, clientKeyPath!);
handler.ClientCertificates.Add(new X509Certificate2(raw.Export(X509ContentType.Pkcs12)));
handler.ServerCertificateCustomValidationCallback = (_, serverCert, chain, _) => { ...chain.Build(serverCert); };
```

Sertifika zinciri gerçek dosyalarla doğrulandı (`openssl x509 -text`):
```
platform-ca (CN=platform-ca, 10 yıl)
  ├── fileservice.crt  (CN=fileservice, SAN: DNS:fileservice,localhost — server cert)
  ├── gateway.crt      (CN=gateway, SAN: DNS:gateway,localhost,IP:127.0.0.1,IP:192.168.64.5 — server cert)
  ├── yonetimapi.crt   (CN=yonetimapi, clientAuth — client cert)
  └── filoapi.crt      (CN=filoapi, clientAuth — client cert)
```

**Önemli sınır**: gateway (nginx) hiçbir `ca.crt` mount etmiyor — yani gateway browser'dan gelen bağlantılarda mTLS istemiyor/doğrulamıyor. mTLS güven çemberi sadece `FileServiceApi ↔ {YonetimApi, FlotaApi}` arasında, browser hiç bu çemberin içinde değil.

mTLS devre dışı bırakılabilir mi? Evet — `Mtls:ServerCertPath` config boşsa (örn. yerel `dotnet run` ile appsettings.json'da bu bölüm hiç yoksa) tüm mTLS bloğu atlanır, düz Kestrel HTTP/HTTPS'e döner. Yani mTLS sadece Docker/production modunda gerçekten zorunlu.

---

## 4. RBAC / Data-Scope — Kim Neyi Görebilir

### 4.1 Personel Tarafı (YonetimApi) — Gerçek Rol Modeli

Roller Keycloak realm'inde tanımlı (`keycloak/realm-platform.json:9-43`), format `{kaynak}.{eylem}.{kapsam}`:
```
personnel.files.read.self / .team / .all
personnel.files.write.self / .all      ← DİKKAT: write.team YOK, sadece self/all var
```

Karar kodu, `YonetimApi/Services/PermissionService.cs:6-72`, kontrol sırası **her zaman all → team → self**:
```csharp
if (HasRole(user, $"{permission}.{action}.all")) return true;
var ownId = GetPersonnelId(user);
if (HasRole(user, $"{permission}.{action}.team"))
    return ownId == targetId || await IsTeamMemberAsync(ownId, targetId);
if (HasRole(user, $"{permission}.{action}.self"))
    return ownId == targetId;
return false;
```
`ownId`, JWT'deki `personnel_id` claim'inden gelir (`GetPersonnelId`, yoksa `preferred_username`/`sub` upper-case fallback). `IsTeamMemberAsync`, gerçek bir SQL sorgusu ile `yonetim.team_members` tablosuna bakar:
```sql
SELECT 1 FROM yonetim.team_members WHERE manager_id = $1 AND personnel_id = $2
```

Personel arama endpoint'i (`GET /api/personnel`) bu servisi **kullanmıyor** — kendi inline SQL scope filtresini yazıyor (`PersonnelEndpoints.cs:360-397`): `read.team` için `personnel_id IN (SELECT personnel_id FROM team_members WHERE manager_id=$1 UNION SELECT $1)`, `read.self` için doğrudan `personnel_id = $1`. Aynı mantık, iki farklı yerde iki farklı şekilde (existence-check vs set-filter) uygulanmış — davranış aynı ama kod tekilleştirilmemiş.

**Ekstra koruma katmanı — fileId scope kontrolü**: `/api/personnel/{id}/files/{fileId}/content` gibi çağrılarda, sadece `CanReadAsync(personnelId)` yetmiyor; ayrıca o `fileId`'nin gerçekten o `personnelId`'ye ait olup olmadığı FileServiceApi'nin `internal/files/ownership` endpoint'i ile çift kontrol ediliyor (`PersonnelEndpoints.cs:574-600`). Bu, "kendi personel kaydına erişimi olan biri başka birinin fileId'sini tahmin edip kendi personnelId path'i üzerinden çekemesin" senaryosunu kapatıyor.

### 4.2 Filo Tarafı (FlotaApi) — Rol Yok, Sadece Claim Eşleşmesi

FlotaApi'de **hiçbir realm-rolü kontrolü yok**. Tüm erişim kontrolü tek bir fonksiyon:
```csharp
// VehicleEndpoints.cs:344-348
private static bool HasVehicleAccess(ClaimsPrincipal user, string vehicleId)
{
    var ownVehicleId = user.FindFirst("vehicle_id")?.Value;
    return !string.IsNullOrEmpty(ownVehicleId) && ownVehicleId == vehicleId;
}
```
Yani: kullanıcının JWT'sindeki `vehicle_id` claim'i URL'deki `{vehicleId}` ile birebir aynı değilse **her zaman 403**. "Tüm araçları gören" bir rol/yetki **yoktur** — mimari olarak "bir kullanıcı hesabı = bir araç" modeli. Bu YonetimApi'nin `.all`/`.team`/`.self` modelinden kökten farklı, çok daha basit bir model.

### 4.3 "Vehicle" Aslında Bir Veritabanı Kaydı Değil — ÖNEMLİ GERÇEK

Doğrudan `db/docker-init/01-schema.sql` içinde arattım: **`filo.vehicles` diye bir tablo yok**. `filo` şemasında tek tablo var: `filo.audit_events`, ve onun içindeki `vehicle_id` sütunu **FK'siz düz bir VARCHAR** — referans verdiği bir "araç" tablosu yok çünkü öyle bir tablo hiç yaratılmamış. Yani sistemde "araç" diye bir varlık, PostgreSQL'de **hiç yok** — sadece Keycloak kullanıcı attribute'u (`vehicle_id`) olarak, JWT claim'i şeklinde var. `PROJECT_STATUS.md`'de zaten not edilmiş: "Fleet vehicle araması: FlotaApi'ye `GET /api/vehicles?search=` eklenirse... şu an yoktur." — bu, neden yok'un tam açıklaması: araç master-data'sı hiç modellenmemiş, sadece dosya ilişkilendirmesi (`entity_type='vehicle', entity_id='<vehicle_id>'`) var.

### 4.4 Cross-Domain İzolasyon — `files.app_policies`

```sql
-- 02-seed.sql
('yonetimapi', ARRAY['personnel'], ARRAY['photo','cv','official_document','document','attachment'], true, true, true, 10485760),
('filoapi',    ARRAY['fleet'],     ARRAY['photo','document','official_document','attachment','report'], true, true, true, 20971520)
```
`yonetimapi` sadece `personnel` domain'ine, `filoapi` sadece `fleet` domain'ine erişebilir — biri diğerinin domain'ine FileServiceApi seviyesinde 403 ile çarpar (`allowed_domains` array kontrolü). Boyut limiti de burada: personel dosyaları max 10 MiB, filo dosyaları max 20 MiB (nginx tarafında da ayrı limit var: 20m/25m — **iki limit birbirinden farklı ve iki ayrı yerde tanımlı**, tutarlılık riski var ama şu an her ikisi de gerçek limitin üstünde olduğu için sorun çıkarmıyor).

---

## 5. Veritabanı — Gerçek Şema (4 şema, 10 tablo, tek DB: `platformdb`)

```
platformdb
├── files.*      (FileServiceApi'nin sahibi olduğu)
│     ├── objects              — dosya metadata kataloğu
│     ├── references           — entity↔dosya bağlantısı + is_primary
│     ├── app_policies         — hangi app hangi domain/tür'e erişebilir
│     ├── relation_type_config — cardinality (single/multi)
│     └── audit_events         — teknik dosya audit'i
├── yonetim.*    (YonetimApi'nin sahibi olduğu)
│     ├── personnel            — 25 personel kaydı (seed'de)
│     ├── team_members         — yönetici↔personel ilişkisi
│     └── audit_events         — domain audit (personel bazlı)
├── filo.*       (FlotaApi'nin sahibi olduğu)
│     └── audit_events         — SADECE bu tablo var, araç master tablosu YOK
└── ops.*        (OpsApi'nin sahibi olduğu)
      └── audit_events         — ops konsolu erişim/işlem audit'i
```

**Kritik gerçek**: bu şemalarda **hiç native PostgreSQL ENUM tipi yok** — tüm "enum" değerleri `VARCHAR + CHECK` ile zorlanıyor, ve bu kontrol de sadece `files.*` tablolarında var:

| Tablo | action/result/status için CHECK var mı? |
|---|---|
| `files.objects.status` | ✅ `CHECK IN ('active','revoked','archived','deleted')` |
| `files.references.status` | ✅ `CHECK IN ('active','revoked')` |
| `files.audit_events.action/result` | ✅ `CHECK IN ('create','read','archive','delete_attempt')` / `('success','denied','not_found','error')` |
| `yonetim.audit_events.action/result` | ❌ Serbest metin, DB seviyesinde kısıtlama yok |
| `filo.audit_events.action/result` | ❌ Serbest metin |
| `ops.audit_events.action/result` | ❌ Serbest metin (sadece yorum satırıyla belgelenmiş) |

`files.references.relation_type` da **FK ile `relation_type_config`'e bağlı değil** — serbest metin, sadece `check_single_primary()` trigger'ı bu tabloya bakıyor (aşağıda).

`files.objects.status = 'deleted'` DB seviyesinde izinli bir değer ama **hiçbir C# kod yolu bu değeri hiç set etmiyor** (FileServiceApi raporunda doğrulandı) — yani "hard delete" kavramsal olarak şemada var ama uygulamada asla üretilmiyor; gerçek yaşam döngüsü sadece `active → archived` (obje) / `active → revoked` (referans).

Seed verisi: 25 personel (`HR001`, `ADM001`, `M001-M003`, `P001-P024`), 3 yönetici-ekip ilişkisi (M001→P001-P007, M002→P008-P014, M003→P015-P021 — **P022/P023/P024'ün yöneticisi yok**), 2 app_policy kaydı (yonetimapi, filoapi). `files.objects`/`references`/`audit_events` ve tüm audit tabloları için **hiç seed verisi yok** — bunlar sadece gerçek kullanım sırasında dolar.

---

## 6. Dosya Depolama Modeli — FileServiceApi

### 6.1 Fiziksel Yerleşim

```
/app/storage/                      (container içi mount, NFS'e bağlı)
  export/     ← kalıcı depolama (ReadPath = ExportPath, ikisi de aynı yolu gösteriyor)
    personnel/ab/cd/abcd1234-....pdf
    fleet/ab/cd/abcd1234-....jpg
    .probe                          ← health check probe dosyası
  staging/    ← geçici yazma alanı
    personnel/... fleet/...
```
Path/sharding kodu (`FileEndpoints.cs:362-372`):
```csharp
var fileId = Guid.NewGuid();
var shard1 = fileIdString.Substring(0, 2);
var shard2 = fileIdString.Substring(2, 2);
var relativePath = $"{domain}/{shard1}/{shard2}/{fileIdString}.{extension}";
```
İki seviyeli, GUID'in ilk 4 hex karakterinden türetilen paylaşım (256×256 klasör/domain). PII (ad-soyad, TCKN vb.) dosya adına **hiç yazılmıyor** — DB'de `original_file_name` alanında saklanıyor.

### 6.2 Upload Akışı — Gerçek, Atomik Adımlar

```csharp
// 1. Staging'e yaz
using (var stagingStream = new FileStream(stagingFull, FileMode.Create, ...))
    await uploadStream.CopyToAsync(stagingStream);
// 2. Staging'deki dosyadan SHA256 hesapla (disk'e gerçekten yazılanı doğrular)
var hashBytes = await sha256.ComputeHashAsync(hashStream);
// 3. Atomic promote: aynı dosya sistemi içinde rename
File.Move(stagingFull, exportFull, overwrite: false);
```
Hata olursa (`IOException`): staging'deki yarım dosya silinir, `503 storage_unavailable` döner. DB kaydı başarısız olursa: export'a taşınmış dosya **geri silinir** (rollback) — disk ile DB asla tutarsız kalmaz. Duplikasyon kontrolü: aynı entity+relation+SHA256 zaten aktifse, yeni yazılan export dosyası silinip `409` döner.

Doğrulama sırası (upload): policy kontrolü (CanCreate + domain + relationType) → boyut limiti (413) → uzantı allow-list (415) → Content-Type eşleşmesi (415) → magic-byte kontrolü (415) → diske yazma → duplikasyon kontrolü (409) → DB insert.

**Magic-byte kontrolü** (gerçek byte'lar, `FileEndpoints.cs:628-646`):
| Uzantı | Kontrol |
|---|---|
| pdf | `25 50 44 46` (`%PDF`) |
| jpg/jpeg | `FF D8 FF` |
| png | `89 50 4E 47 0D 0A 1A 0A` |
| webp | `52 49 46 46 .. .. .. .. 57 45 42 50` (RIFF....WEBP) |

**Uzantı allow-list relation type'a göre değişiyor** (`FileEndpoints.cs:599-609`):
```
cv → yalnız pdf
photo → jpg, jpeg, png, webp
official_document / document / attachment → pdf, jpg, jpeg, png, webp
report → yalnız pdf
```

### 6.3 Download Akışı

```csharp
var etag = $"\"sha256:{fileObject.Sha256}\"";
if (ifNoneMatch == etag) return Results.StatusCode(304);   // disk hiç okunmaz
...
return Results.Stream(fileStream, fileObject.ContentType, enableRangeProcessing: true);
```
SHA256 **indirmede yeniden hesaplanmaz** — yükleme anında hesaplanıp DB'ye yazılmış olan kullanılır. `enableRangeProcessing: true`, ASP.NET Core'un kendi built-in Range/206 mantığını devreye sokuyor (elle yazılmış range-parsing kodu yok). Path traversal koruması: normalize edilmiş path, kök path ile başlamıyorsa 500 + audit. Disk'te dosya yoksa (DB "active" diyor ama binary NFS'te yoksa): 503.

Content-Disposition, RFC 5987 ile encode edilir; resimler `inline`, diğerleri `attachment`:
```csharp
response.Headers["Content-Disposition"] = $"{disposition}; filename=\"{asciiFallback}\"; filename*=UTF-8''{encodedName}";
```

### 6.4 Kardinalite (Single-Primary vs Multi-Primary)

```sql
-- 02-seed.sql
('cv','single'), ('photo','single'), ('official_document','single'),
('document','multi'), ('attachment','multi'), ('report','multi')
```
DB güvence katmanı, gerçek bir trigger — `files.check_single_primary()`, `BEFORE INSERT OR UPDATE ON files.references`:
```sql
IF NEW.is_primary AND NEW.status = 'active' THEN
  IF relation_type NOT 'multi' THEN
    IF (aynı entity+relation_type+app_code için zaten aktif bir primary varsa)
      RAISE EXCEPTION 'single_primary_violation: ...';
```
Yani uygulama kodu zaten arşivleme yapıyor (yeni CV yüklenince eskisini `archived`/`revoked` yapıyor), ama bu trigger **kod hatasına karşı ikinci bir güvenlik ağı** — aynı anda iki aktif primary asla DB'ye giremez.

**Pratik sonuç**: `document`/`attachment`/`report` gibi multi-primary tiplerde YonetimApi'de de FlotaApi'de de **sadece upload route var, content/archive route yok** (route seviyesinde 404). Bu tipleri görüntülemenin tek yolu `/files` (liste) endpoint'i + genel `files/{fileId}/content` route'u.

### 6.5 Hiç Olmayan Şeyler (Bilerek Not Edilmeli)

- **Hard delete endpoint'i yok.** Hiçbir yerde bir dosyayı fiziksel olarak silen bir API çağrısı yok.
- **Update/replace endpoint'i yok.** Bir dosyanın içeriğini "güncellemek" diye bir işlem yok — yeni upload, eskiyi arşivler.
- **`CheckOwnershipAsync` audit yazmıyor** — FileServiceApi'nin tek audit yazmayan endpoint'i.

---

## 7. Ops Console / OpsApi — Bu Oturumda Zaten Derinlemesine Test Edildi

(Bu bölüm, bu oturumda canlı sunucuda bizzat test edilerek doğrulanmıştır — sadece kod okuması değil.)

- OpsApi **Docker socket'ine hiç erişemez** — `docker-compose.yml`'de mount edilmemiş. Container/CPU/RAM/restart/uptime bilgisini, host'ta `systemd` timer'ı ile 5 dakikada bir çalışan `tools/services-status.sh` betiğinin yazdığı `/backup/platform-files/.services-status.json` dosyasından okur.
- Bu ayrım gerçek bir arıza kaynağı oldu: script `/tmp/platform-services-status.err` gibi sabit bir yola stderr yazıyordu; farklı kullanıcı bağlamlarında (root vs `fileapi`) bu dosyaya erişim çakışması, script'i sessizce "failed" durumuna düşürüp Ops ekranında container listesini boş gösteriyordu. `mktemp` ile düzeltildi (commit `6617591`).
- `/ops/dashboard` tek çağrıda `health` (canlı HTTP health-check'ler — yonetimapi/flotaapi/keycloak/gateway/postgres için gerçek zamanlı), `services` (5 dakikalık snapshot), `disk`, `alerts`, `backups`, `version` döner.
- Frontend (`OpsConsole.tsx`) 30 saniyede bir `/ops/dashboard`'ı yeniden çeker; ek olarak bu oturumda eklenen `document.visibilitychange` dinleyicisiyle, sekme uzun süre arka planda kaldıktan sonra öne gelince anında tazelenir (tarayıcıların arka plan sekmelerinde `setInterval`'ı yavaşlatması sorununu çözer).
- `ops.audit_events`, diğer üç audit tablosundan **tamamen bağımsız** ayrı bir şema (`db/docker-init/03-ops-schema.sql` başlığında açıkça yazıyor).
- Rol modeli: `ops.read` (görüntüleme), `ops.execute` (backup tetikleme, servis restart — V1'de UI'da yok), `ops.admin` (tam yetki). Ops rolü olmayan kullanıcı `/ops/*`'a **404** alır (403 değil — varlığı bile sızdırılmıyor).

---

## 8. Audit Sistemi — 4 Ayrı, Bağımsız Tablo

```
files.audit_events          yonetim.audit_events        filo.audit_events         ops.audit_events
─────────────────           ─────────────────────       ──────────────────       ──────────────────
"Hangi app, hangi            "Hangi kullanıcı, hangi     "Hangi kullanıcı,         "Hangi ops kullanıcısı
 dosyaya, ne yaptı?"          personele, ne yaptı?"        hangi araca, ne yaptı?"   hangi ops işlemini yaptı?"
app_code="yonetimapi"        actor="hr001"                actor="fleetuser"         actor="opsadmin"
file_id=UUID                 personnel_id="P001"          vehicle_id="test_arac_1" action="ops.health.read"
action="read"                action="PersonnelCvDownloaded" action="VehiclePhotoUploaded"
CHECK constraint VAR         CHECK constraint YOK          CHECK constraint YOK      CHECK constraint YOK
```
Her katman audit hatasını **yutar** (try/catch + log, exception fırlatmaz) — audit yazımının başarısız olması hiçbir zaman kullanıcı isteğini bloklamaz (üç ayrı serviste de aynı "audit hatası ana akışı engellemez" deseni var).

**304 Not Modified** ve **mTLS reddi** durumlarında audit yazılmaz (304 = gerçek veri erişimi yok; mTLS reddi = uygulama kodu hiç çalışmadan TLS katmanında düşer).

---

## 9. Client (React SPA) — Gerçek Yapı

- **Router yok.** `package.json`'da sadece `react`/`react-dom` var — react-router benzeri hiçbir kütüphane yok. Tüm navigasyon `useState` ile: `App.tsx` → `auth` var mı yok mu → `LoginPage` veya `Dashboard`. `Dashboard.tsx` içinde `view: 'personnel'|'fleet'|'ops'` state'i sekmeleri değiştiriyor. **Sayfa yenilenirse veya URL'e bir "derin link" yazılırsa hiçbir state korunmaz** — her zaman baştan (login veya varsayılan view) başlar.
- Sekme görünürlüğü tamamen JWT claim'lerinden türetiliyor: `hasFleet = !!vehicle_id`, `hasPersonnel = roller "personnel." ile başlıyor mu`, `hasOps = hasOpsAccess()`. **Varsayılan sekme önceliği: Ops > ilk mevcut view > personnel** — ops yetkisi olan biri diğer yetkileri de olsa direkt Ops konsoluna düşer.
- Auth kontrolleri (`auth.ts`) **sadece UI'ı** yönlendirir — gerçek güvenlik sunucu tarafındadır. `canWrite`, `canVehicleWrite` gibi fonksiyonlar sadece "Yükle" butonunu gösterip göstermemeye karar verir; asıl 403 kararı her zaman backend'de.
- Upload, `fetch` değil ham `XMLHttpRequest` ile yapılıyor (`xhrUpload`, `api.ts:56-82`) — sebep: `fetch` upload progress event'i vermiyor, `xhr.upload.onprogress` ile gerçek zamanlı yüzde çubuğu mümkün oluyor.
- **Client-side dosya validasyonu pratik olarak yok** — sadece "dosya seçilmiş mi" kontrolü var; uzantı/boyut/MIME kontrolü tamamen sunucuya bırakılmış, hata mesajları HTTP status koduna göre (413/415/409) sonradan gösteriliyor.

---

## 10. Plan (`PROJE/*.md`) vs Gerçek Uygulama — Farklar

`PROJE/` klasöründeki 5 doküman, implementasyon **öncesi** yazılmış tasarım/planlama notlarıdır (stajyer yönergesi, NFS modeli taslağı, API sözleşmesi taslağı). Gerçek kod bunların çoğunu uyguladı ama bazı yerlerde **bilinçli olarak farklılaştı**:

| Konu | Plan (`PROJE/*.md`) | Gerçek Kod |
|---|---|---|
| NFS export modu | "Read-only export, `all_squash`, runtime yazamaz/silemez" (`files01-nfs-model.md`) | **Gerçekte `rw`** (`/srv/files 192.168.64.5(rw,sync,...)`) — FileServiceApi staging→export'a gerçekten yazıyor. Bu bilinçli bir V1 kısayolu; `PROJECT_STATUS.md`'de "Strict NFS ro/publisher modeli — V2 hardening" olarak zaten not edilmiş. |
| Dizin modeli | `/srv/files/export`, `/staging`, `/manifests`, `/restore-tests` (4 klasör) | Gerçekte sadece `export/` ve `staging/` kullanılıyor container mount'unda; manifests/restore-tests NFS tarafında runbook seviyesinde var ama FileServiceApi kodu bunları hiç bilmiyor. |
| Auth modeli seçenekleri | "OAuth2 client credentials VEYA mTLS VEYA network allowlist" (üç seçenekten biri yeterli deniyor) | Gerçekte **ikisi birden zorunlu** (mTLS + servis JWT) — plan "yeterli" derken kod daha katı davranmış. |
| Endpoint isimleri | `GET /internal/files/{fileId}` dokümantasyonda "path/host asla dönmez" der | Kod bunu birebir uyguluyor — `relative_path` hiçbir API response'unda görünmüyor, doğrulandı. |
| İkinci tüketici uygulama | "İkinci uygulama gelmeden önce merkezi File-Service API kurulmalı" | Gerçekte zaten kuruldu ve FlotaApi ikinci tüketici olarak **plana birebir uygun şekilde** entegre edilmiş (`app_code=filoapi`, ayrı domain=`fleet`, ayrı policy). |
| Vehicle veri modeli | Planlarda hiç detaylandırılmamış (bu 5 doküman sadece personel/Files-01 odaklı) | Gerçekte "vehicle" hiç DB tablosu değil, salt JWT claim + dosya referansı — plan bu konuda zaten sessiz, kod da bilinçli minimal tutulmuş. |

**Sonuç**: Planlama dokümanları ile gerçek kod **büyük ölçüde tutarlı** — mimari kararların (merkezi katalog, app isolation, 404 ile varlık sızdırmama, iki katmanlı audit) hepsi koda geçmiş. En büyük sapma NFS'in read-only değil read-write olması, ki bu proje kendi durum dosyasında (`PROJECT_STATUS.md`) zaten bilinen ve V2'ye ertelenmiş bir teknik borç olarak işaretli — gizli/fark edilmemiş bir sapma değil.

---

## 11. Bilinen Eksikler / Yarım Kalanlar (Mock Değil, Gerçekten Eksik)

Bunlar "yapılmış gibi görünüp aslında yapılmamış" ya da bilinçli olarak V2'ye bırakılmış gerçek boşluklar:

1. **Secret rotasyonu yok.** Keycloak realm JSON'unda demo kullanıcı şifreleri (`Demo1234!`, `ops123`) ve client secret'ları (`yonetimapi-secret-v1`, `filoapi-secret`) **düz metin olarak** dosyada duruyor, hiçbir secret store kullanılmıyor.
2. **Araç (vehicle) master-data'sı hiç yok** — yukarıda detaylandırıldı (§4.3). `GET /api/vehicles?search=` gibi bir endpoint yok, araç listesi/arama UI'da yok.
3. **NFS read-write** (planlanan read-only/publisher modeli yerine) — §10'da detaylandırıldı.
4. **Hard delete hiç implemente edilmemiş** — DB şeması `status='deleted'` değerine izin veriyor ama hiçbir kod yolu bunu üretmiyor.
5. **Ops Dashboard'da audit read endpoint'i yok** — `ops.audit_events` tablosuna yazılıyor ama Ops Console'da "son ops işlemleri" diye bir görünüm yok (yalnızca `tools/server-smoke-test.sh` doğrudan `psql` ile DB'den okuyarak gösteriyor).
6. **Resilience/restart testleri sadece kısmen yapıldı** — bu oturumda `client` container'ı manuel durdurup başlatarak Ops Dashboard'un doğru tepki verdiği kanıtlandı; ama Gateway/FileService/Keycloak/PostgreSQL için sistematik restart-sonrası-toparlanma testleri henüz `PROJECT_STATUS.md`'de "sıradaki adım" olarak duruyor, tamamlanmadı.
7. **Observability yok** — request-id/correlation standardizasyonu (X-Correlation-Id bazı çağrılarda var ama hepsinde değil), `/metrics` endpoint'i, Prometheus/Grafana, distributed tracing — hiçbiri kurulmamış.
8. **`nginx` gateway TLS'i self-signed** — Let's Encrypt/gerçek domain entegrasyonu henüz yok, sadece iç ağ için self-signed sertifika kullanılıyor.
9. **Client tarafı validasyon pratikte yok** — yukarıda belirtildiği gibi (§9), tüm dosya tipi/boyut kontrolü sunucuda.

---

## 12. Hata Kodları — Kim Neden Ne Döner (Gerçek Kod Bazlı)

| Kod | Kim döndürür | Anlamı |
|---|---|---|
| 401 | YonetimApi / FlotaApi / FileServiceApi | Token yok/geçersiz |
| 403 `access_denied` / `data_scope_denied` | YonetimApi / FlotaApi | RBAC/claim eşleşmesi başarısız |
| 403 `file_scope_denied` | YonetimApi | fileId bu personnelId'ye ait değil (ownership check) |
| 403 `policy_denied` | FileServiceApi | app_policy domain/tür izni yok |
| 404 | Herhangi (BFF veya FileServiceApi) | Kayıt yok — **ya da** yetkisiz erişimde varlık bilerek sızdırılmıyor |
| 409 | FileServiceApi | Aynı SHA256 zaten aktif (duplikasyon) |
| 413 | FileServiceApi | Dosya `max_file_size_bytes` limitini aşıyor |
| 415 | FileServiceApi | Uzantı/Content-Type/magic-byte uyuşmazlığı |
| 503 | FileServiceApi | Disk/NFS erişilemiyor veya binary eksik |
| 502/504 | nginx | Upstream servis çökmüş / 120 sn'de cevap yok |
| 304 | FileServiceApi | ETag eşleşti, veri değişmedi (disk okunmaz, audit yazılmaz) |

---

## 13. Sunum İçin Kısa Konuşma Rehberi

Eğer birine bu sistemi 5 dakikada anlatman gerekirse, sıra şöyle olabilir:

1. **"Neden 3 ayrı backend var?"** — Personel (YonetimApi) ve filo (FlotaApi) iş mantığı farklı, ama ikisi de dosya saklıyor. Dosya saklama mantığını (upload/download/validasyon/depolama) tek bir yerde (`FileServiceApi`) merkezi tutup, iki uygulamanın da onu "servis" olarak kullanmasını sağlamışlar — böylece dosya güvenliği (magic-byte, path traversal, boyut limiti) tek yerde yazılır, iki kere değil.
2. **"Neden iki farklı kimlik var?"** — Kullanıcı "ben hr001'im, tüm personeli görebilirim" der; ama FileServiceApi bunu bilmek zorunda değil, o sadece "YonetimApi konuşuyor, YonetimApi'nin personel dosyalarına erişim izni var mı" diye bakar. Kullanıcı bazlı karar (kim neyi görebilir) YonetimApi'de, uygulama bazlı karar (bu uygulama dosya sistemine ne yapabilir) FileServiceApi'de — ikisi ayrı sorumluluk.
3. **"Dosya nasıl güvenli tutulur?"** — Üç katman: (a) uzantı+MIME+magic-byte kontrolü sahte dosya yüklemeyi engeller, (b) staging→export atomik taşıma yarım yüklemenin kalıcı depoya karışmasını engeller, (c) path traversal kontrolü ve path'in API response'unda hiç dönmemesi, dosyaya doğrudan erişimi engeller.
4. **"Neden mTLS + JWT birlikte?"** — JWT çalınabilir/kopyalanabilir; mTLS "bu bağlantıyı gerçekten YonetimApi container'ı mı açtı" diye ağ katmanında ayrıca doğrular. İkisi farklı soruları cevaplıyor, biri diğerinin yerini tutmuyor.
5. **"Ops ekranı ne işe yarıyor?"** — Docker'a doğrudan erişimi olmayan, salt-okunur bir "sağlık paneli". Container durumu, disk doluluğu, backup geçmişi, servis health-check'lerini tek ekranda toplar — production'da "her şey ayakta mı" sorusuna hızlı cevap.
6. **"En büyük eksik ne?"** — Secret rotasyonu (demo şifreler hâlâ kodda), araç yönetimi hiç yapılmamış (sadece JWT claim), ve NFS'in planlanan read-only modelden read-write'a düşmüş olması — bunların hepsi bilinen, `PROJECT_STATUS.md`'de takip edilen teknik borçlar, "unutulmuş" değil.

---

## 14. Soru-Cevap — Sistemi Gerçekten Anlamak İçin

Bu bölümdeki her soru, yukarıdaki bölümlerde referans verilen gerçek kod/konfigürasyona dayanıyor. Amaç ezber değil, "biri sorduğunda kodun neresine bakıp cevap vereceğini bilmek."

### A. Genel Mimari

**1. Neden tek bir "backend" yok da 5 tane var (YonetimApi, FlotaApi, FileServiceApi, OpsApi, gateway)?**
Her biri farklı bir sorumluluğu taşıyor ve bu bilinçli bir ayrım: YonetimApi/FlotaApi "iş mantığı + kullanıcı yetkisi", FileServiceApi "dosya depolama + uygulama yetkisi", OpsApi "salt-okunur izleme", gateway "trafik yönlendirme". Tek bir dev backend'e hepsini koysaydılar, "personel dosyasına kim erişebilir" kararı ile "dosya diskte nerede duruyor" kararı aynı kod tabanında karışırdı — biri değişince diğerini kırma riski artardı. Ayrı servisler, ayrı deploy/ölçeklendirme/sertifika kimliği de sağlıyor (§6, §1).

**2. Sistemdeki veri nereye gidiyor son noktada?**
İki yere: metadata (kim, ne zaman, hangi personel/araç, hangi dosya türü) PostgreSQL'e (`platformdb`, 4 şema); dosyanın kendi byte'ları Files-01'deki NFS paylaşımına (`/srv/files` → container içinde `/app/storage`). İkisi asla aynı yerde değil — bu yüzden "dosya kaydı DB'de var ama disk'te yok" gibi bir tutarsızlık teorik olarak mümkün (FileServiceApi bunu tespit edip 503 döner, §6.3).

**3. Gateway olmadan biri doğrudan YonetimApi'ye ulaşabilir mi?**
Production compose'da hayır — `docker-compose.yml`'de `yonetimapi` servisinin `ports:` alanı yok, sadece `platform-net` Docker ağı içinden erişilebilir. Sadece dev modda (`docker-compose.override.yml` otomatik yüklendiğinde) `5076:8080` ile host'a açılıyor — bu dosya production'da bilerek devre dışı (`docker compose -f docker-compose.yml up`).

**4. FileServiceApi'ye internetten kimse ulaşabilir mi?**
Hayır, üç kat koruma var: (1) `ports:` tanımı yok, host'a hiç açılmıyor; (2) nginx'te ona giden hiçbir `location` kuralı yok — sadece `/internal/` path'i `404` döner; (3) ulaşsa bile mTLS istemci sertifikası olmayan hiçbir bağlantıyı TLS katmanında kabul etmiyor (`ClientCertificateMode.RequireCertificate`).

**5. Bu sistemde "tek nokta arızası" (single point of failure) nedir?**
PostgreSQL — hem `files.*`, hem `yonetim.*`, hem `filo.*`, hem `ops.*` tek bir `platformdb` içinde, tek container. Postgres çökerse hem dosya metadata'sı hem RBAC hem audit hem Ops paneli aynı anda etkilenir. Keycloak da benzer şekilde kritik: çökerse yeni login/refresh/servis token'ı alınamaz (mevcut kısa ömürlü `at` cookie'si süresi dolana kadar çalışmaya devam eder, ama 5 dakika sonra herkes dışarı düşer).

### B. Ops Console / İzleme

**6. Ops ekranı sunuculara SSH/Docker erişimi olmadan container bilgisini nereden alıyor?**
Almıyor aslında — OpsApi'nin Docker'a hiçbir erişimi yok (`docker-compose.yml`'de socket mount edilmemiş, bilinçli bir güvenlik kararı). Bunun yerine host üzerinde (container dışında) `systemd` timer'ı her 5 dakikada bir `tools/services-status.sh` betiğini çalıştırıyor, bu betik gerçek `docker compose ps` çıktısını alıp `/backup/platform-files/.services-status.json` dosyasına yazıyor. OpsApi bu container'a **salt-okunur** mount edilmiş dosyayı okuyor (`STATUS_ROOT=/ops/status-files`, `docker-compose.yml`: `/backup/platform-files:/ops/status-files:ro`). Yani Ops ekranı Docker'a değil, host'un yazdığı bir "anlık fotoğraf" dosyasına bakıyor.

**7. Bu veri ne sıklıkla güncelleniyor, gerçek zamanlı mı?**
Hayır — en fazla 5 dakikada bir (systemd timer periyodu). `/ops/health` alt kısmındaki servis health-check'leri (yonetimapi/flotaapi/keycloak/gateway/postgres) bunun istisnası: onlar OpsApi'nin **o an gerçekten** attığı canlı HTTP/DB isteğinin sonucu, snapshot değil. Yani aynı ekranda hem "5 dakika eski" (container listesi) hem "şu an" (health durumu) veri bir arada gösteriliyor.

**8. Ops ekranı yanlış/eski bilgi gösterebilir mi, ne zaman?**
Evet, iki farklı şekilde yaşandı bu oturumda: (a) `services-status.sh` script'i bir dosya izin çakışması yüzünden defalarca "failed" durumuna düşüp container listesini sürekli boş gösterdi (kök neden bulunup düzeltildi); (b) tarayıcı sekmesi uzun süre arka planda kalırsa, `setInterval` yavaşladığı için ekranda gösterilen "Ölçüm" zamanı gerçek zamandan geride kalabiliyordu (`document.visibilitychange` dinleyicisi eklenerek azaltıldı, ama backend'in kendi 5 dakikalık gecikmesi hâlâ var).

**9. OpsApi'nin Docker socket'ine erişimi neden bilerek engellenmiş?**
Docker socket'i mount etmek, container içindeki bir process'e host üzerinde **root eşdeğeri** yetki vermek demektir (Docker socket üzerinden istenilen container'ı başlatıp durdurabilir, host dosya sistemine host-mount'lu bir container açabilirsiniz). OpsApi internete/kullanıcıya açık bir servis olduğu için, bu riski almamak adına sadece "host'un yazdığı bir dosyayı oku" modeli seçilmiş — çok daha kısıtlı bir saldırı yüzeyi.

**10. Ops ekranındaki health check'ler gerçekten bağlanıp mı bakıyor?**
Evet, gerçek HTTP istekleri: `http://yonetimapi:8080/health`, `http://flotaapi:8080/health`, `http://keycloak:8080/realms/platform`, `https://gateway/health` (mTLS doğrulaması atlanarak, self-signed sertifika için `DangerousAcceptAnyServerCertificateValidator` kullanılıyor — bu Ops'un kendi iç ağdaki bir zaafı, dışa açık değil ama not edilmeye değer), ve Postgres için gerçek bir `SELECT 1` sorgusu. Zaman aşımı 5 saniye.

**11. Backup/disk bilgisi nereden geliyor?**
Ayrı iki host-side script daha var: `tools/disk-check.sh` (saatlik systemd timer) `/backup/platform-files/.disk-status` dosyasını yazıyor; `tools/backup-files01.sh` (günlük timer) `.backup-status` dosyasını ve gerçek backup klasörlerini (`20260701T085033Z` gibi) üretiyor. OpsApi bunları da aynı salt-okunur mount üzerinden okuyor, kendi hiçbir backup işlemi yapmıyor.

**12. Ops'a kim erişebilir, nasıl?**
Keycloak realm'inde `ops.read`/`ops.execute`/`ops.admin` rolleri var; `opsadmin` (read+admin) ve `opsuser01` (sadece read) demo hesapları seed edilmiş. Rolü olmayan biri `/ops/*`'a istek atarsa **404** döner (403 değil) — yani "böyle bir şey var ama sana yetkim yok" bile söylenmiyor, endpoint'in varlığı sızdırılmıyor.

**13. Ops audit'i nerede tutuluyor, kim görebiliyor?**
`ops.audit_events` tablosunda (diğer üç audit tablosundan tamamen ayrı bir şema). Ama Ops Console UI'ında bunu gösteren bir ekran **yok** — sadece `psql` ile elle sorgulanabiliyor (bu, §11'de "bilinen eksik" olarak zaten işaretlendi).

### C. Kimlik / Auth

**14. Şifre nereye gidiyor, kim doğruluyor?**
Kullanıcının girdiği şifre, tarayıcıdan `YonetimApi`'ye (`POST /api/auth/login`) gidiyor, YonetimApi bunu **hiç saklamadan** doğrudan Keycloak'a (`grant_type=password`) iletiyor. Şifreyi gerçekten doğrulayan Keycloak — YonetimApi sadece bir aracı (BFF = Backend-For-Frontend).

**15. Token tarayıcıda nerede saklanıyor?**
Hiçbir JavaScript-erişilebilir yerde. `localStorage`, `sessionStorage`, değişken — hiçbiri kullanılmıyor (kod bazlı doğrulandı: `client/src/api.ts` içinde token'ı tutan tek bir satır yok). Token sadece `HttpOnly` cookie'de (`at`, `rt`) duruyor; tarayıcı bunu her istekte otomatik ekliyor ama JS kodu bu cookie'nin içeriğini **okuyamıyor**.

**16. Biri browser console'dan (`document.cookie`, XSS ile) token'ı çalabilir mi?**
`HttpOnly=true` olduğu için `document.cookie` ile bu cookie'ler hiç görünmez — klasik XSS ile çalınamaz. Ama not: `SameSite=Strict` CSRF'e karşı ayrıca koruma sağlıyor (farklı bir siteden gelen istekte cookie hiç gönderilmiyor).

**17. Access token süresi dolunca (5 dakika) ne olur?**
Frontend, süresi dolmadan ~60 saniye önce arka planda `POST /api/auth/refresh` çağırır (`App.tsx`, `isAccessTokenFresh` kontrolü) — kullanıcı hiçbir şey fark etmez. Eğer bu bir şekilde kaçırılırsa ve `at` süresi dolarsa, YonetimApi 401 döner, frontend bunu yakalayıp refresh dener; o da başarısız olursa login ekranına düşer.

**18. Refresh token (`rt`) da çalınırsa ne olur, tehlikesi nedir?**
`rt` de `HttpOnly`+`Secure`+`SameSite=Strict` olduğu için XSS/CSRF ile çalınması zor, ama bir saldırgan farklı bir yolla (örn. fiziksel erişim, network trafiği düz HTTP'de dinlenirse) `rt`'yi ele geçirirse, `ssoSessionIdleTimeout` (1800 sn = 30 dk) süresince yeni access token alabilir — bu yüzden `Secure` bayrağının HTTPS'te gerçekten aktif olması (kod bunu `X-Forwarded-Proto`/`IsHttps` ile kontrol ediyor) kritik.

**19. YonetimApi kendi kimliğiyle nasıl FileServiceApi'ye giriyor?**
Kullanıcının token'ından tamamen ayrı bir token alıyor: Keycloak'a `client_id=yonetimapi`, `client_secret=yonetimapi-secret-v1` ile `grant_type=client_credentials` isteği atıyor, dönen token'da sabit bir `app_code: "yonetimapi"` claim'i var (Keycloak'ta "hardcoded claim mapper" ile). Bu token bellekte cache'lenip her FileService çağrısında `Authorization: Bearer` header'ına konuyor.

**20. Servis token'ı ile kullanıcı token'ı karışabilir mi?**
Hayır, ikisi farklı Keycloak client'larından (`frontend-test` vs `yonetimapi`/`filoapi`), farklı grant type'larla (`password` vs `client_credentials`) üretiliyor, farklı yerlerde saklanıyor (kullanıcı token'ı cookie'de tarayıcıda, servis token'ı YonetimApi'nin kendi belleğinde) ve farklı hedeflere gidiyor (kullanıcı token'ı YonetimApi'ye, servis token'ı FileServiceApi'ye). Kullanıcının kimliği FileServiceApi'ye ayrı bir header'la (`X-Actor-User-Id`) "bilgi" olarak taşınıyor ama bu **yetki kararı için kullanılmıyor**, sadece audit'te "kim adına yapıldı" diye kaydediliyor.

**21. mTLS ve JWT ikisi de neden var, biri yetmiyor mu?**
Farklı soruları cevaplıyorlar: JWT "bu isteği yapan uygulama (`app_code`) yetkili mi" sorusuna (uygulama katmanı), mTLS "bu TCP bağlantısını gerçekten sertifikası olan bir servis mi açtı" sorusuna (ağ/taşıma katmanı) cevap veriyor. Biri olmasa: sadece JWT olsa, `platform-net` içindeki herhangi bir container (örn. ele geçirilmiş bir container) çalıntı/sahte bir JWT ile FileServiceApi'ye bağlanabilirdi; sadece mTLS olsa, sertifikası olan (yonetimapi/filoapi) her istek app_code kontrolü yapılmadan kabul edilirdi.

**22. Keycloak çökerse ne olur?**
Yeni login/refresh yapılamaz (401), yeni servis token'ı alınamaz — ama YonetimApi'nin zaten önbelleğe aldığı servis token'ı süresi dolana kadar (max 5 dk) çalışmaya devam eder. Var olan kullanıcı oturumları da `at` cookie süresi (5 dk) dolana kadar çalışır, sonra herkes "oturum süresi doldu" ile login ekranına düşer. Keycloak burada gerçek bir tek nokta arızası.

**23. Kullanıcının rolü Keycloak'ta silinirse aktif oturumu anında keser mi?**
Hayır — JWT, imzalandığı anda içindeki `roles` claim'i "donmuş" haldedir; token süresi (5 dk) dolup yeniden login/refresh olana kadar eski roller geçerli kalır. Yani bir rolü geri almanın etkisi en fazla 5 dakika gecikmeli olur — anlık bir "session kill" mekanizması yok.

### D. Yetkilendirme / RBAC

**24. hr001 ile p001 arasındaki fark koda nasıl yansıyor?**
İkisi de aynı `/api/personnel/{id}/files` endpoint'ini çağırıyor; fark tamamen JWT'deki `roles` claim'inde. `hr001`'de `personnel.files.read.all` var → `PermissionService.CanReadAsync` ilk kontrolde `true` döner, hiçbir DB sorgusu bile gerekmez. `p001`'de sadece `personnel.files.read.self` var → kod `ownId == targetId` kontrolüne düşer, sadece kendi ID'sine eşitse geçer.

**25. Bir yönetici (m001) kendi ekibinin dışındaki birine nasıl erişemiyor?**
`personnel.files.read.team` rolü olduğunda kod `yonetim.team_members` tablosuna gerçek bir SQL sorgusu atıyor: `WHERE manager_id = 'M001' AND personnel_id = '<hedef>'`. Sonuç boşsa `false` döner → 403. Bu tablo sadece seed'de M001→P001-P007 gibi sabit ilişkiler tanımlıyor; M001, P008 için sorgu attığında satır bulunamaz.

**26. Kullanıcı kendi personelId'sini JWT'de değiştirip başkasının dosyasına erişebilir mi?**
Hayır — JWT, Keycloak'ın özel anahtarıyla imzalı; içeriği (JWT'nin payload'ı, dolayısıyla `personnel_id` claim'i) değiştirilirse imza doğrulaması (`AddJwtBearer` middleware, JWKS ile) başarısız olur ve istek 401 ile daha en baştan reddedilir — kod hiç çalışmaz.

**27. fleetuser başka bir aracın dosyasına path'i değiştirerek erişebilir mi (`/api/vehicles/test_arac_2/...`)?**
Hayır, `HasVehicleAccess` kontrolü tam bu senaryo için var: JWT'deki `vehicle_id` claim'i (`test_arac_1`) ile URL'deki `{vehicleId}` (`test_arac_2`) birebir eşleşmezse direkt `403 data_scope_denied` döner. `PROJECT_STATUS.md`'de bu senaryo bizzat test edilmiş olarak not edilmiş.

**28. Frontend'deki "buton görünmüyor" ile backend'deki "403" arasındaki fark ne, hangisi güvenlik?**
Sadece backend'deki kontrol güvenliktir. `client/src/auth.ts`'deki `canWrite`/`canVehicleWrite` fonksiyonları sadece UI'da butonu gizliyor/gösteriyor — bunlar tarayıcı tarafında çalıştığı için tarayıcı geliştirici konsolundan bypass edilebilir (buton yoksa da doğrudan `fetch('/api/personnel/P002/cv', {method:'POST',...})` çağrılabilir). Ama bu durumda istek yine YonetimApi'ye gider ve orada gerçek `CanWriteAsync` kontrolü çalışıp 403 döner. Yani frontend kontrolü "kullanıcı deneyimi" içindir, güvenlik sınırı değildir.

**29. Neden `write.team` rolü yok da sadece `write.self`/`write.all` var?**
Bu, kodda gördüğümüz bir tasarım kararı (Keycloak realm'inde böyle tanımlanmış) — yöneticiler ekibinin dosyalarını **görebiliyor** ama **yükleyip/arşivleyemiyor**. Muhtemel sebep: dosya yükleme/arşivleme hassas bir "yazma" işlemi, bunu sadece HR/Admin (`.all`) veya kişinin kendisi (`.self`) yapabilsin, ara kademe yöneticiler yanlışlıkla ekibinin resmi belgesini değiştirmesin diye.

### E. Dosya Depolama

**30. Dosya diskte nerede duruyor, adı ne?**
`/app/storage/export/{domain}/{ilk2hex}/{sonraki2hex}/{tam-guid}.{uzantı}` — örneğin `personnel/a8/f3/a8f3c211-....pdf`. Ad-soyad, TC kimlik no gibi hiçbir kişisel bilgi dosya adında yok; orijinal dosya adı (`redakte.pdf` gibi) sadece veritabanında `original_file_name` alanında tutuluyor.

**31. Aynı isimli iki dosya çakışır mı?**
Hayır — disk üzerindeki gerçek ad her zaman rastgele üretilen bir GUID, kullanıcının verdiği orijinal isim hiç kullanılmıyor. `files.objects.relative_path` üzerinde bir `UNIQUE` kısıtı da var, ama GUID çakışması pratikte imkansıza yakın olduğu için bu sadece ekstra güvence.

**32. Biri `.pdf` uzantılı ama içi resim olan bir dosya yükleyebilir mi?**
Hayır — üç ayrı kontrol var: uzantı allow-list (relation type'a göre), Content-Type header eşleşmesi, ve gerçek dosya baytlarının "magic number"ı (`%PDF` = `25 50 44 46` gibi). Biri PDF uzantısıyla aslında JPEG (`FF D8 FF` ile başlayan) bir dosya yüklerse, magic-byte kontrolü bunu yakalar ve `415 Unsupported Media Type` döner.

**33. Upload sırasında internet/bağlantı kesilirse ne olur?**
Dosya önce `staging/` klasörüne yazılıyor, oradan `export/`'a (kalıcı depoya) sadece **tamamlanmış ve SHA256'sı hesaplanmış** dosya taşınıyor (`File.Move`, atomik rename). Yarım kalan bir upload `staging`'de takılı kalır, `export`'a hiç girmez — yani kalıcı depo her zaman ya "tam dosya" ya "hiç dosya" görür, asla yarım dosya görmez.

**34. Bir dosya "silindi" dendiğinde gerçekten diskten siliniyor mu?**
Hayır — kod incelendiğinde hiçbir "hard delete" (gerçek dosya silme) endpoint'i yok. "Arşivle" dediğinizde sadece veritabanında `status='archived'` (obje) ve `status='revoked'` (referans) olarak işaretleniyor; dosya fiziksel olarak `export/` klasöründe kalmaya devam ediyor. Veritabanı şeması `status='deleted'` diye bir değeri kabul ediyor ama hiçbir kod yolu bunu hiç üretmiyor — yani "silme" aslında implemente edilmemiş bir özellik, sadece "arşivleme" var.

**35. İki kişi aynı anda aynı personelin CV'sini güncellerse (iki upload) ne olur?**
Veritabanı seviyesinde bir trigger (`trg_check_single_primary`) var — aynı anda iki "aktif primary" CV kaydının oluşmasını engelliyor, ikinci isteğin DB insert'i trigger tarafından reddedilir (`single_primary_violation` exception). Uygulama kodu da zaten "yeni CV geldi, eskisini arşivle" mantığını çalıştırıyor; trigger bu mantığın bir hata/race condition durumunda bile bozulmamasını garanti eden ek bir güvenlik ağı.

**36. Dosya indirirken her seferinde bütün dosya mı okunuyor?**
Hayır, iki optimizasyon var: (a) `ETag` (dosyanın SHA256'sı) ile tarayıcı "bu benim önbelleğimdeki ile aynı mı" diye sorabiliyor (`If-None-Match`), aynıysa `304 Not Modified` döner ve disk hiç okunmaz; (b) `Range` header'ı ile sadece dosyanın bir kısmı istenebiliyor (`206 Partial Content`) — büyük bir PDF'in sadece ilk birkaç sayfası gösterilecekse tüm dosyanın indirilmesi gerekmiyor.

**37. NFS (Files-01) çökerse/erişilemez olursa ne olur?**
FileServiceApi disk'e erişemeyeceği için upload/download istekleri `503 storage_unavailable` döner (bu oturumda da bizzat görüldü: dosya varlığı DB'de "active" ama fiziksel olarak okunamıyorsa aynı 503 davranışı tetikleniyor). `/health` endpoint'i de `.probe` dosyasını okuyamayacağı için unhealthy döner, bu da Ops Console'da görünür.

### F. Veritabanı

**38. Kaç tane veritabanı var?**
Fiziksel olarak tek bir PostgreSQL veritabanı (`platformdb`), tek bir container (`server-file-postgres-1`). İçinde 4 mantıksal şema var (`files`, `yonetim`, `filo`, `ops`) — şema, PostgreSQL'de "aynı veritabanı içinde ayrı bir isim alanı" demek, ayrı bir sunucu/instance değil.

**39. `files`, `yonetim`, `filo`, `ops` şemaları neden ayrı, tek şema olsa olmaz mıydı?**
Sahiplik netliği için: her şemanın "sahibi" bir uygulama (`files`→FileServiceApi, `yonetim`→YonetimApi, `filo`→FlotaApi, `ops`→OpsApi). Bu, "hangi tabloyu hangi servis değiştirebilir" sorusunu isim çakışması riski olmadan netleştiriyor — aynı DB'de olsalar da birbirinin tablosuna karışmıyorlar (kod seviyesinde; DB kullanıcı izinleri seviyesinde ayrı bir kısıtlama yok, hepsi aynı `platform` DB kullanıcısıyla bağlanıyor).

**40. Personel tablosu ile dosya tablosu nasıl bağlanıyor?**
Doğrudan bir foreign key ile değil — `files.references` tablosunda `entity_type='personnel'`, `entity_id='P001'` gibi serbest metin alanlarla "gevşek" bağlanıyor. `yonetim.personnel.personnel_id` ile `files.references.entity_id` arasında DB seviyesinde bir FK kısıtlaması **yok** — bu bilinçli bir tasarım, çünkü `files` şeması genel amaçlı (personnel/fleet/ileride başka domain'ler), `yonetim` şemasına doğrudan bağımlı olmasını istemiyorlar.

**41. Bir dosya (obje) silinirse ilişkili audit kaydı ne olur?**
`files.audit_events.file_id` FK'i `ON DELETE SET NULL` ile tanımlı — yani dosya kaydı silinse bile (ki zaten hiç silinmiyor, §34) audit kaydı kalır, sadece `file_id` alanı `NULL` olur. Audit trail hiçbir zaman dosya silinerek kaybolmaz.

**42. "Araç" tablosu var mı?**
Hayır — `filo` şemasında sadece `filo.audit_events` var, araç bilgisini tutan bir tablo (`filo.vehicles` gibi) hiç yaratılmamış. `vehicle_id`, sadece Keycloak'taki kullanıcı attribute'u/JWT claim'i olarak var; PostgreSQL'de bir "araç" varlığı **hiç yok** (§4.3'te detaylı).

**43. Veritabanı şifresi nerede?**
`docker-compose.yml` içinde düz metin: `POSTGRES_PASSWORD: platformpass`, ve bağlantı string'i her üç API'de de `Password=platformpass` şeklinde ortam değişkeninde duruyor. Şifreleme/secret-store kullanılmıyor — bu §11'de "bilinen eksik" (secret rotasyonu) olarak zaten işaretli.

### G. Ağ / Güvenlik

**44. Dışarıdan (internetten/farklı bir makineden) bu sisteme kaç port açık?**
Production compose'da sadece **1 tane**: gateway'in `5090` portu (container içi 443, HTTPS). `ufw` de bunu doğruluyor — api sunucusunda sadece `22` (SSH yönetim) ve `5090` izinli, gerisi varsayılan olarak reddediliyor.

**45. Postgres'e dışarıdan bağlanılabilir mi?**
Hayır — `docker-compose.yml`'de postgres servisinin `ports:` alanı yok, sadece `platform-net` içinden (yani aynı Docker ağındaki diğer container'lardan) erişilebiliyor. Host'un kendisinden bile `psql` ile bağlanmak için `docker exec` ile container'ın içine girmek gerekiyor.

**46. NFS sunucusuna sadece api sunucusu mu bağlanabiliyor, bunu nasıl doğruladık?**
Bu oturumda canlı test edildi: `/etc/exports`'ta `/srv/files` sadece `192.168.64.5` (api sunucusu) IP'sine export edilmiş; ayrıca files01'de `ufw`, `2049/tcp` portunu yine sadece `192.168.64.5`'ten kabul edecek şekilde ayarlı. Bu Mac'ten (`192.168.64.1`, farklı bir IP) gerçek bir `mount_nfs` denemesi yapıldı ve ~4 dakika sonra "Operation timed out" ile başarısız oldu — yani hem export ACL hem firewall seviyesinde çift katmanlı ve gerçekten çalışan bir kısıtlama.

**47. Biri ağ trafiğini dinlerse (Wireshark vb.) şifre görebilir mi?**
Gateway'e giden trafik HTTPS (TLS) ile şifreli olduğu için, gateway'e kadar olan kısımda (tarayıcı → gateway) şifre düz metin görünmez. Gateway'den sonra (gateway → YonetimApi → Keycloak) trafik container ağı içinde ve bazı bağlantılar (örn. YonetimApi→Keycloak `http://keycloak:8080`) **HTTP, TLS'siz** — ama bu trafiğe erişmek için zaten Docker host'una veya container ağına erişim gerekir, dışarıdan dinlenemez (§2'deki network izolasyonu nedeniyle).

**48. HTTPS sertifikası gerçek mi (tarayıcı güvenli der mi)?**
Hayır, self-signed — `certs/generate-certs.sh` kendi CA'sını (`platform-ca`) oluşturup onunla imzalıyor, herhangi bir tarayıcının güvendiği (Let's Encrypt, DigiCert vb.) bir otorite değil. Tarayıcı ilk girişte güvenlik uyarısı gösterir, kullanıcı "yine de devam et" demek zorunda. Gerçek/genel-güvenilir bir sertifika, `PROJECT_STATUS.md`'de "Let's Encrypt + gerçek domain" olarak henüz yapılmamış bir sonraki adım olarak duruyor.

**49. `ufw` (host firewall) olmasaydı ne değişirdi?**
Docker kendi `iptables` kurallarını yönetiyor ve zaten sadece bilerek `ports:` ile işaretlenen container'ları host'a açıyor — yani `ufw` olmasa bile bugünkü fiili durumda ekstra bir port açılmıyordu. Ama `ufw`'nin değeri "gelecekteki hatalara karşı ikinci bir savunma katmanı": biri yanlışlıkla `docker-compose.yml`'e bir `ports:` satırı daha eklerse (örn. debug için postgres'i açarsa), `ufw` olmadan bu direkt dışarıya sızar; `ufw` varken önce firewall kuralına da izin eklemek gerekir — yani "yanlışlıkla açığa çıkma" ihtimali bir kat daha zorlaşır.

**50. `docker-compose.override.yml` production'da yanlışlıkla çalışırsa ne olur?**
`docker compose up` (dosya belirtmeden) çalıştırılırsa Compose bunu otomatik yükler ve Keycloak (`8080`), FileServiceApi (`5205`), YonetimApi (`5076`), FlotaApi (`5077`) portlarını host'a açar — yani gateway'i atlayıp doğrudan bu servislere (ve Keycloak admin/token endpoint'ine) internetten erişilebilir hale gelinir. Bunu önlemek için `setup-server.sh` **her zaman** `-f docker-compose.yml` ile açıkça override'sız çalıştırıyor; bu, "biri elle `docker compose up` yazarsa" senaryosuna karşı hâlâ bir insan-hatası riski.

---

*Bu rapor 2026-07-01/02 tarihli bir Claude Code oturumunda, repodaki gerçek kaynak kod (`YonetimApi/`, `FlotaApi/`, `FileServiceApi/`, `OpsApi/`, `client/src/`, `db/docker-init/*.sql`, `docker-compose.yml`, `nginx/nginx.conf`, `keycloak/realm-platform.json`, `certs/`) doğrudan okunarak hazırlanmıştır. Her iddia dosya:satır referansıyla doğrulanabilir durumdadır.*
