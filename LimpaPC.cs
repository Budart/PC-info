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

enum Kind { Files, Bin, Dns, Reg, Proc, Power, Wu, Tweak }

class Item {
    public string Name; public Kind Kind; public string[] Paths; public string RegKind; public bool On = true;
    public Action Act; public long Bytes; public List<RegEntry> Reg; public string Info = "—"; public bool Hi;
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
    public Color Accent = C.White;
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
                    if (it.On) { using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
                        using (var pen = new Pen(C.Bg, 2.2f)) g.DrawLines(pen, new[] { new PointF(box.X + 4, box.Y + 9), new PointF(box.X + 8, box.Y + 13), new PointF(box.X + 14, box.Y + 5) }); }
                    else using (var pen = new Pen(it.Hi ? C.Mute : Color.FromArgb(58, 58, 61), 1.5f)) g.DrawPath(pen, p);
                }
                var sz = TextRenderer.MeasureText(it.Info, fs);
                int ix = Width - 20 - sz.Width;
                TextRenderer.DrawText(g, it.Name, fr, new Rectangle(50, y, Math.Max(0, ix - 62), RH), it.On ? C.Text : C.Mute, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, it.Info, fs, new Rectangle(ix, y, sz.Width + 4, RH), it.Info == "—" || it.Info == "limpo" ? C.Dim : C.Text, TextFormatFlags.VerticalCenter);
                y += RH;
            }
        }
    }
}

class LBtn : Control {
    public bool Primary; bool hi; public Color Accent = C.White;
    public LBtn(string t, bool primary) { Text = t; Primary = primary; DoubleBuffered = true; Cursor = Cursors.Hand; Height = 44; BackColor = C.Bg; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
    protected override void OnMouseEnter(EventArgs e) { hi = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hi = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent != null ? Parent.BackColor : C.Bg);
        int a = !Enabled ? 90 : hi ? 215 : 255;
        var bg = Primary ? Accent : Color.FromArgb(28, 28, 32);
        using (var p = C.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10)) {
            using (var b = new SolidBrush(Color.FromArgb(a, bg))) g.FillPath(b, p);
            if (!Primary) using (var pen = new Pen(Color.FromArgb(hi ? 120 : 60, Accent))) g.DrawPath(pen, p);
        }
        var fc = Primary ? Color.FromArgb(12, 12, 14) : C.Text;
        using (var f = PCInfo.F(10.5f, true))
            TextRenderer.DrawText(g, Text, f, ClientRectangle, Color.FromArgb(Enabled ? 255 : 120, fc), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// Ícones desenhados (vassoura cruzada com fuzil / controle de videogame)
static class Icons {
    static Pen RP(Color c, float w) { return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; }
    public static void BroomRifle(Graphics g, RectangleF r, Color c) {
        var st = g.Save(); g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2); float s = r.Width / 64f; g.ScaleTransform(s, s);
        // fuzil (diagonal ↘)
        g.RotateTransform(45);
        using (var b = new SolidBrush(Color.FromArgb(200, c))) {
            g.FillRectangle(b, -30, -3, 40, 5);            // cano + corpo
            g.FillRectangle(b, -6, -6, 22, 8);              // ferrolho
            g.FillPolygon(b, new[] { new PointF(14, -6), new PointF(30, -4), new PointF(30, 8), new PointF(16, 3) }); // coronha
            g.FillRectangle(b, 0, 2, 5, 10);                // carregador
            g.FillRectangle(b, -30, -6, 2, 3);              // mira
        }
        g.RotateTransform(-90);
        // vassoura (diagonal ↗)
        using (var p = RP(c, 4.5f)) g.DrawLine(p, -30, 0, 10, 0);
        using (var b = new SolidBrush(c)) g.FillPolygon(b, new[] { new PointF(8, -5), new PointF(8, 5), new PointF(30, 13), new PointF(30, -13) });
        using (var p = new Pen(Color.FromArgb(120, 0, 0, 0), 1.5f)) for (int k = -2; k <= 2; k++) g.DrawLine(p, 13, k * 2, 29, k * 5);
        using (var p = RP(c, 3)) g.DrawLine(p, 8, -6, 8, 6);
        g.Restore(st);
    }
    public static void Gamepad(Graphics g, RectangleF r, Color c) {
        var st = g.Save(); g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2); float s = r.Width / 64f; g.ScaleTransform(s, s);
        using (var gp = new GraphicsPath()) {
            gp.AddArc(-30, -14, 22, 22, 180, 90); gp.AddLine(-19, -14, 19, -14); gp.AddArc(8, -14, 22, 22, 270, 90);
            gp.AddBezier(30, -3, 34, 14, 30, 22, 22, 20); gp.AddBezier(22, 20, 16, 18, 14, 8, 8, 8);
            gp.AddLine(8, 8, -8, 8); gp.AddBezier(-8, 8, -14, 8, -16, 18, -22, 20); gp.AddBezier(-22, 20, -30, 22, -34, 14, -30, -3); gp.CloseFigure();
            using (var b = new SolidBrush(c)) g.FillPath(b, gp);
        }
        var d = Color.FromArgb(20, 14, 30);
        using (var b = new SolidBrush(d)) {
            g.FillRectangle(b, -21, -4, 14, 4); g.FillRectangle(b, -16, -9, 4, 14);       // direcional
            g.FillEllipse(b, 13, -9, 6, 6); g.FillEllipse(b, 19, -3, 6, 6); g.FillEllipse(b, 13, 3, 6, 6); g.FillEllipse(b, 7, -3, 6, 6); // botões
            g.FillEllipse(b, -6, -6, 4, 4); g.FillEllipse(b, 2, -6, 4, 4);
        }
        g.Restore(st);
    }
}

