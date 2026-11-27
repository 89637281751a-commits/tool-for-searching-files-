using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program {
    internal static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickFind");
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [STAThread] static void Main(string[] args) {
        bool first;
        using (var single = new Mutex(true, @"Local\QuickFind.Desktop.2026", out first)) {
            if (!first) return;
            Directory.CreateDirectory(Home);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var ui = new Finder()) {
                ui.Start(!args.Contains("/background"));
                Application.Run();
            }
            GC.KeepAlive(single);
        }
    }
    internal static bool FileError(Exception e) {
        return e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException || e is ArgumentException || e is NotSupportedException;
    }
    internal static uint GetFixedDrives() {
        uint drives = 0;
        foreach (var drive in DriveInfo.GetDrives()) {
            int letter = Char.ToUpperInvariant(drive.Name[0]) - 'A';
            if (letter >= 0 && letter < 26 && drive.DriveType == DriveType.Fixed) drives |= 1u << letter;
        }
        return drives;
    }
    internal static bool IsFixedPath(string path, uint drives) {
        if (String.IsNullOrEmpty(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\') return false;
        char drive = path[0];
        int letter = drive >= 'a' && drive <= 'z' ? drive - 'a' : drive - 'A';
        if (letter < 0 || letter >= 26 || (drives & (1u << letter)) == 0) return false;
        int componentStart = 3;
        for (int i = 3; i <= path.Length; i++) {
            if (i == path.Length || path[i] == '\\') {
                if (i == componentStart || path[i - 1] == '.' || path[i - 1] == ' ') return false;
                componentStart = i + 1;
                continue;
            }
            char c = path[i];
            if (c < ' ' || c == ':' || c == '/' || c == '"' || c == '<' || c == '>' ||
                c == '|' || c == '*' || c == '?') return false;
        }
        return true;
    }
    internal static bool TryGetFixedPathAttributes(string path, uint drives, out FileAttributes attributes) {
        attributes = 0;
        if (!IsFixedPath(path, drives)) return false;
        try {
            // Check parents from the root down before looking at the final component.
            const FileAttributes parentFlags = FileAttributes.Directory | FileAttributes.ReparsePoint;
            for (int i = 3; i < path.Length; i++) {
                if (path[i] == '\\' && (File.GetAttributes(path.Substring(0, i)) & parentFlags) != FileAttributes.Directory)
                    return false;
            }
            attributes = File.GetAttributes(path);
            return true;
        } catch (Exception e) {
            if (!FileError(e)) throw;
            return false;
        }
    }
    internal static bool TryGetFixedFileAttributes(string path, uint drives, out FileAttributes attributes) {
        return TryGetFixedPathAttributes(path, drives, out attributes) && (attributes & FileAttributes.Directory) == 0;
    }
    internal static void SaveLines(string file, IEnumerable<string> lines) {
        string tmp = Path.Combine(Path.GetDirectoryName(file), Guid.NewGuid().ToString("N") + ".part");
        bool created = false;
        try {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                created = true;
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
                    foreach (string line in lines) writer.WriteLine(line);
                }
            }
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
            created = false;
        } finally {
            if (created) {
                try { File.Delete(tmp); }
                catch (Exception e) { if (!FileError(e)) throw; }
            }
        }
    }
}

sealed class Entry {
    internal readonly string PathName, Name, Stem;
    public override string ToString() { return Name; }
    internal Entry(string path) {
        PathName = path; Name = Path.GetFileName(path); Stem = Path.GetFileNameWithoutExtension(Name);
    }
}

