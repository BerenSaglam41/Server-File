# Kanıt: `restart: unless-stopped` + Healthcheck — VM Reboot Sonrası Otomatik Toparlanma

**Tarih:** 2026-07-03
**Kapsam:** Tüm `docker-compose.yml` servisleri
**Durum:** ✅ Tamamlandı, Docker daemon restart ile gerçek senaryo simüle edilerek doğrulandı

---

## Kök Neden Araştırması

Bu oturumda VM'in tekrarlayan, beklenmedik şekilde yeniden başladığı (2 kez, biri git deposunda gerçek
bir disk bozulmasına yol açacak kadar) gözlemlenmişti. Kök neden araştırıldı:

- `journalctl --list-boots` ile VM'in boot geçmişi incelendi — reboot'lar arası boşluklar (dakikalar/saatler)
  ani bir kernel panic/crash desenine uymuyordu (o durumda boşluk neredeyse sıfır olurdu).
- Mac'in kendi güç günlüğü (`pmset -g log`) incelendi: **`'Clamshell Sleep'`** olayları, VM boot
  geçmişindeki reboot aralıklarıyla birebir örtüşüyordu.
- **Sonuç:** Kök neden, host Mac'in kapağının kapanmasıyla uykuya geçmesi — bu, içinde çalışan UTM
  VM'inin düzgün bir kapanma sinyali almadan aniden donmasına neden oluyor. Bu, `docker-compose.yml`'de
  `restart:` politikası olmaması gerçeğiyle birleşince, host uyanınca hiçbir servisin kendiliğinden
  ayağa kalkmamasına yol açıyordu.