// Cabeçalho de cada coluna: faixa em degradê com ícone
class Header : Control {
    public string Title, Sub; public Color Accent; public Action<Graphics, RectangleF, Color> Icon;
    public Header() { DoubleBuffered = true; Height = 104; SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(C.Bg);
        var r = new RectangleF(0, 0, Width - 1, Height - 10);
        using (var p = C.Round(r, 14)) {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(70, Accent), Color.FromArgb(12, Accent), LinearGradientMode.Horizontal)) g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(90, Accent))) g.DrawPath(pen, p);
        }
        var ib = new RectangleF(16, 13, 68, 68);
        using (var p = C.Round(ib, 14)) using (var b = new SolidBrush(Color.FromArgb(40, Accent))) g.FillPath(b, p);
        Icon(g, new RectangleF(ib.X + 6, ib.Y + 6, 56, 56), Accent);
        using (var f = PCInfo.F(19f, true)) TextRenderer.DrawText(g, Title, f, new Point(98, 16), Color.White);
        using (var f = PCInfo.F(10f, false)) TextRenderer.DrawText(g, Sub, f, new Point(100, 54), Color.FromArgb(200, 210, 210, 220));
    }
}

// "Selecionar tudo" (checkbox desenhado)
class SelAll : Control {
    public bool On = true; public Color Accent; public event Action<bool> Changed;
    public SelAll() { DoubleBuffered = true; Height = 34; Cursor = Cursors.Hand; SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnClick(EventArgs e) { On = !On; Invalidate(); if (Changed != null) Changed(On); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(C.Bg);
        var box = new RectangleF(4, 8, 18, 18);
        using (var p = C.Round(box, 5)) {
            if (On) { using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
                using (var pen = new Pen(C.Bg, 2.2f)) g.DrawLines(pen, new[] { new PointF(box.X + 4, box.Y + 9), new PointF(box.X + 8, box.Y + 13), new PointF(box.X + 14, box.Y + 5) }); }
            else using (var pen = new Pen(Color.FromArgb(90, 90, 96), 1.5f)) g.DrawPath(pen, p);
        }
        using (var f = PCInfo.F(10f, true)) TextRenderer.DrawText(g, "Selecionar tudo", f, new Point(30, 8), C.Text);
    }
}

// Card arredondado que hospeda a lista
class ListCard : Panel {
    public ListCard(ListView2 l) { BackColor = C.Bg; Padding = new Padding(2, 8, 2, 8); DoubleBuffered = true; l.Dock = DockStyle.Fill; Controls.Add(l); ResizeRedraw = true; }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = C.Round(new RectangleF(0, 0, Width - 1, Height - 1), 14)) { using (var b = new SolidBrush(C.Card)) e.Graphics.FillPath(b, p); using (var pen = new Pen(C.Line)) e.Graphics.DrawPath(pen, p); }
    }
}

// Tela inteira: 2 colunas (Limpa PC | Game Booster)
class LimpaScreen : TableLayoutPanel {
    public LimpaScreen() {
        BackColor = C.Bg; ColumnCount = 2; RowCount = 1; Padding = new Padding(12, 4, 12, 8);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(new LimpaPanel { Dock = DockStyle.Fill, Margin = new Padding(4, 0, 10, 0) }, 0, 0);
        Controls.Add(new BoosterPanel { Dock = DockStyle.Fill, Margin = new Padding(10, 0, 4, 0) }, 1, 0);
    }
}