sealed class DiskIndex {
    static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
    static readonly string homePrefix = Program.Home + Path.DirectorySeparatorChar;
    readonly string cache = Path.Combine(Program.Home, "index.txt");
    Dictionary<string, Entry> files = new Dictionary<string, Entry>(CI);
    readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    const int MaxPendingChanges = 32768;
    readonly object changeGate = new object();
    readonly Queue<FileSystemEventArgs> changes = new Queue<FileSystemEventArgs>();
    volatile bool rescan, stopping;
    internal volatile bool Scanning = true;
    internal volatile Entry[] Snapshot = new Entry[0];
    internal volatile string Problem = "";
    DateTime nextPreview;
    bool preview;
    internal void Stop() { stopping = true; }
    internal void Run() {
        try { RunIndex(); }
        finally { StopWatchers(); }
    }
    void RunIndex() {
        try {
            if (File.Exists(cache)) {
                uint drives = Program.GetFixedDrives();
                foreach (string p in File.ReadLines(cache))
                    if (Program.IsFixedPath(p, drives) && !Excluded(p)) files[p] = new Entry(p);
                Publish();
            }
        } catch (Exception e) { if (!Program.FileError(e)) throw; }
        DateTime nextScan = DateTime.MinValue, nextSave = DateTime.MinValue;
        bool dirty = false;
        while (!stopping) {
            try {
                if (rescan || DateTime.UtcNow >= nextScan) {
                    Scanning = true;
                    StopWatchers();
                    lock (changeGate) {
                        changes.Clear();
                        rescan = false;
                    }
                    preview = Snapshot.Length == 0; nextPreview = DateTime.UtcNow.AddSeconds(2);
                    var fresh = new Dictionary<string, Entry>(CI);
                    foreach (var d in DriveInfo.GetDrives()) {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        Watch(d.RootDirectory.FullName);
                        Walk(d.RootDirectory.FullName, fresh, merge: false);
                        if (stopping) return;
                    }
                    files = fresh;
                    preview = false; Publish(); Scanning = false; dirty = true;
                    nextScan = DateTime.UtcNow.AddMinutes(30);
                }
                bool updated = false;
                FileSystemEventArgs ev;
                for (int i = 0; i < 4096 && TryDequeueChange(out ev); i++) {
                    var rename = ev as RenamedEventArgs;
                    if (rename != null && !Excluded(rename.OldFullPath)) updated |= Remove(rename.OldFullPath);
                    if (Excluded(ev.FullPath)) continue;
                    if (ev.ChangeType == WatcherChangeTypes.Deleted) updated |= Remove(ev.FullPath);
                    else if (File.Exists(ev.FullPath)) updated |= AddFile(files, ev.FullPath);
                    else if (Directory.Exists(ev.FullPath)) updated |= Walk(ev.FullPath, files, merge: true);
                }
                if (updated) { Publish(); dirty = true; }
                if (dirty && DateTime.UtcNow >= nextSave) {
                    nextSave = DateTime.UtcNow.AddMinutes(2);
                    Program.SaveLines(cache, files.Keys); dirty = false;
                }
                Problem = "";
            } catch (Exception e) {
                if (!Program.FileError(e)) throw;
                Scanning = false; Problem = "Часть папок временно недоступна";
            }
            Thread.Sleep(850);
        }
    }
    void Publish() { Snapshot = files.Values.ToArray(); }
    bool Excluded(string path) {
        return CI.Equals(path, Program.Home) || path.StartsWith(homePrefix, StringComparison.OrdinalIgnoreCase);
    }
    void QueueChange(object sender, FileSystemEventArgs change) {
        lock (changeGate) {
            if (changes.Count >= MaxPendingChanges) rescan = true;
            else changes.Enqueue(change);
        }
    }
    bool TryDequeueChange(out FileSystemEventArgs change) {
        lock (changeGate) {
            if (changes.Count == 0) {
                change = null;
                return false;
            }
            change = changes.Dequeue();
            return true;
        }
    }
    void StopWatchers() {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }
    void Watch(string root) {
        FileSystemWatcher watcher = null;
        try {
            watcher = new FileSystemWatcher(root);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
            watcher.InternalBufferSize = 65536;
            watcher.Created += QueueChange; watcher.Deleted += QueueChange;
            watcher.Renamed += QueueChange; watcher.Error += (s,e) => rescan = true;
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
            watcher = null;
        } catch (Exception e) { if (!Program.FileError(e)) throw; }
        finally { if (watcher != null) watcher.Dispose(); }
    }
    bool Remove(string path) {
        if (files.Remove(path)) return true;
        string prefix = path + Path.DirectorySeparatorChar;
        var children = files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (string child in children) files.Remove(child);
        return children.Length != 0;
    }
    static bool AddFile(Dictionary<string, Entry> target, string path) {
        Entry existing;
        if (target.TryGetValue(path, out existing) && String.Equals(existing.PathName, path, StringComparison.Ordinal))
            return false;
        target[path] = new Entry(path);
        return true;
    }
    bool Walk(string root, Dictionary<string, Entry> target, bool merge) {
        bool updated = false;
        var dirs = new Stack<string>(); dirs.Push(root);
        while (dirs.Count > 0 && !stopping) {
            if (preview && DateTime.UtcNow >= nextPreview) {
                Snapshot = target.Values.ToArray(); nextPreview = DateTime.UtcNow.AddSeconds(4);
            }
            string dir = dirs.Pop();
            try {
                if (CI.Equals(dir, Program.Home) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos()) {
                    var a = item.Attributes;
                    if ((a & FileAttributes.Directory) == 0) {
                        string path = item.FullName;
                        if (merge) updated |= AddFile(target, path);
                        else target[path] = new Entry(path);
                    } else if ((a & FileAttributes.ReparsePoint) == 0) dirs.Push(item.FullName);
                }
            } catch (Exception e) { if (!Program.FileError(e)) throw; }
        }
        return updated;
    }
}