- Kullanıcı, bu ani-uyku sorununu kalıcı olarak engellemenin (Mac'i hiç uyutmama) pil/ısınma bedeli
  taşıdığını kabul edip, sadece **belirti düzeyinde** bir düzeltme (restart policy + healthcheck)
  istedi — kök nedene (host uyku davranışı) dokunulmadı, bilinçli bir karar.

## Yapılan Değişiklik

`docker-compose.yml`'deki **9 servisin tamamına** `restart: unless-stopped` eklendi. Ayrıca, daha önce
sadece `postgres`/`keycloak`/`clamav`'da olan healthcheck, kalan 6 servise de eklendi:

- `fileservice`: HTTPS+mTLS gerektirdiği için sadece TCP port kontrolü (`bash`'in `/dev/tcp/` özelliği).
- `yonetimapi`, `flotaapi`, `opsapi`: Tam HTTP `GET /health` + `200` kontrolü (Keycloak'ın mevcut
  healthcheck deseniyle aynı).
- `client`, `gateway`: `wget` ile (nginx:alpine'ın kendi BusyBox `wget`'i) HTTP/HTTPS kontrolü.

`gateway`'in `depends_on` listesi, basit liste formatından `condition: service_healthy` kullanan obje
formatına çevrildi — böylece Gateway, GERÇEKTEN sağlıklı olmayan bir backend'e trafik yönlendirmeye
başlamıyor.

### Bulunan ve Düzeltilen Bug: `CMD-SHELL` vs `bash -c`

İlk denemede `fileservice`/`yonetimapi`/`flotaapi`/`opsapi` healthcheck'leri **hepsi `unhealthy`** kaldı
— bu da bağımlı servislerin (`yonetimapi`, `flotaapi`, `gateway`) hiç başlamamasına yol açtı. Kök neden:
Docker'ın `CMD-SHELL` direktifi komutu `/bin/sh` ile çalıştırıyor, ama `/dev/tcp/` özelliği **sadece
bash'e özgü** bir özellik — .NET `aspnet` imajının (Debian tabanlı) `/bin/sh`'i `dash`'e symlink'li,
`/dev/tcp/`'yi desteklemiyor (`docker inspect` ile "Directory nonexistent" hatası görüldü). Keycloak'ın
mevcut healthcheck'i çalışıyordu çünkü o imajın `/bin/sh`'i muhtemelen bash-uyumlu.

**Düzeltme:** `test: ["CMD-SHELL", "..."]` yerine `test: ["CMD", "bash", "-c", "..."]` kullanıldı — bu,
Docker'ın shell seçimini bypass edip komutu doğrudan `bash` ile çalıştırır.

## Testler

### Test 1 — Normal deploy sonrası tüm servisler healthy mi

```
server-file-flotaapi-1: Up ... (healthy)
server-file-yonetimapi-1: Up ... (healthy)
server-file-fileservice-1: Up ... (healthy)
server-file-opsapi-1: Up ... (healthy)
server-file-gateway-1: Up ... (healthy)
server-file-postgres-1: Up ... (healthy)
server-file-clamav-1: Up ... (healthy)
server-file-keycloak-1: Up ... (healthy)
server-file-client-1: Up ... (healthy)
```
9/9 servis healthy, bağımlılık zinciri (`fileservice` → `yonetimapi`/`flotaapi` → `gateway`) doğru
sırayla, önceki servis gerçekten sağlıklı olmadan bir sonraki başlamadı.

### Test 2 — `docker kill` yanlış bir test yöntemi olduğu bulundu

İlk denemede `fileservice` container'ı `docker kill` ile öldürülüp `restart:unless-stopped`'ın onu geri
getirip getirmediği test edildi — **geri gelmedi** (`Exited (137)` durumunda kaldı). Araştırma sonucu:
`docker kill`/`docker stop` gibi Docker CLI üzerinden **kasıtlı olarak** gönderilen durdurma komutları,
Docker'ın kendisi tarafından "kullanıcı bunu durdurdu, tekrar başlatma" olarak yorumlanıyor — bu,
`unless-stopped` politikasının **tasarım gereği doğru** davranışı. Bu, gerçek VM-reboot senaryosunu
doğru simüle etmiyordu (o senaryoda hiçbir "kill komutu" yok, her şey aniden donuyor).

### Test 3 — Gerçek senaryo: Docker daemon'ın kendisinin yeniden başlatılması

`sudo systemctl restart docker` ile Docker daemon'ın kendisi yeniden başlatıldı — bu, VM reboot sonrası
tam olarak gerçekleşen olayı (daemon sıfırdan başlıyor, hangi container'ların "restart policy'si var"
bilgisini systemd/docker'ın kendi state'inden okuyor) doğru şekilde simüle eder. Sonuç:

```
(15 saniye sonra, hicbir manuel mudahale olmadan)
server-file-flotaapi-1: Up 20 seconds (healthy)
server-file-yonetimapi-1: Up 20 seconds (healthy)
server-file-fileservice-1: Up 20 seconds (healthy)
server-file-opsapi-1: Up 20 seconds (healthy)
server-file-gateway-1: Up 20 seconds (healthy)
server-file-postgres-1: Up 20 seconds (healthy)
server-file-clamav-1: Up 20 seconds (healthy)
server-file-keycloak-1: Up 20 seconds (healthy)
server-file-client-1: Up 20 seconds (healthy)
```
**Tüm 9 servis, hiçbir manuel `docker compose up -d` komutu çalıştırılmadan, otomatik olarak ayağa kalktı
ve sağlıklı duruma geldi.** Bu, artık bir VM reboot'undan sonra platformun kendiliğinden toparlandığını
kanıtlıyor.

### Regresyon

`tools/server-smoke-test.sh` → 23/23 `[OK]`. `tools/server-safe-test-suite.sh` → 36/36 `[OK]`.

## Bilinçli Kalan Sınır

Kök neden (host Mac'in kapak-kapanma uykusu) düzeltilmedi — kullanıcı, bunu engellemenin (Mac'i hiç
uyutmama) pil/ısınma bedelini kabul etmedi, sadece belirti düzeyinde bir önlem (bu değişiklik) istedi.
VM yine ani şekilde donabilir/yeniden başlayabilir, ama artık bu olduğunda platform **kendiliğinden**
toparlanıyor, elle müdahale gerekmiyor. Disk/git bozulması riski (ani donma anında yazma işlemi devam
ediyorsa) teorik olarak hâlâ mevcut, ama bu ayrı bir, kabul edilmiş risk.
