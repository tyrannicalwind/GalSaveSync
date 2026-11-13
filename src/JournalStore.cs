using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace GalSaveSync {
    // Immutable, content-checked revisions replace the shared mutable index.
    // Two writers can both publish: concurrent leaf revisions become a conflict,
    // rather than one writer overwriting the other's progress.
    public sealed class JournalStore : ICloudStore {
        readonly ICloudStore inner;
        readonly Dictionary<string, Revision> cache = new Dictionary<string, Revision>();
        public JournalStore(ICloudStore inner) { this.inner = inner; }
        public class Revision {
            public int Format { get; set; }
            public string Id { get; set; }
            public List<string> Parents { get; set; }
            public string LegacyHash { get; set; }
            public CloudIndex Index { get; set; }
        }
        class State {
            public CloudIndex Index;
            public string Token, LegacyHash;
            public List<string> Tips = new List<string>();
        }
        static bool IsIndex(string path) { return path.StartsWith("GalgameSaves/games/", StringComparison.Ordinal) && path.EndsWith("/index.json", StringComparison.Ordinal); }
        static string Journal(string path) { return path.Substring(0, path.Length - "index.json".Length) + "journal"; }
        State ReadState(string path) {
            string gameId = path.Split('/')[2];
            var legacy = inner.Get(path);
            CloudIndex legacyIndex = null;
            if (legacy != null) {
                if (legacy.Data.Length > 16 * 1024 * 1024) throw new Exception("旧版云端索引过大。");
                legacyIndex = Json.ReadBytes<CloudIndex>(legacy.Data); SyncEngine.ValidateIndex(legacyIndex, gameId);
            }
            var state = new State { LegacyHash = legacy == null ? null : Snapshots.Hash(legacy.Data) };
            string folder = Journal(path);
            var commits = inner.Files(folder).Where(n => n.Length == 104 && n.EndsWith(".commit", StringComparison.Ordinal) && n[32] == '-' && Snapshots.IsId(n.Substring(0,32)) && Snapshots.IsHash(n.Substring(33,64))).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (commits.Count > 5000) throw new Exception("此游戏的同步修订超过第一版兼容模式限制（5000 次），请保留云端数据并联系开发者。");
            var revisions = new Dictionary<string, Revision>(); long total = 0;
            foreach (string name in commits) {
                string metadataPath = folder + "/" + name.Substring(0,97) + ".json";
                Revision revision;
                if (!cache.TryGetValue(metadataPath, out revision)) {
                    var payload = inner.Get(metadataPath);
                    if (payload == null || payload.Data.Length > 16 * 1024 * 1024 || Snapshots.Hash(payload.Data) != name.Substring(33,64)) throw new Exception("云端同步修订缺失或校验失败，已停止同步；请稍后重试。");
                    revision = Json.ReadBytes<Revision>(payload.Data);
                    if (revision == null || revision.Format != 1 || revision.Id != name.Substring(0,32) || revision.Parents == null || revision.Parents.Any(p => !Snapshots.IsId(p)) || revision.Parents.Distinct().Count() != revision.Parents.Count || (revision.LegacyHash != null && !Snapshots.IsHash(revision.LegacyHash))) throw new Exception("云端同步修订格式无效。");
                    SyncEngine.ValidateIndex(revision.Index, gameId); cache[metadataPath] = revision;
                }
                total += Json.Bytes(revision).Length;
                if (total > 128L * 1024 * 1024) throw new Exception("此游戏的同步修订总量超过 128 MiB，请保留数据并联系开发者。");
                if (revisions.ContainsKey(revision.Id)) throw new Exception("云端存在重复的同步修订 ID。");
                revisions.Add(revision.Id, revision);
            }
            if (legacyIndex == null && revisions.Count == 0) return state;
            // Missing parents can mean the provider's directory listing is lagging. Fail closed.
            if (revisions.Values.Any(r => r.Parents.Any(p => !revisions.ContainsKey(p)))) throw new Exception("WebDAV 目录列表尚未完整返回同步历史，请稍后重试。");
            var remaining = new HashSet<string>(revisions.Keys); var visited = new HashSet<string>();
            while (remaining.Count > 0) {
                var ready = remaining.Where(id => revisions[id].Parents.All(visited.Contains)).ToList();
                if (ready.Count == 0) throw new Exception("云端同步历史存在循环依赖。");
                foreach (var id in ready) { remaining.Remove(id); visited.Add(id); }
            }
            var consumed = new HashSet<string>(revisions.Values.SelectMany(r => r.Parents));
            state.Tips = revisions.Keys.Where(id => !consumed.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var indexes = revisions.Values.Select(r => r.Index).ToList();
            if (legacyIndex != null) indexes.Add(legacyIndex);
            var template = indexes[0];
            foreach (var index in indexes) {
                if (index.Slots.Count != template.Slots.Count || index.Slots.Any(s => !template.Slots.Any(t => t.Id == s.Id && t.Kind == s.Kind))) throw new Exception("云端不同修订的存档位置定义不一致，已停止同步。");
            }
            var versions = new Dictionary<string,VersionRecord>();
            foreach (var version in indexes.SelectMany(i => i.Versions)) {
                VersionRecord existing;
                if (versions.TryGetValue(version.Id, out existing) && Json.Write(existing) != Json.Write(version)) throw new Exception("同一云端版本存在不一致的记录。");
                versions[version.Id] = version;
            }
            var heads = new HashSet<string>(state.Tips.SelectMany(t => revisions[t].Index.Heads));
            // An old client may still update index.json. Preserve that unseen progress as a branch.
            if (legacyIndex != null && !revisions.Values.Any(r => r.LegacyHash == state.LegacyHash)) foreach (var head in legacyIndex.Heads) heads.Add(head);
            state.Index = new CloudIndex { GameId = gameId, Name = template.Name, Slots = template.Slots, Versions = versions.Values.OrderBy(v => v.CreatedUtc, StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal).ToList(), Heads = heads.OrderBy(h => h, StringComparer.Ordinal).ToList() };
            SyncEngine.ValidateIndex(state.Index, gameId);
            state.Token = "\"journal-" + Snapshots.Hash(Encoding.UTF8.GetBytes(String.Join("\n", commits) + "\nlegacy:" + state.LegacyHash)) + "\"";
            return state;
        }
        public RemoteObject Get(string path) {
            if (!IsIndex(path)) return inner.Get(path);
            var state = ReadState(path);
            return state.Index == null ? null : new RemoteObject { Data = Json.Bytes(state.Index), ETag = state.Token };
        }
        public bool Put(string path, byte[] bytes, string etag, bool createOnly) {
            if (!IsIndex(path)) return inner.Put(path, bytes, etag, createOnly);
            var state = ReadState(path);
            if (createOnly ? state.Index != null : state.Token != etag) return false;
            var index = Json.ReadBytes<CloudIndex>(bytes); SyncEngine.ValidateIndex(index, path.Split('/')[2]);
            if (state.Index != null && state.Index.Versions.Any(v => !index.Versions.Any(n => n.Id == v.Id))) throw new Exception("拒绝丢弃云端历史版本。");
            var revision = new Revision { Format = 1, Id = Guid.NewGuid().ToString("N"), Parents = state.Tips, LegacyHash = state.LegacyHash, Index = index };
            byte[] payload = Json.Bytes(revision);
            string folder = Journal(path); inner.EnsureDirectory(folder);
            string stem = folder + "/" + revision.Id + "-" + Snapshots.Hash(payload);
            if (!inner.Put(stem + ".json", payload, null, true)) throw new Exception("同步修订 ID 意外重复，请重试。");
            var received = inner.Get(stem + ".json");
            if (received == null || !received.Data.SequenceEqual(payload)) throw new Exception("同步修订上传校验失败，未发布；本地存档保留。");
            // A zero-byte commit resource is created only after the metadata was verified.
            // Incomplete metadata uploads have no commit and are never consumed by readers.
            if (!inner.Put(stem + ".commit", new byte[0], null, true)) throw new Exception("同步修订发布失败。");
            var committed = inner.Get(stem + ".commit");
            if (committed == null || committed.Data.Length != 0) throw new Exception("同步修订发布确认失败，请稍后检查同步状态。");
            return true;
        }
        public void EnsureDirectory(string path) { inner.EnsureDirectory(path); }
        public List<string> Directories(string path) { return inner.Directories(path); }
        public List<string> Files(string path) { return inner.Files(path); }
        public void Dispose() { inner.Dispose(); }
    }
}
