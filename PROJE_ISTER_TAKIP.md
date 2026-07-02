# PROJE/ Klasöründeki 5 Dosyanın İsterleri — Tik Takibi

✓ = gerçek kodda var ve doğrulandı · ✗ = yok / plana aykırı / gerçek değil

---

## 1) `20-files01-nfs-personel-dosya-plani.md`

**Yapılacaklar:**
- ✓ NFS export modeli ve File-Service runtime allowlist kararı
- ✓ Dosya dizin yapısı ve sahiplik/izin modeli
- ✓ Fotoğraf/CV dosya adlandırma standardı (`{domain}/{sh1}/{sh2}/{guid}.{ext}`)
- ✓ Legacy dosya taşıma planı (`tools/migrate-legacy-files.py` var, hiç kullanılmadı)
- ✓ Backup ve kapasite takibi
- ✓ API erişimi için permission/data-scope bağımlılığı dokümantasyonu

**Kabul Kriterleri:**
- ✓ Files-01 doğrudan istemciye açılmıyor
- ✗ NFS **RO export** kuralı — export gerçekte **rw**
- ✓ Dosya taşıma/rollback planı hazır (kullanılmamış olsa da var)

---

## 2) `file-catalog-model.md`

**Önerilen şema (`files.objects/references/app_policies/audit_events`):**
- ✓ Dördü de DB'de birebir var

**Onboarding Kapısı (2. uygulama şartları):**
- ✓ app_code + izinli domain/tip listesi
- ✓ create/read/archive policy
- ✓ entity reference modeli
- ✓ audit sorumluluğu
- ✓ API endpoint sözleşmesi
- ✓ quota/max file size
- ✗ İstenen test senaryolarının hepsi (create/read/denied/scope-miss/archived/missing-binary) — sadece read + denied test edildi, diğerleri hiç denenmedi

---

## 3) `file-service-api-contract.md`

**V1 Kabul Kriterleri:**
- ✓ Uygulamalar Files-01'e doğrudan gitmiyor
- ✓ Uygulamalar `files.*` tablolarına doğrudan yazmıyor
- ✓ Servis token'ı olmadan cevap vermiyor
- ✓ App policy izin vermeyince 403 (mekanizma var; cross-domain hiç canlı denenmedi)
- ✗ Scope dışı istek **404** olmalıydı — gerçekte **403** dönüyor
- ✓ Stream endpoint ETag/Range/Content-Type/Content-Disposition destekliyor
- ✓ `files.audit_events` her sonuç için yazıyor

**Endpoint Taslağı (resolve/stream/metadata/create/archive):**
- ✓ Beşi de birebir implemente edilmiş

---

## 4) `file-service-intern-brief.md`

**Teslim çıktıları:**
- ✓ API sözleşmesi
- ✓ DB tablo taslağı
- ✓ Auth/policy (mTLS + servis JWT + app_code policy)
- ✓ Prototype API (istenenden fazlası yapılmış)
- ✓ Stream/güvenlik (range, etag, magic-byte, path traversal, max size)

**Sunucu tarafı kalan kontroller:**
- ✓ NFS port erişimi
- ✓ Export sadece runtime IP'sine açık
- ✗ Mount read-only
- ✗ **Runtime yazma/silme yapamıyor mu** — yapabiliyor, kritik sapma
- ✗ NFS-down health check testi

**Yapılmayacaklar listesine uyum:**
- ✓ Frontend Files-01/NFS'e bağlanmıyor
- ✓ Frontend V1'de FileServiceApi'ye doğrudan gitmiyor
- ✓ Uygulamalar `files.*`'a yazmıyor
- ✓ Path'e PII konulmamış
- ✗ **"Gerçek secret/IP/PII commit etmeyin"** — ihlal edilmiş: gerçek client secret'lar (`yonetimapi-secret-v1` vb.), demo şifreler ve gerçek iç ağ IP'leri (`192.168.64.x`) repo/dokümanlarda düz metin duruyor
- ✓ Hard delete tasarlanmamış (yok)

---

## 5) `files01-nfs-model.md`

**Doğrulama Kapıları:**
- ✓ NFS port erişilebilir
- ✗ Mount **read-only**
- ✓ Read probe
- ✗ **Write denial**
- ✓ API scope (yetkili erişir)
- ✓ API scope miss (varlık sızmıyor)
- ✓ Backup restore (hash kontrolü scriptte var)

**Sahiplik/izin modeli (planlanan vs gerçek):**
- ✗ Plan: `export` → `root:files-nfs-ro`, salt-okunur grup. Gerçek: `export` → `files-writer:files-publishers`, **yazılabilir**

---

## Bu 5 Dosyanın Dışında, Sistemde Gerçekten Olan Diğer Şeyler

- ✓ Keycloak login + HttpOnly cookie (BFF) + refresh
- ✓ RBAC (read/write × self/team/all) — personel tarafı
- ✓ Fleet `vehicle_id` claim modeli (bu oturumda bulunup düzeltildi)
- ✓ mTLS (YonetimApi/FlotaApi ↔ FileServiceApi)
- ✓ HTTPS Gateway (ama self-signed sertifika, gerçek/güvenilir değil)
- ✓ Ops Console + OpsApi (container/disk/backup izleme)
- ✓ ufw firewall (iki sunucuda da)
- ✗ Container restart policy (VM/sunucu yeniden başlayınca hiçbir şey kendiliğinden ayağa kalkmıyor)
- ✓ Backup/restore systemd timer'ları
