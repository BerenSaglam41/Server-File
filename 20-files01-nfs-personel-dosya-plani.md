# Files-01 NFS dosya servisi ve personel dosya tasima plani hazirlanmali

- Milestone: `Core Services`
- Onerilen kolon: `To Do`
- Onerilen label'lar: `type/chore`, `area/file-service`, `area/security`, `priority/p1`

## Aciklama

Files-01, YonetimAPI tarafindan personel fotograf ve CV dosyalari icin kullanilacak dosya servisidir. Dosya erisimi istemciye dogrudan acilmadan API uzerinden yetkilendirilmelidir.

## Kapsam

- NFSv4.2 read-only export
- File-Service runtime mount
- `personnel/<shard1>/<shard2>/<file_id>` dizin modeli
- Stable dosya anahtari
- Personel fotograf/CV tasimasi
- Backup ve kapasite takibi

## Yapilacaklar

- [x] NFS export modeli ve File-Service runtime allowlist karari yazilacak.
- [x] Dosya dizin yapisi ve sahiplik/izin modeli belirlenecek.
- [x] Fotograf ve CV dosya adlandirma standardi karara baglanacak.
- [x] Legacy dosyalarin stable key ile yeniden adlandirilma plani hazirlanacak.
- [x] Backup ve kapasite takibi yazilacak.
- [x] API dosya erisimi icin permission/data-scope bagimliligi dokumante edilecek.

## 2026-06-25 Plan Durumu

Files-01 icin secretsiz NFSv4.2 read-only tasarimi ve kurulum runbook'u hazirlandi.

- Tasarim: [docs/file-service/files01-nfs-model.md](../../docs/file-service/files01-nfs-model.md)
- Merkezi katalog: [docs/file-service/file-catalog-model.md](../../docs/file-service/file-catalog-model.md)
- API sozlesmesi: [docs/file-service/file-service-api-contract.md](../../docs/file-service/file-service-api-contract.md)
- Runbook: [docs/runbooks/files01-nfs-setup.md](../../docs/runbooks/files01-nfs-setup.md)
- File-Service runtime host'u -> Files-01 TCP/2049 allowlist'i nihai VLAN/IP sonrasinda #3 altinda dogrulanacak.
- Canli mount, read-only davranis, API scope testi ve backup restore probe sonuclari #23/#9 dogrulama kapilarina aktarilacak.

## 2026-06-26 Karar Notu

- Personel dosyalari icin `photos/cv/media` alt klasorleri yerine `personnel/<shard1>/<shard2>/<file_id>` modeli secildi.
- Dosya tipi, hassasiyet, resmi evrak durumu, retention ve permission bilgileri DB-01 uzerindeki merkezi File Catalog (`files.*`) metadata'sinda tutulacak.
- Files-01 storage-only kalacak; kendi uzerinde ayri API veya PostgreSQL servisi calistirilmayacak.
- YonetimAPI ilk tuketici olacak, fakat dosya metadata'sinin sahibi olmayacak.
- Ikinci uygulama file storage tuketmeye baslamadan once File-Service API veya esdeger merkezi platform servis katmani devreye alinacak.
- V1 erisim modeli `Client -> Uygulama API -> File-Service API -> Files-01` olacak; frontend/mobil dogrudan File-Service API'ye gitmeyecek.

## Kabul Kriterleri

- Files-01 dogrudan istemciye acilmayan bir modelle tasarlanmis olmalidir.
- File-Service runtime mount ve NFS RO export kurallari yazilmis olmalidir.
- Dosya tasima ve rollback plani hazir olmalidir.

## Test ve Dogrulama Notlari

File-Service runtime host'u uzerinden mount, read-only davranis, dosya okuma, NFS down senaryosu ve backup restore testleri kaydedilmelidir.

## Riskler

Dogru yetki modeli olmadan dosya sunucusunu acmak PII sizintisi yaratir. Stable key olmadan ad-soyad bazli dosya adlari cakisma veya isim degisikligiyle kirilir.

## Geri Donus Plani

Dosya tasimasi oncesi kaynak dosyalar korunmali, basarisiz tasimada eski dosya yolu veya eski servis gecici sureyle kullanilabilmelidir.

## Dokumantasyon

[docs/file-service](../../docs/file-service) ve [docs/migration/yonetimapi-first-application-plan.md](../../docs/migration/yonetimapi-first-application-plan.md) guncellenmelidir.

## Done Definition

NFS modeli, dizin/izin yapisi, dosya tasima plani, backup/restore ve API yetki bagimliliklari tamamlanmis olmalidir. Uygulama onceki canli dosya kaynagina bagimli kalmaya devam ediyorsa, Files-01 canliya gecis karari mount ve API smoke test kanitlari geldikten sonra verilir.
