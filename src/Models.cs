using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace GalSaveSync {
    public static class Json {
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(value); }
        public static T Read<T>(string text) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<T>(text); }
        public static T ReadBytes<T>(byte[] bytes) { return Read<T>(Encoding.UTF8.GetString(bytes)); }
        public static byte[] Bytes(object value) { return Encoding.UTF8.GetBytes(Write(value)); }
        public static void Save(string path, object value) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, Write(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
    }
    public class Slot {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
    }
    public class Game {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Executable { get; set; }
        public List<Slot> Slots { get; set; }
        public string BaseVersion { get; set; }
        public string BaseFingerprint { get; set; }
        public bool CloudBound { get; set; }
        public Game() { Slots = new List<Slot>(); }
        public override string ToString() { return Name; }
    }
    public class Settings {
        public string Url { get; set; }
        public string Username { get; set; }
        public string ProtectedPassword { get; set; }
        public string Device { get; set; }
        public List<Game> Games { get; set; }
        public Settings() { Games = new List<Game>(); Device = Environment.MachineName + "-" + Guid.NewGuid().ToString("N").Substring(0, 6); }
    }
    public class FileItem {
        public string Slot { get; set; }
        public string Relative { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
        public string ModifiedUtc { get; set; }
    }
    public class Manifest {
        public int Format { get; set; }
        public string GameId { get; set; }
        public List<Slot> Slots { get; set; }
        public List<FileItem> Files { get; set; }
        public string Fingerprint { get; set; }
    }
    public class VersionRecord {
        public string Id { get; set; }
        public string Parent { get; set; }
        public string Device { get; set; }
        public string CreatedUtc { get; set; }
        public string ModifiedUtc { get; set; }
        public string Fingerprint { get; set; }
        public string ArchiveHash { get; set; }
        public int FileCount { get; set; }
        public long Bytes { get; set; }
        public override string ToString() { return CreatedUtc + " | " + Device + " | " + Id.Substring(0, Math.Min(8, Id.Length)); }
    }
    public class CloudIndex {
        public int Format { get; set; }
        public string GameId { get; set; }
        public string Name { get; set; }
        public List<Slot> Slots { get; set; }
        public List<string> Heads { get; set; }
        public List<VersionRecord> Versions { get; set; }
        public CloudIndex() { Format = 1; Slots = new List<Slot>(); Heads = new List<string>(); Versions = new List<VersionRecord>(); }
    }
    public class RemoteObject {
        public byte[] Data;
        public string ETag;
    }
    public class ConflictException : Exception { public ConflictException(string text) : base(text) {} }
    public class AppData {
        public readonly string Root;
        public Settings Settings;
        public AppData(string root) {
            Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
            string path = Path.Combine(Root, "settings.json");
            if (File.Exists(path)) {
                try { Settings = Json.Read<Settings>(File.ReadAllText(path)); }
                catch { throw new Exception("配置文件无法读取。请保留 settings.json，并检查 settings.json.bak，避免丢失同步记录。"); }
                if (Settings == null || Settings.Games == null) throw new Exception("配置文件不完整。");
            } else Settings = new Settings();
        }
        public void Save() { Json.Save(Path.Combine(Root, "settings.json"), Settings); }
        public void Log(string action, string message) {
            lock (this) File.AppendAllText(Path.Combine(Root, "application.log"), DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " | " + action + " | " + message.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine, Encoding.UTF8);
        }
        public static string Protect(string password) {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        }
        public string Password() {
            if (String.IsNullOrEmpty(Settings.ProtectedPassword)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Settings.ProtectedPassword), null, DataProtectionScope.CurrentUser)); }
            catch { throw new Exception("密码无法解密，请在此 Windows 用户下重新填写 WebDAV 密码。"); }
        }
    }
}
