import importlib.util
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("merge_data", Path(__file__).resolve().parents[1] / "scripts" / "merge_data.py")
merge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(merge)


class MergeDataTests(unittest.TestCase):
    def setUp(self):
        parent = Path(__file__).resolve().parents[1] / ".local" / "checks"
        parent.mkdir(parents=True, exist_ok=True)
        self.cleanup_root = parent.resolve()
        self.temp = tempfile.TemporaryDirectory(dir=parent)
        self.root = Path(self.temp.name)
        self.src, self.dst = self.root / "source", self.root / "destination"
        for directory in (self.src, self.dst):
            directory.mkdir()
            with closing(sqlite3.connect(directory / "battles.sqlite")) as db, db:
                db.executescript("""
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE battles(owner_id TEXT,replay_id TEXT,category TEXT,played_at INTEGER,data_json TEXT,PRIMARY KEY(owner_id,replay_id));
                    CREATE TABLE score_snapshots(owner_id TEXT,replay_id TEXT,lp INTEGER,PRIMARY KEY(owner_id,replay_id),FOREIGN KEY(owner_id,replay_id) REFERENCES battles(owner_id,replay_id));
                    CREATE TABLE sync_state(owner_id TEXT,category TEXT,frontier_id TEXT,completed_at TEXT,PRIMARY KEY(owner_id,category));
                    CREATE TABLE sync_runs(id INTEGER PRIMARY KEY,owner_id TEXT,started_at TEXT,finished_at TEXT,status TEXT,requests INTEGER,inserted INTEGER,detail TEXT);
                """)
                db.execute("INSERT INTO battles VALUES('me','shared','rank',1,'{}')")
                db.execute("INSERT INTO score_snapshots VALUES('me','shared',1000)")
        self.write(self.dst / "session.dpapi", b"older encrypted session", 10)
        self.write(self.src / "session.dpapi", b"newer encrypted session", 20)
        self.write(self.dst / "Accounts" / "1111111111.dpapi", b"retained account", 10)
        self.write(self.src / "Accounts" / "2222222222.dpapi", b"new account", 20)
        self.write(self.dst / "settings.json", b'{"UseProxy":false}', 10)
        self.write(self.src / "settings.json", b'{"UseProxy":true}', 20)

    def tearDown(self):
        self.assertTrue(self.root.resolve().is_relative_to(self.cleanup_root))
        self.temp.cleanup()

    def write(self, path, data, timestamp=20):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        os.utime(path, (timestamp, timestamp))

    def test_merge_backup_wal_sessions_and_paths(self):
        # Leave a WAL connection open to prove that the backup API captures its committed records.
        source = sqlite3.connect(self.src / "battles.sqlite")
        try:
            source.execute("INSERT INTO battles VALUES('opponent','new','casual',2,'{}')")
            source.execute("INSERT INTO score_snapshots VALUES('opponent','new',2000)")
            source.execute("INSERT INTO sync_state VALUES('opponent','casual','new','2026-11-12T00:00:00+00:00')")
            source.execute("INSERT INTO sync_runs VALUES(1,'opponent','start','finish','completed',2,1,'fixture')")
            source.commit()
            self.write(self.src / "Images" / "character_juri.png", b"portrait fixture")
            self.write(self.src / "characters.json", json.dumps([{"Id":16,"Name":"Juri","ToolName":"juri","ImagePath":str(self.src / "Images" / "character_juri.png")}]).encode())
            self.write(self.src / "profile-2222222222.json", json.dumps({"AccountId":"2222222222","Name":"Name","AvatarPath":str(self.src / "Images" / "avatar.jpg"),"UpdatedAt":"2026-11-12T00:00:00+00:00"}).encode())
            self.write(self.src / "Images" / "avatar.jpg", b"avatar")
            self.write(self.dst / "WebView2" / "keep.txt", b"browser data")
            report = merge.apply(self.src, self.dst, self.root / "backup")
            self.assertEqual(report["new_matches"], 1)
            self.assertEqual(sum(report["merged_counts"].values()), 2)
            self.assertEqual(self.dst.joinpath("session.dpapi").read_bytes(), b"newer encrypted session")
            self.assertEqual(self.dst.joinpath("Accounts", "1111111111.dpapi").read_bytes(), b"retained account")
            self.assertEqual(self.dst.joinpath("Accounts", "2222222222.dpapi").read_bytes(), b"new account")
            self.assertTrue(merge.read_json(self.dst / "settings.json")["UseProxy"])
            self.assertEqual(merge.read_json(self.dst / "characters.json")[0]["ImagePath"], str(self.dst / "Images" / "character_juri.png"))
            self.assertEqual(merge.read_json(self.dst / "profile-2222222222.json")["AvatarPath"], str(self.dst / "Images" / "avatar.jpg"))
            self.assertEqual(self.dst.joinpath("WebView2", "keep.txt").read_bytes(), b"browser data")
            self.assertEqual(self.root.joinpath("backup", "destination", "session.dpapi").read_bytes(), b"older encrypted session")
            self.assertEqual(sum(merge.counts(self.root / "backup" / "source").values()), 2)
            self.assertEqual(self.src.joinpath("session.dpapi").read_bytes(), b"newer encrypted session")
            repeat = merge.apply(self.src, self.dst, self.root / "repeat-backup")
            self.assertEqual(repeat["new_matches"], 0)
            with closing(sqlite3.connect(self.dst / "battles.sqlite")) as db:
                self.assertEqual(db.execute("SELECT count(*) FROM sync_runs").fetchone()[0], 1)
                self.assertEqual(db.execute("PRAGMA integrity_check").fetchone()[0], "ok")
        finally:
            source.close()

    def test_running_app_blocks_merge_before_backup(self):
        with merge.data_lock(self.src):
            with self.assertRaises(OSError):
                merge.apply(self.src, self.dst, self.root / "blocked-backup")
        self.assertFalse((self.root / "blocked-backup").exists())
        self.assertEqual(self.dst.joinpath("session.dpapi").read_bytes(), b"older encrypted session")

    def test_nested_directories_are_rejected(self):
        with self.assertRaises(ValueError):
            merge.apply(self.src, self.dst, self.dst / "backup")
        with self.assertRaises(ValueError):
            merge.apply(self.src, self.src, self.root / "backup")

    def test_plan_does_not_mutate_data(self):
        before = self.dst.joinpath("session.dpapi").read_bytes()
        self.assertEqual(merge.plan(self.src, self.dst)["new_matches"], 0)
        self.assertEqual(self.dst.joinpath("session.dpapi").read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
