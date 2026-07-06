# Demo Hesaplar

Bu dosya local/demo test ortamı içindir. Üretim bilgisi değildir.

## Ortak Bilgi

- Keycloak realm: `platform`
- Frontend client: `frontend-test`
- Login adresi: `http://127.0.0.1:5173/`
- Gateway: `http://localhost:5090`
- Personel demo kullanıcılarının şifresi: `Demo1234!`

## Yetki Özeti

**Önemli (2026-07-06 itibarıyla):** Aşağıdaki rol adları (`personnel.files.read.all` vb.) hâlâ
kullanılıyor ama bu rollerin **kaynağı artık Keycloak DEĞİL** — `yonetim.role_assignments` PostgreSQL
tablosu. Keycloak sadece kimlik doğrular (bu kullanıcı kim); yetki kararı DB'den gelir. Rol
atamak/kaldırmak için Keycloak admin paneli yerine `tools/manage-role-assignment.sh` kullanılır. Detay:
`MIMARI.md` bölüm 4, `PROJECT_STATUS.md`'nin "Faz C1" bölümleri.

| Kullanıcı tipi | Roller | Kapsam |
|---|---|---|
| HR | `personnel.files.read.all`, `personnel.files.write.all` | Tüm personeli görür, dosya yükler/arşivler |
| Admin | `personnel.files.read.all`, `personnel.files.write.all` | Tüm personeli görür, dosya yükler/arşivler |
| Manager | `personnel.files.read.team` | Kendi kaydı + yönettiği ekip kayıtlarını görür |
| Self | `personnel.files.read.self` | Sadece kendi personel kaydını görür |
| Ops Read | `ops.read` | Ops Konsolu read-only ekranlarını görür |
| Ops Admin | `ops.read`, `ops.admin` | Ops Konsolu tam yetki rolü; V1'de read-only ekranları görür |

## Login Hesapları

| Login | Şifre | Personnel ID | Ad Soyad | E-posta | Departman | Rol |
|---|---|---|---|---|---|---|
| `hr001` | `Demo1234!` | `HR001` | Zeynep Kaya | zeynep.kaya@demo.local | HR | HR |
| `adm001` | `Demo1234!` | `ADM001` | Admin User | admin@demo.local | IT | Admin |
| `m001` | `Demo1234!` | `M001` | Ayse Demir | ayse.demir@demo.local | IT | Manager |
| `m002` | `Demo1234!` | `M002` | Mehmet Arslan | mehmet.arslan@demo.local | Finance | Manager |
| `m003` | `Demo1234!` | `M003` | Elif Sahin | elif.sahin@demo.local | Operations | Manager |
| `p001` | `Demo1234!` | `P001` | Ahmet Yilmaz | ahmet.yilmaz@demo.local | IT | Self |
| `p002` | `Demo1234!` | `P002` | Can Aydin | can.aydin@demo.local | IT | Self |
| `p003` | `Demo1234!` | `P003` | Deniz Koc | deniz.koc@demo.local | IT | Self |
| `p004` | `Demo1234!` | `P004` | Ece Polat | ece.polat@demo.local | IT | Self |
| `p005` | `Demo1234!` | `P005` | Burak Celik | burak.celik@demo.local | IT | Self |
| `p006` | `Demo1234!` | `P006` | Selin Kurt | selin.kurt@demo.local | IT | Self |
| `p007` | `Demo1234!` | `P007` | Onur Aslan | onur.aslan@demo.local | IT | Self |
| `p008` | `Demo1234!` | `P008` | Melis Ozturk | melis.ozturk@demo.local | Finance | Self |
| `p009` | `Demo1234!` | `P009` | Emre Yildiz | emre.yildiz@demo.local | Finance | Self |
| `p010` | `Demo1234!` | `P010` | Naz Kaplan | naz.kaplan@demo.local | Finance | Self |
| `p011` | `Demo1234!` | `P011` | Kaan Ergin | kaan.ergin@demo.local | Finance | Self |
| `p012` | `Demo1234!` | `P012` | Aylin Bozkurt | aylin.bozkurt@demo.local | Finance | Self |
| `p013` | `Demo1234!` | `P013` | Umut Aksoy | umut.aksoy@demo.local | Finance | Self |
| `p014` | `Demo1234!` | `P014` | Buse Sen | buse.sen@demo.local | Finance | Self |
| `p015` | `Demo1234!` | `P015` | Mert Kara | mert.kara@demo.local | Operations | Self |
| `p016` | `Demo1234!` | `P016` | Derya Gunes | derya.gunes@demo.local | Operations | Self |
| `p017` | `Demo1234!` | `P017` | Tuna Eren | tuna.eren@demo.local | Operations | Self |
| `p018` | `Demo1234!` | `P018` | Esra Bilir | esra.bilir@demo.local | Operations | Self |
| `p019` | `Demo1234!` | `P019` | Kerem Tas | kerem.tas@demo.local | Operations | Self |
| `p020` | `Demo1234!` | `P020` | Nil Ozdemir | nil.ozdemir@demo.local | Operations | Self |
| `p021` | `Demo1234!` | `P021` | Alp Keskin | alp.keskin@demo.local | Operations | Self |
| `p022` | `Demo1234!` | `P022` | Seda Acar | seda.acar@demo.local | Sales | Self |
| `p023` | `Demo1234!` | `P023` | Okan Durmaz | okan.durmaz@demo.local | Sales | Self |
| `p024` | `Demo1234!` | `P024` | Irem Uslu | irem.uslu@demo.local | Sales | Self |