sealed class Recent {
    readonly string file = Path.Combine(Program.Home, "recent.txt");
    static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
    volatile Dictionary<string, long> times = new Dictionary<string, long>(CI);
    readonly object gate = new object();
    internal Recent() {
        try {
            if (File.Exists(file)) {
                uint drives = Program.GetFixedDrives();
                foreach (var line in File.ReadLines(file)) {
                    int i = line.IndexOf('\t');
                    long ticks;
                    if (i <= 0 || !long.TryParse(line.Substring(0, i), out ticks)) continue;
                    string path = line.Substring(i + 1);
                    if (Program.IsFixedPath(path, drives)) times[path] = ticks;
                }
            }
        } catch (Exception e) { if (!Program.FileError(e)) throw; }
    }
    // A dictionary is never modified after it has been published to readers.
    internal IReadOnlyDictionary<string, long> Snapshot { get { return times; } }
    internal void Remember(string path) {
        Merge(new[] { new KeyValuePair<string, long>(path, DateTime.UtcNow.Ticks) }, Program.GetFixedDrives());
    }
    void Merge(IEnumerable<KeyValuePair<string, long>> items, uint drives) {
        lock (gate) {
            var updated = new Dictionary<string, long>(times, CI);
            foreach (var item in items) {
                long old;
                if (!updated.TryGetValue(item.Key, out old) || old < item.Value) updated[item.Key] = item.Value;
            }
            var kept = updated.OrderByDescending(item => item.Value).Where(item => {
                FileAttributes attributes;
                return Program.TryGetFixedFileAttributes(item.Key, drives, out attributes);
            }).Take(10).ToArray();
            times = kept.ToDictionary(item => item.Key, item => item.Value, CI);
            try { Program.SaveLines(file, kept.Select(item => item.Value.ToString() + "\t" + item.Key)); }
            catch (Exception e) { if (!Program.FileError(e)) throw; }
        }
    }
    internal void ImportExplorer() {
        uint drives = Program.GetFixedDrives();
        var items = new List<KeyValuePair<string, long>>();
        object shell = null;
        try {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            FileAttributes attributes;
            const FileAttributes directoryFlags = FileAttributes.Directory | FileAttributes.ReparsePoint;
            if (Program.TryGetFixedPathAttributes(dir, drives, out attributes) &&
                (attributes & directoryFlags) == FileAttributes.Directory) {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(x => x.LastWriteTimeUtc)) {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    object link = null;
                    try {
                        link = ((dynamic)shell).CreateShortcut(f.FullName);
                        string path = ((dynamic)link).TargetPath;
                        if (Program.TryGetFixedFileAttributes(path, drives, out attributes) &&
                            !items.Any(item => CI.Equals(item.Key, path)))
                            items.Add(new KeyValuePair<string, long>(path, f.LastWriteTimeUtc.Ticks));
                    } catch (COMException) {} finally { if (link != null) Marshal.ReleaseComObject(link); }
                    if (items.Count == 10) break;
                }
            }
        } catch (Exception e) { if (!Program.FileError(e) && !(e is COMException)) throw; }
        finally { if (shell != null) Marshal.ReleaseComObject(shell); }
        Merge(items, drives);
    }
}

