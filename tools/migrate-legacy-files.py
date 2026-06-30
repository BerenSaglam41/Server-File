#!/usr/bin/env python3
"""
Legacy dosya migration aracı.

files01-nfs-model.md Migration Manifesti şemasını uygular.
Kaynak dizinden dosyaları okur, shard path yapısına kopyalar,
CSV manifest yazar ve DB'ye (files.objects + files.references) kayıt atar.

── ÖNEMLİ: YAZMA / NFS SINIRI ─────────────────────────────────────────────
MD mimarisinde Files-01 NFS export READ-ONLY mount edilir.
Runtime host üzerinden yazma, silme veya rename beklenmez.

Bu araç Files-01 üzerinde doğrudan (NFS üzerinden değil) çalıştırılmalıdır:
  - Production: ssh files-01 → python3 migrate-legacy-files.py --export-path /srv/files/export ...
  - Test: export path local yazılabilir dizin (test-storage/export)

NFS read-only mount üzerinden (ör. /mnt/platform-files) çalıştırmayın.
─────────────────────────────────────────────────────────────────────────────

Kullanım:
  # Tüm dosyalar tek entity'ye ait (örn: bir personelin CV'leri)
  python3 migrate-legacy-files.py \\
    --source /path/to/legacy/files \\
    --export-path /srv/files/export \\
    --domain personnel --entity-type personnel \\
    --relation-type cv --app-code yonetimapi \\
    --entity-id <personnel_id> \\
    --db-conn "host=localhost dbname=platformdb user=..." \\
    [--dry-run]

  # Her dosya farklı entity'ye ait → eşleştirme CSV'si gerekir
  python3 migrate-legacy-files.py \\
    --source /path/to/legacy/files \\
    --export-path /srv/files/export \\
    --domain personnel --entity-type personnel \\
    --relation-type cv --app-code yonetimapi \\
    --entity-id-map mapping.csv \\
    [--dry-run]

  mapping.csv formatı (başlık satırı zorunlu):
    source_filename,entity_id
    dosya1.pdf,emp_001
    dosya2.pdf,emp_002

--dry-run: dosya kopyalamaz, DB'ye yazmaz; sadece kontroller + manifest üretir.

Güvenlik notları:
  - source_alias manifest'e varsayılan olarak SHA256(dosya_adı)[:16] olarak yazılır (PII koruması).
  - --include-source-names ile gerçek dosya adı yazılır; yalnız PII içermediğinden
    emin olduğunuzda kullanın.
  - --entity-id verilmezse --entity-id-map zorunludur; dosya adından entity_id üretilmez.
"""

import argparse
import csv
import hashlib
import os
import shutil
import sys
import uuid
from datetime import datetime, timezone

ALLOWED_EXTENSIONS = {"pdf", "jpg", "jpeg", "png", "webp"}

CONTENT_TYPE_MAP = {
    "pdf":  "application/pdf",
    "jpg":  "image/jpeg",
    "jpeg": "image/jpeg",
    "png":  "image/png",
    "webp": "image/webp",
}

# (byte_offset, expected_bytes) çiftleri; hepsi eşleşmeli
MAGIC_BYTES: dict[str, list[tuple[int, bytes]]] = {
    "pdf":  [(0, b"%PDF")],
    "jpg":  [(0, b"\xFF\xD8\xFF")],
    "jpeg": [(0, b"\xFF\xD8\xFF")],
    "png":  [(0, b"\x89PNG\r\n\x1a\n")],
    "webp": [(0, b"RIFF"), (8, b"WEBP")],
}

MANIFEST_COLUMNS = [
    "file_id", "entity_type", "file_type", "target_relative_path",
    "extension", "size_bytes", "sha256", "source_alias",
    "migration_status", "checked_at", "notes",
]


# ── Yardımcı fonksiyonlar ────────────────────────────────────────────────────

def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def check_magic(path: str, ext: str) -> bool:
    checks = MAGIC_BYTES.get(ext, [])
    if not checks:
        return True
    with open(path, "rb") as f:
        header = f.read(12)
    return all(header[off: off + len(sig)] == sig for off, sig in checks)


def shard_path(file_id: str, ext: str) -> str:
    return f"{file_id[:2]}/{file_id[2:4]}/{file_id}.{ext}"


def safe_alias(filename: str) -> str:
    """Dosya adının PII içerip içermediği bilinmediğinden hash'liyoruz."""
    return hashlib.sha256(filename.encode()).hexdigest()[:16]


