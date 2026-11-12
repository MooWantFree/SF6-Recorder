"""Merge two local SF6 archives without launching the app or contacting Capcom.

Defaults to a read-only plan. --apply requires both apps to be closed, backs up
both databases and non-browser data first, and leaves the source untouched.
"""
import argparse
from contextlib import ExitStack, closing, contextmanager
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import sys


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def read_db(path):
    return sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)


def counts(directory):
    with closing(read_db(directory / "battles.sqlite")) as db:
        return {f"{owner}/{category}": total for owner, category, total in db.execute(
            "SELECT owner_id,category,count(*) FROM battles GROUP BY owner_id,category")}


def plan(source, destination):
    with closing(read_db(destination / "battles.sqlite")) as db:
        db.execute("ATTACH DATABASE ? AS incoming", ((source / "battles.sqlite").resolve().as_uri() + "?mode=ro",))
        new = db.execute("SELECT count(*) FROM incoming.battles s WHERE NOT EXISTS(SELECT 1 FROM battles d WHERE d.owner_id=s.owner_id AND d.replay_id=s.replay_id)").fetchone()[0]
    return {"source_counts": counts(source), "destination_counts": counts(destination), "new_matches": new}


@contextmanager
def data_lock(directory):
    if os.name != "nt":
        raise RuntimeError("This migration requires Windows file-sharing locks.")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateFileW(str(directory / "instance.lock"), 0xC0000000, 0, None, 4, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise OSError(f"Close the app using this data directory first: {directory}")
    try:
        yield
    finally:
        kernel.CloseHandle(handle)


def backup(directory, target):
    target.mkdir(parents=True, exist_ok=False)
    for path in directory.rglob("*"):
        relative = path.relative_to(directory)
        if "WebView2" in relative.parts or path.name in {"instance.lock", "battles.sqlite", "battles.sqlite-wal", "battles.sqlite-shm"}:
            continue
        if path.is_file() and not path.is_symlink() and path.suffix != ".tmp":
            output = target / relative
            output.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, output)
    # SQLite's backup API includes committed WAL contents, unlike a plain file copy.
    with closing(read_db(directory / "battles.sqlite")) as src, closing(sqlite3.connect(target / "battles.sqlite")) as dst:
        src.backup(dst)
        if dst.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise RuntimeError("Backup database failed integrity check")


def atomic_copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".merge-tmp")
    shutil.copy2(source, temporary)
    os.replace(temporary, destination)


def write_json(path, value):
    temporary = path.with_name(path.name + ".merge-tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def newest(paths, field=None):
    paths = [p for p in paths if p.is_file()]
    if not paths:
        return None
    def key(path):
        if field:
            value = read_json(path).get(field)
            if value:
                return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()
        return path.stat().st_mtime
    return max(paths, key=key)


def merge_databases(source, destination):
    with closing(sqlite3.connect(destination.resolve().as_uri(), uri=True)) as db, db:
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("ATTACH DATABASE ? AS incoming", (source.resolve().as_uri() + "?mode=ro",))
        for table in ("battles", "score_snapshots", "sync_state", "sync_runs"):
            if [r[1] for r in db.execute(f"PRAGMA table_info({table})")] != [r[1] for r in db.execute(f"PRAGMA incoming.table_info({table})")]:
                raise RuntimeError(f"Incompatible table: {table}")
        db.execute("BEGIN IMMEDIATE")
        db.execute("INSERT OR IGNORE INTO battles SELECT * FROM incoming.battles")
        db.execute("INSERT OR IGNORE INTO score_snapshots SELECT * FROM incoming.score_snapshots")
        db.execute("""INSERT INTO sync_state SELECT * FROM incoming.sync_state WHERE true
            ON CONFLICT(owner_id,category) DO UPDATE SET frontier_id=excluded.frontier_id,completed_at=excluded.completed_at
            WHERE julianday(excluded.completed_at)>julianday(sync_state.completed_at)""")
        db.execute("""INSERT INTO sync_runs(owner_id,started_at,finished_at,status,requests,inserted,detail)
            SELECT owner_id,started_at,finished_at,status,requests,inserted,detail FROM incoming.sync_runs s
            WHERE NOT EXISTS(SELECT 1 FROM sync_runs d WHERE d.owner_id=s.owner_id AND d.started_at=s.started_at)""")
        # Version-2 archives are upgraded by BattleStore on first launch. When both
        # archives already know page continuity, preserve those links as well.
        has_links = lambda schema: db.execute(f"SELECT 1 FROM {schema}.sqlite_master WHERE type='table' AND name='score_links'").fetchone()
        if has_links("main") and has_links("incoming"):
            db.execute("INSERT OR IGNORE INTO score_links SELECT * FROM incoming.score_links")
        if db.execute("PRAGMA foreign_key_check").fetchone() or db.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise RuntimeError("Merged database failed validation")


def apply(source, destination, backup_root):
    source, destination, backup_root = source.resolve(), destination.resolve(), backup_root.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError("Data directories must be distinct and not nested")
    if source == backup_root or source in backup_root.parents or destination == backup_root or destination in backup_root.parents:
        raise ValueError("Backups must be outside the data directories")
    with ExitStack() as locks:
        for directory in sorted((source, destination)):
            locks.enter_context(data_lock(directory))
        report = plan(source, destination)
        backup(source, backup_root / "source")
        backup(destination, backup_root / "destination")
        src, dst = backup_root / "source", backup_root / "destination"
        merge_databases(src / "battles.sqlite", destination / "battles.sqlite")
        # Raw encrypted DPAPI files are copied as-is; no credential values are read or printed.
        copied = []
        file_names = {"session.dpapi", "settings.json"}
        for root in (src, dst):
            file_names.update(str(p.relative_to(root)) for p in (root / "Accounts").glob("*.dpapi"))
            file_names.update(str(p.relative_to(root)) for p in (root / "Images").glob("*") if p.is_file())
        for name in sorted(file_names):
            selected = newest([src / name, dst / name])
            if selected:
                atomic_copy(selected, destination / name)
                if selected.suffix == ".dpapi":
                    if hashlib.sha256(selected.read_bytes()).digest() != hashlib.sha256((destination / name).read_bytes()).digest():
                        raise RuntimeError("Encrypted session copy failed validation")
                    copied.append(name)
        character_rows = {}
        for path in sorted([p for p in (dst / "characters.json", src / "characters.json") if p.exists()], key=lambda p: p.stat().st_mtime):
            for row in read_json(path):
                row["ImagePath"] = str(destination / "Images" / Path(row["ImagePath"]).name)
                character_rows[row["Id"]] = row
        if character_rows:
            write_json(destination / "characters.json", list(character_rows.values()))
        for pattern, date_field in (("profile-*.json", "UpdatedAt"), ("scores-*.json", "FetchedAt")):
            names = {p.name for root in (src, dst) for p in root.glob(pattern)}
            for name in names:
                selected = newest([src / name, dst / name], date_field)
                data = read_json(selected)
                if "AvatarPath" in data and data["AvatarPath"]:
                    data["AvatarPath"] = str(destination / "Images" / Path(data["AvatarPath"]).name)
                write_json(destination / name, data)
        report.update({"merged_counts": counts(destination), "encrypted_files_preserved": copied, "backup": str(backup_root)})
        write_json(backup_root / "report.json", report)
        return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--backup", type=Path)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    if args.apply and args.backup is None:
        parser.error("--apply requires a new --backup directory")
    result = apply(args.source, args.destination, args.backup) if args.apply else plan(args.source, args.destination)
    print(json.dumps(result, ensure_ascii=True, indent=2))