struct Match {
    const int Limit = 100;
    static readonly IComparer<Match> Order = Comparer<Match>.Create(Compare);
    internal readonly Entry Item;
    readonly int Rank;
    readonly long Last;
    Match(Entry item, int rank, long last) {
        Item = item;
        Rank = rank;
        Last = last;
    }
    internal static int Compare(Match x, Match y) {
        int c = x.Rank.CompareTo(y.Rank); if (c!=0) return c;
        c = y.Last.CompareTo(x.Last); if (c!=0) return c;
        c = StringComparer.OrdinalIgnoreCase.Compare(x.Item.Name,y.Item.Name); if (c!=0) return c;
        return StringComparer.OrdinalIgnoreCase.Compare(x.Item.PathName,y.Item.PathName);
    }
    internal static Entry[] Search(Entry[] all, string q, IReadOnlyDictionary<string, long> times, CancellationToken token) {
        var best = new SortedSet<Match>(Order);
        Match worst = default(Match);
        int i=0;
        foreach (Entry item in all) {
            if ((i++ & 1023)==0) token.ThrowIfCancellationRequested();
            int pos = item.Name.IndexOf(q,StringComparison.OrdinalIgnoreCase);
            if (pos<0) continue;
            int rank = String.Equals(item.Name,q,StringComparison.OrdinalIgnoreCase) || String.Equals(item.Stem,q,StringComparison.OrdinalIgnoreCase) ? 0 : pos==0 ? 1 : 2;
            bool full = best.Count == Limit;
            if (full && rank > worst.Rank) continue;
            long last; times.TryGetValue(item.PathName,out last);
            var result = new Match(item, rank, last);
            if ((!full || Compare(result, worst) < 0) && best.Add(result)) {
                if (full) best.Remove(worst);
                if (best.Count == Limit) worst = best.Max;
            }
        }
        return best.Select(m=>m.Item).ToArray();
    }
}