def load_entity_map(map_path: str) -> dict[str, str]:
    result = {}
    with open(map_path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        if not {"source_filename", "entity_id"}.issubset(reader.fieldnames or []):
            print("HATA: --entity-id-map CSV'sinde 'source_filename' ve 'entity_id' sütunları olmalı.", file=sys.stderr)
            sys.exit(1)
        for row in reader:
            result[row["source_filename"].strip()] = row["entity_id"].strip()
    return result


def collect_files(source_dir: str):
    for root, _, files in os.walk(source_dir):
        for name in sorted(files):
            if name.startswith("."):
                continue
            yield os.path.join(root, name), name


# ── Ana migration akışı ──────────────────────────────────────────────────────

def migrate(args):
    # psycopg2 kontrolü
    psycopg2 = None
    if not args.dry_run:
        try:
            import psycopg2 as _pg
            psycopg2 = _pg
        except ImportError:
            print("HATA: psycopg2 kurulu değil. 'pip install psycopg2-binary' çalıştır.", file=sys.stderr)
            sys.exit(1)

    # entity_id kaynağını belirle
    entity_map: dict[str, str] = {}
    if args.entity_id_map:
        entity_map = load_entity_map(args.entity_id_map)
    elif not args.entity_id:
        print("HATA: --entity-id veya --entity-id-map gereklidir.", file=sys.stderr)
        sys.exit(1)

    # NFS read-only mount kontrolü: hedef dizin yazılabilir mi?
    if not args.dry_run:
        probe = os.path.join(args.export_path, ".write-probe")
        try:
            with open(probe, "w") as f:
                f.write("probe")
            os.remove(probe)
        except OSError as e:
            print(f"HATA: --export-path yazılabilir değil: {args.export_path}", file=sys.stderr)
            print(f"  Neden: {e}", file=sys.stderr)
            print("  Bu araç Files-01 üzerinde doğrudan çalıştırılmalıdır.", file=sys.stderr)
            print("  NFS read-only mount üzerinden çalıştırmayın.", file=sys.stderr)
            sys.exit(1)

    conn = None
    if not args.dry_run:
        conn = psycopg2.connect(args.db_conn)
        conn.autocommit = False

    manifest_path = args.manifest or f"migration-manifest-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S')}.csv"
    rows: list[dict] = []
    ok = skipped = failed = 0

    all_files = list(collect_files(args.source))
    print(f"Kaynak dizin: {args.source}")
    print(f"Export path : {args.export_path}")
    print(f"Bulunan dosya adayı: {len(all_files)}")

    for src_path, filename in all_files:
        checked_at = datetime.now(timezone.utc).isoformat()
        alias = filename if args.include_source_names else safe_alias(filename)

        # ── 1. Uzantı kontrolü ───────────────────────────────────────────────
        ext = filename.rsplit(".", 1)[-1].lower() if "." in filename else ""
        if ext not in ALLOWED_EXTENSIONS:
            skipped += 1
            rows.append(_row("", args, ext, "", 0, "", alias,
                             "skipped", checked_at, f"uzantı izinsiz: .{ext}"))
            continue

        file_id = str(uuid.uuid4())
        rel_path = f"{args.domain}/{shard_path(file_id, ext)}"
        target_abs = os.path.join(args.export_path, rel_path)
        size_bytes = os.path.getsize(src_path)

        # ── 2. Magic-byte kontrolü ───────────────────────────────────────────
        if not check_magic(src_path, ext):
            skipped += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, "", alias,
                             "skipped", checked_at, "magic-byte uyuşmuyor"))
            continue

        # ── 3. SHA256 hesapla ────────────────────────────────────────────────
        try:
            digest = sha256_of(src_path)
        except Exception as e:
            failed += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, "", alias,
                             "failed", checked_at, f"sha256 hatası: {e}"))
            continue

        if args.dry_run:
            ok += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "pending", checked_at, "dry-run"))
            continue

        # ── 4. Duplicate hash kontrolü (DB) ─────────────────────────────────
        try:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT file_id FROM files.objects WHERE sha256 = %s AND status = 'active'",
                    (digest,),
                )
                dup = cur.fetchone()
        except Exception as e:
            failed += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "failed", checked_at, f"duplicate kontrol hatası: {e}"))
            continue

        if dup:
            skipped += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "skipped", checked_at, f"duplicate hash, mevcut file_id: {dup[0]}"))
            continue

        # ── 5. entity_id belirle ─────────────────────────────────────────────
        entity_id = args.entity_id or entity_map.get(filename)
        if not entity_id:
            skipped += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "skipped", checked_at, f"entity_id eşleşmesi bulunamadı: {alias}"))
            continue

        # ── 6. Dosyayı kopyala ───────────────────────────────────────────────
        copied = False
        try:
            os.makedirs(os.path.dirname(target_abs), exist_ok=True)
            shutil.copy2(src_path, target_abs)
            actual = sha256_of(target_abs)
            if actual != digest:
                raise ValueError(f"kopya hash uyuşmadı (beklenen={digest}, gerçek={actual})")
            copied = True
        except Exception as e:
            if os.path.exists(target_abs):
                os.remove(target_abs)
            failed += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "failed", checked_at, f"kopyalama hatası: {e}"))
            continue

        # ── 7. DB işlemleri (transaction) ───────────────────────────────────
        content_type = CONTENT_TYPE_MAP[ext]
        try:
            with conn.cursor() as cur:
                # Varsa aktif eski referansı revoke + archive et
                cur.execute("""
                    SELECT r.file_id FROM files.references r
                    WHERE r.app_code = %s AND r.entity_type = %s AND r.entity_id = %s
                      AND r.relation_type = %s AND r.is_primary = true AND r.status = 'active'
                """, (args.app_code, args.entity_type, entity_id, args.relation_type))
                existing = cur.fetchone()
                if existing:
                    old_id = existing[0]
                    cur.execute(
                        "UPDATE files.references SET status = 'revoked' WHERE file_id = %s AND status = 'active'",
                        (old_id,),
                    )
                    cur.execute(
                        "UPDATE files.objects SET status = 'archived' WHERE file_id = %s",
                        (old_id,),
                    )

                cur.execute("""
                    INSERT INTO files.objects
                      (file_id, domain, relative_path, content_type, extension,
                       size_bytes, sha256, classification, status,
                       created_by_app, created_by_user, created_at)
                    VALUES (%s,%s,%s,%s,%s,%s,%s,%s,'active',%s,'migration',now())
                """, (file_id, args.domain, rel_path, content_type, ext,
                      size_bytes, digest, args.classification, args.app_code))

                cur.execute("""
                    INSERT INTO files.references
                      (file_id, app_code, entity_type, entity_id,
                       relation_type, is_primary, status)
                    VALUES (%s,%s,%s,%s,%s,true,'active')
                """, (file_id, args.app_code, args.entity_type, entity_id, args.relation_type))

            conn.commit()
            ok += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "verified", checked_at, ""))
        except Exception as e:
            conn.rollback()
            # Dosyayı geri al — DB tutarsızlığını önle
            if copied and os.path.exists(target_abs):
                os.remove(target_abs)
            failed += 1
            rows.append(_row(file_id, args, ext, rel_path, size_bytes, digest, alias,
                             "failed", checked_at, f"DB hatası (dosya geri alındı): {e}"))

    if conn:
        conn.close()

    with open(manifest_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=MANIFEST_COLUMNS)
        writer.writeheader()
        writer.writerows(rows)

    total = ok + skipped + failed
    print(f"\nManifest yazıldı : {manifest_path}")
    print(f"  Toplam işlenen : {total}")
    print(f"  Başarılı        : {ok}")
    print(f"  Atlandı         : {skipped}  (uzantı/magic/duplicate/entity_id eksik)")
    print(f"  Başarısız       : {failed}")
    if args.dry_run:
        print("  (dry-run — dosya kopyalanmadı, DB'ye yazılmadı)")
    if not args.include_source_names:
        print("  NOT: source_alias sütunu SHA256(dosya_adı)[:16] — gerçek ad gizlendi.")


