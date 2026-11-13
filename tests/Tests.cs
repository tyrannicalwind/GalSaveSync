using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.IO.Compression;
using GalSaveSync;

sealed class MemoryStore : ICloudStore {
    public readonly Dictionary<string, RemoteObject> Items = new Dictionary<string, RemoteObject>();
    public bool FailWrites, CorruptDownloads, RejectNextIndex;
    public Action<string> BeforeWrite;
    int counter;
    public RemoteObject Get(string path) {
        RemoteObject value; if (!Items.TryGetValue(path, out value)) return null;
        return new RemoteObject { Data = CorruptDownloads && path.EndsWith(".zip") ? new byte[] { 1,2,3 } : value.Data.ToArray(), ETag = value.ETag };
    }
    public bool Put(string path, byte[] bytes, string etag, bool createOnly) {
        if (BeforeWrite != null) BeforeWrite(path);
        if (FailWrites) throw new IOException("simulated upload interruption");
        if (RejectNextIndex && path.EndsWith("index.json")) { RejectNextIndex = false; return false; }
        RemoteObject current; bool exists = Items.TryGetValue(path, out current);
        if (createOnly ? exists : (!exists || current.ETag != etag)) return false;
        Items[path] = new RemoteObject { Data = bytes.ToArray(), ETag = "\"" + (++counter) + "\"" }; return true;
    }
    public void EnsureDirectory(string path) {}
    public List<string> Directories(string path) { return Items.Keys.Where(p => p.StartsWith(path + "/")).Select(p => p.Substring(path.Length + 1).Split('/')[0]).Distinct().ToList(); }
    public List<string> Files(string path) { return Items.Keys.Where(p => p.StartsWith(path + "/") && !p.Substring(path.Length + 1).Contains('/')).Select(p => p.Substring(path.Length + 1)).ToList(); }
    public void Dispose() {}
}
static class Tests {
    static string root;
    static int passed;
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static void Throws(Action action, string name) { bool threw = false; try { action(); } catch { threw = true; } Assert(threw, name); }
    static Game NewGame(string folder, string name) {
        return new Game { Id = Guid.NewGuid().ToString("N"), Name = name, Slots = new List<Slot> { new Slot { Id = Guid.NewGuid().ToString("N"), Kind = "directory", Path = folder } } };
    }
    static Game Bind(Game source, string path) { var g = Json.Read<Game>(Json.Write(source)); g.BaseVersion = g.BaseFingerprint = null; g.Slots[0].Path = path; return g; }
    static VersionRecord Head(SyncEngine engine, Game g) { string e; var i = engine.Index(g, out e); return i.Versions.Single(v => v.Id == i.Heads.Single()); }
    static string FileAt(Game game) { return Path.Combine(game.Slots[0].Path, "save.dat"); }
    static void Write(Game game, string value) { Directory.CreateDirectory(game.Slots[0].Path); File.WriteAllText(FileAt(game), value); }
    static string Read(Game game) { return File.ReadAllText(FileAt(game)); }
    static void Suite(ICloudStore store, string suffix) {
        var a = new AppData(Path.Combine(root, "state-a-" + suffix)); var b = new AppData(Path.Combine(root, "state-b-" + suffix));
        a.Settings.Device = "device-a"; b.Settings.Device = "device-b";
        var ga = NewGame(Path.Combine(root, "save-a-" + suffix), "测试游戏「空 格」"); Write(ga, "progress-1"); a.Settings.Games.Add(ga);
        var ea = new SyncEngine(a, store); ea.Upload(ga); var first = Head(ea, ga);
        Assert(first.Fingerprint == ga.BaseFingerprint, suffix + " initial upload publishes validated version");
        var discovered = ea.Discover(); Assert(discovered.Any(i => i.GameId == ga.Id), suffix + " discover cloud game");
        var gb = Bind(ga, Path.Combine(root, "save-b-" + suffix)); b.Settings.Games.Add(gb); var eb = new SyncEngine(b, store);
        eb.Sync(gb); Assert(Read(gb) == "progress-1", suffix + " missing local folder bootstraps from cloud");
        Write(gb, "progress-2"); eb.Sync(gb); ea.Sync(ga); Assert(Read(ga) == "progress-2", suffix + " second device upload and first device download");
        Assert(Directory.GetFiles(Path.Combine(a.Root, "backups", ga.Id), "*.zip").Length > 0, suffix + " local backup before restore");
        Write(ga, "branch-a"); Write(gb, "branch-b"); ea.Upload(ga);
        Throws(() => eb.Sync(gb), suffix + " divergent local/cloud progress blocks automatic overwrite");
        Assert(Read(gb) == "branch-b", suffix + " conflict leaves local save untouched");
        eb.Upload(gb); string tag; var conflicted = ea.Index(ga, out tag);
        Assert(conflicted.Heads.Count == 2, suffix + " upload preserves both cloud branches");
        Throws(() => ea.Sync(ga), suffix + " multiple heads require explicit choice");
        string branchB = gb.BaseVersion; eb.ChooseHead(gb, branchB); ea.Sync(ga);
        Assert(Read(ga) == "branch-b", suffix + " choose branch and sync");
        ea.Restore(ga, first); Assert(Read(ga) == "progress-1", suffix + " restore historical snapshot");
        Throws(() => ea.Upload(ga), suffix + " unchanged historical snapshot is not republished as latest");
        ea.Sync(ga); Assert(Read(ga) == "branch-b", suffix + " sync after history restore follows cloud head");
        var other = NewGame(Path.Combine(root, "other-" + suffix), "另一个游戏"); Write(other, "other-progress"); ea.Upload(other);
        Assert(Read(ga) == "branch-b" && Head(ea, ga).Id == branchB, suffix + " game namespaces are isolated");
        string baseline = ga.BaseVersion; ea.Upload(ga); Assert(ga.BaseVersion == baseline, suffix + " unchanged saves avoid redundant upload");
    }
    static void FailureSuite() {
        var store = new MemoryStore(); var data = new AppData(Path.Combine(root, "fail-state")); var game = NewGame(Path.Combine(root, "fail-save"), "Failure tests"); Write(game, "original");
        var engine = new SyncEngine(data, store); engine.Upload(game); var initial = Head(engine, game); string initialId = initial.Id;
        Write(game, "new"); store.FailWrites = true; Throws(() => engine.Upload(game), "interrupted upload reports failure"); store.FailWrites = false;
        Assert(Head(engine, game).Id == initialId && Read(game) == "new", "failed upload leaves cloud head and local files intact");
        store.CorruptDownloads = true; Throws(() => engine.Upload(game), "upload readback checksum failure"); store.CorruptDownloads = false;
        Assert(Head(engine, game).Id == initialId, "unverified upload is not published");
        store.RejectNextIndex = true; engine.Upload(game); Assert(Read(game) == "new" && Head(engine, game).Id != initialId, "CAS race retries safely");
        store.CorruptDownloads = true; Throws(() => engine.Restore(game, initial), "corrupted download rejected"); store.CorruptDownloads = false;
        Assert(Read(game) == "new", "corrupted download does not modify local save");
        string savedPath = game.Slots[0].Path; game.Slots[0].Path = Path.Combine(root, "does-not-exist"); Throws(() => engine.Upload(game), "missing local folder cannot become an empty upload"); game.Slots[0].Path = savedPath;
        // Malicious archive with a manifest path escape, otherwise valid checksums.
        var manifest = new Manifest { Format = 1, GameId = game.Id, Slots = game.Slots.Select(s => new Slot { Id = s.Id, Kind = s.Kind }).ToList(), Files = new List<FileItem> { new FileItem { Slot = game.Slots[0].Id, Relative = "../escape.dat", Size = 1, Hash = Snapshots.Hash(new byte[]{1}) } } };
        manifest.Fingerprint = Snapshots.Fingerprint(manifest); byte[] malicious;
        using (var buffer = new MemoryStream()) {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)) { byte[] json = Json.Bytes(manifest); using (var s = zip.CreateEntry("snapshot.json").Open()) s.Write(json,0,json.Length); }
            malicious = buffer.ToArray();
        }
        Throws(() => Snapshots.Unpack(malicious, game, Path.Combine(root, "evil-stage")), "ZIP path traversal rejected");
        Assert(!File.Exists(Path.Combine(root, "escape.dat")), "ZIP cannot escape staging folder");
        // Two configured targets: force a failure on target 2 after target 1 has been installed.
        var multi = NewGame(Path.Combine(root, "rollback-save"), "Rollback"); Write(multi, "old-first");
        string second = Path.Combine(root, "rollback-file.dat"); File.WriteAllText(second, "old-second");
        var slot = new Slot { Id = Guid.NewGuid().ToString("N"), Kind = "file", Path = second }; multi.Slots.Add(slot);
        var m = Snapshots.Scan(multi, false); byte[] old = Snapshots.Pack(multi, m);
        Write(multi, "current-first"); File.WriteAllText(second, "current-second");
        string stage = Path.Combine(root, "rollback-stage"); Directory.CreateDirectory(stage); Snapshots.Unpack(old, multi, stage);
        using (var held = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Throws(() => Snapshots.ReplaceFromStage(multi, stage), "multi-target installation failure triggers rollback");
            Assert(Read(multi) == "current-first", "first target rolled back after second target fails");
        }
        Assert(File.ReadAllText(second) == "current-second", "locked target remains intact");
        Snapshots.ReplaceFromStage(multi, stage); Assert(Read(multi) == "old-first" && File.ReadAllText(second) == "old-second", "multi-slot directory and file restore succeeds");
        File.WriteAllText(Path.Combine(multi.Slots[0].Path, "extra.dat"), "extra");
        Snapshots.ReplaceFromStage(multi, stage); Assert(!File.Exists(Path.Combine(multi.Slots[0].Path, "extra.dat")), "restore removes stale files while backups preserve original snapshot");
        var overlap = Json.Read<Game>(Json.Write(multi)); overlap.Slots[1].Path = Path.Combine(overlap.Slots[0].Path, "nested.dat"); Throws(() => Snapshots.ValidateGame(overlap, data.Root), "overlapping save paths rejected");
        var drive = Json.Read<Game>(Json.Write(game)); drive.Slots[0].Path = Path.GetPathRoot(root); Throws(() => Snapshots.ValidateGame(drive, data.Root), "whole-drive save path rejected");
        data.Settings.ProtectedPassword = AppData.Protect("test-secret-密码"); Assert(data.Password() == "test-secret-密码", "Windows DPAPI credential round trip");
        data.Save(); Assert(!File.ReadAllText(Path.Combine(data.Root, "settings.json")).Contains("test-secret"), "configuration does not contain plaintext password");
        var reload = new AppData(data.Root); Assert(reload.Password() == "test-secret-密码", "saved configuration can be reloaded");
        string localBackup = Snapshots.Backup(game, data.Root, "test"); Write(game, "after-backup");
        string cloudBefore = Head(engine, game).Id;
        engine.RestoreLocal(game, localBackup);
        Assert(Read(game) == "new" && Head(engine, game).Id == cloudBefore, "offline local-backup recovery does not modify cloud");
        game.Executable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        Throws(() => engine.Upload(game), "running configured game blocks upload");
        Throws(() => engine.RestoreLocal(game, localBackup), "running configured game blocks restore");
        game.Executable = null;
        Assert(engine.WaitForStable(game) == Snapshots.Scan(game, false).Fingerprint, "post-exit stable-save detection");
    }
    static void JournalSuite() {
        var raw = new MemoryStore();
        var a = new AppData(Path.Combine(root, "journal-race-a")); var b = new AppData(Path.Combine(root, "journal-race-b"));
        var ga = NewGame(Path.Combine(root, "journal-race-save-a"), "迁移及并发测试"); Write(ga, "legacy-1"); a.Settings.Games.Add(ga);
        var legacyEngine = new SyncEngine(a, raw); legacyEngine.Upload(ga);
        var oldDevice = new AppData(Path.Combine(root, "legacy-device")); var oldGame = Bind(ga, Path.Combine(root, "legacy-device-save"));
        oldGame.BaseVersion = ga.BaseVersion; oldGame.BaseFingerprint = ga.BaseFingerprint; oldDevice.Settings.Games.Add(oldGame);
        string indexPath = "GalgameSaves/games/" + ga.Id + "/index.json"; byte[] originalIndex = raw.Get(indexPath).Data;
        var ea = new SyncEngine(a, new JournalStore(raw)); Write(ga, "journal-2"); ea.Upload(ga);
        Assert(raw.Get(indexPath).Data.SequenceEqual(originalIndex), "legacy index is read without overwriting it");
        string tag; Assert(ea.Index(ga, out tag).Versions.Count == 2, "legacy history imported into journal revisions");
        var gb = Bind(ga, Path.Combine(root, "journal-race-save-b")); b.Settings.Games.Add(gb);
        var eb = new SyncEngine(b, new JournalStore(raw)); eb.Sync(gb);
        Write(ga, "simultaneous-a"); Write(gb, "simultaneous-b");
        raw.BeforeWrite = path => {
            if (path.Contains("/journal/") && path.EndsWith(".json")) { raw.BeforeWrite = null; eb.Upload(gb); }
        };
        ea.Upload(ga);
        var raced = ea.Index(ga, out tag);
        Assert(raced.Heads.Count == 2 && raced.Heads.Contains(ga.BaseVersion) && raced.Heads.Contains(gb.BaseVersion), "simultaneous immutable commits preserve both writers");
        Assert(raced.Versions.Count == 4, "concurrent publication loses no historical versions");
        Throws(() => ea.Sync(ga), "concurrent journal leaves block automatic restore");
        ea.ChooseHead(ga, ga.BaseVersion); Assert(Head(ea, ga).Id == ga.BaseVersion, "resolution revision consumes all observed leaves");
        string folder = "GalgameSaves/games/" + ga.Id + "/journal/";
        raw.Put(folder + Guid.NewGuid().ToString("N") + "-" + Snapshots.Hash(new byte[]{4}) + ".json", new byte[]{4}, null, true);
        Assert(Head(ea, ga).Id == ga.BaseVersion, "uncommitted metadata is ignored");
        string previous = ga.BaseVersion; Write(ga, "uncommitted-progress");
        raw.BeforeWrite = path => { if (path.EndsWith(".commit")) throw new IOException("simulated commit interruption"); };
        Throws(() => ea.Upload(ga), "interrupted commit reports failure"); raw.BeforeWrite = null;
        Assert(Head(ea, ga).Id == previous && Read(ga) == "uncommitted-progress", "interrupted revision does not replace cloud head or local save");
        // Simulate an older client publishing after the migration.
        Write(oldGame, "old-client-new-progress"); new SyncEngine(oldDevice, raw).Upload(oldGame);
        var mixed = ea.Index(ga, out tag);
        Assert(mixed.Heads.Count == 2 && mixed.Heads.Contains(oldGame.BaseVersion), "legacy-client changes are retained as an explicit branch");
        ea.ChooseHead(ga, oldGame.BaseVersion); Assert(Head(ea, ga).Id == oldGame.BaseVersion, "resolution acknowledges changed legacy index");
        string commit = raw.Items.Keys.First(p => p.StartsWith(folder) && p.EndsWith(".commit"));
        string metadata = commit.Substring(0, commit.Length - 7) + ".json"; byte[] original = raw.Get(metadata).Data;
        raw.Items[metadata].Data = new byte[]{9};
        Throws(() => new SyncEngine(a, new JournalStore(raw)).Index(ga, out tag), "committed metadata corruption blocks sync");
        raw.Items[metadata].Data = original;
        Assert(new SyncEngine(a, new JournalStore(raw)).Index(ga, out tag).Heads.Single() == oldGame.BaseVersion, "metadata validation recovers when correct data is restored");
    }
    static int Main(string[] args) {
        try {
            root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            Suite(new MemoryStore(), "memory"); FailureSuite(); Suite(new JournalStore(new MemoryStore()), "journal"); JournalSuite();
            if (args.Length > 1) using (var dav = new WebDavStore(args[1], "tester", "test-only")) {
                dav.TestConnection(); Assert(true, "real HTTP WebDAV read/write/list probe"); Suite(new JournalStore(dav), "http");
                Throws(() => { using (var bad = new WebDavStore(args[1], "tester", "wrong")) bad.TestConnection(); }, "HTTP bad credentials rejected");
                using (var noetag = new WebDavStore(args[1]+"noetag/", "tester", "test-only")) { noetag.TestConnection(); Assert(true, "connection test succeeds without ETag"); Suite(new JournalStore(noetag), "noetag"); }
                using (var weak = new WebDavStore(args[1]+"weak/", "tester", "test-only")) { weak.TestConnection(); Suite(new JournalStore(weak), "weak"); }
                using (var noConditions = new WebDavStore(args[1]+"unsafe/", "tester", "test-only")) Suite(new JournalStore(noConditions), "no-conditions");
                Throws(() => { using (var redirect = new WebDavStore(args[1]+"redirect/", "tester", "test-only")) redirect.TestConnection(); }, "HTTP redirects rejected without forwarding credentials");
            }
            using (var httpStore = new WebDavStore("http://example.com/dav", "u", "p")) Assert(true, "non-local HTTP URL accepted without connecting");
            Throws(() => { using (var invalidStore = new WebDavStore("ftp://example.com/dav", "u", "p")) {} }, "non-HTTP URL rejected");
            Console.WriteLine("ALL PASSED: " + passed); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