class LimpaPanel : UserControl {
    static readonly Color ACC = Color.FromArgb(0, 190, 255);
    ListView2 list = new ListView2 { Accent = ACC }; Label total = new Label(), status = new Label();
    LBtn scan = new LBtn("Analisar", false) { Accent = ACC }, clean = new LBtn("Limpar", true) { Accent = ACC };
    List<Item> items = new List<Item>();

    void F(string n, params string[] p) { items.Add(new Item { Name = n, Kind = Kind.Files, Paths = p }); list.Rows.Add(items.Last()); }
    void R(string n, string k) { items.Add(new Item { Name = n, Kind = Kind.Reg, RegKind = k }); list.Rows.Add(items.Last()); }
    void S(string n, Kind k) { items.Add(new Item { Name = n, Kind = k }); list.Rows.Add(items.Last()); }

    public LimpaPanel() {
        BackColor = C.Bg; DoubleBuffered = true; ResizeRedraw = true;
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
        F("Cache do Brave / Opera", L + @"\BraveSoftware\Brave-Browser\User Data\*\Cache", L + @"\BraveSoftware\Brave-Browser\User Data\*\Code Cache", L + @"\Opera Software\*\Cache", A + @"\Opera Software\*\Code Cache");
        F("Cache de internet do Windows", L + @"\Microsoft\Windows\INetCache\IE", L + @"\Microsoft\Windows\INetCache\Low");
        F("Cache de ícones", L + @"\Microsoft\Windows\Explorer\iconcache_*.db");
        F("Otimização de Entrega (Windows)", W + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
        F("Relatórios de erro do usuário", L + @"\Microsoft\Windows\WER\ReportArchive", L + @"\Microsoft\Windows\WER\ReportQueue");
        F("Cache OpenGL (NVIDIA/AMD)", L + @"\NVIDIA\GLCache", L + @"\AMD\GLCache", A + @"\NVIDIA\ComputeCache");
        F("Arquivos de instalação NVIDIA/AMD", PD + @"\NVIDIA Corporation\Downloader", @"%SYSTEMDRIVE%\AMD\*");
        F("Cache do Discord", A + @"\discord\Cache", A + @"\discord\Code Cache", A + @"\discord\GPUCache");
        F("Cache do Spotify", L + @"\Spotify\Storage", L + @"\Spotify\Data");
        F("Cache do Teams / Slack", A + @"\Microsoft\Teams\Cache", A + @"\Microsoft\Teams\Code Cache", A + @"\Slack\Cache", A + @"\Slack\Code Cache");
        F("Cache web da Steam / Epic", L + @"\Steam\htmlcache", L + @"\EpicGamesLauncher\Saved\webcache*");
        F("Prefetch do Windows", W + @"\Prefetch\*.pf");
        F("Lista de arquivos recentes", A + @"\Microsoft\Windows\Recent\*.lnk");
        list.Rows.Add("SISTEMA");
        S("Lixeira", Kind.Bin); S("Cache de DNS", Kind.Dns);
        list.Rows.Add("REGISTRO  ·  BACKUP EM DOCUMENTOS\\LIMPAPC_BACKUPS");
        R("MUICache inválido", "mui"); R("App Paths inválidos", "apppaths");
        R("SharedDLLs inexistentes", "shareddlls"); R("Desinstaladores órfãos", "uninstall");
        foreach (var it in items) if (new[] { "Lista de arquivos recentes", "Cache do Windows Update", "Desinstaladores órfãos", "Prefetch do Windows", "Arquivos de instalação NVIDIA/AMD" }.Contains(it.Name)) it.On = false;

        var head = new Header { Dock = DockStyle.Top, Title = "Limpa PC", Sub = "Arquivos temporários, caches e registro", Accent = ACC, Icon = Icons.BroomRifle };
        var sel = new SelAll { Dock = DockStyle.Top, Accent = ACC, On = false };
        sel.Changed += on => { foreach (var i in items) i.On = on; list.Invalidate(); };
        total.Text = "Selecione e clique em Analisar"; total.ForeColor = C.Mute; total.Font = PCInfo.F(10f, true); total.TextAlign = ContentAlignment.MiddleRight;
        total.Dock = DockStyle.Fill; total.BackColor = C.Bg;
        var selRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = C.Bg }; sel.Dock = DockStyle.Left; sel.Width = 170;
        selRow.Controls.Add(total); selRow.Controls.Add(sel);

        var foot = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 98, ColumnCount = 2, RowCount = 2, BackColor = C.Bg };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        status.ForeColor = C.Mute; status.Font = PCInfo.F(9f, false); status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft;
        foot.Controls.Add(status, 0, 0); foot.SetColumnSpan(status, 2);
        scan.Dock = clean.Dock = DockStyle.Fill; scan.Margin = new Padding(0, 4, 6, 2); clean.Margin = new Padding(6, 4, 0, 2);
        foot.Controls.Add(scan, 0, 1); foot.Controls.Add(clean, 1, 1);
        scan.Click += (s, e) => Run(false); clean.Click += (s, e) => Run(true);