def _row(file_id, args, ext, rel_path, size_bytes, digest, alias, status, checked_at, notes):
    return {
        "file_id": file_id,
        "entity_type": args.entity_type,
        "file_type": args.relation_type,
        "target_relative_path": rel_path,
        "extension": ext,
        "size_bytes": size_bytes,
        "sha256": digest,
        "source_alias": alias,
        "migration_status": status,
        "checked_at": checked_at,
        "notes": notes,
    }


# ── CLI ──────────────────────────────────────────────────────────────────────

def main():
    p = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("--source", required=True,
                   help="Legacy dosyaların bulunduğu kaynak dizin")
    p.add_argument("--export-path", required=True, dest="export_path",
                   help="Files-01 export dizini (doğrudan yazılabilir erişim gerekir; "
                        "production'da /srv/files/export, test'te test-storage/export). "
                        "NFS read-only mount'a YÖNELTMEYİN.")
    p.add_argument("--domain", required=True,
                   help="files.objects.domain (örn: personnel)")
    p.add_argument("--entity-type", required=True, dest="entity_type",
                   help="files.references.entity_type")
    p.add_argument("--relation-type", required=True, dest="relation_type",
                   help="files.references.relation_type (cv, photo…)")
    p.add_argument("--app-code", required=True, dest="app_code",
                   help="files.app_policies ve references.app_code")
    p.add_argument("--entity-id", dest="entity_id", default=None,
                   help="Tüm dosyalar için sabit entity_id (tek personel/varlık batch'i)")
    p.add_argument("--entity-id-map", dest="entity_id_map", default=None,
                   help="source_filename→entity_id eşleştirme CSV'si (çok varlıklı batch)")
    p.add_argument("--classification", default="internal",
                   help="files.objects.classification (varsayılan: internal)")
    p.add_argument("--db-conn", dest="db_conn",
                   default="host=localhost dbname=platformdb user=mustafaberen41",
                   help="psycopg2 bağlantı string'i")
    p.add_argument("--manifest", default=None,
                   help="Manifest CSV çıktı yolu (varsayılan: migration-manifest-<zaman>.csv)")
    p.add_argument("--include-source-names", action="store_true", dest="include_source_names",
                   help="Kaynak dosya adını manifest'e yaz (PII içermediğinden emin olduğunuzda)")
    p.add_argument("--dry-run", action="store_true", dest="dry_run",
                   help="Kopyalama ve DB yazma olmadan çalış; sadece kontrol + manifest")
    args = p.parse_args()
    migrate(args)


if __name__ == "__main__":
    main()
