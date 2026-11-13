using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GalSaveSync;
static class TimeSyncTests {
    static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    static void Save(Game game, string content, DateTime time) {
        Directory.CreateDirectory(game.Slots[0].Path); string path = Path.Combine(game.Slots[0].Path, "save.dat");
        File.WriteAllText(path, content); File.SetLastWriteTimeUtc(path, time);
    }
    static int Main(string[] args) {
        try {
            string root = Path.GetFullPath(args[0]);
            var raw = new MemoryStore(); var store = new JournalStore(raw);
            var a = new AppData(Path.Combine(root,"state-a")); var b = new AppData(Path.Combine(root,"state-b"));
            var ga = new Game { Id=Guid.NewGuid().ToString("N"), Name="按时间同步", Slots=new List<Slot>{new Slot{Id=Guid.NewGuid().ToString("N"),Kind="directory",Path=Path.Combine(root,"save-a")}} };
            var gb = Json.Read<Game>(Json.Write(ga)); gb.Slots[0].Path=Path.Combine(root,"save-b");
            a.Settings.Games.Add(ga); b.Settings.Games.Add(gb);
            var ea=new SyncEngine(a,store); var eb=new SyncEngine(b,store);
            DateTime t = new DateTime(2026,11,1,0,0,0,DateTimeKind.Utc);
            Save(ga,"first",t); ea.Sync(ga); eb.Sync(gb);
            Save(ga,"local-newer",t.AddHours(2)); Save(gb,"offline-older",t.AddHours(1));
            ea.Sync(ga); string localVersion=ga.BaseVersion;
            Check(ea.Status(ga)=="已同步" && ga.BaseVersion!=null,"newer local save uploads despite independent device edits");
            eb.Sync(gb);
            string bFile=Path.Combine(gb.Slots[0].Path,"save.dat");
            Check(File.ReadAllText(bFile)=="local-newer" && File.GetLastWriteTimeUtc(bFile)==t.AddHours(2) && Directory.GetFiles(Path.Combine(b.Root,"backups",gb.Id),"*.zip").Length>0,"newer cloud save restores with backup and original modification time");
            Save(gb,"cloud-newest",t.AddHours(4)); eb.Upload(gb);
            // Simulate an additional older parallel cloud head.
            string etag; var index=ea.Index(ga,out etag); index.Heads.Add(localVersion); store.Put("GalgameSaves/games/"+ga.Id+"/index.json",Json.Bytes(index),etag,false);
            ea.Sync(ga); Check(File.ReadAllText(Path.Combine(ga.Slots[0].Path,"save.dat"))=="cloud-newest","multiple cloud heads automatically select the latest save time");
            Save(ga,"different-at-same-time",t.AddHours(4));
            bool conflict=false; try {ea.Sync(ga);} catch(ConflictException) {conflict=true;}
            var old = new VersionRecord{CreatedUtc=t.ToString("o")};
            Check(conflict && File.ReadAllText(Path.Combine(ga.Slots[0].Path,"save.dat"))=="different-at-same-time" && SyncEngine.VersionTime(old)==t,"equal timestamps preserve local content; legacy versions fall back to upload time");
            return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
