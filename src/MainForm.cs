using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;

namespace GalSaveSync {
    public sealed class MainForm : Form {
        readonly AppData data;
        readonly ListView games = new ListView();
        readonly TextBox activity = new TextBox();
        readonly Label status = new Label();
        readonly FlowLayoutPanel toolbar = new FlowLayoutPanel();
        readonly List<Button> actions = new List<Button>();
        readonly Timer timer = new Timer();
        bool busy;
        Process playing;
        Game playingGame;
        public MainForm(AppData data) {
            this.data = data;
            Text = "存档桥 · GalSaveSync"; Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(1060, 680); MinimumSize = new Size(900, 600); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);
            var heading = new Label { Text = "存档桥", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), AutoSize = true, Location = new Point(22, 18) };
            var subtitle = new Label { Text = "WebDAV 多游戏存档同步  ·  启动前同步 / 退出后备份上传", AutoSize = true, Location = new Point(24, 65), ForeColor = Color.DimGray };
            toolbar.SetBounds(20, 98, 1020, 88); toolbar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; toolbar.WrapContents = true;
            AddButton("WebDAV 设置", delegate { ShowSettings(); });
            AddButton("添加游戏", delegate { EditGame(null); });
            AddButton("编辑路径", delegate { if (Selected != null) EditGame(Selected); });
            AddButton("导入云端游戏", async delegate { await ImportGames(); });
            AddButton("检查全部", async delegate { await CheckAll(); });
            AddButton("同步所选", async delegate { await SelectedOperation((e,g) => e.Sync(g)); });
            AddButton("上传本地", async delegate { await UploadSelected(); });
            AddButton("历史 / 下载", async delegate { await ShowHistory(); });
            AddButton("同步并启动", async delegate { await Launch(); });
            AddButton("本地备份", async delegate { var g = Selected; if (g != null) await Run(async delegate { return await Task.Run(() => { Snapshots.ValidateGame(g, data.Root); SyncEngine.EnsureNotRunning(g); return "本地备份已保存：" + Snapshots.Backup(g, data.Root, "manual"); }); }); });
            AddButton("恢复本地备份", async delegate { await RestoreLocalBackup(); });
            AddButton("打开数据目录", delegate { Process.Start("explorer.exe", data.Root); });
            AddButton("移除本地条目", delegate { RemoveSelected(); });
            games.SetBounds(20, 194, 1020, 265); games.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            games.View = View.Details; games.FullRowSelect = true; games.MultiSelect = false; games.HideSelection = false;
            games.Columns.Add("游戏", 230); games.Columns.Add("状态", 330); games.Columns.Add("存档位置", 410);
            games.DoubleClick += async delegate { if (!busy && Selected != null) await Launch(); };
            var hint = new Label { Text = "首次使用：设置 WebDAV → 添加游戏（另一台电脑选“导入云端游戏”）→ 同步并启动。", AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Location = new Point(22, 473) };
            activity.SetBounds(20, 501, 1020, 135); activity.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            activity.Multiline = true; activity.ReadOnly = true; activity.ScrollBars = ScrollBars.Vertical; activity.BackColor = Color.White;
            status.SetBounds(22, 648, 1010, 24); status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; status.Text = "就绪 · 仅支持普通文件 / 文件夹存档，选择实际游戏 EXE。";
            Controls.AddRange(new Control[] { heading, subtitle, toolbar, games, hint, activity, status });
            timer.Interval = 2000; timer.Tick += async delegate { await PollGame(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (busy) { e.Cancel = true; MessageBox.Show(this, "操作正在进行，请等待完成后再关闭。", "请稍候"); return; }
                if (playing != null && MessageBox.Show(this, "游戏仍在运行。关闭客户端后无法在退出时自动上传。确定关闭？", "关闭客户端", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
            };
            Reload(); Append("程序已启动。运行日志和恢复前备份保存在 Data 文件夹。尚未连接 WebDAV。");
        }
        Game Selected { get { return games.SelectedItems.Count == 0 ? null : games.SelectedItems[0].Tag as Game; } }
        void AddButton(string text, EventHandler handler) {
            var button = new Button { Text = text, AutoSize = true, Height = 34, Margin = new Padding(0, 0, 7, 7), Padding = new Padding(8, 2, 8, 2), FlatStyle = FlatStyle.Standard };
            button.Click += handler; toolbar.Controls.Add(button); actions.Add(button);
        }
        void SetEnabled() { foreach (var button in actions) button.Enabled = !busy && playing == null; }
        void Append(string text) {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), text); return; }
            activity.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); status.Text = text.Replace(Environment.NewLine, " ");
        }
        void Reload() {
            string selected = Selected == null ? null : Selected.Id; games.Items.Clear();
            foreach (var game in data.Settings.Games) {
                var item = new ListViewItem(new [] { game.Name, "待检查", String.Join("; ", game.Slots.Select(s => s.Path)) }) { Tag = game };
                games.Items.Add(item); if (game.Id == selected) item.Selected = true;
            }
            if (games.SelectedItems.Count == 0 && games.Items.Count > 0) games.Items[0].Selected = true;
        }
        void SetStatus(Game game, string text) { foreach (ListViewItem item in games.Items) if (((Game)item.Tag).Id == game.Id) item.SubItems[1].Text = text; }
        async Task Run(Func<Task<string>> operation) {
            if (busy || playing != null) return;
            busy = true; SetEnabled();
            try { string message = await operation(); if (!String.IsNullOrEmpty(message)) Append(message); }
            catch (Exception ex) {
                Append(ex.Message);
                try { data.Log("操作失败", ex.Message); } catch {}
                MessageBox.Show(this, ex.Message, ex is ConflictException ? "需要选择存档" : "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            } finally { busy = false; SetEnabled(); }
        }
        ICloudStore NewStore() {
            if (String.IsNullOrWhiteSpace(data.Settings.Url)) throw new Exception("请先设置 WebDAV 地址和账号。");
            return new JournalStore(new WebDavStore(data.Settings.Url, data.Settings.Username, data.Password()));
        }
        async Task<string> OnEngine(Func<SyncEngine,string> action) {
            return await Task.Run(() => { using (var store = NewStore()) { var engine = new SyncEngine(data, store); engine.Progress = Append; return action(engine); } });
        }
        async Task SelectedOperation(Func<SyncEngine,Game,string> action) {
            var game = Selected; if (game == null) return;
            await Run(async delegate { string result = await OnEngine(e => action(e, game)); SetStatus(game, result); return game.Name + "：" + result; });
        }
        async Task UploadSelected() {
            if (Selected == null) return;
            if (MessageBox.Show(this, "上传本地进度并保留历史。自动同步按存档最后修改时间选择较新的版本。继续？", "上传本地", MessageBoxButtons.YesNo) == DialogResult.Yes) await SelectedOperation((e,g) => e.Upload(g));
        }
        async Task RestoreLocalBackup() {
            var game = Selected; if (game == null) return;
            using (var pick = new OpenFileDialog { Filter = "存档桥备份 (*.zip)|*.zip", InitialDirectory = Path.Combine(data.Root, "backups", game.Id) }) {
                if (pick.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "将备份并替换此游戏的本地存档；云端保持原样。继续？", "恢复本地备份", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                await Run(async delegate { return await Task.Run(() => {
                    using (var unused = new OfflineStore()) return new SyncEngine(data, unused).RestoreLocal(game, pick.FileName);
                }); });
            }
        }
        void ShowSettings() { using (var form = new SettingsForm(data)) if (form.ShowDialog(this) == DialogResult.OK) Append("WebDAV 设置已保存。请点击“检查全部”或“同步所选”。"); }
        void EditGame(Game game) {
            using (var form = new GameForm(game, data)) if (form.ShowDialog(this) == DialogResult.OK) {
                if (game == null) data.Settings.Games.Add(form.Result);
                else data.Settings.Games[data.Settings.Games.IndexOf(game)] = form.Result;
                data.Save(); Reload(); Append("游戏配置已保存。");
            }
        }
        void RemoveSelected() {
            var game = Selected; if (game == null) return;
            if (MessageBox.Show(this, "仅移除此电脑上的游戏配置。云端快照、本地存档和备份都会保留。继续？", "移除 " + game.Name, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            data.Settings.Games.Remove(game); data.Save(); Reload();
        }
        async Task CheckAll() {
            await Run(async delegate {
                foreach (var game in data.Settings.Games) {
                    try { SetStatus(game, await OnEngine(e => e.Status(game))); }
                    catch (Exception ex) { SetStatus(game, ex.Message); }
                }
                data.Log("检查全部", "已检查 " + data.Settings.Games.Count + " 款游戏"); return "全部检查完成，详见游戏列表。";
            });
        }
        async Task ImportGames() {
            List<CloudIndex> discovered = null;
            await Run(async delegate {
                await Task.Run(() => { using (var store = NewStore()) discovered = new SyncEngine(data, store).Discover(); });
                return "找到 " + discovered.Count + " 款云端游戏。";
            });
            if (discovered == null) return;
            var available = discovered.Where(c => !data.Settings.Games.Any(g => g.Id == c.GameId)).ToList();
            if (available.Count == 0) { Append("没有尚未导入的云端游戏。"); return; }
            using (var dialog = new Form { Text = "选择要导入的云端游戏", Size = new Size(520, 430), StartPosition = FormStartPosition.CenterParent, Font = Font }) {
                var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = "Name" }; foreach (var cloud in available) list.Items.Add(cloud);
                var button = new Button { Text = "导入并设置本机路径", Dock = DockStyle.Bottom, Height = 40 };
                button.Click += delegate {
                    var cloud = list.SelectedItem as CloudIndex; if (cloud == null) return;
                    var imported = new Game { Id = cloud.GameId, Name = cloud.Name, CloudBound = true, Slots = cloud.Slots.Select(s => new Slot { Id = s.Id, Kind = s.Kind, Path = "" }).ToList() };
                    using (var edit = new GameForm(imported, data)) if (edit.ShowDialog(dialog) == DialogResult.OK) { data.Settings.Games.Add(edit.Result); data.Save(); dialog.DialogResult = DialogResult.OK; }
                };
                dialog.Controls.Add(list); dialog.Controls.Add(button); if (list.Items.Count > 0) list.SelectedIndex = 0; dialog.ShowDialog(this);
            }
            Reload();
        }
        async Task ShowHistory() {
            var game = Selected; if (game == null) return;
            CloudIndex index = null;
            await Run(async delegate { return await OnEngine(e => { string tag; index = e.Index(game, out tag); return index == null ? "云端尚无存档。" : "已读取历史版本。"; }); });
            if (index == null) return;
            using (var dialog = new Form { Text = game.Name + " · 历史版本", Size = new Size(900, 510), StartPosition = FormStartPosition.CenterParent, Font = Font }) {
                var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
                list.Columns.Add("时间（本机时区）", 190); list.Columns.Add("设备", 190); list.Columns.Add("文件数", 70); list.Columns.Add("状态", 125); list.Columns.Add("版本 ID", 280);
                foreach (var version in index.Versions.AsEnumerable().Reverse()) {
                    DateTime time; string timeText = DateTime.TryParse(version.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out time) ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : version.CreatedUtc;
                    var row = new ListViewItem(new [] { timeText, version.Device, version.FileCount.ToString(), index.Heads.Contains(version.Id) ? "云端当前 / 分支" : "历史", version.Id }) { Tag = version }; list.Items.Add(row);
                }
                var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 85 };
                var restore = new Button { Text = "下载恢复到本机", AutoSize = true, Height = 34 };
                var current = new Button { Text = "设为云端当前版本", AutoSize = true, Height = 34 };
                var info = new Label { Text = "下载前自动备份。选择云端当前版本会解决分支，其他快照保留；本地随后需同步。", AutoSize = true, Padding = new Padding(4, 8, 0, 0) };
                VersionRecord chosen = null; bool chooseHead = false;
                restore.Click += delegate { if (list.SelectedItems.Count == 0) return; if (MessageBox.Show(dialog, "将备份并替换当前电脑的此游戏存档。继续？", "恢复存档", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { chosen = (VersionRecord)list.SelectedItems[0].Tag; dialog.DialogResult = DialogResult.OK; } };
                current.Click += delegate { if (list.SelectedItems.Count == 0) return; if (MessageBox.Show(dialog, "其他设备将以此版本为云端当前进度。其他分支保留在历史记录。继续？", "选择云端进度", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { chosen = (VersionRecord)list.SelectedItems[0].Tag; chooseHead = true; dialog.DialogResult = DialogResult.OK; } };
                panel.Controls.AddRange(new Control[] { restore, current, info }); dialog.Controls.Add(list); dialog.Controls.Add(panel);
                if (dialog.ShowDialog(this) == DialogResult.OK && chosen != null) {
                    await Run(async delegate { string result = await OnEngine(e => chooseHead ? e.ChooseHead(game, chosen.Id) : e.Restore(game, chosen)); SetStatus(game, result); return result; });
                }
            }
        }
        async Task Launch() {
            var game = Selected; if (game == null || busy || playing != null) return;
            if (String.IsNullOrEmpty(game.Executable) || !File.Exists(game.Executable)) { MessageBox.Show(this, "请先在“编辑路径”中设置实际游戏 EXE。", "无法启动"); return; }
            await Run(async delegate {
                bool offline = false;
                try { Append(await OnEngine(e => e.Sync(game))); }
                catch (ConflictException) { throw; }
                catch (Exception ex) {
                    if (MessageBox.Show(this, "同步未完成：" + ex.Message + "\n\n是否继续使用本地存档离线游玩？退出后会尝试上传。", "离线启动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) throw;
                    offline = true;
                }
                Snapshots.ValidateGame(game, data.Root); SyncEngine.EnsureNotRunning(game);
                playing = Process.Start(new ProcessStartInfo(game.Executable) { WorkingDirectory = Path.GetDirectoryName(game.Executable), UseShellExecute = true });
                if (playing == null) throw new Exception("没有获得游戏进程，无法监控退出，请手动同步。");
                playingGame = game; timer.Start(); SetStatus(game, "运行中（退出后上传）"); data.Log("启动", game.Name + (offline ? " | 离线" : " | 已同步")); return "已启动 " + game.Name + "。请保持客户端打开，退出游戏后会自动上传。";
            });
        }
        async Task PollGame() {
            if (playing == null || busy || !playing.HasExited) return;
            timer.Stop(); playing.Dispose(); playing = null; var game = playingGame; playingGame = null;
            await Run(async delegate {
                string result = await OnEngine(e => { Append("游戏已退出，等待存档写入完成…"); e.WaitForStable(game); return e.Sync(game); });
                SetStatus(game, result); return game.Name + "：" + result;
            });
        }
    }

    sealed class OfflineStore : ICloudStore {
        public RemoteObject Get(string path) { throw new InvalidOperationException("此操作不连接云端。"); }
        public bool Put(string path, byte[] bytes, string etag, bool createOnly) { throw new InvalidOperationException("此操作不连接云端。"); }
        public void EnsureDirectory(string path) { throw new InvalidOperationException("此操作不连接云端。"); }
        public List<string> Directories(string path) { throw new InvalidOperationException("此操作不连接云端。"); }
        public List<string> Files(string path) { throw new InvalidOperationException("此操作不连接云端。"); }
        public void Dispose() {}
    }

    public sealed class SettingsForm : Form {
        readonly TextBox url = new TextBox(), username = new TextBox(), password = new TextBox();
        public SettingsForm(AppData data) {
            Text = "WebDAV 设置"; ClientSize = new Size(650, 320); StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            AddField("WebDAV 文件夹地址", url, 25); AddField("用户名", username, 90); AddField("密码 / 应用专用密码", password, 145); password.UseSystemPasswordChar = true;
            url.Text = data.Settings.Url; username.Text = data.Settings.Username;
            try { password.Text = data.Password(); } catch {}
            var hint = new Label { Text = "程序会在此地址下创建 GalgameSaves 文件夹。密码由当前 Windows 用户加密保存。", Location = new Point(22, 205), AutoSize = true };
            var test = new Button { Text = "测试连接和读写", Location = new Point(22, 257), Size = new Size(190, 35) };
            var save = new Button { Text = "保存", Location = new Point(525, 257), Size = new Size(100, 35) };
            bool testing = false;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (testing) e.Cancel = true; };
            test.Click += async delegate {
                string endpoint = url.Text.Trim(), user = username.Text, pass = password.Text;
                testing = true; test.Enabled = save.Enabled = url.Enabled = username.Enabled = password.Enabled = false;
                try { await Task.Run(() => { using (var store = new WebDavStore(endpoint, user, pass)) store.TestConnection(); }); MessageBox.Show(this, "连接、上传下载和目录列表检查通过。同步使用独立修订记录，无需 ETag。", "测试成功"); }
                catch (Exception ex) { try { data.Log("连接测试失败", ex.Message); } catch {} MessageBox.Show(this, ex.Message, "测试未通过"); }
                finally { testing = false; test.Enabled = save.Enabled = url.Enabled = username.Enabled = password.Enabled = true; }
            };
            save.Click += delegate {
                try {
                    using (var store = new WebDavStore(url.Text.Trim(), username.Text, password.Text)) {}
                    bool changed = !String.IsNullOrEmpty(data.Settings.Url) && (data.Settings.Url != url.Text.Trim() || data.Settings.Username != username.Text);
                    if (changed && MessageBox.Show(this, "更换存储地址或账号后，会清除本地的上次同步版本记录。存档、云端历史和备份保留。继续？", "更换存储", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    data.Settings.Url = url.Text.Trim(); data.Settings.Username = username.Text; data.Settings.ProtectedPassword = AppData.Protect(password.Text);
                    if (changed) foreach (var game in data.Settings.Games) { game.BaseVersion = null; game.BaseFingerprint = null; }
                    data.Save(); data.Log("设置", "WebDAV 配置已更新（不记录凭据）"); DialogResult = DialogResult.OK;
                } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            };
            Controls.AddRange(new Control[] { hint, test, save });
        }
        void AddField(string label, TextBox input, int y) { Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(22, y) }); input.SetBounds(205, y - 3, 420, 28); Controls.Add(input); }
    }

    public sealed class GameForm : Form {
        readonly TextBox name = new TextBox(), executable = new TextBox();
        readonly DataGridView slots = new DataGridView();
        readonly Game original;
        readonly AppData data;
        public Game Result;
        public GameForm(Game game, AppData data) {
            this.data = data; original = game == null ? new Game { Id = Guid.NewGuid().ToString("N") } : Json.Read<Game>(Json.Write(game));
            Text = game == null ? "添加游戏" : "编辑游戏 / 绑定本机路径"; ClientSize = new Size(820, 520); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9);
            Controls.Add(new Label { Text = "游戏名称", Location = new Point(20, 25), AutoSize = true }); name.SetBounds(120, 22, 670, 28); name.Text = original.Name;
            Controls.Add(new Label { Text = "游戏 EXE", Location = new Point(20, 73), AutoSize = true }); executable.SetBounds(120, 70, 565, 28); executable.Text = original.Executable;
            var browse = new Button { Text = "浏览", Location = new Point(700, 67), Size = new Size(90, 30) };
            browse.Click += delegate { using (var file = new OpenFileDialog { Filter = "游戏程序 (*.exe)|*.exe" }) if (file.ShowDialog(this) == DialogResult.OK) executable.Text = file.FileName; };
            slots.SetBounds(20, 130, 770, 235); slots.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            slots.AllowUserToAddRows = false; slots.AllowUserToDeleteRows = false; slots.MultiSelect = false; slots.SelectionMode = DataGridViewSelectionMode.FullRowSelect; slots.RowHeadersVisible = false; slots.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            slots.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "类型", ReadOnly = true, FillWeight = 20 });
            slots.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "本机存档绝对路径（可直接输入尚不存在的路径）", FillWeight = 80 });
            foreach (Slot slot in original.Slots) AddSlot(slot);
            var folder = new Button { Text = "添加文件夹", Location = new Point(20, 383), AutoSize = true };
            var fileButton = new Button { Text = "添加单个文件", Location = new Point(140, 383), AutoSize = true };
            var remove = new Button { Text = "删除位置", Location = new Point(280, 383), AutoSize = true };
            var rebind = new Button { Text = "浏览所选路径", Location = new Point(400, 383), AutoSize = true };
            folder.Enabled = fileButton.Enabled = remove.Enabled = !original.CloudBound;
            folder.Click += delegate { using (var pick = new FolderBrowserDialog { Description = "选择具体的存档文件夹" }) if (pick.ShowDialog(this) == DialogResult.OK) AddSlot(new Slot { Id = Guid.NewGuid().ToString("N"), Kind = "directory", Path = pick.SelectedPath }); };
            fileButton.Click += delegate { using (var pick = new OpenFileDialog { Filter = "所有文件|*.*" }) if (pick.ShowDialog(this) == DialogResult.OK) AddSlot(new Slot { Id = Guid.NewGuid().ToString("N"), Kind = "file", Path = pick.FileName }); };
            remove.Click += delegate { if (slots.SelectedRows.Count > 0) slots.Rows.Remove(slots.SelectedRows[0]); };
            rebind.Click += delegate {
                if (slots.SelectedRows.Count == 0) return; var row = slots.SelectedRows[0]; var slot = (Slot)row.Tag;
                if (slot.Kind == "directory") { using (var pick = new FolderBrowserDialog()) if (pick.ShowDialog(this) == DialogResult.OK) row.Cells[1].Value = pick.SelectedPath; }
                else { using (var pick = new OpenFileDialog { Filter = "所有文件|*.*" }) if (pick.ShowDialog(this) == DialogResult.OK) row.Cells[1].Value = pick.FileName; }
            };
            var hint = new Label { Text = original.CloudBound ? "已绑定云端：位置编号和类型固定，可修改本机路径。新增存档位置请新建独立游戏条目。" : "可添加多个位置。请选择存档目录，避免选中整个游戏安装目录。", AutoSize = true, Location = new Point(20, 430) };
            var save = new Button { Text = "保存配置", Location = new Point(660, 465), Size = new Size(130, 35) };
            save.Click += delegate {
                try {
                    slots.EndEdit(); original.Name = name.Text.Trim(); original.Executable = executable.Text.Trim(); original.Slots.Clear();
                    foreach (DataGridViewRow row in slots.Rows) { var slot = (Slot)row.Tag; original.Slots.Add(new Slot { Id = slot.Id, Kind = slot.Kind, Path = Convert.ToString(row.Cells[1].Value).Trim() }); }
                    Snapshots.ValidateGame(original, data.Root);
                    foreach (var other in data.Settings.Games.Where(g => g.Id != original.Id)) foreach (var a in original.Slots) foreach (var b in other.Slots) {
                        string x = Snapshots.Full(a.Path) + "\\", y = Snapshots.Full(b.Path) + "\\";
                        if (x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase)) throw new Exception("此路径与游戏“" + other.Name + "”的存档位置重叠，请分开配置。");
                    }
                    if (!String.IsNullOrEmpty(original.Executable) && (!File.Exists(original.Executable) || !original.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) throw new Exception("请选择存在的游戏 EXE，或暂时留空。");
                    Result = original; DialogResult = DialogResult.OK;
                } catch (Exception ex) { MessageBox.Show(this, ex.Message, "配置未保存"); }
            };
            Controls.AddRange(new Control[] { name, executable, browse, slots, folder, fileButton, remove, rebind, hint, save });
        }
        void AddSlot(Slot slot) { int row = slots.Rows.Add(slot.Kind == "file" ? "单个文件" : "文件夹", slot.Path); slots.Rows[row].Tag = slot; }
    }
}
