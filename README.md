# Dosya Sistemi Projesi

Personel ve filo (araç) dosyalarını merkezi, denetlenebilir bir dosya servisi üzerinden yöneten bir
platform. İki uygulama (personel yönetimi, filo yönetimi) aynı dosya servisini paylaşır; her dosya
tek bir yerde saklanır, kimin hangi dosyaya eriştiği/erişebileceği merkezi olarak denetlenir.

## Servisler

| Servis | Görev |
|---|---|
| `gateway` (nginx) | Dışarıya açık tek kapı — TLS, reverse proxy, rate limiting, ticket/public dosya servisi |
| `yonetimapi` | Personel yönetimi API'si (.NET) — kullanıcı girişi (BFF), personel/dosya işlemleri |
| `flotaapi` | Filo (araç) yönetimi API'si (.NET) |
| `fileserviceapi` | Merkezi dosya servisi (.NET) — tüm dosya create/read/archive işlemleri, tek yazma noktası |
| `opsapi` | Salt-okunur operasyon/gözlem API'si (.NET) — sistem sağlığı, servis durumu, yedekleme durumu |
| `client` | React SPA (personel/filo arayüzü) |
| `keycloak` | Kimlik doğrulama (OIDC) — "bu kişi kim?" |
| `postgres` | Tüm uygulama verisi + `yonetim.role_assignments` (yetki kaynağı — bkz. aşağıda) |
| `clamav` | Yüklenen her dosya için fail-closed virüs taraması |
| `FilesPublisher` (Files-01, ayrı VM) | Dosya içeriğinin fiziksel diske yazıldığı tek nokta (mTLS korumalı) |

İki fiziksel sunucu (UTM VM) kullanılıyor: **api-server** (yukarıdaki tüm docker-compose servisleri) ve
**files01** (NFS depolama + FilesPublisher). Detay: `KURULUM.md`.

## Hızlı Başlangıç

Sıfırdan kurulum için `KURULUM.md`'yi takip edin (iki sunucu sırasıyla: files-01 → api-server).
Zaten kurulu bir ortamda servisleri ayağa kaldırmak için:

```bash
docker compose up -d
bash tools/server-smoke-test.sh
```

## Yetkilendirme Modeli (önemli, sık yanlış anlaşılan nokta)

Keycloak **sadece kimlik doğrular** ("bu kişi kim?"). **"Bu kişi ne yapabilir?"** sorusunun cevabı
Keycloak rollerinden DEĞİL, `yonetim.role_assignments` PostgreSQL tablosundan gelir — bu, kademeli bir
göçle (Faz C1) yapıldı, detay `PROJECT_STATUS.md`'de. Rol atamak/kaldırmak için Keycloak admin
paneline değil, `tools/manage-role-assignment.sh`'a bakın.

## Dokümantasyon Haritası

- **`PROJECT_STATUS.md`** — projenin GÜNCEL durumu, tamamlanan her işin ne/neden/nasıl test edildiği.
  Herhangi bir işe başlamadan önce buraya bakın.
- **`MIMARI.md`** — tam teknik referans: auth, storage, RBAC, audit, dosya akışı. Kod ile birlikte
  okunacak şekilde tasarlandı.
- **`KURULUM.md`** — sıfırdan kurulum adımları (iki sunucu, sertifikalar, migration'lar, servisler).
- **`DEMO_HESAPLAR.md`** — test/demo kullanıcı hesapları ve yetkileri.
- **`runbooks/`** — operasyonel prosedürler: NFS kurulumu (`files01-nfs-setup.md`), production
  sertleştirme (`production-hardening.md`), gözlemlenebilirlik planı (`observability-plan.md`).
- **`proof/`** — her önemli özellik/güvenlik değişikliği için gerçek test kanıtları (canlı komutlar
  ve çıktılar) — "çalışıyor" iddialarının arkasındaki kanıt burada.
- **`PROJE/`** — hedef/gelecek mimari üzerine tartışma dokümanları (mevcut implementasyonun DEĞİL,
  olası genişleme yönlerinin belgeleri).
- **`tools/`** — deploy, test ve operasyon script'leri (`server-smoke-test.sh`,
  `server-safe-test-suite.sh`, `manage-role-assignment.sh`, yedekleme araçları).

## Test

```bash
bash tools/server-smoke-test.sh        # hızlı, temel doğrulama
bash tools/server-safe-test-suite.sh   # kapsamlı, üretici-tüketici olmayan güvenli test seti
```

Her iki script de canlı bir ortama karşı gerçek HTTP istekleri atar; mock/stub kullanmaz.
