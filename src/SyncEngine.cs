using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;

namespace GalSaveSync {
    public sealed class SyncEngine {
        readonly AppData data;
        readonly ICloudStore store;
        public Action<string> Progress = delegate {};
        public SyncEngine(AppData data, ICloudStore store) { this.data = data; this.store = store; }
        static string Folder(Game game) { return "GalgameSaves/games/" + game.Id; }
        internal static DateTime ParseTime(string value) {
            DateTime time;
            if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out time)) throw new Exception("存档时间无效，已停止同步。");
            return time.ToUniversalTime();
        }
        internal static DateTime VersionTime(VersionRecord version) { return ParseTime(String.IsNullOrEmpty(version.ModifiedUtc) ? version.CreatedUtc : version.ModifiedUtc); }
        static DateTime LocalTime(Manifest manifest) { return manifest.Files.Count == 0 ? DateTime.MinValue : manifest.Files.Max(f => ParseTime(f.ModifiedUtc)); }
        static VersionRecord Latest(CloudIndex index) {
            var candidates = index.Versions.Where(v => index.Heads.Contains(v.Id)).OrderByDescending(VersionTime).ToList();
            if (candidates.Count == 0) throw new Exception("云端没有可同步的版本。");
            var latest = candidates[0];
            if (candidates.Any(v => VersionTime(v) == VersionTime(latest) && v.Fingerprint != latest.Fingerprint)) throw new ConflictException("云端有时间相同但内容不同的存档，请在历史版本中选择。");
            return latest;
        }
        internal static void ValidateIndex(CloudIndex index, string gameId) {
            if (index == null || index.Format != 1 || index.GameId != gameId || index.Versions == null || index.Heads == null || index.Slots == null || index.Slots.Count == 0 || index.Slots.Count > 32 || String.IsNullOrWhiteSpace(index.Name)) throw new Exception("云端游戏索引无效。");
            if (index.Versions.Count > 20000 || index.Slots.Any(s => !Snapshots.IsId(s.Id) || (s.Kind != "file" && s.Kind != "directory")) || index.Slots.Select(s => s.Id).Distinct().Count() != index.Slots.Count) throw new Exception("云端游戏配置无效。");
            if (index.Versions.Any(v => !Snapshots.IsId(v.Id) || !Snapshots.IsHash(v.ArchiveHash) || !Snapshots.IsHash(v.Fingerprint) || (v.Parent != null && !Snapshots.IsId(v.Parent))) || index.Versions.Select(v => v.Id).Distinct().Count() != index.Versions.Count) throw new Exception("云端版本记录无效。");
            if (index.Heads.Any(h => !index.Versions.Any(v => v.Id == h)) || index.Heads.Distinct().Count() != index.Heads.Count || (index.Versions.Count > 0 && index.Heads.Count == 0)) throw new Exception("云端当前版本无效。");
        }
        public CloudIndex Index(Game game, out string etag) {
            if (!Snapshots.IsId(game.Id)) throw new Exception("游戏 ID 无效。");
            var remote = store.Get(Folder(game) + "/index.json"); etag = remote == null ? null : remote.ETag;
            if (remote == null) return null;
            if (remote.Data.Length > 16 * 1024 * 1024) throw new Exception("云端索引过大。");
            var index = Json.ReadBytes<CloudIndex>(remote.Data); ValidateIndex(index, game.Id); return index;
        }
        void Ready(Game game) { Snapshots.ValidateGame(game, data.Root); EnsureNotRunning(game); }
        public static void EnsureNotRunning(Game game) {
            if (String.IsNullOrEmpty(game.Executable)) return;
            string path = Snapshots.Full(game.Executable);
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))) {
                using (process) {
                    try { if (String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase)) throw new ConflictException("游戏正在运行，请先退出游戏再同步。"); }
                    catch (ConflictException) { throw; }
                    catch { throw new ConflictException("检测到同名游戏进程，无法确认是否安全，请退出后重试。"); }
                }
            }
        }
        static void MatchSlots(Game game, CloudIndex index) {
            if (index.Slots.Count != game.Slots.Count || index.Slots.Any(s => !game.Slots.Any(local => local.Id == s.Id && local.Kind == s.Kind))) throw new Exception("此游戏的本地存档位置定义和云端不一致，请从云端导入并重新绑定路径。");
        }
        void SetBaseline(Game game, VersionRecord version) {
            game.BaseVersion = version.Id; game.BaseFingerprint = version.Fingerprint; game.CloudBound = true; data.Save();
        }
        public string Upload(Game game) {
            Ready(game); Progress("检查本地存档…");
            Manifest manifest = Snapshots.Scan(game, false);
            string etag; var index = Index(game, out etag);
            if (index != null) {
                MatchSlots(game, index);
                var heads = index.Versions.Where(v => index.Heads.Contains(v.Id)).ToList();
                DateTime newest = heads.Count == 0 ? DateTime.MinValue : heads.Max(v => VersionTime(v));
                var matching = heads.FirstOrDefault(v => VersionTime(v) == newest && v.Fingerprint == manifest.Fingerprint);
                if (matching != null) { SetBaseline(game, matching); return "内容一致，无需上传。"; }
            }
            if (manifest.Files.Count == 0) throw new Exception("存档为空，已停止上传。请先在游戏中保存一次。");
            Progress("打包并校验存档…"); byte[] archive = Snapshots.Pack(game, manifest);
            if (Snapshots.Scan(game, false).Fingerprint != manifest.Fingerprint) throw new Exception("存档在打包期间发生变化，请退出游戏后重试。");
            var version = new VersionRecord { Id = Guid.NewGuid().ToString("N"), Parent = game.BaseVersion, Device = data.Settings.Device, CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ModifiedUtc = LocalTime(manifest).ToString("o"), Fingerprint = manifest.Fingerprint, ArchiveHash = Snapshots.Hash(archive), FileCount = manifest.Files.Count, Bytes = archive.Length };
            store.EnsureDirectory(Folder(game) + "/snapshots");
            Progress("上传快照…");
            string archivePath = Folder(game) + "/snapshots/" + version.Id + ".zip";
            if (!store.Put(archivePath, archive, null, true)) throw new Exception("快照 ID 意外重复，请重试。");
            var received = store.Get(archivePath);
            if (received == null || Snapshots.Hash(received.Data) != version.ArchiveHash) throw new Exception("上传后的快照校验失败，未发布版本；本地存档保持完整。");
            // Publish only after the immutable archive is fully uploaded and verified.
            for (int attempt = 0; attempt < 5; attempt++) {
                index = Index(game, out etag);
                if (index == null) index = new CloudIndex { GameId = game.Id, Name = game.Name, Slots = game.Slots.Select(s => new Slot { Id = s.Id, Kind = s.Kind }).ToList() };
                MatchSlots(game, index);
                if (version.Parent != null && !index.Versions.Any(v => v.Id == version.Parent)) throw new Exception("上次同步的父版本已不在云端，可能切换了 WebDAV。快照已上传但未发布，请重新绑定游戏。");
                index.Versions.Add(version);
                index.Heads = index.Heads.Where(h => VersionTime(index.Versions.Single(v => v.Id == h)) >= VersionTime(version)).ToList();
                index.Heads.Add(version.Id);
                if (store.Put(Folder(game) + "/index.json", Json.Bytes(index), etag, etag == null)) {
                    SetBaseline(game, version);
                    index = Index(game, out etag);
                    if (index == null || !index.Versions.Any(v => v.Id == version.Id)) throw new Exception("快照已发布，但 WebDAV 列表暂未返回新版本，请稍后检查状态。");
                    string result = "上传成功，历史版本保留；自动同步使用最后修改时间较新的存档。";
                    data.Log("上传", game.Name + " | " + version.Id + " | " + result); return result;
                }
            }
            throw new Exception("云端版本持续变化，未发布快照。请稍后重试；本地存档未丢失。");
        }
        public string Restore(Game game, VersionRecord version) {
            Ready(game);
            string etag; var index = Index(game, out etag);
            if (index == null) throw new Exception("云端没有此游戏。"); MatchSlots(game, index);
            version = index.Versions.SingleOrDefault(v => v.Id == version.Id);
            if (version == null) throw new Exception("找不到此历史版本。");
            Progress("下载并校验快照…");
            var archive = store.Get(Folder(game) + "/snapshots/" + version.Id + ".zip");
            if (archive == null || Snapshots.Hash(archive.Data) != version.ArchiveHash) throw new Exception("下载的快照缺失或校验失败，未修改本地存档。");
            string stage = Path.Combine(data.Root, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try {
                Manifest manifest = Snapshots.Unpack(archive.Data, game, stage);
                if (manifest.Fingerprint != version.Fingerprint) throw new Exception("快照与云端版本记录不一致。");
                foreach (var file in manifest.Files.Where(f => String.IsNullOrEmpty(f.ModifiedUtc))) File.SetLastWriteTimeUtc(Path.Combine(stage, file.Slot, file.Relative.Replace('/', '\\')), VersionTime(version));
                EnsureNotRunning(game); Progress("备份当前本地存档…");
                string backup = Snapshots.Backup(game, data.Root, "before-restore");
                // Detect external edits between backup and installation.
                string before = Snapshots.Scan(game, true).Fingerprint;
                using (var zipped = new System.IO.Compression.ZipArchive(File.OpenRead(backup), System.IO.Compression.ZipArchiveMode.Read))
                using (var reader = new StreamReader(zipped.GetEntry("snapshot.json").Open()))
                    if (Json.Read<Manifest>(reader.ReadToEnd()).Fingerprint != before) throw new Exception("本地存档在备份后发生变化，已取消恢复。");
                EnsureNotRunning(game); Progress("恢复存档…"); Snapshots.ReplaceFromStage(game, stage);
                SetBaseline(game, version);
                data.Log("恢复", game.Name + " | " + version.Id + " | 原存档备份：" + backup);
                return "恢复成功，原存档已备份到：" + backup;
            } finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch {} }
        }
        public string Sync(Game game) {
            Ready(game); string etag; var index = Index(game, out etag);
            if (index == null || index.Heads.Count == 0) return Upload(game);
            MatchSlots(game, index);
            var head = Latest(index);
            bool allMissing = game.Slots.All(s => !Directory.Exists(Snapshots.Full(s.Path)) && !File.Exists(Snapshots.Full(s.Path)));
            if (allMissing) return Restore(game, head);
            var local = Snapshots.Scan(game, false);
            if (local.Fingerprint == head.Fingerprint) { SetBaseline(game, head); return "本地和云端已同步。"; }
            int comparison = LocalTime(local).CompareTo(VersionTime(head));
            if (comparison > 0) return Upload(game);
            if (comparison < 0) return Restore(game, head);
            throw new ConflictException("本地和云端的最后修改时间相同，但内容不同，请选择上传本地或从历史版本下载。");
        }
        public string RestoreLocal(Game game, string archivePath) {
            Ready(game);
            if (new FileInfo(archivePath).Length > Snapshots.MaxBytes) throw new Exception("本地备份超过 512 MiB。");
            byte[] archive = File.ReadAllBytes(archivePath);
            string stage = Path.Combine(data.Root, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try {
                Snapshots.Unpack(archive, game, stage);
                string before = Snapshots.Scan(game, true).Fingerprint;
                string backup = Snapshots.Backup(game, data.Root, "before-local-restore");
                if (Snapshots.Scan(game, true).Fingerprint != before) throw new Exception("本地存档在备份期间变化，已取消恢复。");
                EnsureNotRunning(game); Snapshots.ReplaceFromStage(game, stage);
                // Keep the previous cloud baseline so restored local progress is detected as a local edit.
                data.Log("本地备份恢复", game.Name + " | 原存档备份：" + backup);
                return "本地备份恢复成功，云端未修改。请检查游戏进度后再同步。";
            } finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch {} }
        }
        public string Status(Game game) {
            Ready(game); string etag; var index = Index(game, out etag);
            if (index == null) return "云端暂无存档";
            MatchSlots(game, index);
            if (game.Slots.Any(s => !(s.Kind == "file" ? File.Exists(Snapshots.Full(s.Path)) : Directory.Exists(Snapshots.Full(s.Path))))) return "需要下载 / 部分路径缺失";
            var local = Snapshots.Scan(game, false);
            var head = Latest(index);
            if (local.Fingerprint == head.Fingerprint) return "已同步";
            int comparison = LocalTime(local).CompareTo(VersionTime(head));
            return comparison > 0 ? "本地存档较新，将上传" : comparison < 0 ? "云端存档较新，将下载" : "时间相同，内容不同，需选择";
        }
        public List<CloudIndex> Discover() {
            store.EnsureDirectory("GalgameSaves/games"); var result = new List<CloudIndex>();
            foreach (var id in store.Directories("GalgameSaves/games").Where(Snapshots.IsId)) {
                string etag; var index = Index(new Game { Id = id }, out etag); if (index != null) result.Add(index);
            }
            return result;
        }
        public string ChooseHead(Game game, string versionId) {
            for (int attempt = 0; attempt < 5; attempt++) {
                string etag; var index = Index(game, out etag);
                if (index == null || !index.Versions.Any(v => v.Id == versionId)) throw new Exception("版本不存在。");
                index.Heads = new List<string> { versionId };
                if (store.Put(Folder(game) + "/index.json", Json.Bytes(index), etag, false)) {
                    data.Log("选择云端版本", game.Name + " | " + versionId); return "已设为云端当前版本；其他历史快照仍然保留。请执行同步以更新本地。";
                }
            }
            throw new Exception("云端版本持续变化，请重试。");
        }
        public string WaitForStable(Game game) {
            string previous = null;
            for (int i = 0; i < 10; i++) {
                Thread.Sleep(1500);
                try {
                    string current = Snapshots.Scan(game, false).Fingerprint;
                    if (current == previous) return current; previous = current;
                } catch (IOException) { previous = null; }
            }
            throw new Exception("存档仍在写入或被占用，暂未上传。请稍后手动同步。");
        }
    }
}