sealed class Finder : Form {
    const int WM_INPUT=0xff;
    const int EM_SETCUEBANNER=0x1501;
    static readonly Color WindowBackground = Color.FromArgb(21, 23, 25);
    static readonly Color SearchBackground = Color.FromArgb(36, 39, 43);
    static readonly Color TextColor = Color.FromArgb(241, 243, 244);
    static readonly Color SecondaryText = Color.FromArgb(167, 176, 183);
    static readonly Color Separator = Color.FromArgb(46, 52, 57);
    static readonly Color SelectedBackground = Color.FromArgb(32, 48, 46);
    static readonly Color Accent = Color.FromArgb(114, 231, 203);
    static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
    [StructLayout(LayoutKind.Sequential)] struct RawDevice {
        public ushort Page, Usage; public uint Flags; public IntPtr Target;
    }
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool RegisterRawInputDevices(RawDevice[] devices,uint count,uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input,uint cmd,IntPtr data,ref uint size,uint header);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string text);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet=CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);
    readonly TextBox query = new TextBox();
    readonly SearchField searchField;
    readonly TableLayoutPanel footer = new TableLayoutPanel();
    readonly ListBox list = new ListBox();
    readonly Label status = new Label();
    readonly Label shortcuts = new Label();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem startup = new ToolStripMenuItem("Запускать при входе в Windows");
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
    readonly Font resultFont = new Font("Segoe UI", 12);
    readonly Font searchFont = new Font("Segoe UI", 16);
    readonly Font small = new Font("Segoe UI", 9);
    readonly Icon appIcon;
    readonly int layoutDpi;
    readonly DiskIndex index = new DiskIndex();
    readonly Recent recent = new Recent();
    readonly Stopwatch clock = Stopwatch.StartNew();
    IntPtr packet = Marshal.AllocHGlobal(128);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly HashSet<string> ActiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".cpl", ".msc", ".msi", ".msp",
        ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application", ".appref-ms",
        ".ps1", ".psm1", ".hta", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".sct",
        ".lnk", ".url", ".website", ".scf", ".reg", ".jar", ".py", ".pyw"
    };
    long pressed, released=-1000;
    bool controlHeld, chord, importing, closing;
    CancellationTokenSource pending;
    Entry[] shownSnapshot;
    bool wasScanning, highContrast;
    string displayedQuery, openWhenReady, shownProblem;
    public Finder() {
        SuspendLayout();
        Text = "QuickFind";
        Font = resultFont;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        layoutDpi = Math.Max(1, (int)Math.Round(CurrentAutoScaleDimensions.Width));
        ClientSize = new Size(690, 452);
        MinimumSize = new Size(440, 300);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        KeyPreview = true;
        appIcon = LoadAppIcon();
        if (appIcon != null) Icon = appIcon;

        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16, 14, 16, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        query.BorderStyle = BorderStyle.None;
        query.Font = searchFont;
        query.AccessibleName = "Имя файла";
        query.HandleCreated += (s, e) => {
            if (IsWindows) SendMessage(query.Handle, EM_SETCUEBANNER, new IntPtr(1), "Имя файла");
        };
        searchField = new SearchField(query, layoutDpi) {
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), TabIndex = 0
        };
        layout.Controls.Add(searchField, 0, 0);

        list.Dock = DockStyle.Fill;
        list.Margin = Padding.Empty;
        list.TabIndex = 1;
        list.BorderStyle = BorderStyle.None;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.IntegralHeight = false;
        list.AccessibleName = "Найденные файлы";
        list.HandleCreated += (s, e) => ApplyListTheme();
        layout.Controls.Add(list, 0, 1);

        footer.AutoSize = true;
        footer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        footer.Dock = DockStyle.Fill;
        footer.Margin = Padding.Empty;
        footer.ColumnCount = 1;
        footer.RowCount = 2;
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        footer.Paint += (s, e) => {
            using (var pen = new Pen(highContrast ? SystemColors.WindowText : Separator))
                e.Graphics.DrawLine(pen, 0, 0, footer.ClientSize.Width, 0);
        };
        status.AutoSize = true;
        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0, 7, 0, 0);
        status.Font = small;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Visible = false;
        shortcuts.AutoSize = true;
        shortcuts.Dock = DockStyle.Fill;
        shortcuts.Margin = new Padding(0, 1, 0, 0);
        shortcuts.Font = small;
        shortcuts.Text = "↑ ↓  Выбрать    Enter  Открыть    Esc  Закрыть";
        shortcuts.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(shortcuts, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        ApplyTheme();
        ResumeLayout(true);
        list.DrawItem+=DrawFile; list.MouseDoubleClick+=(s,e)=>{if(list.IndexFromPoint(e.Location)>=0) OpenSelected();};
        query.TextChanged+=(s,e)=>{openWhenReady=null;UpdateList();};
        VisibleChanged+=(s,e)=>{
            if (!Visible) {
                openWhenReady = null;
                if (pending != null) pending.Cancel();
            }
        };
        tray.Icon = appIcon ?? SystemIcons.Application;
        tray.Text = "QuickFind · дважды Ctrl";
        var menu=new ContextMenuStrip(); menu.Items.Add("Открыть поиск",null,(s,e)=>ShowFinder());
        startup.Click+=(s,e)=>SetStartup(!startup.Checked); menu.Items.Add(startup);
        menu.Items.Add("Выход",null,(s,e)=>{closing=true;tray.Visible=false;Application.Exit();});
        tray.ContextMenuStrip=menu; tray.DoubleClick+=(s,e)=>ShowFinder(); tray.Visible=true;
        using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) startup.Checked=key!=null && key.GetValue("QuickFind")!=null;
        refresh.Interval=700; refresh.Tick+=(s,e)=>{
            if (!Visible) return;
            if (!Object.ReferenceEquals(shownSnapshot, index.Snapshot) || wasScanning != index.Scanning ||
                shownProblem != index.Problem) UpdateList();
        };
        refresh.Start();
    }
    static int ScalePixels(int pixels, int dpi) {
        return (int)Math.Round(pixels * dpi / 96.0);
    }
    static GraphicsPath RoundedRectangle(RectangleF bounds, float radius) {
        var path = new GraphicsPath();
        float diameter = 2 * Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
        if (diameter <= 0) return path;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    static void DrawMagnifier(Graphics graphics, RectangleF bounds, Color color, float width) {
        using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
            float lens = bounds.Width * 0.63f;
            graphics.DrawEllipse(pen, bounds.Left, bounds.Top, lens, lens);
            graphics.DrawLine(pen, bounds.Left + lens * 0.85f, bounds.Top + lens * 0.85f,
                bounds.Left + bounds.Width * 0.91f, bounds.Top + bounds.Height * 0.91f);
        }
    }
    static Icon LoadAppIcon() {
        if (!IsWindows) return null;
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception e) {
            if (!Program.FileError(e)) throw;
            return null;
        }
    }
    void UpdateRowHeight() {
        list.ItemHeight = Math.Min(255, Math.Max(ScalePixels(57, layoutDpi),
            resultFont.Height + small.Height + ScalePixels(14, layoutDpi)));
    }
    void ApplyTheme() {
        if (searchField == null) return;
        highContrast = SystemInformation.HighContrast;
        BackColor = highContrast ? SystemColors.Window : WindowBackground;
        ForeColor = highContrast ? SystemColors.WindowText : TextColor;
        list.BackColor = BackColor;
        list.ForeColor = ForeColor;
        footer.BackColor = BackColor;
        status.ForeColor = shortcuts.ForeColor = highContrast ? SystemColors.WindowText : SecondaryText;
        searchField.ApplyTheme(highContrast);
        ApplyCaptionTheme();
        ApplyListTheme();
        UpdateRowHeight();
        list.Invalidate();
        footer.Invalidate();
    }
    void ApplyCaptionTheme() {
        if (!IsWindows || !IsHandleCreated) return;
        try {
            int dark = highContrast ? 0 : 1;
            if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
            int caption = highContrast ? -1 : ColorTranslator.ToWin32(WindowBackground);
            int text = highContrast ? -1 : ColorTranslator.ToWin32(TextColor);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
        } catch (DllNotFoundException) {} catch (EntryPointNotFoundException) {}
    }
    void ApplyListTheme() {
        if (!IsWindows || !list.IsHandleCreated) return;
        try { SetWindowTheme(list.Handle, highContrast ? null : "DarkMode_Explorer", null); }
        catch (DllNotFoundException) {} catch (EntryPointNotFoundException) {}
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        ApplyCaptionTheme();
        if (searchField != null) UpdateRowHeight();
    }
    protected override void OnSystemColorsChanged(EventArgs e) {
        base.OnSystemColorsChanged(e);
        ApplyTheme();
    }
    sealed class SearchField : Panel {
        readonly TextBox input;
        readonly int dpi;
        bool highContrast;
        internal SearchField(TextBox input, int dpi) {
            this.input = input;
            this.dpi = dpi;
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = false;
            Controls.Add(input);
            input.GotFocus += (s, e) => Invalidate();
            input.LostFocus += (s, e) => Invalidate();
        }
        internal void ApplyTheme(bool useHighContrast) {
            highContrast = useHighContrast;
            BackColor = highContrast ? SystemColors.Window : WindowBackground;
            input.BackColor = highContrast ? SystemColors.Window : SearchBackground;
            input.ForeColor = highContrast ? SystemColors.WindowText : TextColor;
            Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs e) {
            base.OnLayout(e);
            if (input == null) return;
            int inset = ScalePixels(15, dpi), glyphArea = ScalePixels(44, dpi);
            int height = input.PreferredHeight;
            input.SetBounds(inset, Math.Max(0, (Height - height) / 2), Math.Max(0, Width - inset - glyphArea), height);
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) input.Focus();
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color outline = highContrast ? SystemColors.WindowText :
                input.Focused ? Color.FromArgb(65, 79, 80) : Separator;
            using (var shape = RoundedRectangle(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), ScalePixels(11, dpi)))
            using (var fill = new SolidBrush(input.BackColor))
            using (var pen = new Pen(outline)) {
                e.Graphics.FillPath(fill, shape);
                e.Graphics.DrawPath(pen, shape);
            }
            int glyphSize = ScalePixels(20, dpi);
            var glyph = new RectangleF(Width - ScalePixels(15, dpi) - glyphSize, (Height - glyphSize) / 2f, glyphSize, glyphSize);
            DrawMagnifier(e.Graphics, glyph, highContrast ? SystemColors.WindowText : SecondaryText, ScalePixels(2, dpi));
        }
    }
    internal void SetStartup(bool enabled) {
        try {
            using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                if(enabled) key.SetValue("QuickFind","\""+Application.ExecutablePath+"\" /background");
                else key.DeleteValue("QuickFind",false);
            }
            startup.Checked=enabled;
        } catch (Exception e) { MessageBox.Show("Не удалось изменить автозапуск. "+e.Message,Text); }
    }
    internal void Start(bool visible) {
        var dev = new RawDevice {Page=1,Usage=6,Flags=0x100,Target=Handle};
        if (!RegisterRawInputDevices(new[]{dev},1,(uint)Marshal.SizeOf(typeof(RawDevice))))
            MessageBox.Show("Двойной Ctrl недоступен. Поиск можно открыть через значок в области уведомлений.",Text);
        new Thread(index.Run){IsBackground=true,Priority=ThreadPriority.BelowNormal}.Start();
        if(visible) ShowFinder();
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg==WM_INPUT && packet!=IntPtr.Zero) {
            uint size=128, header=(uint)(8+2*IntPtr.Size);
            uint got=GetRawInputData(m.LParam,0x10000003,packet,ref size,header);
            if(got!=UInt32.MaxValue && got>=header+16 && Marshal.ReadInt32(packet)==1) {
                int key=(ushort)Marshal.ReadInt16(packet,(int)header+6);
                bool up=(Marshal.ReadInt16(packet,(int)header+2)&1)!=0;
                if(key==17 || key==162 || key==163) CtrlEvent(up);
                else if (!up) { released=-1000; if(controlHeld) chord=true; }
            }
        }
        base.WndProc(ref m);
    }
    void CtrlEvent(bool up) {
        long now=clock.ElapsedMilliseconds;
        if (!up) {
            if (!controlHeld) { controlHeld=true; pressed=now; chord=new[]{16,18,91,92}.Any(k=>GetAsyncKeyState(k)<0); }
            return;
        }
        if (!controlHeld) return;
        controlHeld=false;
        if (chord || now-pressed>450) { released=-1000; return; }
        if(now-released<=480) { released=-1000; BeginInvoke((Action)ShowFinder); }
        else released=now;
    }
    void ShowFinder() {
        if(closing) return;
        Show(); WindowState=FormWindowState.Normal; SetForegroundWindow(Handle); Activate();
        openWhenReady=null; query.Clear(); list.ClearSelected(); query.Focus(); UpdateList();
        if(!importing) {
            importing=true;
            Task.Factory.StartNew(recent.ImportExplorer).ContinueWith(task=>{
                if(task.IsFaulted) Trace.WriteLine(task.Exception);
                if(closing || IsDisposed) return;
                BeginInvoke((Action)(()=>{importing=false; if(Visible) UpdateList();}));
            });
        }
    }
    async void UpdateList() {
        if (closing || !Visible) return;
        if (pending!=null) pending.Cancel();
        var source=new CancellationTokenSource(); pending=source;
        var token=source.Token;
        string q=query.Text;
        var snapshot=index.Snapshot; shownSnapshot=snapshot; wasScanning=index.Scanning;
        try {
            var times = recent.Snapshot;
            Entry[] hits;
            if (q.Length==0) hits=times.OrderByDescending(x=>x.Value).Take(10).Select(x=>new Entry(x.Key)).ToArray();
            else {
                if(displayedQuery!=q) list.Items.Clear();
                SetStatus("Поиск…");
                await Task.Delay(65,token);
                hits=await Task.Run(()=>Match.Search(snapshot,q,times,token),token);
            }
            if (token.IsCancellationRequested || !Visible) return;
            var old=displayedQuery==q ? list.SelectedItem as Entry : null;
            displayedQuery=q;
            list.BeginUpdate();
            try {
                list.Items.Clear(); list.Items.AddRange(hits);
                if(hits.Length>0) list.SelectedIndex=Math.Max(0,Array.FindIndex(hits,v=>old!=null && v.PathName==old.PathName));
            } finally { list.EndUpdate(); }
            string note = q.Length == 0 ? (hits.Length == 0 ? "Пока нет недавних файлов" : "Недавние файлы") :
                hits.Length == 0 ? "Совпадений пока нет" : "";
            shownProblem = index.Problem;
            SetStatus(String.Join("   ·   ", new[] {
                note, index.Scanning ? "Идёт индексация дисков…" : "", shownProblem
            }.Where(text => text.Length != 0)));
            if(openWhenReady==q) {openWhenReady=null; OpenSelected();}
        } catch(OperationCanceledException) {} finally { if(pending==source) pending=null; source.Dispose(); }
    }
    void SetStatus(string message) {
        status.Text = message;
        status.Visible = message.Length != 0;
    }
    void DrawFile(object sender, DrawItemEventArgs e) {
        if (e.Index < 0 || e.Index >= list.Items.Count) return;
        var item = (Entry)list.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0 || e.Index == list.SelectedIndex;
        Color background = highContrast ? SystemColors.Window : WindowBackground;
        Color main = highContrast ? SystemColors.WindowText : TextColor;
        Color secondary = highContrast ? SystemColors.WindowText : SecondaryText;
        using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);

        int dpi = layoutDpi;
        var outline = new Rectangle(e.Bounds.Left + 1, e.Bounds.Top + 1, e.Bounds.Width - 2, e.Bounds.Height - 3);
        if (selected) {
            if (highContrast) {
                using (var brush = new SolidBrush(SystemColors.Highlight))
                    e.Graphics.FillRectangle(brush, outline);
                main = secondary = SystemColors.HighlightText;
            } else {
                var graphicsState = e.Graphics.Save();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float border = Math.Max(1, ScalePixels(1, dpi));
                var shapeBounds = RectangleF.Inflate(outline, -border / 2, -border / 2);
                using (var shape = RoundedRectangle(shapeBounds, ScalePixels(8, dpi)))
                using (var brush = new SolidBrush(SelectedBackground))
                using (var pen = new Pen(Accent, border)) {
                    e.Graphics.FillPath(brush, shape);
                    e.Graphics.DrawPath(pen, shape);
                }
                e.Graphics.Restore(graphicsState);
            }
        } else if (e.Index + 1 < list.Items.Count) {
            int inset = ScalePixels(12, dpi);
            using (var pen = new Pen(highContrast ? SystemColors.WindowText : Separator))
                e.Graphics.DrawLine(pen, e.Bounds.Left + inset, e.Bounds.Bottom - 1,
                    e.Bounds.Right - inset, e.Bounds.Bottom - 1);
        }

        int margin = ScalePixels(13, dpi), gap = ScalePixels(3, dpi);
        var line = new Rectangle(e.Bounds.Left + margin,
            e.Bounds.Top + (e.Bounds.Height - resultFont.Height - small.Height - gap) / 2,
            Math.Max(0, e.Bounds.Width - 2 * margin), resultFont.Height);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, item.Name, resultFont, line, main, flags | TextFormatFlags.EndEllipsis);
        line.Y += line.Height + gap;
        line.Height = small.Height;
        TextRenderer.DrawText(e.Graphics, Path.GetDirectoryName(item.PathName), small, line, secondary,
            flags | TextFormatFlags.PathEllipsis);
        if (list.Focused && (e.State & DrawItemState.Focus) != 0) {
            outline.Inflate(-ScalePixels(3, dpi), -ScalePixels(3, dpi));
            Color fill = selected ? highContrast ? SystemColors.Highlight : SelectedBackground : background;
            ControlPaint.DrawFocusRectangle(e.Graphics, outline, main, fill);
        }
    }
    void OpenSelected() {
        var item=list.SelectedItem as Entry;
        if(item==null) return;
        try {
            const string unavailable = "Файл находится вне поддерживаемых локальных папок или недоступен";
            FileAttributes attributes;
            if (!Program.TryGetFixedFileAttributes(item.PathName, Program.GetFixedDrives(), out attributes)) {
                SetStatus(unavailable);
                return;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || ActiveExtensions.Contains(Path.GetExtension(item.PathName))) {
                string prompt = "Windows может запустить программу или открыть другой ресурс через этот файл.\n\n"
                    + item.PathName + "\n\nОткрыть файл?";
                if (MessageBox.Show(this, prompt, "Подтверждение открытия файла", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                if (!Program.TryGetFixedFileAttributes(item.PathName, Program.GetFixedDrives(), out attributes)) {
                    SetStatus(unavailable);
                    return;
                }
            }
            using (Process.Start(new ProcessStartInfo(item.PathName) { UseShellExecute = true })) {}
            Hide(); recent.Remember(item.PathName);
        } catch(Exception e) { MessageBox.Show(this,e.Message,"Не удалось открыть файл"); }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys key) {
        if(key==Keys.Escape) {Hide();return true;}
        if(key==Keys.Enter) {
            if(pending!=null && list.SelectedItem==null) openWhenReady=query.Text;
            else OpenSelected();
            return true;
        }
        if(key==Keys.Down || key==Keys.Up) {
            if(list.Items.Count>0) list.SelectedIndex=Math.Max(0,Math.Min(list.Items.Count-1,list.SelectedIndex+(key==Keys.Down?1:-1)));
            return true;
        }
        return base.ProcessCmdKey(ref msg,key);
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if(!closing && e.CloseReason==CloseReason.UserClosing) {e.Cancel=true;Hide();}
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing) {
        if (disposing) {
            closing = true;
            if (pending != null) pending.Cancel();
            index.Stop(); refresh.Dispose(); tray.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) {
            resultFont.Dispose(); searchFont.Dispose(); small.Dispose();
            if (appIcon != null) appIcon.Dispose();
        }
        if(packet!=IntPtr.Zero) {Marshal.FreeHGlobal(packet);packet=IntPtr.Zero;}
    }
}