        var card = new ListCard(list) { Dock = DockStyle.Fill };
        Controls.Add(card); Controls.Add(selRow); Controls.Add(head); Controls.Add(foot);
    }
    void UI(Action a) { if (IsHandleCreated) BeginInvoke(a); }
    void Busy(bool b) { scan.Enabled = clean.Enabled = list.Enabled = !b; }

    void Run(bool doClean) {
        var sel = items.Where(i => i.On).ToList();
        if (sel.Count == 0) { status.Text = "Nenhum item selecionado."; return; }
        if (doClean && MessageBox.Show(FindForm(), "Limpar " + sel.Count + " itens selecionados?", "Limpa PC", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
            UI(() => { status.Text = msg; total.Text = Cleaner.Fmt(s2) + (doClean ? " liberados" : " a liberar"); total.ForeColor = ACC; Busy(false); list.Invalidate(); });
        }) { IsBackground = true }.Start();
    }
}

class BoosterPanel : UserControl {
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    static readonly Color ACC = Color.FromArgb(185, 110, 255);
    ListView2 list = new ListView2 { Accent = ACC }; Label status = new Label(), info = new Label();
    LBtn go = new LBtn("⚡  Turbinar", true) { Accent = ACC };
    List<Item> items = new List<Item>();
    System.Windows.Forms.Timer meas = new System.Windows.Forms.Timer { Interval = 5000 };

    void T(string n, bool on, Action a) { items.Add(new Item { Name = n, Kind = Kind.Tweak, On = on, Act = a }); list.Rows.Add(items.Last()); }
    void P(string n, bool on, params string[] procs) { items.Add(new Item { Name = n, Kind = Kind.Proc, Paths = procs, On = on }); list.Rows.Add(items.Last()); }

