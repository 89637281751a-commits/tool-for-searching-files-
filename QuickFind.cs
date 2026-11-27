using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
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
    [STAThread] static void Main(string[] args) {
        bool first;
        using (var single = new Mutex(true, @"Local\QuickFind.Desktop.2026", out first)) {
            if (!first) return;
            Directory.CreateDirectory(Home);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var ui = new Finder()) {
                if (args.Contains("/setup")) ui.SetStartup(true);
                ui.Start(!args.Contains("/background"));
                Application.Run();
            }
            GC.KeepAlive(single);
        }
    }
    internal static bool FileError(Exception e) {
        return e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException || e is ArgumentException || e is NotSupportedException;
    }
    internal static void SaveLines(string file, IEnumerable<string> lines) {
        string tmp = file + ".part";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(tmp, file, null);
        else File.Move(tmp, file);
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
    readonly string cache = Path.Combine(Program.Home, "index.txt");
    readonly Dictionary<string, Entry> files = new Dictionary<string, Entry>(CI);
    readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    readonly ConcurrentQueue<FileSystemEventArgs> changes = new ConcurrentQueue<FileSystemEventArgs>();
    volatile bool rescan, stopping;
    internal volatile bool Scanning = true;
    internal volatile Entry[] Snapshot = new Entry[0];
    internal volatile string Problem = "";
    DateTime nextPreview;
    bool preview;
    internal void Stop() { stopping = true; }
    internal void Run() {
        try { if (File.Exists(cache)) {
            foreach (string p in File.ReadLines(cache)) if (p.Length > 2) files[p] = new Entry(p);
            Publish();
        }} catch (Exception e) { if (!Program.FileError(e)) throw; }
        DateTime nextScan = DateTime.MinValue, nextSave = DateTime.MinValue;
        bool dirty = false;
        while (!stopping) {
            try {
                if (rescan || DateTime.UtcNow >= nextScan) {
                    rescan = false; Scanning = true;
                    foreach (var w in watchers) w.Dispose();
                    watchers.Clear();
                    FileSystemEventArgs ignored;
                    while (changes.TryDequeue(out ignored)) {}
                    preview = Snapshot.Length == 0; nextPreview = DateTime.UtcNow.AddSeconds(2);
                    var fresh = new Dictionary<string, Entry>(CI);
                    foreach (var d in DriveInfo.GetDrives()) {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        Watch(d.RootDirectory.FullName);
                        Walk(d.RootDirectory.FullName, fresh);
                        if (stopping) return;
                    }
                    files.Clear(); foreach (var pair in fresh) files.Add(pair.Key, pair.Value);
                    preview = false; Publish(); Scanning = false; dirty = true;
                    nextScan = DateTime.UtcNow.AddMinutes(30);
                }
                bool updated = false;
                FileSystemEventArgs ev;
                for (int i = 0; i < 4096 && changes.TryDequeue(out ev); i++) {
                    if (Excluded(ev.FullPath)) continue;
                    var rename = ev as RenamedEventArgs;
                    if (rename != null) Remove(rename.OldFullPath);
                    if (ev.ChangeType == WatcherChangeTypes.Deleted) Remove(ev.FullPath);
                    else if (File.Exists(ev.FullPath)) files[ev.FullPath] = new Entry(ev.FullPath);
                    else if (Directory.Exists(ev.FullPath)) Walk(ev.FullPath, files);
                    updated = true;
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
        return path.StartsWith(Program.Home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    void Watch(string root) {
        try {
            var w = new FileSystemWatcher(root);
            w.IncludeSubdirectories = true;
            w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
            w.InternalBufferSize = 65536;
            w.Created += (s,e) => changes.Enqueue(e); w.Deleted += (s,e) => changes.Enqueue(e);
            w.Renamed += (s,e) => changes.Enqueue(e); w.Error += (s,e) => rescan = true;
            watchers.Add(w); w.EnableRaisingEvents = true;
        } catch (Exception e) { if (!Program.FileError(e)) throw; }
    }
    void Remove(string path) {
        if (files.Remove(path)) return;
        string prefix = path + Path.DirectorySeparatorChar;
        foreach (string child in files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray()) files.Remove(child);
    }
    void Walk(string root, Dictionary<string,Entry> target) {
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
                    if ((a & FileAttributes.Directory) == 0) target[item.FullName] = new Entry(item.FullName);
                    else if ((a & FileAttributes.ReparsePoint) == 0) dirs.Push(item.FullName);
                }
            } catch (Exception e) { if (!Program.FileError(e)) throw; }
        }
    }
}

sealed class Recent {
    readonly string file = Path.Combine(Program.Home, "recent.txt");
    readonly Dictionary<string,long> times = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new object();
    internal Recent() {
        try {
            if (File.Exists(file)) foreach (var line in File.ReadLines(file)) {
                int i = line.IndexOf('\t'); long ticks;
                if (i > 0 && long.TryParse(line.Substring(0,i),out ticks)) times[line.Substring(i+1)] = ticks;
            }
        } catch (Exception e) { if (!Program.FileError(e)) throw; }
    }
    internal Dictionary<string,long> Copy() {
        lock (gate) return new Dictionary<string,long>(times,StringComparer.OrdinalIgnoreCase);
    }
    internal void Remember(string p) { Merge(new[] {new KeyValuePair<string,long>(p,DateTime.UtcNow.Ticks)}); }
    void Merge(IEnumerable<KeyValuePair<string,long>> items) {
        lock (gate) {
            foreach (var x in items) {
                long old;
                if (!times.TryGetValue(x.Key,out old) || old < x.Value) times[x.Key] = x.Value;
            }
            var kept = times.Where(x => File.Exists(x.Key)).OrderByDescending(x => x.Value).Take(10).ToArray();
            times.Clear(); foreach (var x in kept) times[x.Key] = x.Value;
            try { Program.SaveLines(file, kept.Select(x => x.Value.ToString() + "\t" + x.Key)); }
            catch (Exception e) { if (!Program.FileError(e)) throw; }
        }
    }
    internal void ImportExplorer() {
        var items = new List<KeyValuePair<string,long>>();
        object shell = null;
        try {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(x=>x.LastWriteTimeUtc)) {
                object link = null;
                try {
                    link = ((dynamic)shell).CreateShortcut(f.FullName);
                    string path = ((dynamic)link).TargetPath;
                    if (!String.IsNullOrEmpty(path) && File.Exists(path) && !items.Any(x=>String.Equals(x.Key,path,StringComparison.OrdinalIgnoreCase)))
                        items.Add(new KeyValuePair<string,long>(path,f.LastWriteTimeUtc.Ticks));
                } catch (COMException) {} finally { if(link!=null) Marshal.ReleaseComObject(link); }
                if (items.Count == 10) break;
            }
        } catch (Exception e) { if (!Program.FileError(e) && !(e is COMException)) throw; }
        finally { if (shell!=null) Marshal.ReleaseComObject(shell); }
        Merge(items);
    }
}

sealed class Match {
    internal Entry Item; internal int Rank; internal long Last;
    internal static int Compare(Match x, Match y) {
        int c = x.Rank.CompareTo(y.Rank); if (c!=0) return c;
        c = y.Last.CompareTo(x.Last); if (c!=0) return c;
        c = StringComparer.OrdinalIgnoreCase.Compare(x.Item.Name,y.Item.Name); if (c!=0) return c;
        return StringComparer.OrdinalIgnoreCase.Compare(x.Item.PathName,y.Item.PathName);
    }
    internal static Entry[] Search(Entry[] all, string q, Dictionary<string,long> times, CancellationToken token) {
        var best = new SortedSet<Match>(Comparer<Match>.Create(Compare));
        int i=0;
        foreach (Entry item in all) {
            if ((i++ & 1023)==0) token.ThrowIfCancellationRequested();
            int pos = item.Name.IndexOf(q,StringComparison.OrdinalIgnoreCase);
            if (pos<0) continue;
            int rank = String.Equals(item.Name,q,StringComparison.OrdinalIgnoreCase) || String.Equals(item.Stem,q,StringComparison.OrdinalIgnoreCase) ? 0 : pos==0 ? 1 : 2;
            if (best.Count==100 && rank>best.Max.Rank) continue;
            long last; times.TryGetValue(item.PathName,out last);
            var result = new Match {Item=item, Rank=rank, Last=last};
            if (best.Count<100 || Compare(result,best.Max)<0) {
                best.Add(result); if (best.Count>100) best.Remove(best.Max);
            }
        }
        return best.Select(m=>m.Item).ToArray();
    }
}

sealed class Finder : Form {
    const int WM_INPUT=0xff;
    static readonly Color WindowColor = Color.FromArgb(23, 26, 29);
    static readonly Color InputColor = Color.FromArgb(43, 48, 52);
    static readonly Color RowColor = Color.FromArgb(32, 36, 39);
    static readonly Color ActiveRowColor = Color.FromArgb(37, 53, 53);
    static readonly Color TextColor = Color.FromArgb(240, 244, 244);
    static readonly Color HintColor = Color.FromArgb(184, 196, 197);
    static readonly Color DividerColor = Color.FromArgb(66, 75, 78);
    static readonly Color Mint = Color.FromArgb(118, 247, 211);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    readonly Panel searchBox = new Panel();
    readonly Font searchFont = new Font("Segoe UI", 14);
    readonly Icon appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

    [StructLayout(LayoutKind.Sequential)] struct RawDevice {
        public ushort Page, Usage; public uint Flags; public IntPtr Target;
    }
    [DllImport("user32.dll", SetLastError=true)]
    static extern bool RegisterRawInputDevices(RawDevice[] devices,uint count,uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input,uint cmd,IntPtr data,ref uint size,uint header);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    readonly TextBox query = new TextBox();
    readonly ListBox list = new ListBox();
    readonly Label status = new Label();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem startup = new ToolStripMenuItem("Запускать при входе в Windows");
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
    readonly Font small = new Font("Segoe UI",9);
    readonly DiskIndex index = new DiskIndex();
    readonly Recent recent = new Recent();
    readonly Stopwatch clock = Stopwatch.StartNew();
    IntPtr packet = Marshal.AllocHGlobal(128);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    long pressed, released=-1000;
    bool controlHeld, chord, importing, closing;
    CancellationTokenSource pending;
    Entry[] shownSnapshot;
    bool wasScanning;
    string displayedQuery, openWhenReady;
    public Finder() {
        Text = "QuickFind";
        Icon = appIcon;
        Font = new Font("Segoe UI", 11);
        BackColor = WindowColor;
        ForeColor = TextColor;
        Size = new Size(690, 480);
        MinimumSize = new Size(440, 300);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        KeyPreview = true;

        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14),
            BackColor = WindowColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        searchBox.Dock = DockStyle.Top;
        searchBox.Height = searchFont.Height + 22;
        searchBox.Margin = new Padding(0, 0, 0, 12);
        searchBox.Padding = new Padding(12, 10, 42, 6);
        searchBox.BackColor = InputColor;
        searchBox.Paint += PaintSearchBox;
        searchBox.MouseDown += (sender, args) => query.Focus();

        query.Font = searchFont;
        query.BackColor = InputColor;
        query.ForeColor = TextColor;
        query.BorderStyle = BorderStyle.None;
        query.AccessibleName = "Имя файла";
        query.Dock = DockStyle.Top;
        query.GotFocus += (sender, args) => searchBox.Invalidate();
        query.LostFocus += (sender, args) => searchBox.Invalidate();
        searchBox.Controls.Add(query);
        layout.Controls.Add(searchBox, 0, 0);

        list.Dock = DockStyle.Fill;
        list.Margin = System.Windows.Forms.Padding.Empty;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = RowColor;
        list.ForeColor = TextColor;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.IntegralHeight = false;
        list.ItemHeight = Font.Height + small.Height + 16;
        list.AccessibleName = "Найденные файлы";
        layout.Controls.Add(list, 0, 1);

        status.ForeColor = HintColor;
        status.Font = small;
        status.AutoSize = true;
        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0, 10, 0, 0);
        layout.Controls.Add(status, 0, 2);
        Controls.Add(layout);
        list.DrawItem+=DrawFile; list.MouseDoubleClick+=(s,e)=>{if(list.IndexFromPoint(e.Location)>=0) OpenSelected();};
        query.TextChanged+=(s,e)=>{openWhenReady=null;UpdateList();};
        VisibleChanged+=(s,e)=>{if(!Visible) openWhenReady=null;};
        tray.Icon=appIcon; tray.Text="Поиск файлов · дважды Ctrl";
        var menu=new ContextMenuStrip(); menu.Items.Add("Открыть поиск",null,(s,e)=>ShowFinder());
        startup.Click+=(s,e)=>SetStartup(!startup.Checked); menu.Items.Add(startup);
        menu.Items.Add("Выход",null,(s,e)=>{closing=true;tray.Visible=false;Application.Exit();});
        tray.ContextMenuStrip=menu; tray.DoubleClick+=(s,e)=>ShowFinder(); tray.Visible=true;
        using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) startup.Checked=key!=null && key.GetValue("QuickFind")!=null;
        refresh.Interval=700; refresh.Tick+=(s,e)=>{
            if (!Visible) return;
            if (!Object.ReferenceEquals(shownSnapshot,index.Snapshot) || wasScanning!=index.Scanning) UpdateList();
        };
        refresh.Start();
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        int dark = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
    }

    void PaintSearchBox(object sender, PaintEventArgs e) {
        var box = searchBox.ClientRectangle;
        if (box.Width < 2 || box.Height < 2) return;
        using (var border = new Pen(query.Focused ? Mint : DividerColor))
            e.Graphics.DrawRectangle(border, 0, 0, box.Width - 1, box.Height - 1);
        float unit = Math.Max(1f, searchFont.Height / 22f);
        float x = box.Right - 30 * unit, y = box.Height / 2f - 7 * unit;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var ink = new Pen(HintColor, 1.6f * unit)) {
            e.Graphics.DrawEllipse(ink, x, y, 11 * unit, 11 * unit);
            e.Graphics.DrawLine(ink, x + 9.5f * unit, y + 9.5f * unit,
                x + 15 * unit, y + 15 * unit);
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
            var times=recent.Copy();
            Entry[] hits;
            if (q.Length==0) hits=times.OrderByDescending(x=>x.Value).Take(10).Select(x=>new Entry(x.Key)).ToArray();
            else {
                if(displayedQuery!=q) list.Items.Clear();
                status.Text="Поиск…";
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
            string note=q.Length==0 ? (hits.Length==0?"Пока нет недавних файлов":"Недавние файлы") :
                hits.Length==0 ? "Совпадений пока нет" : "Enter — открыть    ↑ ↓ — выбрать    Esc — закрыть";
            status.Text=note+(index.Scanning?"   ·   Идёт индексация дисков…":"")+
                (index.Problem.Length==0?"":"   ·   "+index.Problem);
            if(openWhenReady==q) {openWhenReady=null; OpenSelected();}
        } catch(OperationCanceledException) {} finally { if(pending==source) pending=null; source.Dispose(); }
    }
    void DrawFile(object sender, DrawItemEventArgs e) {
        if (e.Index < 0 || e.Index >= list.Items.Count) return;
        var item = (Entry)list.Items[e.Index];
        Rectangle bounds = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var fill = new SolidBrush(selected ? ActiveRowColor : RowColor))
            e.Graphics.FillRectangle(fill, bounds);
        if (selected) {
            using (var pen = new Pen(Mint, 2))
                e.Graphics.DrawRectangle(pen, bounds.X + 1, bounds.Y + 1,
                    Math.Max(1, bounds.Width - 3), Math.Max(1, bounds.Height - 3));
        } else {
            using (var pen = new Pen(DividerColor))
                e.Graphics.DrawLine(pen, bounds.Left + 12, bounds.Bottom - 1, bounds.Right - 12, bounds.Bottom - 1);
        }
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        int top = bounds.Top + (bounds.Height - Font.Height - small.Height - 4) / 2;
        var r = new Rectangle(bounds.Left + 13, top, Math.Max(1, bounds.Width - 26), Font.Height + 2);
        TextRenderer.DrawText(e.Graphics, item.Name, Font, r, TextColor, flags | TextFormatFlags.EndEllipsis);
        r.Y = r.Bottom + 1;
        r.Height = small.Height + 2;
        TextRenderer.DrawText(e.Graphics, Path.GetDirectoryName(item.PathName), small, r, HintColor,
            flags | TextFormatFlags.PathEllipsis);
    }
    void OpenSelected() {
        var item=list.SelectedItem as Entry;
        if(item==null) return;
        try {
            if(!File.Exists(item.PathName)) { status.Text="Файл перемещён или недоступен"; return; }
            Process.Start(new ProcessStartInfo(item.PathName){UseShellExecute=true});
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
        if(disposing) {closing=true; index.Stop(); refresh.Dispose(); tray.Dispose(); small.Dispose(); searchFont.Dispose(); if(appIcon!=null) appIcon.Dispose();}
        base.Dispose(disposing);
        if(packet!=IntPtr.Zero) {Marshal.FreeHGlobal(packet);packet=IntPtr.Zero;}
    }
}
