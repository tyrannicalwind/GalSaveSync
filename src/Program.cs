using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace GalSaveSync {
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            if (args.Length == 2 && args[0] == "--data") root = Path.GetFullPath(args[1]);
            string lockName = "Local\\GalSaveSync-" + Snapshots.Hash(System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant())).Substring(0, 24);
            bool owns;
            using (var mutex = new Mutex(true, lockName, out owns)) {
                if (!owns) { MessageBox.Show("此数据目录的存档桥已经运行。请使用已打开的窗口。", "存档桥"); return; }
                try {
                    using (var form = new MainForm(new AppData(root))) Application.Run(form);
                } catch (Exception ex) { MessageBox.Show(ex.Message, "存档桥启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