    public BoosterPanel() {
        BackColor = C.Bg; DoubleBuffered = true;
        list.Rows.Add("SEGUNDO PLANO");
        P("OneDrive", true, "OneDrive");
        P("Microsoft Teams / Skype", true, "Teams", "ms-teams", "Skype", "SkypeApp", "SkypeBackgroundHost");
        P("Adobe (Creative Cloud, updaters)", true, "AdobeARM", "CCXProcess", "CoreSync", "Creative Cloud", "AdobeIPCBroker", "Adobe Desktop Service", "AdobeUpdateService", "CCLibrary");
        P("Atualizadores (Google, Java, etc.)", true, "GoogleUpdate", "jusched", "MicrosoftEdgeUpdate", "Dropbox Update");
        P("Widgets / Celular / Copilot", true, "Widgets", "WidgetService", "PhoneExperienceHost", "YourPhone", "Copilot");
        P("Nuvem (Dropbox, Google Drive, iCloud)", true, "Dropbox", "GoogleDriveFS", "iCloudDrive", "iCloudServices");
        P("Leitores e escritório (Office, PDF)", false, "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "Acrobat", "AcroRd32", "FoxitPDFReader");
        P("Navegadores (Chrome, Edge, Firefox…)", false, "chrome", "msedge", "firefox", "opera", "brave", "vivaldi");
        P("Torrent (qBittorrent, uTorrent…)", true, "qbittorrent", "uTorrent", "BitTorrent", "Deluge", "transmission-qt");
        P("Lojas em 2º plano (EA, Ubisoft, Battle.net)", false, "EADesktop", "EABackgroundService", "UbisoftConnect", "upc", "Battle.net", "EpicGamesLauncher", "GalaxyClient");
        P("RGB e periféricos (iCUE, Armoury, G HUB)", false, "iCUE", "ArmouryCrate", "ArmourySocketServer", "lghub", "lghub_agent", "RazerCentralService", "SignalRgb", "OpenRGB");
        P("Overlays e utilitários de GPU (NVIDIA, Adrenalin, Afterburner)", false, "NVIDIA app", "NVIDIA Share", "nvcontainer", "RadeonSoftware", "AMDRSServ", "MSIAfterburner", "RTSS");
        P("Gravação e stream (OBS, Streamlabs)", false, "obs64", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core");
        P("VPNs (NordVPN, Proton, Express…)", false, "NordVPN", "nordvpn-service", "ProtonVPN", "ProtonVPNService", "ExpressVPN", "expressvpnd", "Surfshark");
        P("Utilitários do fabricante (Lenovo, Dell, HP, ASUS)", true, "LenovoVantage", "LenovoVantageService", "SupportAssistAgent", "Dell.TechHub", "HPSmart", "HPPrintScanDoctor", "AsusSoftwareManager", "MyASUS", "AcerQuickAccess");
        P("Assistentes de IA (ChatGPT, Claude, Gemini)", false, "ChatGPT", "Claude", "Gemini", "Perplexity");
        P("Bloco de notas e utilitários (Notion, Evernote, Obsidian)", false, "Notion", "Evernote", "Obsidian", "Todoist");
        P("Editores (VS Code, Photoshop…)", false, "Code", "Photoshop", "Illustrator", "AfterFX", "Premiere Pro", "blender");
        list.Rows.Add("SOCIAL E MÚSICA");
        P("Discord", false, "Discord");
        P("Mensageiros (WhatsApp, Telegram, Zoom)", true, "WhatsApp", "Telegram", "Zoom", "Signal", "Messenger");
        P("Música (Spotify, Deezer, Apple Music…)", false, "Spotify", "Deezer", "iTunes", "AppleMusic", "TIDAL", "Amazon Music", "YouTube Music");
        list.Rows.Add("SISTEMA");
        items.Add(new Item { Name = "Plano de energia: Alto desempenho", Kind = Kind.Power }); list.Rows.Add(items.Last());
        items.Add(new Item { Name = "Liberar RAM ociosa dos processos", Kind = Kind.Bin }); list.Rows.Add(items.Last());
        items.Add(new Item { Name = "Pausar Windows Update (até reiniciar)", Kind = Kind.Wu, On = false }); list.Rows.Add(items.Last());
        items.Add(new Item { Name = "Limpar cache de DNS", Kind = Kind.Dns }); list.Rows.Add(items.Last());
        T("Liberar memória em espera (standby)", true, Tweaks.PurgeStandby);
        T("Ativar Modo de Jogo do Windows", true, Tweaks.GameMode);
        T("Desativar gravação em 2º plano (Xbox DVR)", false, Tweaks.NoDvr);
        T("Prioridade de CPU/GPU para jogos (MMCSS)", false, Tweaks.Mmcss);
        T("Desativar aceleração do mouse", false, Tweaks.NoMouseAccel);
        T("Pausar indexação de pesquisa (até reiniciar)", false, () => Tweaks.StopSvc("WSearch"));
        T("Pausar SysMain / Superfetch (até reiniciar)", false, () => Tweaks.StopSvc("SysMain"));
        T("Reduzir efeitos visuais (animações)", false, Tweaks.LessFx);

        var head = new Header { Dock = DockStyle.Top, Title = "Game Booster", Sub = "Fecha apps em segundo plano antes de jogar", Accent = ACC, Icon = Icons.Gamepad };
        var sel = new SelAll { Dock = DockStyle.Left, Width = 170, Accent = ACC, On = false };
        sel.Changed += on => { foreach (var i in items) i.On = on; list.Invalidate(); };
        info.Dock = DockStyle.Fill; info.ForeColor = C.Mute; info.Font = PCInfo.F(10f, true); info.TextAlign = ContentAlignment.MiddleRight; info.BackColor = C.Bg;
        var selRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = C.Bg }; selRow.Controls.Add(info); selRow.Controls.Add(sel);

        var foot = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 98, ColumnCount = 1, RowCount = 2, BackColor = C.Bg };
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        status.ForeColor = C.Mute; status.Font = PCInfo.F(9f, false); status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft;
        go.Dock = DockStyle.Fill; go.Margin = new Padding(0, 4, 0, 2);
        foot.Controls.Add(status, 0, 0); foot.Controls.Add(go, 0, 1);
        go.Click += (s, e) => Boost();

        var card = new ListCard(list) { Dock = DockStyle.Fill };
        Controls.Add(card); Controls.Add(selRow); Controls.Add(head); Controls.Add(foot);
        meas.Tick += (s, e) => Measure();
        VisibleChanged += (s, e) => { meas.Enabled = Visible; if (Visible) Measure(); };
    }

