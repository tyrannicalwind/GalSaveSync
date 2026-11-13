// Executes the actual form event flow against a toy game and local WebDAV.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using GalSaveSync;
static class UiFlowTests {
    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var data = new AppData(Path.Combine(args[0], "ui-state"));
        data.Settings.Url = args[1]; data.Settings.Username = "tester"; data.Settings.ProtectedPassword = AppData.Protect("test-only");
        string saveRoot = Path.Combine(Path.GetDirectoryName(args[2]), "fixture-game-save"); Directory.CreateDirectory(saveRoot);
        File.WriteAllText(Path.Combine(saveRoot, "save.dat"), "before-game-launch");
        var game = new Game { Id = Guid.NewGuid().ToString("N"), Name = "自动启动链路测试", Executable = args[2] };
        game.Slots.Add(new Slot { Id = Guid.NewGuid().ToString("N"), Kind = "directory", Path = saveRoot }); data.Settings.Games.Add(game);
        try {
            using (var store = new JournalStore(new WebDavStore(args[1], "tester", "test-only"))) new SyncEngine(data, store).Upload(game);
            string before = game.BaseVersion;
            int result = 1;
            using (var form = new MainForm(data)) {
                var watchdog = new System.Threading.Timer(delegate { Console.Error.WriteLine("FAIL: UI flow timed out"); Environment.Exit(1); }, null, 30000, System.Threading.Timeout.Infinite);
                form.Shown += async delegate {
                    try {
                        var list = (ListView)typeof(MainForm).GetField("games", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); list.Items[0].Selected = true;
                        var launch = typeof(MainForm).GetMethod("Launch", BindingFlags.NonPublic | BindingFlags.Instance);
                        await (Task)launch.Invoke(form, null);
                        for (int i = 0; i < 80 && game.BaseVersion == before; i++) await Task.Delay(200);
                        if (game.BaseVersion == before) throw new Exception("Automatic upload did not publish a new version.");
                        using (var store = new JournalStore(new WebDavStore(args[1], "tester", "test-only"))) {
                            string etag; var index = new SyncEngine(data, store).Index(game, out etag);
                            if (index.Versions.Count != 2 || index.Heads.Single() != game.BaseVersion || index.Versions.Last().Fingerprint != Snapshots.Scan(game, false).Fingerprint) throw new Exception("Cloud version does not match the game's final save.");
                        }
                        if (File.ReadAllText(Path.Combine(saveRoot, "save.dat")) != "progress-after-game-exit") throw new Exception("Toy game failed to save.");
                        Console.WriteLine("PASS: actual MainForm launch -> toy game writes save -> exit timer -> stable scan -> automatic WebDAV upload");
                        Console.WriteLine("PASS: uploaded version matches post-exit save and history contains both versions");
                        result = 0;
                    } catch (Exception ex) { Console.Error.WriteLine(ex); }
                    finally { watchdog.Dispose(); form.Close(); }
                };
                Application.Run(form);
            }
            return result;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
