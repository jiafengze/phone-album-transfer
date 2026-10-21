using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace PhoneAlbum
{
    // ======================= 数据模型 =======================

    public class MediaItem
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public long DateAdded { get; set; }
        public string RelPath { get; set; }
        public string Bucket { get; set; }
        public string Mime { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long DurationMs { get; set; }
        public bool IsVideo { get; set; }
        // MTP 模式下保存 Shell 的 FolderItem，传输时要用
        public object Native;

        public DateTime LocalDate
        {
            get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(DateAdded).ToLocalTime(); }
        }

        public string RemotePath
        {
            get { return "/sdcard/" + RelPath + Name; }
        }
    }

    public class Album
    {
        public string Key;
        public string Title;
        public List<MediaItem> Items = new List<MediaItem>();
        public string Group = "相册";

        public long TotalSize
        {
            get { long s = 0; for (int i = 0; i < Items.Count; i++) s += Items[i].Size; return s; }
        }

        public override string ToString()
        {
            return Title + "  (" + Items.Count + " · " + Util.Size(TotalSize) + ")";
        }
    }

    public static class Util
    {
        public static string Size(long b)
        {
            if (b >= 1073741824L) return (b / 1073741824.0).ToString("0.00") + " GB";
            if (b >= 1048576L) return (b / 1048576.0).ToString("0.0") + " MB";
            if (b >= 1024L) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        public static string Duration(long ms)
        {
            if (ms <= 0) return "";
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + ":" + t.ToString("mm\\:ss");
            return t.ToString("m\\:ss");
        }

        public static string Sanitize(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (Array.IndexOf(bad, name[i]) >= 0) sb.Append('_');
                else sb.Append(name[i]);
            }
            string r = sb.ToString().Trim().TrimEnd('.');
            return r.Length == 0 ? "未命名" : r;
        }

        public static string AlbumTitle(string relPath)
        {
            string p = relPath == null ? "" : relPath.TrimEnd('/');
            if (p == "DCIM/Camera") return "相机";
            if (p == "Pictures/Screenshots") return "截屏";
            if (p == "Pictures/Screen recordings" || p == "DCIM/ScreenRecorder") return "录屏";
            if (p == "Pictures") return "Pictures";
            if (p == "DCIM") return "DCIM";
            int i = p.LastIndexOf('/');
            string leaf = i >= 0 ? p.Substring(i + 1) : p;
            return leaf.Length == 0 ? "其他" : leaf;
        }
    }

    // ======================= 主窗口 =======================

    public class MainForm : Form
    {
        private readonly string _adb;
        private List<Album> _albums = new List<Album>();
        private List<MediaItem> _all = new List<MediaItem>();
        private bool _loading;

        private TreeView tree;
        private ListView list;
        private SplitContainer split;
        private ToolStrip toolStrip;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel statusCount;
        private ToolStripProgressBar progressBar;
        private TextBox txtDest;
        private ToolStripButton btnCancel;
        private ToolStripButton btnTransfer;
        private ToolStripButton btnTransferAlbum;
        private ToolStripButton btnOpen;

        private Thread worker;
        private volatile bool _cancel;

        // ---- MTP 兜底模式 ----
        private bool _mtpMode;
        private object _shell;
        private string _mtpDeviceName = "";
        private bool _selfTest;
        private string _selfTestLog;

        private int sortColumn = -1;
        private bool sortAsc = true;

        static readonly string[] ColKeys = new string[] {
            "_id", "_display_name", "_size", "date_added", "relative_path",
            "bucket_display_name", "mime_type", "width", "height", "duration"
        };

        public MainForm()
        {
            _adb = FindAdb();

            // 命令行 --mtp 可强制走 MTP 模式（用于在开了 USB 调试的机器上测试兜底逻辑）
            string[] argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length; i++)
            {
                if (string.Equals(argv[i], "--mtp", StringComparison.OrdinalIgnoreCase)) _mtpMode = true;
                if (string.Equals(argv[i], "--selftest", StringComparison.OrdinalIgnoreCase)) { _mtpMode = true; _selfTest = true; }
            }
            if (_adb == null) _mtpMode = true;

            BuildUi();
            Load += delegate
            {
                try { split.SplitterDistance = (int)(split.Width * 0.34); } catch { }
                WriteLayoutLog();
                BeginLoadAlbums();
            };
        }

        // ------------------ 界面 ------------------

        private void BuildUi()
        {
            // 程序通过 app.manifest 声明了 DPI 感知，因此这里拿到的都是真实物理像素。
            // 按系统缩放比例手动放大整个 UI，保证在 125%/150% 屏幕上大小合适。
            float s = SystemScale();
            AutoScaleMode = AutoScaleMode.None;
            Text = "手机相册传输";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F * s);
            MinimumSize = new Size((int)(760 * s), (int)(500 * s));
            Rectangle wa0 = Screen.PrimaryScreen.WorkingArea;
            int w0 = Math.Max((int)(700 * s), Math.Min((int)(1100 * s), wa0.Width - 60));
            int h0 = Math.Max((int)(480 * s), Math.Min((int)(680 * s), wa0.Height - 60));
            Size = new Size(w0, h0);
            Location = new Point(wa0.X + (wa0.Width - w0) / 2, wa0.Y + (wa0.Height - h0) / 2);

            toolStrip = new ToolStrip();
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip.RenderMode = ToolStripRenderMode.System;
            toolStrip.Padding = new Padding(4, 2, 4, 2);

            ToolStripButton btnRefresh = new ToolStripButton("刷新清单");
            btnRefresh.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnRefresh.Click += delegate { BeginLoadAlbums(); };

            btnTransfer = new ToolStripButton("传输所选");
            btnTransfer.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnTransfer.Enabled = false;
            btnTransfer.Click += delegate { TransferSelected(); };

            btnTransferAlbum = new ToolStripButton("传输当前相册");
            btnTransferAlbum.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnTransferAlbum.Enabled = false;
            btnTransferAlbum.Click += delegate { TransferCurrentAlbum(); };

            btnCancel = new ToolStripButton("取消");
            btnCancel.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnCancel.Enabled = false;
            btnCancel.Click += delegate { _cancel = true; SetStatus("正在取消..."); };

            btnOpen = new ToolStripButton("打开目标文件夹");
            btnOpen.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnOpen.Click += delegate
            {
                try { if (Directory.Exists(txtDest.Text)) Process.Start("explorer.exe", "\"" + txtDest.Text + "\""); }
                catch { }
            };

            ToolStripLabel lbl = new ToolStripLabel("目标:");
            txtDest = new TextBox();
            txtDest.Width = (int)(260 * s);
            txtDest.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "手机相册");
            ToolStripControlHost host = new ToolStripControlHost(txtDest);
            host.AutoSize = false;
            host.Width = (int)(270 * s);

            ToolStripButton btnBrowse = new ToolStripButton("...");
            btnBrowse.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnBrowse.Click += delegate
            {
                FolderBrowserDialog f = new FolderBrowserDialog();
                f.Description = "选择保存位置";
                if (Directory.Exists(txtDest.Text)) f.SelectedPath = txtDest.Text;
                if (f.ShowDialog() == DialogResult.OK) txtDest.Text = f.SelectedPath;
            };

            toolStrip.Items.Add(btnRefresh);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(btnTransfer);
            toolStrip.Items.Add(btnTransferAlbum);
            toolStrip.Items.Add(btnCancel);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(lbl);
            toolStrip.Items.Add(host);
            toolStrip.Items.Add(btnBrowse);
            toolStrip.Items.Add(new ToolStripSeparator());
            toolStrip.Items.Add(btnOpen);

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterDistance = 300;
            split.FixedPanel = FixedPanel.Panel1;

            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.ShowLines = true;
            tree.ItemHeight = (int)(22 * s);
            tree.BorderStyle = BorderStyle.None;
            tree.AfterSelect += OnAlbumSelect;
            tree.MouseDown += OnTreeMouseDown;

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = true;
            list.HideSelection = false;
            list.GridLines = false;
            list.BorderStyle = BorderStyle.None;
            list.Columns.Add("文件名", (int)(250 * s));
            list.Columns.Add("类型", (int)(70 * s));
            list.Columns.Add("大小", (int)(88 * s), HorizontalAlignment.Right);
            list.Columns.Add("时间", (int)(132 * s));
            list.Columns.Add("分辨率", (int)(100 * s));
            list.Columns.Add("时长", (int)(62 * s), HorizontalAlignment.Right);
            list.ColumnClick += OnColumnClick;
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.MouseDown += OnListMouseDown;
            list.KeyDown += OnListKeyDown;

            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(list);

            StatusStrip ss = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("正在启动...");
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusCount = new ToolStripStatusLabel("");
            progressBar = new ToolStripProgressBar();
            progressBar.Width = 180;
            progressBar.Visible = false;
            ss.Items.Add(statusLabel);
            ss.Items.Add(statusCount);
            ss.Items.Add(progressBar);

            Controls.Add(split);
            Controls.Add(toolStrip);
            Controls.Add(ss);
        }

        // ------------------ adb 定位与调用 ------------------

        private static string FindAdb()
        {
            // 1) 优先用程序自带的 platform-tools（发行版就是这么打包的）
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] bundled = new string[] {
                Path.Combine(baseDir, "platform-tools", "adb.exe"),
                Path.Combine(baseDir, "platform-tools", "adb", "adb.exe"),
                Path.Combine(baseDir, "adb.exe"),
                Path.Combine(baseDir, "bin", "adb.exe")
            };
            for (int i = 0; i < bundled.Length; i++)
            {
                try { if (File.Exists(bundled[i])) return bundled[i]; } catch { }
            }

            string cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneAlbum\\adb.path");
            try
            {
                if (File.Exists(cached))
                {
                    string c = File.ReadAllText(cached).Trim();
                    if (File.Exists(c)) return c;
                }
            }
            catch { }

            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                string[] dirs = pathVar.Split(';');
                for (int i = 0; i < dirs.Length; i++)
                {
                    string d = dirs[i].Trim();
                    if (d.Length == 0) continue;
                    try
                    {
                        string f = Path.Combine(d, "adb.exe");
                        if (File.Exists(f)) return f;
                    }
                    catch { }
                }
            }

            string pkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\WinGet\\Packages");
            if (Directory.Exists(pkg))
            {
                try
                {
                    string[] hits = Directory.GetFiles(pkg, "adb.exe", SearchOption.AllDirectories);
                    if (hits.Length > 0) return hits[0];
                }
                catch { }
            }
            return null;
        }

        private static float SystemScale()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "AppliedDPI", 96);
                int dpi = Convert.ToInt32(v);
                if (dpi >= 96 && dpi <= 480) return dpi / 96f;
            }
            catch { }
            return 1f;
        }

        private int RunAdb(string args, out string stdout, out string stderr)
        {
            stdout = ""; stderr = "";
            ProcessStartInfo psi = new ProcessStartInfo(_adb, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            StringBuilder so = new StringBuilder();
            StringBuilder se = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) se.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                stdout = so.ToString();
                stderr = se.ToString();
                return p.ExitCode;
            }
        }

        // ------------------ 读取清单 ------------------

        private void BeginLoadAlbums()
        {
            if (_loading) return;
            _loading = true;
            btnTransfer.Enabled = false;
            btnTransferAlbum.Enabled = false;
            tree.Nodes.Clear();
            list.Items.Clear();
            progressBar.Visible = true;
            progressBar.Style = ProgressBarStyle.Marquee;

            if (_mtpMode)
            {
                SetStatus("MTP 模式：正在读取手机文件夹（比 ADB 慢，请稍候）...");
                // 不能在这里同步调用：Load 阶段阻塞会让窗体根本不显示出来。
                // 丢到消息队列里，等窗口真正显示后再执行。
                BeginInvoke(new MethodInvoker(delegate { DoLoadMtp(); }));
                return;
            }

            SetStatus("正在读取手机相册索引...");
            Thread t = new Thread(delegate() { DoLoad(); });
            t.IsBackground = true;
            t.Start();
        }

        private void DoLoad()
        {
            try
            {
                string outTxt, errTxt;
                RunAdb("devices", out outTxt, out errTxt);
                if (!Regex.IsMatch(outTxt ?? "", @"\sdevice\s*\r?$", RegexOptions.Multiline))
                {
                    // ADB 不可用 -> 自动降级到 MTP
                    _loading = false;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _mtpMode = true;
                        BeginLoadAlbums();
                    }));
                    return;
                }

                List<MediaItem> items = new List<MediaItem>();

                UiStatus("正在读取图片索引...");
                items.AddRange(QueryMedia("images", false));
                UiStatus("正在读取视频索引...");
                items.AddRange(QueryMedia("video", true));

                _all = items;

                List<Album> albums = new List<Album>();
                var groups = items.GroupBy(delegate(MediaItem m) { return m.RelPath; });
                foreach (var g in groups)
                {
                    Album a = new Album();
                    a.Key = g.Key;
                    a.Title = Util.AlbumTitle(g.Key);
                    a.Items = g.OrderByDescending(delegate(MediaItem m) { return m.DateAdded; }).ToList();
                    albums.Add(a);
                }
                albums = albums.OrderByDescending(delegate(Album a) { return a.Items.Count; }).ToList();
                _albums = albums;

                UiBuildTree(albums, items);
                UiMarquee(false);
                UiStatus("清单读取完成，共 " + items.Count + " 个文件。左侧选择相册，右侧勾选后点『传输所选』。");
                _loading = false;
            }
            catch (Exception ex)
            {
                UiStatus("读取失败: " + ex.Message);
                UiMarquee(false);
                _loading = false;
            }
        }

        private List<MediaItem> QueryMedia(string kind, bool isVideo)
        {
            List<MediaItem> res = new List<MediaItem>();
            string uri = "content://media/external/" + kind + "/media";
            string proj = "_id:_display_name:_size:date_added:relative_path:bucket_display_name:mime_type:width:height";
            if (isVideo) proj += ":duration";

            string so, se;
            RunAdb("shell content query --uri " + uri + " --projection " + proj, out so, out se);
            if (string.IsNullOrEmpty(so)) return res;

            string splitPattern = ", (?=" + string.Join("|", ColKeys.Select(delegate(string k) { return Regex.Escape(k) + "="; }).ToArray()) + ")";
            Regex splitter = new Regex(splitPattern);
            Regex rowHead = new Regex(@"^Row:\s*\d+\s+");

            string[] lines = so.Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li].TrimEnd('\r', '\n', ' ');
                if (line.Length == 0 || !line.StartsWith("Row:")) continue;
                line = rowHead.Replace(line, "");
                string[] parts = splitter.Split(line);

                MediaItem m = new MediaItem();
                m.IsVideo = isVideo;
                for (int i = 0; i < parts.Length; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string k = parts[i].Substring(0, eq).Trim();
                    string v = parts[i].Substring(eq + 1);
                    switch (k)
                    {
                        case "_display_name": m.Name = v; break;
                        case "_size": m.Size = ParseLong(v); break;
                        case "date_added": m.DateAdded = ParseLong(v); break;
                        case "relative_path": m.RelPath = v; break;
                        case "bucket_display_name": m.Bucket = v; break;
                        case "mime_type": m.Mime = v; break;
                        case "width": m.Width = (int)ParseLong(v); break;
                        case "height": m.Height = (int)ParseLong(v); break;
                        case "duration": m.DurationMs = ParseLong(v); break;
                    }
                }
                if (m.Name == null || m.RelPath == null) continue;
                res.Add(m);
            }
            return res;
        }

        private static long ParseLong(string s)
        {
            long v;
            if (long.TryParse(s.Trim(), out v)) return v;
            return 0;
        }

        // ------------------ MTP 兜底模式 ------------------
        //  不走 adb，直接用 Windows Shell COM 读便携设备。
        //  优点：手机不用开 USB 调试，插线选「传输文件」就能用。
        //  缺点：慢、没有拍摄时间（MTP 返回 1899 年）、CopyHere 需要消息泵。

        static readonly string[] MediaExt = new string[] {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif",
            ".mp4", ".3gp", ".mkv", ".mov", ".avi", ".wmv", ".ts", ".webm"
        };

        private static bool IsVideoExt(string name)
        {
            string e = Path.GetExtension(name).ToLowerInvariant();
            return e == ".mp4" || e == ".3gp" || e == ".mkv" || e == ".mov"
                || e == ".avi" || e == ".wmv" || e == ".ts" || e == ".webm";
        }

        private static bool IsMediaFile(string name)
        {
            return Array.IndexOf(MediaExt, Path.GetExtension(name).ToLowerInvariant()) >= 0;
        }

        private void DoLoadMtp()
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) { SetStatus("系统不支持 Shell.Application"); _loading = false; return; }
                dynamic shell = Activator.CreateInstance(shellType);
                _shell = shell;

                dynamic thisPC = shell.NameSpace("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                if (thisPC == null) { SetStatus("无法打开「此电脑」"); _loading = false; return; }

                dynamic device = null;
                foreach (dynamic it in thisPC.Items())
                {
                    try
                    {
                        if ((bool)it.IsFolder && !(bool)it.IsFileSystem) { device = it; break; }
                    }
                    catch { }
                }
                if (device == null)
                {
                    SetStatus("没找到手机。请插线，并在通知栏选择「传输文件」。");
                    UiMarquee(false);
                    _loading = false;
                    return;
                }

                _mtpDeviceName = (string)device.Name;
                SetStatus("设备：" + _mtpDeviceName + "，正在打开存储...");
                Application.DoEvents();

                dynamic store = null;
                foreach (dynamic it in ((dynamic)device.GetFolder).Items())
                {
                    if ((bool)it.IsFolder) { store = it; break; }
                }
                if (store == null)
                {
                    SetStatus("手机存储打不开，请先解锁手机屏幕。");
                    UiMarquee(false);
                    _loading = false;
                    return;
                }

                List<MediaItem> items = new List<MediaItem>();
                dynamic root = (dynamic)store.GetFolder;
                int folderCount = 0;

                foreach (dynamic top in root.Items())
                {
                    if (_cancel) break;
                    bool isDir = false;
                    string topName = "";
                    try { isDir = (bool)top.IsFolder; topName = (string)top.Name; } catch { continue; }
                    if (!isDir || topName.StartsWith(".")) continue;
                    folderCount++;
                    SetStatus("扫描 " + topName + " ...");
                    Application.DoEvents();
                    WalkMtp(top, topName, items, 2);
                }

                List<Album> albums = new List<Album>();
                var groups = items.GroupBy(delegate(MediaItem m) { return m.RelPath; });
                foreach (var g in groups)
                {
                    if (g.Count() == 0) continue;
                    Album a = new Album();
                    a.Key = g.Key;
                    a.Title = Util.AlbumTitle(g.Key);
                    a.Items = g.ToList();
                    albums.Add(a);
                }
                albums = albums.OrderByDescending(delegate(Album a) { return a.Items.Count; }).ToList();
                _albums = albums;
                _all = items;

                UiBuildTree(albums, items);
                UiMarquee(false);
                Text = "手机相册传输 — MTP 模式";
                SetStatus("MTP 模式（无需 USB 调试）：共 " + items.Count + " 个媒体文件。时间列不可用，速度约 28 MB/s。");
                _loading = false;
                if (_selfTest) RunSelfTest();
            }
            catch (Exception ex)
            {
                SetStatus("MTP 读取失败：" + ex.Message);
                UiMarquee(false);
                _loading = false;
            }
        }

        private void WalkMtp(dynamic folderItem, string relPath, List<MediaItem> items, int depth)
        {
            if (depth < 0 || items.Count > 40000) return;

            dynamic f = null;
            try { f = folderItem.GetFolder; } catch { return; }
            if (f == null) return;

            List<object> subDirs = new List<object>();
            try
            {
                foreach (dynamic it in f.Items())
                {
                    try
                    {
                        if ((bool)it.IsFolder) { subDirs.Add(it); continue; }
                        string nm = (string)it.Name;
                        if (!IsMediaFile(nm)) continue;
                        long sz = 0;
                        try { sz = Convert.ToInt64(it.ExtendedProperty("System.Size")); } catch { }
                        MediaItem m = new MediaItem();
                        m.Name = nm;
                        m.Size = sz;
                        m.RelPath = relPath + "/";
                        m.DateAdded = 0;
                        m.IsVideo = IsVideoExt(nm);
                        m.Native = it;
                        items.Add(m);
                    }
                    catch { }
                }
            }
            catch { }

            if ((items.Count % 200) < 2) Application.DoEvents();

            for (int i = 0; i < subDirs.Count; i++)
            {
                string sn = "";
                try { sn = (string)((dynamic)subDirs[i]).Name; } catch { continue; }
                if (sn.StartsWith(".")) continue;
                WalkMtp((dynamic)subDirs[i], relPath + "/" + sn, items, depth - 1);
            }
        }

        private void DoTransferMtp(List<MediaItem> items, string destRoot, long totalBytes)
        {
            int ok = 0, skipped = 0, failed = 0;
            long doneBytes = 0;
            Stopwatch sw = Stopwatch.StartNew();
            List<string> errors = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                if (_cancel) break;
                MediaItem m = items[i];
                string albumDir = Path.Combine(destRoot, Util.Sanitize(Util.AlbumTitle(m.RelPath)));
                string destFile = Path.Combine(albumDir, Util.Sanitize(m.Name));

                try
                {
                    if (!Directory.Exists(albumDir)) Directory.CreateDirectory(albumDir);

                    if (File.Exists(destFile) && new FileInfo(destFile).Length == m.Size)
                    {
                        skipped++;
                        doneBytes += m.Size;
                        UiProgress(i + 1, items.Count, doneBytes, totalBytes, sw, skipped, ok, failed);
                        Application.DoEvents();
                        continue;
                    }

                    if (_selfTestLog != null) File.AppendAllText(_selfTestLog, "copy: " + m.Name + " native=" + (m.Native != null) + " size=" + m.Size + "\r\n");
                    bool done = false;
                    if (m.Native != null)
                    {
                        dynamic tgt = ((dynamic)_shell).NameSpace(albumDir);
                        if (tgt != null)
                        {
                            try { if (File.Exists(destFile)) File.Delete(destFile); } catch { }
                            tgt.CopyHere(m.Native, 1556);
                            Stopwatch w = Stopwatch.StartNew();
                            while (w.Elapsed.TotalSeconds < 300)
                            {
                                Application.DoEvents();
                                if (File.Exists(destFile))
                                {
                                    System.Threading.Thread.Sleep(40);
                                    done = true;
                                    break;
                                }
                                System.Threading.Thread.Sleep(30);
                            }
                        }
                    }

                    if (done && File.Exists(destFile) && new FileInfo(destFile).Length == m.Size)
                    {
                        ok++;
                        doneBytes += m.Size;
                    }
                    else
                    {
                        failed++;
                        if (errors.Count < 30) errors.Add(m.Name);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    if (_selfTestLog != null) { try { File.AppendAllText(_selfTestLog, "EX: " + ex.ToString() + "\r\n"); } catch { } }
                    if (errors.Count < 30) errors.Add(m.Name + "  " + ex.Message);
                }

                UiProgress(i + 1, items.Count, doneBytes, totalBytes, sw, skipped, ok, failed);
                Application.DoEvents();
            }

            sw.Stop();
            UiDone(ok, skipped, failed, doneBytes, sw.Elapsed.TotalSeconds, errors);
        }

        // 自检：MTP 模式下取一个小相册的单个文件试传一次，把结果写到临时文件后退出。
        // 用法：PhoneAlbum.exe --selftest
        private void RunSelfTest()
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt");
            _selfTestLog = log;
            File.WriteAllText(log, "stage=entered\r\n");
            try
            {
                File.AppendAllText(log, "stage=albums=" + _albums.Count + "\r\n");
                Album pick = null;
                for (int i = 0; i < _albums.Count; i++)
                {
                    if (_albums[i].Items.Count >= 1 && _albums[i].Items.Count <= 6) { pick = _albums[i]; break; }
                }
                if (pick == null && _albums.Count > 0) pick = _albums[_albums.Count - 1];
                if (pick == null)
                {
                    File.WriteAllText(log, "result=FAIL\r\nreason=没有可用相册\r\n");
                    BeginInvoke(new MethodInvoker(delegate { Application.Exit(); }));
                    return;
                }

                List<MediaItem> one = new List<MediaItem>();
                one.Add(pick.Items[0]);
                string dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_out");
                if (Directory.Exists(dest)) Directory.Delete(dest, true);

                File.AppendAllText(log, "stage=before-copy file=" + one[0].Name + " size=" + one[0].Size + "\r\n");
                Stopwatch sw = Stopwatch.StartNew();
                DoTransferMtp(one, dest, one[0].Size);
                sw.Stop();
                File.AppendAllText(log, "stage=after-copy\r\n");

                string outFile = Path.Combine(dest, Util.Sanitize(Util.AlbumTitle(one[0].RelPath)), Util.Sanitize(one[0].Name));
                bool exists = File.Exists(outFile);
                long got = exists ? new FileInfo(outFile).Length : -1;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("album=" + pick.Title);
                sb.AppendLine("file=" + one[0].Name);
                sb.AppendLine("expected=" + one[0].Size);
                sb.AppendLine("got=" + got);
                sb.AppendLine("exists=" + exists);
                sb.AppendLine("seconds=" + sw.Elapsed.TotalSeconds.ToString("0.00"));
                sb.AppendLine("result=" + (exists && got == one[0].Size ? "PASS" : "FAIL"));
                File.AppendAllText(log, sb.ToString());
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, "result=EXCEPTION\r\n" + ex.ToString()); } catch { }
            }
            BeginInvoke(new MethodInvoker(delegate { Application.Exit(); }));
        }

        // ------------------ 填充界面 ------------------

        private void UiBuildTree(List<Album> albums, List<MediaItem> all)
        {
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { UiBuildTree(albums, all); })); return; }

            tree.BeginUpdate();
            tree.Nodes.Clear();

            TreeNode root = new TreeNode("手机相册");
            root.ImageIndex = 0;

            int imgCount = all.Count(delegate(MediaItem m) { return !m.IsVideo; });
            int vidCount = all.Count(delegate(MediaItem m) { return m.IsVideo; });
            long totalSize = all.Sum(delegate(MediaItem m) { return m.Size; });

            TreeNode nAll = new TreeNode("全部  (" + all.Count + " · " + Util.Size(totalSize) + ")");
            Album aAll = new Album();
            aAll.Key = "*";
            aAll.Title = "全部";
            aAll.Items = all.OrderByDescending(delegate(MediaItem m) { return m.DateAdded; }).ToList();
            nAll.Tag = aAll;

            TreeNode nImg = new TreeNode("全部图片  (" + imgCount + ")");
            Album aImg = new Album();
            aImg.Key = "*img";
            aImg.Title = "全部图片";
            aImg.Items = all.Where(delegate(MediaItem m) { return !m.IsVideo; }).OrderByDescending(delegate(MediaItem m) { return m.DateAdded; }).ToList();
            nImg.Tag = aImg;

            TreeNode nVid = new TreeNode("全部视频  (" + vidCount + ")");
            Album aVid = new Album();
            aVid.Key = "*vid";
            aVid.Title = "全部视频";
            aVid.Items = all.Where(delegate(MediaItem m) { return m.IsVideo; }).OrderByDescending(delegate(MediaItem m) { return m.DateAdded; }).ToList();
            nVid.Tag = aVid;

            root.Nodes.Add(nAll);
            root.Nodes.Add(nImg);
            root.Nodes.Add(nVid);

            TreeNode sep = new TreeNode("—— 相册 ——");
            root.Nodes.Add(sep);

            for (int i = 0; i < albums.Count; i++)
            {
                Album a = albums[i];
                TreeNode n = new TreeNode(a.Title + "  (" + a.Items.Count + " · " + Util.Size(a.TotalSize) + ")");
                n.Tag = a;
                n.ToolTipText = a.Key;
                root.Nodes.Add(n);
            }

            tree.Nodes.Add(root);
            root.Expand();
            tree.EndUpdate();
            if (root.Nodes.Count > 0) tree.SelectedNode = root.Nodes[0];
            UpdateButtons();
        }

        private void WriteLayoutLog()
        {
            try
            {
                string txt = "ScreenBounds=" + Screen.PrimaryScreen.Bounds.ToString() + "\r\n" +
                    "WorkingArea=" + Screen.PrimaryScreen.WorkingArea.ToString() + "\r\n" +
                    "FormSize=" + Size.ToString() + "\r\n" +
                    "FormLocation=" + Location.ToString() + "\r\n" +
                    "ClientSize=" + ClientSize.ToString() + "\r\n" +
                    "SplitterDistance=" + split.SplitterDistance + "\r\n" +
                    "AutoScaleMode=" + AutoScaleMode.ToString() + "\r\n" +
                    "AutoScaleFactor=" + AutoScaleFactor.ToString();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.txt"), txt);
            }
            catch { }
        }

        private void OnAlbumSelect(object sender, TreeViewEventArgs e)
        {
            Album a = e.Node.Tag as Album;
            if (a == null) { list.Items.Clear(); return; }
            ShowAlbum(a);
        }

        private void ShowAlbum(Album a)
        {
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < a.Items.Count; i++)
            {
                MediaItem m = a.Items[i];
                string ext = Path.GetExtension(m.Name).TrimStart('.').ToLowerInvariant();
                string type = m.IsVideo ? "视频 " + ext : "图片 " + ext;
                ListViewItem lvi = new ListViewItem(m.Name);
                lvi.SubItems.Add(type);
                lvi.SubItems.Add(Util.Size(m.Size));
                lvi.SubItems.Add(m.DateAdded > 0 ? m.LocalDate.ToString("yyyy-MM-dd HH:mm") : "未知");
                lvi.SubItems.Add(m.Width > 0 ? (m.Width + "×" + m.Height) : "");
                lvi.SubItems.Add(m.IsVideo ? Util.Duration(m.DurationMs) : "");
                lvi.Tag = m;
                list.Items.Add(lvi);
            }
            list.EndUpdate();
            SetStatus("相册「" + a.Title + "」：" + a.Items.Count + " 个文件，合计 " + Util.Size(a.TotalSize));
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool idle = (worker == null || !worker.IsAlive);
            btnTransfer.Enabled = idle && list.SelectedItems.Count > 0;
            btnTransferAlbum.Enabled = idle && list.Items.Count > 0;
        }

        // ------------------ 排序 ------------------

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn == e.Column) sortAsc = !sortAsc;
            else { sortColumn = e.Column; sortAsc = true; }
            list.ListViewItemSorter = new ItemComparer(e.Column, sortAsc);
            list.Sort();
        }

        private class ItemComparer : System.Collections.IComparer
        {
            private int col;
            private bool asc;
            public ItemComparer(int c, bool a) { col = c; asc = a; }
            public int Compare(object x, object y)
            {
                ListViewItem a = (ListViewItem)x;
                ListViewItem b = (ListViewItem)y;
                int r;
                if (col == 2)
                {
                    long sa = ((MediaItem)a.Tag).Size;
                    long sb = ((MediaItem)b.Tag).Size;
                    r = sa.CompareTo(sb);
                }
                else if (col == 3)
                {
                    r = ((MediaItem)a.Tag).DateAdded.CompareTo(((MediaItem)b.Tag).DateAdded);
                }
                else if (col == 5)
                {
                    r = ((MediaItem)a.Tag).DurationMs.CompareTo(((MediaItem)b.Tag).DurationMs);
                }
                else
                {
                    r = string.Compare(a.SubItems[col].Text, b.SubItems[col].Text, StringComparison.CurrentCulture);
                }
                return asc ? r : -r;
            }
        }

        // ------------------ 传输 ------------------

        private void TransferSelected()
        {
            List<MediaItem> items = new List<MediaItem>();
            for (int i = 0; i < list.SelectedItems.Count; i++)
                items.Add((MediaItem)list.SelectedItems[i].Tag);
            StartTransfer(items, "所选");
        }

        private void TransferCurrentAlbum()
        {
            List<MediaItem> items = new List<MediaItem>();
            for (int i = 0; i < list.Items.Count; i++)
                items.Add((MediaItem)list.Items[i].Tag);
            StartTransfer(items, "当前相册");
        }

        private void StartTransfer(List<MediaItem> items, string what)
        {
            if (worker != null && worker.IsAlive) { MessageBox.Show("正在传输中，请先等待或取消。"); return; }
            if (items == null || items.Count == 0) { MessageBox.Show("没有要传输的文件。"); return; }

            string destRoot = txtDest.Text.Trim();
            if (destRoot.Length == 0) { MessageBox.Show("请先设置目标文件夹。"); return; }
            try { Directory.CreateDirectory(destRoot); }
            catch (Exception ex) { MessageBox.Show("无法创建目标文件夹: " + ex.Message); return; }

            long totalBytes = 0;
            for (int i = 0; i < items.Count; i++) totalBytes += items[i].Size;

            DialogResult dr = MessageBox.Show(
                "准备传输 " + what + " 共 " + items.Count + " 个文件，合计 " + Util.Size(totalBytes) + "。\n\n保存到:\n" + destRoot,
                "确认传输", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (dr != DialogResult.OK) return;

            _cancel = false;
            btnCancel.Enabled = true;
            btnTransfer.Enabled = false;
            btnTransferAlbum.Enabled = false;
            progressBar.Visible = true;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 0;

            if (_mtpMode)
            {
                // MTP 的 Shell.CopyHere 依赖窗口消息泵，必须在 UI 线程执行
                DoTransferMtp(items, destRoot, totalBytes);
                return;
            }

            worker = new Thread(delegate() { DoTransfer(items, destRoot, totalBytes); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void DoTransfer(List<MediaItem> items, string destRoot, long totalBytes)
        {
            int ok = 0, skipped = 0, failed = 0;
            long doneBytes = 0;
            Stopwatch sw = Stopwatch.StartNew();
            List<string> errors = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                if (_cancel) break;
                MediaItem m = items[i];
                string albumDir = Path.Combine(destRoot, Util.Sanitize(Util.AlbumTitle(m.RelPath)));
                string destFile = Path.Combine(albumDir, Util.Sanitize(m.Name));

                try
                {
                    if (!Directory.Exists(albumDir)) Directory.CreateDirectory(albumDir);

                    if (File.Exists(destFile) && new FileInfo(destFile).Length == m.Size)
                    {
                        skipped++;
                        doneBytes += m.Size;
                        UiProgress(i + 1, items.Count, doneBytes, totalBytes, sw, skipped, ok, failed);
                        continue;
                    }

                    string so, se;
                    RunAdb("pull \"" + m.RemotePath + "\" \"" + destFile + "\"", out so, out se);

                    if (File.Exists(destFile) && new FileInfo(destFile).Length == m.Size)
                    {
                        ok++;
                        doneBytes += m.Size;
                    }
                    else
                    {
                        failed++;
                        if (errors.Count < 30) errors.Add(m.Name + "  " + (se != null ? se.Trim() : ""));
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Count < 30) errors.Add(m.Name + "  " + ex.Message);
                }
                UiProgress(i + 1, items.Count, doneBytes, totalBytes, sw, skipped, ok, failed);
            }

            sw.Stop();
            UiDone(ok, skipped, failed, doneBytes, sw.Elapsed.TotalSeconds, errors);
        }

        private void UiProgress(int done, int total, long doneBytes, long totalBytes, Stopwatch sw, int skipped, int ok, int failed)
        {
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { UiProgress(done, total, doneBytes, totalBytes, sw, skipped, ok, failed); })); return; }
            int pct = totalBytes > 0 ? (int)(doneBytes * 100 / totalBytes) : (done * 100 / Math.Max(1, total));
            if (pct > 100) pct = 100;
            progressBar.Value = pct;
            double speed = sw.Elapsed.TotalSeconds > 0 ? (doneBytes / 1048576.0) / sw.Elapsed.TotalSeconds : 0;
            statusLabel.Text = "传输中 " + done + "/" + total + "   " + Util.Size(doneBytes) + " / " + Util.Size(totalBytes)
                + "   " + speed.ToString("0.0") + " MB/s   成功 " + ok + "  跳过 " + skipped + (failed > 0 ? "  失败 " + failed : "");
        }

        private void UiDone(int ok, int skipped, int failed, long bytes, double secs, List<string> errors)
        {
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { UiDone(ok, skipped, failed, bytes, secs, errors); })); return; }
            progressBar.Visible = false;
            btnCancel.Enabled = false;
            worker = null;
            UpdateButtons();

            double speed = secs > 0 ? (bytes / 1048576.0) / secs : 0;
            string msg = "完成：成功 " + ok + "，跳过 " + skipped + "，失败 " + failed
                + "，共 " + Util.Size(bytes) + "，用时 " + secs.ToString("0.0") + " 秒，平均 " + speed.ToString("0.0") + " MB/s";
            statusLabel.Text = msg;

            if (failed > 0 && errors.Count > 0 && !_selfTest)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("有 " + failed + " 个文件失败：");
                sb.AppendLine();
                for (int i = 0; i < errors.Count; i++) sb.AppendLine(errors[i]);
                MessageBox.Show(sb.ToString(), "部分失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------ 交互辅助 ------------------

        private void OnTreeMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            TreeNode n = tree.GetNodeAt(e.X, e.Y);
            if (n == null) return;
            tree.SelectedNode = n;
            Album a = n.Tag as Album;
            if (a == null) return;

            ContextMenuStrip cm = new ContextMenuStrip();
            ToolStripMenuItem mi = new ToolStripMenuItem("传输此相册全部 (" + a.Items.Count + " · " + Util.Size(a.TotalSize) + ")");
            mi.Click += delegate { StartTransfer(a.Items, "相册「" + a.Title + "」"); };
            cm.Items.Add(mi);
            cm.Show(tree, e.Location);
        }

        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ContextMenuStrip cm = new ContextMenuStrip();
            int sel = list.SelectedItems.Count;

            ToolStripMenuItem miSel = new ToolStripMenuItem("传输所选 (" + sel + ")");
            miSel.Enabled = sel > 0;
            miSel.Click += delegate { TransferSelected(); };
            cm.Items.Add(miSel);

            ToolStripMenuItem miAll = new ToolStripMenuItem("传输本相册全部 (" + list.Items.Count + ")");
            miAll.Click += delegate { TransferCurrentAlbum(); };
            cm.Items.Add(miAll);

            cm.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem miSelectAll = new ToolStripMenuItem("全选");
            miSelectAll.Click += delegate { for (int i = 0; i < list.Items.Count; i++) list.Items[i].Selected = true; UpdateButtons(); };
            cm.Items.Add(miSelectAll);

            cm.Show(list, e.Location);
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                for (int i = 0; i < list.Items.Count; i++) list.Items[i].Selected = true;
                UpdateButtons();
                e.Handled = true;
            }
        }

        private void SetStatus(string s)
        {
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { statusLabel.Text = s; })); return; }
            statusLabel.Text = s;
        }

        private void UiStatus(string s) { SetStatus(s); }

        private void UiMarquee(bool on)
        {
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { UiMarquee(on); })); return; }
            progressBar.Style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            progressBar.Visible = on;
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