    static Process[] Find(string[] names) {
        return names.SelectMany(n => { try { return Process.GetProcessesByName(n); } catch { return new Process[0]; } }).ToArray();
    }
    void Measure() {
        long tot = 0; int open = 0;
        foreach (var it in items) {
            if (it.Kind == Kind.Proc) {
                var ps = Find(it.Paths); long mem = 0;
                foreach (var p in ps) try { mem += p.WorkingSet64; } catch { }
                if (ps.Length > 0) { open++; if (it.On) tot += mem; }
                if (it.Info != "feito" || ps.Length > 0) it.Info = ps.Length == 0 ? "não aberto" : Cleaner.Fmt(mem);
            } else if (it.Info == "—") it.Info = "pronto";
        }
        info.Text = open + " grupos abertos  ·  ~" + Cleaner.Fmt(tot); info.ForeColor = ACC;
        list.Invalidate();
    }
    void Boost() {
        if (!items.Any(i => i.On)) { status.Text = "Nenhum item selecionado."; return; }
        go.Enabled = list.Enabled = false; status.Text = "Turbinando…"; Refresh();
        long freed = 0; int killed = 0;
        foreach (var it in items.Where(i => i.On)) {
            switch (it.Kind) {
                case Kind.Proc:
                    foreach (var p in Find(it.Paths)) try { freed += p.WorkingSet64; p.Kill(); killed++; } catch { }
                    break;
                case Kind.Power:
                    try {
                        var psi = new ProcessStartInfo("powercfg.exe", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") { CreateNoWindow = true, UseShellExecute = false };
                        using (var p = Process.Start(psi)) p.WaitForExit(10000);
                    } catch { }
                    break;
                case Kind.Bin:
                    foreach (var p in Process.GetProcesses()) try { EmptyWorkingSet(p.Handle); } catch { }
                    break;
                case Kind.Wu: Cleaner.Svc(true); break;
                case Kind.Dns: Cleaner.FlushDns(); break;
                case Kind.Tweak: try { it.Act(); } catch { } break;
            }
            it.Info = "feito";
        }
        list.Invalidate();
        status.Text = killed + " processos encerrados · ~" + Cleaner.Fmt(freed) + " de RAM liberados. Bom jogo!";
        status.ForeColor = C.Text; go.Enabled = list.Enabled = true;
    }
}

// Ajustes de desempenho do Game Booster
static class Tweaks {
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int cls, ref int info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TP { public int Count; public long Luid; public int Attr; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool dis, ref TP n, int len, IntPtr prev, IntPtr rl);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int act, int p, int[] v, int win);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int act, int p, IntPtr v, int win);
    static void Priv(string name) {
        IntPtr t; if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out t)) return;
        long l; if (!LookupPrivilegeValue(null, name, out l)) return;
        var tp = new TP { Count = 1, Luid = l, Attr = 2 }; AdjustTokenPrivileges(t, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
    }
    public static void PurgeStandby() { Priv("SeProfileSingleProcessPrivilege"); int cmd = 4; NtSetSystemInformation(80, ref cmd, 4); } // MemoryPurgeStandbyList
    public static void GameMode() {
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord);
    }
    public static void NoDvr() {
        Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
    }
    public static void Mmcss() {
        string k = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        Registry.SetValue(k, "SystemResponsiveness", 10, RegistryValueKind.DWord);
        Registry.SetValue(k + @"\Tasks\Games", "GPU Priority", 8, RegistryValueKind.DWord);
        Registry.SetValue(k + @"\Tasks\Games", "Priority", 6, RegistryValueKind.DWord);
        Registry.SetValue(k + @"\Tasks\Games", "Scheduling Category", "High", RegistryValueKind.String);
    }
    public static void NoMouseAccel() {
        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed", "0");
        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1", "0");
        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2", "0");
        SystemParametersInfo(0x0004, 0, new[] { 0, 0, 0 }, 3); // SPI_SETMOUSE: aplica na hora
    }
    public static void LessFx() {
        SystemParametersInfo(0x1043, 0, IntPtr.Zero, 3); // SPI_SETMENUANIMATION off
        SystemParametersInfo(0x1003, 0, IntPtr.Zero, 3); // SPI_SETCOMBOBOXANIMATION off
        SystemParametersInfo(0x1005, 0, IntPtr.Zero, 3); // SPI_SETLISTBOXSMOOTHSCROLLING off
        SystemParametersInfo(0x0049, 0, IntPtr.Zero, 3); // SPI_SETANIMATION (min/max) — via estrutura padrão desligada
        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0");
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, RegistryValueKind.DWord);
    }
    public static void StopSvc(string n) {
        try { using (var s = new ServiceController(n)) if (s.Status == ServiceControllerStatus.Running) { s.Stop(); s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); } } catch { }
    }
}

