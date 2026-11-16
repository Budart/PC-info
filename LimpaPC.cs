// LimpaPC (integrado ao PC Info) - limpador leve. Não instala nada, não roda na inicialização.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;


static class C {
    public static readonly Color Bg = Color.FromArgb(11, 11, 12), Card = Color.FromArgb(18, 18, 20),
        Line = Color.FromArgb(32, 32, 35), Text = Color.FromArgb(230, 230, 230), Mute = Color.FromArgb(120, 120, 128),
        Dim = Color.FromArgb(80, 80, 88), White = Color.FromArgb(242, 242, 242);
    public static GraphicsPath Round(RectangleF r, float rad) {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

enum Kind { Files, Bin, Dns, Reg, Proc, Power }

class Item {
    public string Name; public Kind Kind; public string[] Paths; public string RegKind; public bool On = true;
    public long Bytes; public List<RegEntry> Reg; public string Info = "—"; public bool Hi;
}
class RegEntry { public RegistryKey Root; public string Key; public string Value; }

// ---------------- Limpeza ----------------
static class Cleaner {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr h, string root, uint flags);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("dnsapi.dll")] static extern bool DnsFlushResolverCache();

    // Expande caminhos com * (ex: ...\User Data\*\Cache)
    public static IEnumerable<string> Expand(string path) {
        path = Environment.ExpandEnvironmentVariables(path);
        var parts = path.Split('\\');
        var cur = new List<string> { parts[0] + "\\" };
        for (int i = 1; i < parts.Length; i++) {
            var seg = parts[i]; bool last = i == parts.Length - 1; var next = new List<string>();
            foreach (var c in cur) {
                try {
                    if (seg.Contains("*")) {
                        if (Directory.Exists(c)) {
                            next.AddRange(Directory.GetDirectories(c, seg));
                            if (last) next.AddRange(Directory.GetFiles(c, seg));
                        }
                    } else {
                        var p = Path.Combine(c, seg);
                        if (Directory.Exists(p) || (last && File.Exists(p))) next.Add(p);
                    }
                } catch { }
            }
            cur = next;
        }
        return cur;
    }
    static IEnumerable<string> Targets(string[] paths) {
        foreach (var p in paths) foreach (var e in Expand(p)) {
            if (File.Exists(e)) { yield return e; continue; }
            string[] ch = new string[0];
            try { ch = Directory.GetFileSystemEntries(e); } catch { }
            foreach (var c in ch) yield return c;
        }
    }
    static long DirSize(string d) {
        long s = 0;
        try { foreach (var f in Directory.GetFiles(d)) try { s += new FileInfo(f).Length; } catch { } } catch { }
        try { foreach (var sub in Directory.GetDirectories(d)) {
            try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
            s += DirSize(sub); } } catch { }
        return s;
    }
    public static long Size(string[] paths) {
        long s = 0;
        foreach (var t in Targets(paths)) {
            try { if (File.Exists(t)) s += new FileInfo(t).Length; else s += DirSize(t); } catch { }
        }
        return s;
    }
    static void Del(string p) {
        try {
            if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); return; }
            if (!Directory.Exists(p)) return;
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(p); return; }
            foreach (var e in Directory.GetFileSystemEntries(p)) Del(e);
            Directory.Delete(p);
        } catch { }
    }
    public static void Clear(string[] paths) { foreach (var t in Targets(paths).ToList()) Del(t); }

    public static long BinSize() {
        var i = new SHQUERYRBINFO(); i.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
        try { SHQueryRecycleBin(null, ref i); } catch { } return i.i64Size;
    }
    public static void EmptyBin() { try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); } catch { } }
    public static void FlushDns() { try { DnsFlushResolverCache(); } catch { } }

    public static void Svc(bool stop) {
        foreach (var n in new[] { "wuauserv", "bits" }) try {
            using (var s = new ServiceController(n)) {
                if (stop && s.Status == ServiceControllerStatus.Running) { s.Stop(); s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                if (!stop && s.Status == ServiceControllerStatus.Stopped) s.Start();
            }
        } catch { }
    }

    // ---------- Registro ----------
    static bool PathOk(string v) {
        if (string.IsNullOrWhiteSpace(v)) return true;
        v = Environment.ExpandEnvironmentVariables(v).Trim();
        if (v.StartsWith("\"")) { int e = v.IndexOf('"', 1); if (e > 1) v = v.Substring(1, e - 1); }
        else {
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^(.+?\.(exe|dll|ico|cpl|msc))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) v = m.Groups[1].Value;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^[A-Za-z]:\\")) return true; // não é caminho local: não mexe
        return File.Exists(v) || Directory.Exists(v);
    }
    static bool LocalMissing(string p) {
        return System.Text.RegularExpressions.Regex.IsMatch(p, @"^[A-Za-z]:\\") && !File.Exists(p) && !Directory.Exists(p);
    }
    static RegistryKey HKLM64() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

    public static List<RegEntry> FindReg(string kind) {
        var o = new List<RegEntry>();
        try {
            switch (kind) {
                case "mui": {
                    const string k = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
                    using (var key = Registry.CurrentUser.OpenSubKey(k)) if (key != null)
                        foreach (var n in key.GetValueNames()) {
                            var p = System.Text.RegularExpressions.Regex.Replace(n, @"\.(FriendlyAppName|ApplicationCompany)$", "");
                            if (LocalMissing(p)) o.Add(new RegEntry { Root = Registry.CurrentUser, Key = k, Value = n });
                        }
                    break; }
                case "apppaths": {
                    const string k = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
                    foreach (var root in new[] { HKLM64(), Registry.CurrentUser })
                        using (var key = root.OpenSubKey(k)) if (key != null)
                            foreach (var s in key.GetSubKeyNames())
                                using (var sk = key.OpenSubKey(s)) {
                                    var d = sk == null ? null : sk.GetValue("") as string;
                                    if (d != null && !PathOk(d)) o.Add(new RegEntry { Root = root, Key = k + "\\" + s });
                                }
                    break; }
                case "shareddlls": {
                    const string k = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
                    var root = HKLM64();
                    using (var key = root.OpenSubKey(k)) if (key != null)
                        foreach (var n in key.GetValueNames())
                            if (LocalMissing(n)) o.Add(new RegEntry { Root = root, Key = k, Value = n });
                    break; }
                case "uninstall": {
                    var list = new[] {
                        Tuple.Create(HKLM64(), @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                        Tuple.Create(HKLM64(), @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                        Tuple.Create(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") };
                    foreach (var t in list)
                        using (var key = t.Item1.OpenSubKey(t.Item2)) if (key != null)
                            foreach (var s in key.GetSubKeyNames()) {
                                if (s.StartsWith("{")) continue; // MSI: ignora por segurança
                                using (var sk = key.OpenSubKey(s)) {
                                    if (sk == null) continue;
                                    var u = sk.GetValue("UninstallString") as string; var loc = sk.GetValue("InstallLocation") as string;
                                    if (u != null && !PathOk(u) && (string.IsNullOrEmpty(loc) || !Directory.Exists(loc)))
                                        o.Add(new RegEntry { Root = t.Item1, Key = t.Item2 + "\\" + s });
                                }
                            }
                    break; }
            }
        } catch { }
        return o;
    }
    public static string BackupReg(List<RegEntry> es) {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LimpaPC_Backups");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"); int i = 0;
        foreach (var k in es.Select(e => (e.Root == Registry.CurrentUser ? "HKCU\\" : "HKLM\\") + e.Key).Distinct()) {
            try {
                var psi = new ProcessStartInfo("reg.exe", "export \"" + k + "\" \"" + Path.Combine(dir, "backup_" + stamp + "_" + (i++) + ".reg") + "\" /y")
                    { CreateNoWindow = true, UseShellExecute = false };
                using (var p = Process.Start(psi)) p.WaitForExit(30000);
            } catch { }
        }
        return dir;
    }
    public static void RemoveReg(List<RegEntry> es) {
        foreach (var e in es) try {
            if (e.Value != null) using (var k = e.Root.OpenSubKey(e.Key, true)) { if (k != null) k.DeleteValue(e.Value, false); }
            else e.Root.DeleteSubKeyTree(e.Key, false);
        } catch { }
    }
    public static string Fmt(long b) {
        if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
        if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
        if (b >= 1024) return (b / 1024) + " KB";
        return b + " B";
    }
}

// ---------------- Interface ----------------
class ListView2 : Control {
    public List<object> Rows = new List<object>(); // string = cabeçalho, Item = linha
    int scroll; const int RH = 36, HH = 34;
    public ListView2() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = C.Card; Cursor = Cursors.Hand; }
    int TotalH() { return Rows.Sum(r => r is string ? HH : RH) + 16; }
    protected override void OnMouseWheel(MouseEventArgs e) {
        scroll = Math.Max(0, Math.Min(Math.Max(0, TotalH() - Height), scroll - e.Delta / 3)); Invalidate();
    }
    object HitTest(int y) {
        int cy = 8 - scroll;
        foreach (var r in Rows) { int h = r is string ? HH : RH; if (y >= cy && y < cy + h) return r; cy += h; }
        return null;
    }
    protected override void OnMouseClick(MouseEventArgs e) {
        var it = HitTest(e.Y) as Item; if (it != null && Enabled) { it.On = !it.On; Invalidate(); }
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        var h = HitTest(e.Y) as Item; bool ch = false;
        foreach (var r in Rows.OfType<Item>()) { bool v = r == h; if (r.Hi != v) { r.Hi = v; ch = true; } }
        if (ch) Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { foreach (var r in Rows.OfType<Item>()) r.Hi = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var fh = new Font("Segoe UI", 8f, FontStyle.Bold)) using (var fr = new Font("Segoe UI", 10.5f)) using (var fs = new Font("Segoe UI", 9.5f)) {
            int y = 8 - scroll;
            foreach (var r in Rows) {
                var s = r as string;
                if (s != null) { TextRenderer.DrawText(g, s, fh, new Point(20, y + 14), C.Dim); y += HH; continue; }
                var it = (Item)r;
                var box = new RectangleF(20, y + 9, 18, 18);
                using (var p = C.Round(box, 5)) {
                    if (it.On) { using (var b = new SolidBrush(C.White)) g.FillPath(b, p);
                        using (var pen = new Pen(C.Bg, 2.2f)) g.DrawLines(pen, new[] { new PointF(box.X + 4, box.Y + 9), new PointF(box.X + 8, box.Y + 13), new PointF(box.X + 14, box.Y + 5) }); }
                    else using (var pen = new Pen(it.Hi ? C.Mute : Color.FromArgb(58, 58, 61), 1.5f)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, it.Name, fr, new Point(50, y + 8), it.On ? C.Text : C.Mute);
                var sz = TextRenderer.MeasureText(it.Info, fs);
                TextRenderer.DrawText(g, it.Info, fs, new Point(Width - 20 - sz.Width, y + 10), it.Info == "—" || it.Info == "limpo" ? C.Dim : C.Text);
                y += RH;
            }
        }
    }
}

class LBtn : Control {
    public bool Primary; bool hi;
    public LBtn(string t, bool primary) { Text = t; Primary = primary; DoubleBuffered = true; Cursor = Cursors.Hand; Height = 44; SetStyle(ControlStyles.SupportsTransparentBackColor, true); BackColor = C.Bg; }
    protected override void OnMouseEnter(EventArgs e) { hi = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hi = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(C.Bg);
        int a = !Enabled ? 90 : hi ? 215 : 255;
        var bg = Primary ? C.White : Color.FromArgb(22, 22, 24);
        using (var p = C.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10)) {
            using (var b = new SolidBrush(Color.FromArgb(a, bg))) g.FillPath(b, p);
            if (!Primary) using (var pen = new Pen(Color.FromArgb(42, 42, 45))) g.DrawPath(pen, p);
        }
        var fc = Primary ? C.Bg : C.Text;
        using (var f = new Font("Segoe UI Semibold", 10.5f))
            TextRenderer.DrawText(g, Text, f, ClientRectangle, Color.FromArgb(Enabled ? 255 : 120, fc), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

class LimpaForm : Form {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    ListView2 list = new ListView2(); Label total = new Label(), status = new Label();
    LBtn scan = new LBtn("Analisar", false), clean = new LBtn("Limpar", true);
    List<Item> items = new List<Item>();

    void F(string n, params string[] p) { items.Add(new Item { Name = n, Kind = Kind.Files, Paths = p }); list.Rows.Add(items.Last()); }
    void R(string n, string k) { items.Add(new Item { Name = n, Kind = Kind.Reg, RegKind = k }); list.Rows.Add(items.Last()); }
    void S(string n, Kind k) { items.Add(new Item { Name = n, Kind = k }); list.Rows.Add(items.Last()); }

    public LimpaForm() {
        Text = "PC Info — Limpeza"; BackColor = C.Bg; ClientSize = new Size(560, 700); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        string L = "%LOCALAPPDATA%", A = "%APPDATA%", W = "%WINDIR%", PD = "%PROGRAMDATA%";
        list.Rows.Add("ARQUIVOS E CACHE");
        F("Temporários do usuário", "%TEMP%");
        F("Temporários do Windows", W + @"\Temp");
        F("Cache do Chrome", L + @"\Google\Chrome\User Data\*\Cache", L + @"\Google\Chrome\User Data\*\Code Cache", L + @"\Google\Chrome\User Data\*\GPUCache");
        F("Cache do Edge", L + @"\Microsoft\Edge\User Data\*\Cache", L + @"\Microsoft\Edge\User Data\*\Code Cache", L + @"\Microsoft\Edge\User Data\*\GPUCache");
        F("Cache do Firefox", L + @"\Mozilla\Firefox\Profiles\*\cache2");
        F("Cache de miniaturas", L + @"\Microsoft\Windows\Explorer\thumbcache_*.db");
        F("Cache do Windows Update", W + @"\SoftwareDistribution\Download");
        F("Despejos de erro (dumps)", L + @"\CrashDumps", W + @"\Minidump", W + @"\MEMORY.DMP");
        F("Relatórios de erro (WER)", PD + @"\Microsoft\Windows\WER\ReportArchive", PD + @"\Microsoft\Windows\WER\ReportQueue");
        F("Logs do Windows", W + @"\Logs\CBS\*.log", W + @"\Logs\DISM\*.log");
        F("Cache de shaders (DirectX)", L + @"\D3DSCache", L + @"\NVIDIA\DXCache", L + @"\AMD\DxCache");
        F("Lista de arquivos recentes", A + @"\Microsoft\Windows\Recent\*.lnk");
        list.Rows.Add("SISTEMA");
        S("Lixeira", Kind.Bin); S("Cache de DNS", Kind.Dns);
        list.Rows.Add("REGISTRO  ·  BACKUP AUTOMÁTICO EM DOCUMENTOS\\LIMPAPC_BACKUPS");
        R("MUICache inválido", "mui"); R("App Paths inválidos", "apppaths");
        R("SharedDLLs inexistentes", "shareddlls"); R("Desinstaladores órfãos", "uninstall");
        foreach (var it in items) if (new[] { "Lista de arquivos recentes", "Cache do Windows Update", "Desinstaladores órfãos" }.Contains(it.Name)) it.On = false;

        var title = new Label { Text = "LimpaPC", ForeColor = Color.White, Font = new Font("Segoe UI", 20f, FontStyle.Bold), AutoSize = true, Location = new Point(26, 20) };
        total.Text = "Selecione e clique em Analisar"; total.ForeColor = C.Mute; total.AutoSize = true; total.Location = new Point(29, 64); total.Font = new Font("Segoe UI", 10f);
        var all = Link("Marcar tudo", 29, () => { foreach (var i in items) i.On = true; list.Invalidate(); });
        var none = Link("Desmarcar tudo", 125, () => { foreach (var i in items) i.On = false; list.Invalidate(); });

        var card = new Panel { Location = new Point(28, 128), Size = new Size(504, 440), BackColor = C.Bg };
        card.Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = C.Round(new RectangleF(0, 0, card.Width - 1, card.Height - 1), 14)) { using (var b = new SolidBrush(C.Card)) e.Graphics.FillPath(b, p); using (var pen = new Pen(C.Line)) e.Graphics.DrawPath(pen, p); } };
        list.Location = new Point(2, 8); list.Size = new Size(500, 424); card.Controls.Add(list);

        status.ForeColor = C.Mute; status.Location = new Point(29, 580); status.Size = new Size(504, 46); status.Font = new Font("Segoe UI", 9f);
        scan.Location = new Point(28, 632); scan.Width = 246; clean.Location = new Point(286, 632); clean.Width = 246;
        scan.Click += (s, e) => Run(false); clean.Click += (s, e) => Run(true);
        var boost = new LBtn("⚡ Game Booster", false) { Location = new Point(382, 26), Width = 150, Height = 38 };
        boost.Click += (s, e) => { using (var b = new BoosterForm()) b.ShowDialog(this); };
        Controls.AddRange(new Control[] { title, total, all, none, card, status, scan, clean, boost });
    }
    Label Link(string t, int x, Action a) {
        var l = new Label { Text = t, ForeColor = Color.FromArgb(150, 150, 158), AutoSize = true, Location = new Point(x, 100), Cursor = Cursors.Hand };
        l.Click += (s, e) => a(); l.MouseEnter += (s, e) => l.ForeColor = Color.White; l.MouseLeave += (s, e) => l.ForeColor = Color.FromArgb(150, 150, 158);
        return l;
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e); int on = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } // barra de título escura
    }
    void UI(Action a) { if (IsHandleCreated) BeginInvoke(a); }
    void Busy(bool b) { scan.Enabled = clean.Enabled = list.Enabled = !b; }

    void Run(bool doClean) {
        var sel = items.Where(i => i.On).ToList();
        if (sel.Count == 0) return;
        if (doClean && MessageBox.Show(this, "Limpar " + sel.Count + " itens selecionados?", "LimpaPC", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Busy(true);
        new Thread(() => {
            long sum = 0; var regAll = new List<RegEntry>();
            foreach (var it in items) {
                if (!it.On) { if (!doClean) { it.Info = "—"; UI(list.Invalidate); } continue; }
                var name = it.Name; UI(() => status.Text = (doClean ? "Limpando: " : "Analisando: ") + name);
                switch (it.Kind) {
                    case Kind.Files:
                        if (!doClean) { it.Bytes = Cleaner.Size(it.Paths); it.Info = Cleaner.Fmt(it.Bytes); sum += it.Bytes; }
                        else {
                            long before = Cleaner.Size(it.Paths); bool wu = it.Name == "Cache do Windows Update";
                            if (wu) Cleaner.Svc(true);
                            Cleaner.Clear(it.Paths);
                            if (wu) Cleaner.Svc(false);
                            sum += Math.Max(0, before - Cleaner.Size(it.Paths)); it.Info = "limpo";
                        }
                        break;
                    case Kind.Bin:
                        if (!doClean) { it.Bytes = Cleaner.BinSize(); it.Info = Cleaner.Fmt(it.Bytes); sum += it.Bytes; }
                        else { sum += Cleaner.BinSize(); Cleaner.EmptyBin(); it.Info = "limpo"; }
                        break;
                    case Kind.Dns:
                        if (!doClean) it.Info = "pronto"; else { Cleaner.FlushDns(); it.Info = "limpo"; }
                        break;
                    case Kind.Reg:
                        if (!doClean) { it.Reg = Cleaner.FindReg(it.RegKind); it.Info = it.Reg.Count + " itens"; }
                        else { regAll.AddRange(it.Reg ?? Cleaner.FindReg(it.RegKind)); it.Reg = null; it.Info = "limpo"; }
                        break;
                }
                UI(list.Invalidate);
            }
            string msg;
            if (!doClean) msg = "Análise concluída.";
            else {
                msg = "Concluído. " + Cleaner.Fmt(sum) + " liberados.";
                if (regAll.Count > 0) { var dir = Cleaner.BackupReg(regAll); Cleaner.RemoveReg(regAll); msg += " " + regAll.Count + " entradas de registro removidas (backup em " + dir + ")."; }
            }
            long s2 = sum;
            UI(() => { status.Text = msg; total.Text = Cleaner.Fmt(s2) + (doClean ? " liberados" : " podem ser liberados"); total.ForeColor = C.Text; Busy(false); list.Invalidate(); });
        }) { IsBackground = true }.Start();
    }
}

// ---------------- Game Booster ----------------
class BoosterForm : Form {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    ListView2 list = new ListView2(); Label status = new Label();
    LBtn go = new LBtn("Turbinar", true);
    List<Item> items = new List<Item>();

    void P(string n, bool on, params string[] procs) { items.Add(new Item { Name = n, Kind = Kind.Proc, Paths = procs, On = on }); list.Rows.Add(items.Last()); }

    public BoosterForm() {
        Text = "Game Booster"; BackColor = C.Bg; ClientSize = new Size(520, 640); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        list.Rows.Add("SEGUNDO PLANO");
        P("OneDrive", true, "OneDrive");
        P("Microsoft Teams / Skype", true, "Teams", "ms-teams", "Skype", "SkypeApp", "SkypeBackgroundHost");
        P("Adobe (Creative Cloud, updaters)", true, "AdobeARM", "CCXProcess", "CoreSync", "Creative Cloud", "AdobeIPCBroker", "Adobe Desktop Service", "AdobeUpdateService", "CCLibrary");
        P("Atualizadores (Google, Java, etc.)", true, "GoogleUpdate", "jusched", "MicrosoftEdgeUpdate", "Dropbox Update");
        P("Widgets / Celular / Copilot", true, "Widgets", "WidgetService", "PhoneExperienceHost", "YourPhone", "Copilot");
        P("Nuvem (Dropbox, Google Drive, iCloud)", true, "Dropbox", "GoogleDriveFS", "iCloudDrive", "iCloudServices");
        P("Leitores e escritório (Office, PDF)", false, "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "Acrobat", "AcroRd32", "FoxitPDFReader");
        P("Navegadores (Chrome, Edge, Firefox…)", false, "chrome", "msedge", "firefox", "opera", "brave", "vivaldi");
        list.Rows.Add("SOCIAL E MÚSICA");
        P("Discord", false, "Discord");
        P("Música (Spotify, Deezer, Apple Music…)", false, "Spotify", "Deezer", "iTunes", "AppleMusic", "TIDAL", "Amazon Music", "YouTube Music");
        list.Rows.Add("SISTEMA");
        items.Add(new Item { Name = "Plano de energia: Alto desempenho", Kind = Kind.Power }); list.Rows.Add(items.Last());
        items.Add(new Item { Name = "Liberar RAM ociosa dos processos", Kind = Kind.Bin }); list.Rows.Add(items.Last());

        var title = new Label { Text = "Game Booster", ForeColor = Color.White, Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true, Location = new Point(24, 18) };
        var sub = new Label { Text = "Fecha apps em segundo plano antes de jogar", ForeColor = C.Mute, AutoSize = true, Location = new Point(27, 58), Font = new Font("Segoe UI", 10f) };
        var card = new Panel { Location = new Point(24, 96), Size = new Size(472, 430), BackColor = C.Bg };
        card.Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = C.Round(new RectangleF(0, 0, card.Width - 1, card.Height - 1), 14)) { using (var b = new SolidBrush(C.Card)) e.Graphics.FillPath(b, p); using (var pen = new Pen(C.Line)) e.Graphics.DrawPath(pen, p); } };
        list.Location = new Point(2, 8); list.Size = new Size(468, 414); card.Controls.Add(list);
        status.ForeColor = C.Mute; status.Location = new Point(27, 536); status.Size = new Size(470, 40);
        go.Location = new Point(24, 580); go.Width = 472;
        go.Click += (s, e) => Boost();
        Controls.AddRange(new Control[] { title, sub, card, status, go });
        Shown += (s, e) => Measure();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); int on = 1; try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } }

    static Process[] Find(string[] names) {
        return names.SelectMany(n => { try { return Process.GetProcessesByName(n); } catch { return new Process[0]; } }).ToArray();
    }
    void Measure() {
        foreach (var it in items) {
            if (it.Kind == Kind.Proc) {
                var ps = Find(it.Paths); long mem = 0;
                foreach (var p in ps) try { mem += p.WorkingSet64; } catch { }
                it.Info = ps.Length == 0 ? "não aberto" : Cleaner.Fmt(mem);
            } else it.Info = "pronto";
        }
        list.Invalidate();
    }
    void Boost() {
        go.Enabled = list.Enabled = false; status.Text = "Turbinando…"; Refresh();
        long freed = 0; int killed = 0;
        foreach (var it in items.Where(i => i.On)) {
            switch (it.Kind) {
                case Kind.Proc:
                    foreach (var p in Find(it.Paths)) try { freed += p.WorkingSet64; p.Kill(); killed++; } catch { }
                    break;
                case Kind.Power:
                    try { // Alto desempenho
                        var psi = new ProcessStartInfo("powercfg.exe", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") { CreateNoWindow = true, UseShellExecute = false };
                        using (var p = Process.Start(psi)) p.WaitForExit(10000);
                    } catch { }
                    break;
                case Kind.Bin: // libera RAM ociosa
                    foreach (var p in Process.GetProcesses()) try { EmptyWorkingSet(p.Handle); } catch { }
                    break;
            }
            it.Info = "feito";
        }
        list.Invalidate();
        status.Text = killed + " processos encerrados · ~" + Cleaner.Fmt(freed) + " de RAM liberados. Bom jogo!";
        status.ForeColor = C.Text; go.Enabled = list.Enabled = true;
    }
}

