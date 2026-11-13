using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace GalSaveSync {
    public static class Snapshots {
        public const long MaxBytes = 512L * 1024 * 1024;
        public static string Hash(Stream stream) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        public static string Hash(byte[] data) { using (var stream = new MemoryStream(data)) return Hash(stream); }
        public static bool IsId(string id) { Guid parsed; return id != null && id.Length == 32 && Guid.TryParseExact(id, "N", out parsed); }
        public static bool IsHash(string hash) { return hash != null && hash.Length == 64 && hash.All(c => "0123456789abcdef".Contains(c)); }
        static bool Within(string candidate, string parent) { return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        public static string Full(string path) { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path ?? "")).TrimEnd('\\'); }
        public static void NoLinks(string path) {
            string current = path;
            while (!String.IsNullOrEmpty(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new Exception("存档路径不能包含符号链接或目录联接：" + current);
                current = Path.GetDirectoryName(current);
            }
        }
        public static void ValidateGame(Game game, string dataRoot) {
            if (!IsId(game.Id) || String.IsNullOrWhiteSpace(game.Name)) throw new Exception("游戏 ID 或名称无效。");
            if (game.Slots == null || game.Slots.Count == 0 || game.Slots.Count > 32) throw new Exception("请设置 1 到 32 个存档位置。");
            var seen = new List<string>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in game.Slots) {
                if (!IsId(slot.Id) || !ids.Add(slot.Id) || (slot.Kind != "directory" && slot.Kind != "file")) throw new Exception("存档位置配置无效。");
                if (String.IsNullOrWhiteSpace(slot.Path) || !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(slot.Path))) throw new Exception("请选择绝对存档路径。");
                string path = Full(slot.Path); string drive = Path.GetPathRoot(path).TrimEnd('\\');
                if (path == drive || path.StartsWith("\\\\")) throw new Exception("请选择本地具体的存档文件夹或文件，不能选择整个磁盘或网络路径。");
                if (Within(Full(dataRoot), path) || Within(path, Full(dataRoot))) throw new Exception("存档位置不能包含或位于程序的数据目录。");
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!String.IsNullOrEmpty(windows) && Within(path, Full(windows))) throw new Exception("不能把 Windows 系统目录设为存档位置。");
                if (seen.Any(p => Within(path, p) || Within(p, path))) throw new Exception("同一游戏的存档位置不能重叠。");
                if ((slot.Kind == "file" && Directory.Exists(path)) || (slot.Kind == "directory" && File.Exists(path))) throw new Exception("存档类型与路径不一致。");
                NoLinks(path); seen.Add(path);
            }
        }
        static IEnumerable<string> Walk(string root) {
            foreach (string file in Directory.GetFiles(root).OrderBy(p => p, StringComparer.Ordinal)) { NoLinks(file); yield return file; }
            foreach (string dir in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal)) {
                NoLinks(dir); foreach (string file in Walk(dir)) yield return file;
            }
        }
        public static string Fingerprint(Manifest manifest) {
            var text = new StringBuilder("GalSaveSync-1\n");
            foreach (Slot slot in manifest.Slots.OrderBy(s => s.Id, StringComparer.Ordinal)) text.Append(slot.Id).Append(':').Append(slot.Kind).Append('\n');
            foreach (FileItem file in manifest.Files.OrderBy(f => f.Slot + "/" + f.Relative, StringComparer.Ordinal))
                text.Append(file.Slot).Append('/').Append(file.Relative).Append('\0').Append(file.Size).Append(':').Append(file.Hash).Append('\n');
            return Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }
        public static Manifest Scan(Game game, bool allowMissing) {
            var manifest = new Manifest { Format = 1, GameId = game.Id, Slots = game.Slots.Select(s => new Slot { Id = s.Id, Kind = s.Kind }).ToList(), Files = new List<FileItem>() };
            long total = 0;
            foreach (Slot slot in game.Slots) {
                string root = Full(slot.Path); NoLinks(root);
                bool exists = slot.Kind == "directory" ? Directory.Exists(root) : File.Exists(root);
                if (!exists) { if (allowMissing) continue; throw new Exception("存档位置不存在，已停止上传：" + root); }
                IEnumerable<string> paths = slot.Kind == "directory" ? Walk(root) : new [] { root };
                foreach (string path in paths) {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        total += stream.Length;
                        if (total > MaxBytes || manifest.Files.Count >= 50000) throw new Exception("存档超过第一版限制（解压后 512 MiB / 50000 个文件）。");
                        manifest.Files.Add(new FileItem { Slot = slot.Id, Relative = slot.Kind == "file" ? "content" : path.Substring(root.Length + 1).Replace('\\', '/'), Size = stream.Length, Hash = Hash(stream), ModifiedUtc = File.GetLastWriteTimeUtc(path).ToString("o") });
                    }
                }
            }
            manifest.Fingerprint = Fingerprint(manifest); return manifest;
        }
        public static string Source(Game game, FileItem file) {
            Slot slot = game.Slots.Single(s => s.Id == file.Slot);
            return slot.Kind == "file" ? Full(slot.Path) : Path.Combine(Full(slot.Path), file.Relative.Replace('/', '\\'));
        }
        public static byte[] Pack(Game game, Manifest manifest) {
            using (var output = new MemoryStream()) {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) {
                    foreach (FileItem item in manifest.Files) {
                        byte[] bytes;
                        using (var stream = new FileStream(Source(game, item), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                            if (stream.Length != item.Size) throw new Exception("存档仍在写入，请稍后重试。");
                            bytes = new byte[(int)stream.Length]; int pos = 0, n;
                            while ((n = stream.Read(bytes, pos, bytes.Length - pos)) > 0) pos += n;
                        }
                        if (Hash(bytes) != item.Hash) throw new Exception("存档仍在写入，请稍后重试。");
                        using (var entry = zip.CreateEntry("payload/" + item.Slot + "/" + item.Relative, CompressionLevel.Optimal).Open()) entry.Write(bytes, 0, bytes.Length);
                    }
                    byte[] json = Json.Bytes(manifest);
                    using (var entry = zip.CreateEntry("snapshot.json").Open()) entry.Write(json, 0, json.Length);
                }
                if (output.Length > MaxBytes) throw new Exception("压缩包超过 512 MiB 限制。");
                return output.ToArray();
            }
        }
        static void ValidateRelative(string relative) {
            if (String.IsNullOrEmpty(relative) || relative.Length > 220 || relative.Contains('\\') || relative.Split('/').Any(p => String.IsNullOrWhiteSpace(p) || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new Exception("快照包含不安全的文件名。");
            foreach (string part in relative.Split('/')) {
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (new [] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) throw new Exception("快照包含 Windows 保留文件名。");
            }
        }
        public static Manifest Unpack(byte[] bytes, Game game, string stage) {
            using (var input = new MemoryStream(bytes))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read)) {
                var entry = zip.GetEntry("snapshot.json");
                if (entry == null || entry.Length > 16 * 1024 * 1024) throw new Exception("存档快照清单缺失或过大。");
                Manifest manifest;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) manifest = Json.Read<Manifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Format != 1 || manifest.GameId != game.Id || manifest.Files == null || manifest.Slots == null || manifest.Files.Count > 50000) throw new Exception("快照格式或游戏 ID 不匹配。");
                if (manifest.Slots.Count != game.Slots.Count || manifest.Slots.Select(s => s.Id).Distinct().Count() != manifest.Slots.Count || manifest.Slots.Any(s => !game.Slots.Any(local => local.Id == s.Id && local.Kind == s.Kind))) throw new Exception("存档位置定义不匹配，请重新绑定此云端游戏的存档位置。");
                if (Fingerprint(manifest) != manifest.Fingerprint) throw new Exception("快照清单校验失败。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); names.Add("snapshot.json"); long total = 0;
                foreach (FileItem file in manifest.Files) {
                    if (!game.Slots.Any(s => s.Id == file.Slot) || !IsHash(file.Hash) || file.Size < 0) throw new Exception("无效的快照文件记录。");
                    ValidateRelative(file.Relative);
                    if (game.Slots.Single(s => s.Id == file.Slot).Kind == "file" && file.Relative != "content") throw new Exception("单文件存档格式无效。");
                    string name = "payload/" + file.Slot + "/" + file.Relative;
                    if (!names.Add(name)) throw new Exception("快照包含重复路径。");
                    var payload = zip.GetEntry(name); if (payload == null || payload.Length != file.Size) throw new Exception("快照文件缺失或大小错误。");
                    total += file.Size; if (total > MaxBytes) throw new Exception("快照解压大小超过 512 MiB。");
                    string destination = Path.Combine(stage, file.Slot, file.Relative.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var source = payload.Open()) using (var target = new FileStream(destination, FileMode.CreateNew)) {
                        byte[] buffer = new byte[65536]; long copied = 0; int n;
                        while ((n = source.Read(buffer, 0, buffer.Length)) > 0) { copied += n; if (copied > file.Size) throw new Exception("解压数据超过声明大小。"); target.Write(buffer, 0, n); }
                    }
                    using (var check = File.OpenRead(destination)) if (Hash(check) != file.Hash) throw new Exception("快照内容校验失败。");
                    if (!String.IsNullOrEmpty(file.ModifiedUtc)) File.SetLastWriteTimeUtc(destination, SyncEngine.ParseTime(file.ModifiedUtc));
                }
                if (zip.Entries.Count != names.Count || zip.Entries.Any(e => !names.Contains(e.FullName))) throw new Exception("快照包含清单以外的条目。");
                foreach (var slot in game.Slots) Directory.CreateDirectory(Path.Combine(stage, slot.Id));
                return manifest;
            }
        }
        public static string Backup(Game game, string dataRoot, string reason) {
            string dir = Path.Combine(dataRoot, "backups", game.Id); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + reason + ".zip");
            File.WriteAllBytes(path, Pack(game, Scan(game, true))); return path;
        }
        public static void ReplaceFromStage(Game game, string stage) {
            // Keep original directories beside their targets until all target moves succeed.
            var originals = new Dictionary<Slot, string>(); var installed = new List<Slot>();
            try {
                foreach (Slot slot in game.Slots) {
                    string target = Full(slot.Path); NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string original = target + ".galsave-rollback-" + Guid.NewGuid().ToString("N");
                    if (slot.Kind == "file" ? File.Exists(target) : Directory.Exists(target)) {
                        if (slot.Kind == "file") File.Move(target, original); else Directory.Move(target, original);
                        originals.Add(slot, original);
                    }
                    // Copy to a sibling first so the final move stays on the destination volume.
                    string ready = target + ".galsave-stage-" + Guid.NewGuid().ToString("N");
                    try {
                        if (slot.Kind == "file") {
                            string content = Path.Combine(stage, slot.Id, "content");
                            if (File.Exists(content)) { File.Copy(content, ready); File.Move(ready, target); installed.Add(slot); }
                        } else {
                            CopyDirectory(Path.Combine(stage, slot.Id), ready); Directory.Move(ready, target); installed.Add(slot);
                        }
                    } finally { if (File.Exists(ready)) File.Delete(ready); if (Directory.Exists(ready)) Directory.Delete(ready, true); }
                }
            } catch (Exception failure) {
                var rollbackErrors = new List<string>();
                foreach (Slot slot in game.Slots.AsEnumerable().Reverse()) {
                    try {
                        string target = Full(slot.Path);
                        if (installed.Contains(slot)) { if (slot.Kind == "file") File.Delete(target); else Directory.Delete(target, true); }
                        if (originals.ContainsKey(slot)) { if (slot.Kind == "file") File.Move(originals[slot], target); else Directory.Move(originals[slot], target); }
                    } catch (Exception e) { rollbackErrors.Add(e.Message); }
                }
                if (rollbackErrors.Count > 0) throw new Exception("恢复失败，部分路径回滚失败。原文件仍保留在 .galsave-rollback-* 旁路目录，并有本地备份。" + String.Join("; ", rollbackErrors), failure);
                throw new Exception("恢复失败，已回滚原文件：" + failure.Message, failure);
            }
            foreach (var pair in originals) {
                // Failure to remove a rollback copy must not turn a successful restore into an apparent failure.
                try { if (pair.Key.Kind == "file") File.Delete(pair.Value); else Directory.Delete(pair.Value, true); } catch {}
            }
        }
        static void CopyDirectory(string source, string destination) {
            Directory.CreateDirectory(destination);
            foreach (string file in Walk(source)) {
                string target = Path.Combine(destination, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target);
            }
        }
    }
}