// Relatório de RAM/CPU: retrato do Gerenciador de Tarefas no momento do clique
class RamReport : UserControl {
    [StructLayout(LayoutKind.Sequential)] class MEMSTAT { public int Len = 64; public int Load; public ulong Total, Avail, PTot, PAvail, VTot, VAvail, VExt; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMSTAT m);
    static readonly Color ACC = Color.FromArgb(255, 170, 40);
    ListView lv = new ListView(); Label head = new Label(), sub = new Label(); ImageList img = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
    LBtn upd = new LBtn("⟳  Atualizar", false) { Accent = ACC }, cpy = new LBtn("Copiar relatório", true) { Accent = ACC };
    List<string[]> data = new List<string[]>(); bool busy;
    public RamReport() {
        BackColor = C.Bg; DoubleBuffered = true; Padding = new Padding(20, 8, 20, 12);
        head.Font = PCInfo.F(19f, true); head.ForeColor = Color.White; head.Dock = DockStyle.Top; head.Height = 40; head.Text = "Relatório de RAM";
        sub.Font = PCInfo.F(10f, false); sub.ForeColor = C.Mute; sub.Dock = DockStyle.Top; sub.Height = 30;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, BackColor = C.Bg, Padding = new Padding(0, 4, 0, 6) };
        upd.Width = 150; cpy.Width = 170; upd.Margin = new Padding(0, 0, 10, 0); bar.Controls.Add(upd); bar.Controls.Add(cpy);
        upd.Click += (s, e) => Snapshot(); cpy.Click += (s, e) => Copy();
        lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.BorderStyle = BorderStyle.None; lv.OwnerDraw = true;
        lv.BackColor = C.Card; lv.ForeColor = C.Text; lv.Font = PCInfo.F(10f, false);
        foreach (var c in new[] { "Processo", "Instâncias", "RAM", "% da RAM", "CPU" }) lv.Columns.Add(c, 100, c == "Processo" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
        lv.DrawColumnHeader += (s, e) => {
            using (var b = new SolidBrush(Color.FromArgb(24, 24, 28))) e.Graphics.FillRectangle(b, e.Bounds);
            var f = TextFormatFlags.VerticalCenter | (e.ColumnIndex == 0 ? TextFormatFlags.Left : TextFormatFlags.Right);
            using (var ft = PCInfo.F(9f, true)) TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpper(), ft, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height), C.Mute, f);
        };
        lv.DrawItem += (s, e) => { };
        lv.DrawSubItem += (s, e) => {
            var g = e.Graphics; var bg = e.Item.Selected ? Color.FromArgb(70, ACC) : e.ItemIndex % 2 == 0 ? C.Card : Color.FromArgb(24, 24, 28);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
            var r = e.Bounds;
            if (e.ColumnIndex == 0) {
                int ix = e.Item.Tag is int ? (int)e.Item.Tag : -1; if (ix >= 0 && ix < img.Images.Count) g.DrawImage(img.Images[ix], r.X + 8, r.Y + (r.Height - 20) / 2, 20, 20);
                TextRenderer.DrawText(g, e.SubItem.Text, lv.Font, new Rectangle(r.X + 36, r.Y, r.Width - 40, r.Height), C.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            } else {
                if (e.ColumnIndex == 3) { double pc; if (double.TryParse(e.SubItem.Text.TrimEnd('%', ' '), out pc)) using (var b = new SolidBrush(Color.FromArgb(90, ACC))) g.FillRectangle(b, r.X + 6, r.Bottom - 6, (int)((r.Width - 12) * Math.Min(1, pc / 25)), 3); }
                TextRenderer.DrawText(g, e.SubItem.Text, lv.Font, new Rectangle(r.X, r.Y, r.Width - 12, r.Height), e.ColumnIndex == 2 ? Color.White : C.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        };
        lv.SmallImageList = new ImageList { ImageSize = new Size(1, 30) }; // altura da linha
        lv.Resize += (s, e) => { int w = lv.ClientSize.Width; lv.Columns[0].Width = Math.Max(200, w - 4 * 120 - 4); for (int i = 1; i < 5; i++) lv.Columns[i].Width = 120; };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = C.Card, Padding = new Padding(1) }; card.Controls.Add(lv);
        Controls.Add(card); Controls.Add(bar); Controls.Add(sub); Controls.Add(head);
    }
    static string Fmt(long b) { return Cleaner.Fmt(b); }
    public void Snapshot() {
        if (busy) return; busy = true; sub.Text = "Medindo processos (CPU em ~1 s)…"; upd.Enabled = false;
        new Thread(() => {
            var t0 = new Dictionary<int, TimeSpan>(); var procs = Process.GetProcesses();
            foreach (var p in procs) try { t0[p.Id] = p.TotalProcessorTime; } catch { }
            var sw = Stopwatch.StartNew(); Thread.Sleep(1000); double el = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
            var mem = new MEMSTAT(); GlobalMemoryStatusEx(mem);
            var groups = new Dictionary<string, object[]>(); // nome -> [instâncias, ram, cpuMs, caminho]
            foreach (var p in procs) {
                try {
                    long ws = p.WorkingSet64; double cpu = 0; TimeSpan a;
                    if (t0.TryGetValue(p.Id, out a)) try { cpu = (p.TotalProcessorTime - a).TotalMilliseconds; } catch { }
                    string n = p.ProcessName; object[] gr;
                    if (!groups.TryGetValue(n, out gr)) { string path = null; try { path = p.MainModule.FileName; } catch { } gr = new object[] { 0, 0L, 0.0, path }; groups[n] = gr; }
                    gr[0] = (int)gr[0] + 1; gr[1] = (long)gr[1] + ws; gr[2] = (double)gr[2] + cpu;
                } catch { }
            }
            var rows = groups.OrderByDescending(kv => (long)kv.Value[1]).ToList();
            var icons = new List<Bitmap>(); var items = new List<ListViewItem>(); data = new List<string[]>();
            foreach (var kv in rows) {
                long ram = (long)kv.Value[1]; double pc = mem.Total > 0 ? ram * 100.0 / mem.Total : 0, cp = (double)kv.Value[2] * 100 / el;
                string[] v = { kv.Key, kv.Value[0].ToString(), Fmt(ram), pc.ToString("0.0") + "%", cp.ToString("0.0") + "%" };
                data.Add(v);
                int ii = -1; var path = kv.Value[3] as string;
                if (path != null) try { using (var ic = Icon.ExtractAssociatedIcon(path)) { icons.Add(new Bitmap(ic.ToBitmap(), 20, 20)); ii = icons.Count - 1; } } catch { }
                items.Add(new ListViewItem(v) { Tag = ii });
            }
            string s2 = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + "  ·  RAM em uso: " + Fmt((long)(mem.Total - mem.Avail)) + " de " + Fmt((long)mem.Total) + " (" + mem.Load + "%)  ·  livre: " + Fmt((long)mem.Avail) + "  ·  " + procs.Length + " processos";
            BeginInvoke((Action)(() => {
                lv.BeginUpdate(); lv.Items.Clear(); img.Images.Clear(); foreach (var b in icons) img.Images.Add(b);
                lv.Items.AddRange(items.ToArray()); lv.EndUpdate(); sub.Text = s2; sub.ForeColor = ACC; upd.Enabled = true; busy = false;
            }));
        }) { IsBackground = true }.Start();
    }
    void Copy() {
        var sb = new System.Text.StringBuilder(); sb.AppendLine("RELATÓRIO DE RAM - " + sub.Text); sb.AppendLine();
        sb.AppendLine(string.Format("{0,-40}{1,6}{2,12}{3,9}{4,9}", "Processo", "Inst.", "RAM", "%RAM", "CPU"));
        foreach (var d in data) sb.AppendLine(string.Format("{0,-40}{1,6}{2,12}{3,9}{4,9}", d[0].Length > 39 ? d[0].Substring(0, 39) : d[0], d[1], d[2], d[3], d[4]));
        try { Clipboard.SetText(sb.ToString()); } catch { }
    }
}