## Ops Hesapları

| Login | Şifre | Roller | Kapsam |
|---|---|---|---|
| `opsadmin` | `ops123` | `ops.read`, `ops.admin` | Ops dashboard ve V1 read-only kontroller |
| `opsuser01` | `ops456` | `ops.read` | Ops dashboard ve V1 read-only kontroller |

## Ekip İlişkileri

| Manager login | Manager personel | Görebildiği ekip |
|---|---|---|
| `m001` | `M001` Ayse Demir | `P001` Ahmet Yilmaz, `P002` Can Aydin, `P003` Deniz Koc, `P004` Ece Polat, `P005` Burak Celik, `P006` Selin Kurt, `P007` Onur Aslan |
| `m002` | `M002` Mehmet Arslan | `P008` Melis Ozturk, `P009` Emre Yildiz, `P010` Naz Kaplan, `P011` Kaan Ergin, `P012` Aylin Bozkurt, `P013` Umut Aksoy, `P014` Buse Sen |
| `m003` | `M003` Elif Sahin | `P015` Mert Kara, `P016` Derya Gunes, `P017` Tuna Eren, `P018` Esra Bilir, `P019` Kerem Tas, `P020` Nil Ozdemir, `P021` Alp Keskin |

## Hızlı Test Komutları

```bash
BASE="http://localhost:8080/realms/platform/protocol/openid-connect/token"

HR_TOKEN=$(curl -s "$BASE" \
  -d grant_type=password \
  -d client_id=frontend-test \
  -d username=hr001 \
  -d password=Demo1234! \
  | jq -r .access_token)

curl -H "Authorization: Bearer $HR_TOKEN" \
  "http://localhost:5090/api/personnel?search="

curl -X POST \
  -H "Authorization: Bearer $HR_TOKEN" \
  -F "file=@test.pdf" \
  "http://localhost:5090/api/personnel/P001/cv"

curl -X POST \
  -H "Authorization: Bearer $HR_TOKEN" \
  "http://localhost:5090/api/personnel/P001/cv/archive"
```

## Beklenen Davranış

- `hr001` ve `adm001`: 29 personel görür, upload ve archive yapabilir.
- `m001`: kendi kaydı + IT ekibindeki `P001-P007` kayıtlarını görür, upload yapamaz.
- `m002`: kendi kaydı + Finance ekibindeki `P008-P014` kayıtlarını görür, upload yapamaz.
- `m003`: kendi kaydı + Operations ekibindeki `P015-P021` kayıtlarını görür, upload yapamaz.
- `p001` ... `p024`: sadece kendi kaydını görür, upload yapamaz.
