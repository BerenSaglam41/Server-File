# Özet Test Listesi

✅ = Çalışıyor, test edebilirsin · 🟡 = Kod var, hiç canlı test edilmedi · ❌ = Yok / bozuk

## Giriş / Kimlik
- ✅ Personel/HR/Admin/Manager login (`hr001`, `adm001`, `m001-m003`, `p001-p024` / `Demo1234!`)
- ✅ Fleet login (`fleetuser` / `Demo1234!`)
- ✅ Ops login (`opsadmin`/`ops123`, `opsuser01`/`ops456`)
- ✅ Token yenileme (refresh), logout

## Personel Yetkileri
- ✅ hr001/adm001 → tüm personeli görür
- ✅ m001 → kendi ekibini görür, başka ekibi göremez (403)
- ✅ p001 → sadece kendini görür, başkasını göremez (403)
- ✅ m001/p001 → dosya yükleyemez (yazma rolü yok, 403)
- 🟡 fileId ile başkasının dosyasına path üzerinden erişim denemesi (kod engelliyor, canlı denenmedi çünkü test personelinde dosya yoktu)

## Filo (Araç) Yetkileri
- ✅ fleetuser → kendi aracını (`test_arac_1`) görür
- ✅ fleetuser → başka aracı (`test_arac_2`) göremez / yükleyemez (403)
- ❌ fleetuser → kendi aracına gerçek dosya YÜKLEME hiç denenmedi (sadece red senaryoları test edildi)
- ❌ Filo tarafında arşivleme hiç denenmedi

## Dosya İşlemleri (Personel tarafı)
- ✅ CV/dosya indirme (gerçek boyut doğrulandı)
- ✅ Sahte dosya (uzantı/magic-byte uyumsuz) reddi — geçmişte test edilmiş, bu oturumda tekrarlanmadı
- 🟡 Çok büyük dosya (413), yanlış format (415), 304/206 (cache/parça indirme) — kodda var, hiç denenmedi
- ❌ Dosya "silme" diye bir şey yok, sadece arşivleme var (bunu bilerek dene, hard delete arama)

## Ops Ekranı
- ✅ Container listesi, CPU/RAM/uptime, restart sonrası doğru güncelleniyor
- ✅ Disk/backup bilgisi
- ✅ Ops rolü olmayan biri `/ops`'a giremiyor (404)
- 🟡 Ops audit geçmişi ekranda yok (sadece DB'den elle sorgulanabiliyor)

## Altyapı / Güvenlik
- ✅ NFS sadece api sunucusuna açık (başka yerden bağlanılamıyor — bizzat test edildi)
- ✅ ufw her iki sunucuda da aktif, sadece gerekli portlar açık
- ❌ NFS **read-only değil** — API sunucusu aslında dosya sunucusuna (files01) yazabiliyor, plan bunun tersini istiyordu
- ❌ Container çökerse/sunucu yeniden başlarsa kendiliğinden ayağa kalkmıyor (restart policy yok) — bizzat gözlemlendi
- 🟡 NFS koparsa sistem ne yapar (503 dönmesi bekleniyor) — hiç denenmedi

## Cross-check (uygulamalar birbirine karışmasın diye)
- 🟡 YonetimApi'nin filo dosyasına, ya da FlotaApi'nin personel dosyasına erişmeye çalışması (kod engelliyor, hiç denenmedi)

---
Detaylı, kanıt referanslı hali: `DOGRULAMA_KONTROL_LISTESI.md`
