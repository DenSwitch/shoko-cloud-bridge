#!/usr/bin/env python3
"""
Shoko Cloud Indexer (Colab / Linux / Windows)
=============================================
Incrementally computes file hashes (ED2K, CRC32, MD5, SHA1) via native librhash
with live progress bars (percentage, MB/s, ETA per file and overall) and MediaInfo
for new video files only, saving:
  1. _shoko_cloud_index.json      — full hash & MediaInfo tree
  2. _shoko_cloud_index.meta.json — lightweight metadata beacon (~160 bytes) with timestamp & SHA1,
     allowing Shoko Cloud Bridge to poll for changes without downloading the main 1MB+ index file.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.util
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

if sys.platform.startswith("win"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

try:
    from tqdm.auto import tqdm
except ImportError:
    tqdm = None

VIDEO_EXTENSIONS = {
    ".mkv", ".avi", ".mp4", ".mov", ".ogm",
    ".wmv", ".mpg", ".mpeg", ".mk3d", ".m4v"
}
INDEX_FILENAME = "_shoko_cloud_index.json"
META_FILENAME = "_shoko_cloud_index.meta.json"

RHASH_CRC32 = 0x01
RHASH_MD5 = 0x04
RHASH_SHA1 = 0x08
RHASH_ED2K = 0x80
RHPR_HEX = 0x02
RHPR_UPPERCASE = 0x08
ALL_IDS = RHASH_ED2K | RHASH_CRC32 | RHASH_MD5 | RHASH_SHA1


def ensure_dependencies():
    script_dir = Path(__file__).resolve().parent
    local_rhash = script_dir / ("rhash.exe" if sys.platform.startswith("win") else "rhash")
    if local_rhash.exists() and str(script_dir) not in os.environ.get("PATH", ""):
        os.environ["PATH"] = str(script_dir) + os.pathsep + os.environ.get("PATH", "")

    if sys.platform.startswith("linux"):
        missing = []
        if not shutil.which("rhash"):
            missing.append("rhash")
        if not shutil.which("mediainfo"):
            missing.append("mediainfo")
        if missing:
            print(f"[*] Installing dependencies: {', '.join(missing)}...")
            prefix = ["sudo"] if hasattr(os, "geteuid") and os.geteuid() != 0 and shutil.which("sudo") else []
            subprocess.run(prefix + ["apt-get", "update", "-qq"], check=True)
            subprocess.run(prefix + ["apt-get", "install", "-y", "-qq"] + missing, check=True)
    elif not load_librhash() and not shutil.which("rhash"):
        raise RuntimeError(
            "rhash utility is not found on this system.\n"
            "- Windows: download rhash.exe and place it in your PATH or in this script folder.\n"
            "- Linux: run 'sudo apt-get install -y rhash mediainfo'."
        )


def load_librhash():
    candidates = [
        "/usr/lib/x86_64-linux-gnu/librhash.so.0",
        "/usr/lib/x86_64-linux-gnu/librhash.so",
        "/usr/lib/aarch64-linux-gnu/librhash.so.0",
        ctypes.util.find_library("rhash"),
    ]
    for path in candidates:
        if not path:
            continue
        try:
            lib = ctypes.CDLL(path)
            lib.rhash_library_init()
            lib.rhash_init.argtypes = [ctypes.c_uint]
            lib.rhash_init.restype = ctypes.c_void_p
            lib.rhash_update.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
            lib.rhash_final.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
            lib.rhash_print.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_uint, ctypes.c_int]
            lib.rhash_free.argtypes = [ctypes.c_void_p]
            return lib
        except Exception:
            continue
    return None


_LIBRHASH = None


def compute_rhash_with_progress(filepath: str, filesize: int, desc: str = "", overall_bar=None) -> tuple[str, str, str, str]:
    global _LIBRHASH
    if _LIBRHASH is None:
        _LIBRHASH = load_librhash()

    if _LIBRHASH is not None:
        ctx = _LIBRHASH.rhash_init(ALL_IDS)
        chunk_size = 8 * 1024 * 1024  # 8 MB buffer
        buf = bytearray(chunk_size)
        c_buf = (ctypes.c_char * chunk_size).from_buffer(buf)

        try:
            if tqdm is not None:
                with tqdm(
                    total=filesize,
                    unit="B",
                    unit_scale=True,
                    unit_divisor=1024,
                    desc=desc,
                    leave=False,
                    dynamic_ncols=True,
                ) as pbar:
                    with open(filepath, "rb") as f:
                        while True:
                            n = f.readinto(buf)
                            if not n:
                                break
                            _LIBRHASH.rhash_update(ctx, ctypes.byref(c_buf), n)
                            pbar.update(n)
                            if overall_bar is not None:
                                overall_bar.update(n)
            else:
                read_bytes = 0
                t0 = time.time()
                last_print = 0.0
                with open(filepath, "rb") as f:
                    while True:
                        n = f.readinto(buf)
                        if not n:
                            break
                        _LIBRHASH.rhash_update(ctx, ctypes.byref(c_buf), n)
                        read_bytes += n
                        now = time.time()
                        if now - last_print >= 0.5 or read_bytes == filesize:
                            dt = max(now - t0, 0.001)
                            pct = (read_bytes / filesize * 100) if filesize else 100.0
                            spd = (read_bytes / (1024 ** 2)) / dt
                            sys.stdout.write(
                                f"\r    Progress: {pct:5.1f}% ({read_bytes / (1024**2):.1f}/{filesize / (1024**2):.1f} MB) | {spd:.1f} MB/s"
                            )
                            sys.stdout.flush()
                            last_print = now
                sys.stdout.write("\n")

            _LIBRHASH.rhash_final(ctx, None)
            out = ctypes.create_string_buffer(130)
            results = []
            for hid in (RHASH_ED2K, RHASH_CRC32, RHASH_MD5, RHASH_SHA1):
                _LIBRHASH.rhash_print(out, ctx, hid, RHPR_HEX | RHPR_UPPERCASE)
                results.append(out.value.decode("ascii").upper())
            return results[0], results[1], results[2], results[3]
        finally:
            _LIBRHASH.rhash_free(ctx)

    cmd = [
        "rhash",
        "--percents",
        "--uppercase",
        "--ed2k",
        "--crc32",
        "--md5",
        "--sha1",
        "--printf=%E\\t%C\\t%M\\t%H\\n",
        filepath,
    ]
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=None, text=True, check=True)
    line = res.stdout.strip().splitlines()[-1]
    ed2k, crc32, md5, sha1 = line.split("\t")
    if overall_bar is not None:
        overall_bar.update(filesize)
    return ed2k.upper(), crc32.upper(), md5.upper(), sha1.upper()


def compute_mediainfo(filepath: str) -> dict | None:
    try:
        cmd = ["mediainfo", "--OUTPUT=JSON", filepath]
        res = subprocess.run(cmd, capture_output=True, text=True, check=True)
        out = res.stdout.strip()
        if out.startswith("{"):
            return json.loads(out)
    except Exception as e:
        print(f"  [!] Warning: failed to extract MediaInfo for {filepath}: {e}")
    return None


def load_index(index_path: Path) -> dict:
    if not index_path.exists():
        return {"version": 1, "updated_at": None, "files": {}}
    try:
        with open(index_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        if "files" not in data or not isinstance(data["files"], dict):
            return {"version": 1, "updated_at": None, "files": data}
        return data
    except Exception as e:
        print(f"[!] Error reading {index_path}: {e}. Creating backup and starting fresh index.")
        backup = index_path.with_suffix(".json.bak")
        shutil.copy2(index_path, backup)
        return {"version": 1, "updated_at": None, "files": {}}


def save_index_and_meta_atomic(index_path: Path, manifest: dict):
    now_iso = datetime.datetime.now(datetime.timezone.utc).isoformat()
    manifest["updated_at"] = now_iso

    payload_bytes = json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8")
    index_sha1 = hashlib.sha1(payload_bytes).hexdigest().upper()

    tmp_path = index_path.with_suffix(".json.tmp")
    with open(tmp_path, "wb") as f:
        f.write(payload_bytes)
    os.replace(tmp_path, index_path)

    meta = {
        "version": 1,
        "updated_at": now_iso,
        "file_count": len(manifest.get("files", {})),
        "index_size": len(payload_bytes),
        "index_sha1": index_sha1,
    }
    meta_path = index_path.parent / META_FILENAME
    meta_tmp = meta_path.with_suffix(".json.tmp")
    with open(meta_tmp, "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    os.replace(meta_tmp, meta_path)


def scan_and_index(anime_root: Path, subfolder: str = "", prune_deleted: bool = True):
    ensure_dependencies()

    anime_root = anime_root.resolve()
    if not anime_root.exists():
        raise FileNotFoundError(f"Directory not found: {anime_root}")

    index_path = anime_root / INDEX_FILENAME
    meta_path = anime_root / META_FILENAME
    manifest = load_index(index_path)
    existing_files: dict[str, dict] = manifest.setdefault("files", {})

    scan_root = (anime_root / subfolder.strip("/")) if subfolder.strip() else anime_root
    if not scan_root.exists():
        raise FileNotFoundError(f"Subfolder not found: {scan_root}")

    print(f"[*] Scanning file tree in: {scan_root}")
    discovered: dict[str, tuple[Path, int, float]] = {}

    for dirpath, dirnames, filenames in os.walk(scan_root):
        dirnames[:] = [
            d for d in sorted(dirnames)
            if not d.startswith(".") and d not in ("$RECYCLE.BIN", "-Archives")
        ]
        for fname in sorted(filenames):
            ext = os.path.splitext(fname)[1].lower()
            if ext not in VIDEO_EXTENSIONS:
                continue
            full_path = Path(dirpath) / fname
            rel_path = full_path.relative_to(anime_root).as_posix()
            st = full_path.stat()
            if st.st_size > 0:
                discovered[rel_path] = (full_path, st.st_size, st.st_mtime)

    disappeared_by_name_size: dict[tuple[str, int], dict] = {}
    removed_count = 0
    if not subfolder.strip():
        for old_rel, old_entry in list(existing_files.items()):
            if old_rel not in discovered:
                old_name = os.path.basename(old_rel)
                old_size = int(old_entry.get("size", 0))
                if old_size > 0 and old_entry.get("ed2k"):
                    disappeared_by_name_size[(old_name, old_size)] = old_entry
                if prune_deleted:
                    del existing_files[old_rel]
                    removed_count += 1

    to_hash: list[tuple[str, Path, int, float]] = []
    skipped = 0
    relocated = 0

    for rel_path, (full_path, size, mtime) in discovered.items():
        cur = existing_files.get(rel_path)
        if cur and int(cur.get("size", -1)) == size and cur.get("ed2k"):
            skipped += 1
            continue

        fname = os.path.basename(rel_path)
        moved_entry = disappeared_by_name_size.get((fname, size))
        if moved_entry and moved_entry.get("ed2k"):
            existing_files[rel_path] = {
                **moved_entry,
                "size": size,
                "mtime": round(mtime, 2),
            }
            relocated += 1
            continue

        to_hash.append((rel_path, full_path, size, mtime))

    total_new_bytes = sum(x[2] for x in to_hash)
    total_new_gb = total_new_bytes / (1024 ** 3)
    print(f"[*] Total video files discovered: {len(discovered)}")
    print(f"    - Already indexed (skipped): {skipped}")
    print(f"    - Relocated (hash reused without reading): {relocated}")
    print(f"    - Removed stale entries: {removed_count}")
    print(f"    - New files to hash: {len(to_hash)} ({total_new_gb:.2f} GB)")

    if relocated > 0 or removed_count > 0 or not meta_path.exists():
        save_index_and_meta_atomic(index_path, manifest)

    if not to_hash:
        print(f"[+] Everything is up to date! Index: {index_path} | Meta: {meta_path}")
        return

    overall_bar = (
        tqdm(
            total=total_new_bytes,
            unit="B",
            unit_scale=True,
            unit_divisor=1024,
            desc="Overall Progress",
            dynamic_ncols=True,
        )
        if tqdm is not None and len(to_hash) > 1
        else None
    )

    try:
        for idx, (rel_path, full_path, size, mtime) in enumerate(to_hash, 1):
            size_mb = size / (1024 ** 2)
            short_name = os.path.basename(rel_path)
            if len(short_name) > 45:
                short_name = short_name[:42] + "..."

            t0 = time.time()
            ed2k, crc32, md5, sha1 = compute_rhash_with_progress(
                str(full_path),
                size,
                desc=f"[{idx}/{len(to_hash)}] {short_name}",
                overall_bar=overall_bar,
            )
            mediainfo_obj = compute_mediainfo(str(full_path))
            dt = max(time.time() - t0, 0.001)
            speed = size_mb / dt

            existing_files[rel_path] = {
                "size": size,
                "mtime": round(mtime, 2),
                "ed2k": ed2k,
                "crc32": crc32,
                "md5": md5,
                "sha1": sha1,
                "mediainfo": mediainfo_obj,
            }

            save_index_and_meta_atomic(index_path, manifest)
            msg = f"[{idx}/{len(to_hash)}] ✓ {rel_path} | ED2K: {ed2k} | {speed:.1f} MB/s ({dt:.1f} s)"
            if overall_bar is not None:
                overall_bar.write(msg)
            else:
                print(msg)
    finally:
        if overall_bar is not None:
            overall_bar.close()

    print(f"\n[+] Done! Index: {index_path} | Meta: {meta_path} (total entries: {len(existing_files)})")


def auto_find_anime_dir() -> Path:
    mydrive = Path("/content/drive/MyDrive/Anime")
    if mydrive.exists():
        return mydrive
    shareddrives = Path("/content/drive/Shareddrives")
    if shareddrives.exists():
        for d in sorted(shareddrives.iterdir()):
            sub = d / "Anime"
            if sub.exists():
                return sub
    raise FileNotFoundError(
        "Could not automatically find the Anime folder. Please specify --path"
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Shoko Cloud Indexer")
    parser.add_argument("--path", type=str, default=None, help="Path to the Anime folder")
    parser.add_argument("--subfolder", type=str, default="", help="Optional subfolder inside Anime")
    parser.add_argument("--no-prune", action="store_true", help="Do not remove deleted files from index")
    args = parser.parse_args()

    target_dir = Path(args.path) if args.path else auto_find_anime_dir()
    scan_and_index(target_dir, subfolder=args.subfolder, prune_deleted=not args.no_prune)
