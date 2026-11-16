using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;
using System.Net.NetworkInformation;

[assembly: AssemblyTitle("PC Info")]
[assembly: AssemblyDescription("Relatorio de hardware e software do PC")]
[assembly: AssemblyCompany("Budart")]
[assembly: AssemblyProduct("PC Info")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Budart")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]

class PCInfo : Form
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    static Color BG = Color.FromArgb(13, 13, 15), CARD = Color.FromArgb(24, 24, 28), ROW2 = Color.FromArgb(19, 19, 22),
        TXT = Color.FromArgb(230, 230, 235), DIM = Color.FromArgb(140, 140, 150), ACC = Color.FromArgb(0, 170, 255), SEL = Color.FromArgb(0, 70, 120);
    ListView lv = new ListView();
    Label status = new Label();
    List<string[]> rows = new List<string[]>(); // sec, key, val
    List<string[]> ess = new List<string[]>();
    TableLayoutPanel grid; bool full = false; Button modeBtn; Loader loader = new Loader();
    Dictionary<string, string[]> hl = new Dictionary<string, string[]>(); // card -> main, sub
    Dictionary<string, Label[]> cards = new Dictionary<string, Label[]>();
    string sec = "";
    int hoverIdx = -1, copiedIdx = -1;
    System.Windows.Forms.Timer flash = new System.Windows.Forms.Timer { Interval = 1200 };
    static Font ICON = new Font("Segoe MDL2 Assets", 10);
    const string GCOPY = "\uE8C8", GOK = "\uE73E";

    static string FAM = null;
    internal static Font F(float sz, bool semi)
    {
        if (FAM == null)
        {
            var fams = new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name).ToList();
            FAM = fams.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        }
        if (semi) { var n = FAM == "Segoe UI" ? "Segoe UI Semibold" : FAM.Replace("Text", "Display") + " Semibold"; try { var f = new Font(n, sz); if (f.Name == n) return f; } catch { } return new Font(FAM, sz, FontStyle.Bold); }
        return new Font(FAM, sz);
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 0 && args[0] == "/limpa") Application.Run(new LimpaForm()); else Application.Run(new PCInfo());
    }
    static void OpenLimpa(IWin32Window owner)
    {
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (admin) { var f = new LimpaForm(); f.Show(owner); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "/limpa") { Verb = "runas", UseShellExecute = true }); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); int v = 1; try { DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { } }

    NetMonitor net = new NetMonitor();
    PCInfo()
    {
        Shown += (s, e) => net.Show(); FormClosed += (s, e) => net.Close();
        Text = "PC Info"; Width = 1100; Height = 800; MinimumSize = new Size(800, 500);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen; Font = F(10f, false); BackColor = BG; ForeColor = TXT;

        // topo
        var top = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = BG, Padding = new Padding(16, 10, 16, 6) };
        var title = new Label { Text = "PC INFO", Font = F(18f, true), ForeColor = TXT, AutoSize = true, Dock = DockStyle.Left };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = BG };
        modeBtn = Btn("☰  Modo completo", (s, e) => { full = !full; modeBtn.Text = full ? "◧  Modo essencial" : "☰  Modo completo"; Fill(); }, false);
        bar.Controls.Add(Btn("🧹  Limpeza", (s, e) => OpenLimpa(this), false));
        bar.Controls.Add(Btn("🌐  Rede", (s, e) => { if (net.Visible) net.Hide(); else net.Show(); }, false));
        bar.Controls.Add(modeBtn);
        bar.Controls.Add(Btn("⟳  Atualizar", (s, e) => Load2(), false));
        bar.Controls.Add(Btn("Copiar seleção", (s, e) => CopySel(), false));
        bar.Controls.Add(Btn("Copiar tudo", (s, e) => Clipboard.SetText(Report()), false));
        bar.Controls.Add(Btn("💾  Salvar TXT", (s, e) => SaveTxt(), true));
        top.Controls.Add(bar); top.Controls.Add(title);

        // cards de destaque
        grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 128, ColumnCount = 4, RowCount = 1, BackColor = BG, Padding = new Padding(12, 0, 12, 8) };
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        string[][] defs = { new[] { "CPU", "PROCESSADOR" }, new[] { "GPU", "PLACA DE VÍDEO" }, new[] { "RAM", "MEMÓRIA" }, new[] { "MB", "PLACA-MÃE" } };
        Color[] cols = { Color.FromArgb(0, 170, 255), Color.FromArgb(118, 220, 80), Color.FromArgb(255, 170, 40), Color.FromArgb(200, 110, 255) };
        for (int i = 0; i < 4; i++) grid.Controls.Add(Card(defs[i][0], defs[i][1], cols[i]), i, 0);

        // lista
        lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.OwnerDraw = true; lv.BorderStyle = BorderStyle.None;
        lv.BackColor = BG; lv.ForeColor = TXT; lv.HeaderStyle = ColumnHeaderStyle.None;
        lv.Columns.Add("Item", 320); lv.Columns.Add("Valor", 700);
        lv.DrawItem += (s, e) => { };
        lv.DrawSubItem += DrawSub;
        flash.Tick += (s, e) => { flash.Stop(); int i = copiedIdx; copiedIdx = -1; if (i >= 0 && i < lv.Items.Count) lv.Invalidate(lv.Items[i].Bounds); };
        lv.MouseMove += (s, e) =>
        {
            var h = lv.HitTest(e.Location); int i = h.Item == null ? -1 : h.Item.Index;
            if (i != hoverIdx) { int o = hoverIdx; hoverIdx = i; if (o >= 0 && o < lv.Items.Count) lv.Invalidate(lv.Items[o].Bounds); if (i >= 0) lv.Invalidate(lv.Items[i].Bounds); }
            lv.Cursor = i >= 0 && OnIcon(h.Item, e.X) ? Cursors.Hand : Cursors.Default;
        };
        lv.MouseLeave += (s, e) => { int o = hoverIdx; hoverIdx = -1; if (o >= 0 && o < lv.Items.Count) lv.Invalidate(lv.Items[o].Bounds); };
        lv.MouseClick += (s, e) =>
        {
            var it = lv.HitTest(e.Location).Item; if (it == null || !OnIcon(it, e.X)) return;
            string txt = it.Tag as string == "H" ? SectionText(it.Index) : it.SubItems[1].Text;
            if (string.IsNullOrEmpty(txt)) return;
            Clipboard.SetText(txt); copiedIdx = it.Index; flash.Stop(); flash.Start(); lv.Invalidate(it.Bounds);
            status.Text = "Copiado: " + (txt.Length > 80 ? txt.Substring(0, 80) + "…" : txt.Replace("\r\n", " | "));
        };
        lv.Resize += (s, e) => lv.Columns[1].Width = Math.Max(200, lv.ClientSize.Width - lv.Columns[0].Width - 4);
        lv.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };
        lv.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.C) CopySel(); if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in lv.Items) i.Selected = true; };
        var cm = new ContextMenuStrip { BackColor = CARD, ForeColor = TXT, ShowImageMargin = false };
        cm.Items.Add("Copiar linha", null, (s, e) => CopySel());
        cm.Items.Add("Copiar só o valor", null, (s, e) => { if (lv.SelectedItems.Count > 0) Clipboard.SetText(lv.SelectedItems[0].SubItems[1].Text); });
        lv.ContextMenuStrip = cm;
        var lvWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 0), BackColor = BG };
        lvWrap.Controls.Add(lv);

        status.Dock = DockStyle.Bottom; status.Height = 26; status.ForeColor = DIM; status.Padding = new Padding(16, 0, 0, 0); status.TextAlign = ContentAlignment.MiddleLeft;
        loader.Dock = DockStyle.Fill; Controls.Add(loader);
        Controls.Add(lvWrap); Controls.Add(grid); Controls.Add(top); Controls.Add(status);
        Shown += (s, e) => Load2();
    }

    Control Card(string key, string cap, Color c)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = CARD, Margin = new Padding(4), Padding = new Padding(14, 10, 10, 8) };
        p.Paint += (s, e) => { using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, 0, 0, 4, p.Height); };
        var l1 = new Label { Text = cap, ForeColor = c, Font = F(8.5f, true), Dock = DockStyle.Top, Height = 20 };
        var l2 = new Label { Text = "…", ForeColor = TXT, Font = F(13.5f, true), Dock = DockStyle.Top, Height = 48, AutoEllipsis = true, Padding = new Padding(0, 0, 24, 0) };
        var l3 = new Label { Text = "", ForeColor = DIM, Font = F(9f, false), Dock = DockStyle.Fill, AutoEllipsis = true };
        var cb = new Label { Text = GCOPY, Font = ICON, ForeColor = Color.FromArgb(110, 110, 120), Size = new Size(26, 22), TextAlign = ContentAlignment.MiddleCenter, Anchor = AnchorStyles.Top | AnchorStyles.Right, Cursor = Cursors.Hand };
        cb.Location = new Point(p.Width - 34, 6);
        p.Resize += (s, e) => cb.Location = new Point(p.Width - 34, 6);
        cb.MouseEnter += (s, e) => cb.ForeColor = TXT; cb.MouseLeave += (s, e) => { if (cb.Text == GCOPY) cb.ForeColor = Color.FromArgb(110, 110, 120); };
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (s, e) => { t.Stop(); cb.Text = GCOPY; cb.ForeColor = Color.FromArgb(110, 110, 120); };
        cb.Click += (s, e) =>
        {
            var txt = cap + ": " + cards[key][0].Text + " (" + cards[key][1].Text + ")";
            Clipboard.SetText(txt); cb.Text = GOK; cb.ForeColor = Color.FromArgb(118, 220, 80); t.Stop(); t.Start(); status.Text = "Copiado: " + txt;
        };
        p.Controls.Add(cb);
        p.Controls.Add(l3); p.Controls.Add(l2); p.Controls.Add(l1); cb.BringToFront();
        cards[key] = new[] { l2, l3 };
        return p;
    }

    Button Btn(string t, EventHandler h, bool primary)
    {
        var b = new Button { Text = t, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, Margin = new Padding(6, 0, 0, 0), Padding = new Padding(8, 0, 8, 0),
            BackColor = primary ? ACC : CARD, ForeColor = primary ? Color.Black : TXT, Cursor = Cursors.Hand, Font = F(9f, true) };
        b.FlatAppearance.BorderColor = primary ? ACC : Color.FromArgb(50, 50, 58);
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(60, 195, 255) : Color.FromArgb(40, 40, 48);
        b.Click += h; return b;
    }

    void DrawSub(object s, DrawListViewSubItemEventArgs e)
    {
        bool hdr = e.Item.Tag as string == "H";
        var g = e.Graphics; var r = e.Bounds;
        Color bg = e.Item.Selected ? SEL : hdr ? BG : (e.ItemIndex % 2 == 0 ? CARD : ROW2);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        if (hdr)
        {
            if (e.ColumnIndex == 0)
            {
                using (var f = F(10.5f, true)) TextRenderer.DrawText(g, e.Item.Text.ToUpper(), f, new Rectangle(r.X + 34, r.Y + 6, 600, r.Height - 6), ACC, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            using (var pn = new Pen(Color.FromArgb(45, 45, 55))) g.DrawLine(pn, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
            if (e.ColumnIndex == 0 && e.Item.Text != "") DrawIcon(g, r, e.ItemIndex == copiedIdx, e.ItemIndex == hoverIdx);
            return;
        }
        var fc = e.ColumnIndex == 0 ? DIM : TXT;
        int off = e.ColumnIndex == 0 ? 36 : 10;
        TextRenderer.DrawText(g, e.SubItem.Text, lv.Font, new Rectangle(r.X + off, r.Y, r.Width - off - 4, r.Height), fc,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (e.ColumnIndex == 0) DrawIcon(g, r, e.ItemIndex == copiedIdx, e.ItemIndex == hoverIdx);
    }

    bool OnIcon(ListViewItem it, int x) { if (it.Tag as string == "H" && it.Text == "") return false; return x >= it.Bounds.Left && x <= it.Bounds.Left + 34; }

    string SectionText(int idx)
    {
        var sb = new StringBuilder(); sb.AppendLine("== " + lv.Items[idx].Text + " ==");
        for (int i = idx + 1; i < lv.Items.Count && lv.Items[i].Tag as string != "H"; i++) sb.AppendLine(lv.Items[i].Text + ": " + lv.Items[i].SubItems[1].Text);
        return sb.ToString();
    }

    void DrawIcon(Graphics g, Rectangle r, bool ok, bool hot)
    {
        var ir = new Rectangle(r.X + 6, r.Y + (r.Height - 22) / 2, 24, 22);
        if (hot && !ok) using (var b = new SolidBrush(Color.FromArgb(45, 45, 55))) g.FillRectangle(b, ir);
        TextRenderer.DrawText(g, ok ? GOK : GCOPY, ICON, ir, ok ? Color.FromArgb(118, 220, 80) : (hot ? TXT : Color.FromArgb(90, 90, 100)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void Load2()
    {
        status.Text = "Coletando informações..."; lv.Items.Clear(); rows.Clear(); ess.Clear(); hl.Clear(); grid.Visible = false; loader.Show(); loader.BringToFront();
        new Thread(() => { try { Collect(); } catch { } try { CollectEss(); } catch { } BeginInvoke((Action)Fill); }) { IsBackground = true }.Start();
    }

    void Fill()
    {
        loader.Hide(); grid.Visible = true;
        lv.BeginUpdate(); lv.Items.Clear();
        string last = null;
        foreach (var r in Cur)
        {
            if (r[0] != last) { if (last != null) lv.Items.Add(new ListViewItem(new[] { "", "" }) { Tag = "H" }); lv.Items.Add(new ListViewItem(new[] { r[0], "" }) { Tag = "H" }); last = r[0]; }
            lv.Items.Add(new ListViewItem(new[] { r[1], r[2] }));
        }
        lv.EndUpdate();
        foreach (var k in cards.Keys) if (hl.ContainsKey(k)) { cards[k][0].Text = hl[k][0]; cards[k][1].Text = hl[k][1]; }
        status.Text = (full ? "Modo completo" : "Modo essencial") + "  •  " + Cur.Count + " itens  •  " + Environment.MachineName + "  •  " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
    }

    List<string[]> Cur { get { return full ? rows : ess; } }

    void CopySel()
    {
        var sb = new StringBuilder();
        foreach (ListViewItem i in lv.SelectedItems) if (i.Tag as string != "H") sb.AppendLine(i.Text + ": " + i.SubItems[1].Text);
        if (sb.Length > 0) Clipboard.SetText(sb.ToString());
    }

    string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("RELATÓRIO DO SISTEMA - " + Environment.MachineName + " - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        sb.AppendLine();
        foreach (var k in new[] { "CPU", "GPU", "RAM", "MB" }) if (hl.ContainsKey(k)) sb.AppendLine(k.PadRight(5) + ": " + hl[k][0] + "  (" + hl[k][1] + ")");
        string last = null;
        foreach (var r in Cur)
        {
            if (r[0] != last) { sb.AppendLine(); sb.AppendLine("==== " + r[0].ToUpper() + " ===="); last = r[0]; }
            sb.AppendLine(r[1].PadRight(34) + ": " + r[2]);
        }
        return sb.ToString();
    }

    void SaveTxt()
    {
        var d = new SaveFileDialog { Filter = "Texto|*.txt", FileName = "PCInfo_" + Environment.MachineName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt" };
        if (d.ShowDialog() == DialogResult.OK) { File.WriteAllText(d.FileName, Report(), Encoding.UTF8); status.Text = "Salvo: " + d.FileName; }
    }

    static string DdrType(object smb, object mt)
    {
        int t = Convert.ToInt32(smb ?? 0); if (t == 0 || t == 2) t = Convert.ToInt32(mt ?? 0);
        switch (t) { case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4"; case 27: return "LPDDR"; case 28: return "LPDDR2";
            case 29: return "LPDDR3"; case 30: return "LPDDR4"; case 34: return "DDR5"; case 35: return "LPDDR5"; default: return "DDR?"; }
    }

    // ---------- helpers ----------
    void S(string s) { sec = s; }
    void A(string k, object v) { if (v == null) return; var s = v.ToString().Trim(); if (s.Length == 0) return; rows.Add(new[] { sec, k, s }); }

    IEnumerable<ManagementObject> Q(string ns, string q)
    {
        var list = new List<ManagementObject>();
        try { using (var s = new ManagementObjectSearcher(ns, q)) foreach (ManagementObject o in s.Get()) list.Add(o); } catch { }
        return list;
    }
    IEnumerable<ManagementObject> Q(string q) { return Q("root\\cimv2", q); }
    static object P(ManagementObject o, string n) { try { return o[n]; } catch { return null; } }
    static string Gb(object b) { try { return (Convert.ToDouble(b) / 1073741824.0).ToString("0.##") + " GB"; } catch { return null; } }
    static string Dt(object d) { try { return ManagementDateTimeConverter.ToDateTime(d.ToString()).ToString("dd/MM/yyyy HH:mm"); } catch { return null; } }
    static object Reg(string path, string name) { try { return Registry.GetValue(path, name, null); } catch { return null; } }
    static string Edid(object arr) { try { return new string(((ushort[])arr).Where(c => c != 0).Select(c => (char)c).ToArray()); } catch { return null; } }

    // ---------- modo essencial ----------
    void E(string sct, string k, string v) { if (!string.IsNullOrWhiteSpace(v)) ess.Add(new[] { sct, k, System.Text.RegularExpressions.Regex.Replace(v.Trim(), @"\s+", " ") }); }
    static string Rx(string s, string p, string r) { return System.Text.RegularExpressions.Regex.Replace(s ?? "", p, r, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(); }
    static string Brand(string m)
    {
        m = (m ?? "").Trim(); var u = m.ToUpper();
        if (u.StartsWith("ASUS")) return "ASUS"; if (u.StartsWith("MICRO-STAR") || u == "MSI") return "MSI"; if (u.StartsWith("GIGABYTE")) return "Gigabyte";
        if (u.StartsWith("ASROCK")) return "ASRock"; if (u.StartsWith("DELL")) return "Dell"; if (u.StartsWith("HEWLETT") || u == "HP") return "HP"; if (u.StartsWith("LENOVO")) return "Lenovo";
        if (u.StartsWith("ACER")) return "Acer"; if (u.StartsWith("INTEL")) return "Intel"; if (u.StartsWith("BIOSTAR")) return "Biostar";
        return Rx(m, @"[,.]?\s*(Co\.?,?\s*)?(Ltd\.?|Inc\.?|Corporation|Corp\.?|Technology|International|Computer)\b\.?", "").Trim(' ', ',', '.');
    }
    static string Partner(string pnp)
    {
        var m = System.Text.RegularExpressions.Regex.Match(pnp ?? "", @"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        switch (m.Groups[1].Value.ToUpper())
        {
            case "1043": return "ASUS"; case "1462": return "MSI"; case "1458": return "Gigabyte"; case "3842": return "EVGA"; case "19DA": return "Zotac";
            case "196E": return "PNY"; case "1569": return "Palit"; case "10B0": return "Gainward"; case "1DA2": return "Sapphire"; case "148C": return "PowerColor";
            case "1682": return "XFX"; case "1849": return "ASRock"; case "7377": return "Colorful"; case "1B4C": return "Galax"; case "10DE": return "NVIDIA (Founders Edition)";
            case "1002": return "AMD (referência)"; case "174B": return "Sapphire"; case "1B0A": return "Inno3D"; case "1028": return "Dell"; case "103C": return "HP"; case "17AA": return "Lenovo";
            default: return null;
        }
    }

    void CollectEss()
    {
        const string NT = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        // CPU
        foreach (var o in Q("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            var n = Rx((P(o, "Name") ?? "").ToString(), @"\((R|TM)\)|\bCPU\b|@.*$|\d+-Core Processor|Processor|with Radeon.*$", "");
            n = Rx(n, @"\s+", " ");
            E("Processador", "CPU", n); E("Processador", "Núcleos / threads", P(o, "NumberOfCores") + " / " + P(o, "NumberOfLogicalProcessors"));
            if (hl.ContainsKey("CPU")) hl["CPU"][0] = n;
            break;
        }
        // GPU
        var vram = new Dictionary<string, string>();
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                foreach (var sk in k.GetSubKeyNames()) using (var s2 = k.OpenSubKey(sk)) { try { var d = s2.GetValue("DriverDesc") as string; var m = s2.GetValue("HardwareInformation.qwMemorySize"); if (d != null && m != null) vram[d] = Math.Round(Convert.ToDouble(m) / 1073741824.0) + " GB"; } catch { } }
        } catch { }
        int bestScore = 0;
        foreach (var o in Q("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController"))
        {
            var name = ((P(o, "Name") ?? "") as string).Trim();
            var pnp = (P(o, "PNPDeviceID") as string ?? "").ToUpper();
            if (!pnp.StartsWith("PCI\\") || System.Text.RegularExpressions.Regex.IsMatch(name, "Basic|Remote|Virtual|Mirror|Parsec|IddCx|Indirect|SudoMaker|Meta|Hyper-V|VMware|VirtualBox|Citrix|DisplayLink|Spacedesk", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            int score = pnp.Contains("VEN_10DE") || pnp.Contains("VEN_1002") ? 2 : 1;
            name = Rx(name, @"\((R|TM)\)", "");
            var pt = Partner(P(o, "PNPDeviceID") as string);
            var full2 = (pt != null && !pt.StartsWith("NVIDIA") && !pt.StartsWith("AMD") && !name.StartsWith(pt, StringComparison.OrdinalIgnoreCase) ? pt + " " : "") + name + (vram.ContainsKey(P(o, "Name") as string ?? "") ? " " + vram[(string)P(o, "Name")] : "");
            E("Placa de vídeo", "GPU", full2);
            if (score > bestScore) { bestScore = score; hl["GPU"] = new[] { full2, "Driver " + P(o, "DriverVersion") }; }
            if (pt != null) E("Placa de vídeo", "  Fabricante da placa", pt);
            E("Placa de vídeo", "  Driver", P(o, "DriverVersion") as string);
        }
        if (bestScore == 0) hl["GPU"] = new[] { "Nenhuma GPU física detectada", "" };
        // Placa-mãe
        foreach (var o in Q("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            var mb = Brand(P(o, "Manufacturer") as string) + " " + ((P(o, "Product") ?? "") as string).Trim();
            E("Placa-mãe", "Placa-mãe", mb); if (hl.ContainsKey("MB")) hl["MB"][0] = mb;
        }
        foreach (var o in Q("SELECT SMBIOSBIOSVersion FROM Win32_BIOS")) E("Placa-mãe", "BIOS", P(o, "SMBIOSBIOSVersion") as string);
        // RAM
        double tot = 0; var caps = new List<string>(); var parts = new List<string>(); string ddr = "", spd = "";
        foreach (var o in Q("SELECT * FROM Win32_PhysicalMemory"))
        {
            double c = 0; try { c = Convert.ToDouble(P(o, "Capacity")); } catch { } tot += c; caps.Add(Math.Round(c / 1073741824.0) + "");
            ddr = DdrType(P(o, "SMBIOSMemoryType"), P(o, "MemoryType")); spd = (P(o, "ConfiguredClockSpeed") ?? P(o, "Speed")) + " MT/s";
            var pn = (Brand(P(o, "Manufacturer") as string) + " " + ((P(o, "PartNumber") ?? "") as string).Trim()).Trim(); if (pn.Length > 0 && !parts.Contains(pn)) parts.Add(pn);
        }
        var cfg = caps.GroupBy(x => x).Select(gp => gp.Count() + "x" + gp.Key + " GB");
        E("Memória RAM", "RAM", Math.Round(tot / 1073741824.0) + " GB " + ddr + " " + spd + " (" + string.Join(" + ", cfg) + ")");
        if (parts.Count > 0) E("Memória RAM", "  Módulo", string.Join(" / ", parts));
        // Armazenamento
        var media = new Dictionary<string, string>();
        foreach (var o in Q("root\\Microsoft\\Windows\\Storage", "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk"))
        {
            var mt = Convert.ToInt32(P(o, "MediaType") ?? 0); var bt = Convert.ToInt32(P(o, "BusType") ?? 0);
            media[(P(o, "FriendlyName") ?? "").ToString()] = bt == 17 ? "SSD NVMe" : mt == 4 ? "SSD SATA" : mt == 3 ? "HDD" : bt == 7 ? "USB" : "";
        }
        foreach (var o in Q("SELECT Model, Size FROM Win32_DiskDrive"))
        {
            var m = ((P(o, "Model") ?? "") as string).Trim(); double sz = 0; try { sz = Convert.ToDouble(P(o, "Size")) / 1e9; } catch { }
            var t = media.ContainsKey(m) ? media[m] : "";
            E("Armazenamento", "Disco", m + " (" + (sz >= 1000 ? (sz / 1000).ToString("0.#") + " TB" : sz.ToString("0") + " GB") + (t != "" ? " " + t : "") + ")");
        }
        // Monitores
        foreach (var o in Q("root\\wmi", "SELECT * FROM WmiMonitorID")) E("Monitores", "Monitor", (Edid(P(o, "ManufacturerName")) + " " + Edid(P(o, "UserFriendlyName"))).Trim());
        // Rede / Áudio
        foreach (var o in Q("SELECT Name FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetEnabled=True")) E("Rede e áudio", "Rede", Rx(P(o, "Name") as string, @"\((R|TM)\)", ""));
        foreach (var o in Q("SELECT Name FROM Win32_SoundDevice")) E("Rede e áudio", "Áudio", P(o, "Name") as string);
        // Sistema
        foreach (var o in Q("SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
            E("Sistema", "Windows", Rx(P(o, "Caption") as string, "^Microsoft ", "") + " " + Reg(NT, "DisplayVersion") + " (build " + P(o, "BuildNumber") + "." + Reg(NT, "UBR") + ", " + P(o, "OSArchitecture") + ")");
        foreach (var o in Q("root\\Microsoft\\Windows\\Defender", "SELECT AMProductVersion, AntivirusSignatureVersion FROM MSFT_MpComputerStatus"))
            E("Sistema", "Windows Defender", P(o, "AMProductVersion") + " (assinaturas " + P(o, "AntivirusSignatureVersion") + ")");
        E("Sistema", "Computador", Environment.MachineName);
    }

    // ---------- coleta ----------
    void Collect()
    {
        const string NT = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        S("Sistema Operacional");
        foreach (var o in Q("SELECT * FROM Win32_OperatingSystem"))
        {
            A("Windows", P(o, "Caption")); A("Versão", Reg(NT, "DisplayVersion"));
            A("Build", P(o, "BuildNumber") + "." + Reg(NT, "UBR")); A("Arquitetura", P(o, "OSArchitecture"));
            A("Idioma (código)", P(o, "OSLanguage")); A("Instalado em", Dt(P(o, "InstallDate")));
            A("Último boot", Dt(P(o, "LastBootUpTime"))); A("Diretório Windows", P(o, "WindowsDirectory"));
            A("Usuário registrado", P(o, "RegisteredUser")); A("Nº de série", P(o, "SerialNumber"));
        }
        A("Nome do computador", Environment.MachineName); A("Usuário atual", Environment.UserDomainName + "\\" + Environment.UserName);
        A("Uptime", TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue).ToString(@"d\d\ hh\h\ mm\m"));

        S("Placa-mãe / BIOS");
        foreach (var o in Q("SELECT * FROM Win32_ComputerSystem")) { A("Fabricante do sistema", P(o, "Manufacturer")); A("Modelo do sistema", P(o, "Model")); A("Tipo", P(o, "SystemType")); }
        foreach (var o in Q("SELECT * FROM Win32_BaseBoard")) { A("Placa-mãe fabricante", P(o, "Manufacturer")); A("Placa-mãe modelo", P(o, "Product")); A("Placa-mãe revisão", P(o, "Version")); A("Placa-mãe serial", P(o, "SerialNumber")); hl["MB"] = new[] { (P(o, "Product") ?? "").ToString(), (P(o, "Manufacturer") ?? "").ToString() }; }
        foreach (var o in Q("SELECT * FROM Win32_BIOS")) { A("BIOS fabricante", P(o, "Manufacturer")); A("BIOS versão", P(o, "SMBIOSBIOSVersion")); A("BIOS data", Dt(P(o, "ReleaseDate"))); if (hl.ContainsKey("MB")) hl["MB"][1] += " • BIOS " + P(o, "SMBIOSBIOSVersion"); }
        var sb = Reg(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        A("Modo de boot", sb != null ? "UEFI" : "Legacy/Desconhecido"); A("Secure Boot", sb == null ? "N/D" : (Convert.ToInt32(sb) == 1 ? "Ativado" : "Desativado"));

        S("Processador");
        foreach (var o in Q("SELECT * FROM Win32_Processor"))
        {
            A("Nome", P(o, "Name")); A("Fabricante", P(o, "Manufacturer")); A("Núcleos / Threads", P(o, "NumberOfCores") + " / " + P(o, "NumberOfLogicalProcessors"));
            A("Clock máximo", P(o, "MaxClockSpeed") + " MHz"); A("Clock atual", P(o, "CurrentClockSpeed") + " MHz");
            A("Cache L2", P(o, "L2CacheSize") + " KB"); A("Cache L3", P(o, "L3CacheSize") + " KB"); A("Soquete", P(o, "SocketDesignation"));
            A("ID", P(o, "ProcessorId")); A("Descrição", P(o, "Description"));
            A("Virtualização (firmware)", (P(o, "VirtualizationFirmwareEnabled") ?? "N/D"));
            if (!hl.ContainsKey("CPU")) hl["CPU"] = new[] { (P(o, "Name") ?? "").ToString().Trim(), P(o, "NumberOfCores") + " núcleos / " + P(o, "NumberOfLogicalProcessors") + " threads • " + (Convert.ToDouble(P(o, "MaxClockSpeed")) / 1000).ToString("0.0#") + " GHz" };
        }

        S("Memória RAM");
        foreach (var o in Q("SELECT * FROM Win32_ComputerSystem")) A("Total instalado", Gb(P(o, "TotalPhysicalMemory")));
        foreach (var o in Q("SELECT * FROM Win32_OperatingSystem")) A("Livre agora", Gb(Convert.ToDouble(P(o, "FreePhysicalMemory")) * 1024));
        int n = 0; double tot = 0; string ddr = "", spd = "";
        foreach (var o in Q("SELECT * FROM Win32_PhysicalMemory"))
        {
            n++; ddr = DdrType(P(o, "SMBIOSMemoryType"), P(o, "MemoryType")); spd = (P(o, "ConfiguredClockSpeed") ?? P(o, "Speed")) + " MT/s";
            try { tot += Convert.ToDouble(P(o, "Capacity")); } catch { }
            A("Pente " + n + " (" + P(o, "DeviceLocator") + ")",
                ddr + " | " + Gb(P(o, "Capacity")) + " | " + spd + " | " + P(o, "Manufacturer") + " | " + (P(o, "PartNumber") ?? "").ToString().Trim());
        }
        A("Tipo", ddr); A("Velocidade configurada", spd); A("Pentes instalados", n);
        hl["RAM"] = new[] { Gb(tot) + " " + ddr, n + " pente(s) • " + spd };
        foreach (var o in Q("SELECT * FROM Win32_PhysicalMemoryArray")) { A("Slots totais", P(o, "MemoryDevices")); A("Capacidade máx. suportada", Gb(Convert.ToDouble(P(o, "MaxCapacity")) * 1024)); }

        S("Placa de Vídeo");
        var vram = new Dictionary<string, string>();
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                foreach (var sk in k.GetSubKeyNames()) using (var s2 = k.OpenSubKey(sk)) { try { var d = s2.GetValue("DriverDesc") as string; var m = s2.GetValue("HardwareInformation.qwMemorySize"); if (d != null && m != null) vram[d] = Gb(m); } catch { } }
        } catch { }
        foreach (var o in Q("SELECT * FROM Win32_VideoController"))
        {
            var name = (string)P(o, "Name"); A("GPU", name);
            var vr = vram.ContainsKey(name ?? "") ? vram[name] : Gb(P(o, "AdapterRAM"));
            A("  VRAM", vram.ContainsKey(name ?? "") ? vram[name] : Gb(P(o, "AdapterRAM")));
            A("  Versão do driver", P(o, "DriverVersion")); A("  Data do driver", Dt(P(o, "DriverDate")));
            A("  Processador de vídeo", P(o, "VideoProcessor"));
            if (P(o, "CurrentHorizontalResolution") != null) A("  Resolução atual", P(o, "CurrentHorizontalResolution") + "x" + P(o, "CurrentVerticalResolution") + " @ " + P(o, "CurrentRefreshRate") + " Hz");
        }
        A("DirectX (registro)", Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\DirectX", "Version"));

        S("Monitores");
        foreach (var o in Q("root\\wmi", "SELECT * FROM WmiMonitorID"))
            A("Monitor", Edid(P(o, "ManufacturerName")) + " " + Edid(P(o, "UserFriendlyName")) + " | Serial: " + Edid(P(o, "SerialNumberID")) + " | Fabricado: " + P(o, "YearOfManufacture"));
        foreach (var sc in Screen.AllScreens) A("Tela " + sc.DeviceName, sc.Bounds.Width + "x" + sc.Bounds.Height + (sc.Primary ? " (principal)" : ""));

        S("Armazenamento");
        var media = new Dictionary<string, string>();
        foreach (var o in Q("root\\Microsoft\\Windows\\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
        {
            var mt = Convert.ToInt32(P(o, "MediaType") ?? 0); var bt = Convert.ToInt32(P(o, "BusType") ?? 0);
            media[(P(o, "FriendlyName") ?? "").ToString()] = (mt == 4 ? "SSD" : mt == 3 ? "HDD" : "?") + (bt == 17 ? " NVMe" : bt == 11 ? " SATA" : bt == 7 ? " USB" : "") + " | Saúde: " + (Convert.ToInt32(P(o, "HealthStatus") ?? 0) == 0 ? "OK" : "Atenção");
        }
        foreach (var o in Q("SELECT * FROM Win32_DiskDrive"))
        {
            var m = (P(o, "Model") ?? "").ToString();
            A("Disco: " + m, Gb(P(o, "Size")) + (media.ContainsKey(m) ? " | " + media[m] : "") + " | Firmware: " + P(o, "FirmwareRevision") + " | Serial: " + (P(o, "SerialNumber") ?? "").ToString().Trim());
        }
        foreach (var o in Q("SELECT * FROM Win32_LogicalDisk WHERE DriveType=3"))
            A("Partição " + P(o, "DeviceID"), P(o, "VolumeName") + " | " + P(o, "FileSystem") + " | Livre " + Gb(P(o, "FreeSpace")) + " de " + Gb(P(o, "Size")));

        S("Rede");
        foreach (var o in Q("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=True"))
        {
            A("Adaptador", P(o, "Description")); A("  MAC", P(o, "MACAddress"));
            var ip = P(o, "IPAddress") as string[]; if (ip != null) A("  IP", string.Join(", ", ip));
            var gw = P(o, "DefaultIPGateway") as string[]; if (gw != null) A("  Gateway", string.Join(", ", gw));
            var dns = P(o, "DNSServerSearchOrder") as string[]; if (dns != null) A("  DNS", string.Join(", ", dns));
            A("  DHCP", (bool)(P(o, "DHCPEnabled") ?? false) ? "Sim" : "Não");
        }
        foreach (var o in Q("SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetEnabled=True"))
            if (P(o, "Speed") != null) A("Velocidade: " + P(o, "Name"), (Convert.ToDouble(P(o, "Speed")) / 1e6) + " Mbps");

        S("Áudio e Periféricos");
        foreach (var o in Q("SELECT * FROM Win32_SoundDevice")) A("Áudio", P(o, "Name") + " (" + P(o, "Manufacturer") + ")");
        foreach (var o in Q("SELECT * FROM Win32_Keyboard")) A("Teclado", P(o, "Description"));
        foreach (var o in Q("SELECT * FROM Win32_PointingDevice")) A("Mouse", P(o, "Name"));
        foreach (var o in Q("SELECT * FROM Win32_Printer")) A("Impressora", P(o, "Name"));
        foreach (var o in Q("SELECT * FROM Win32_Battery")) A("Bateria", P(o, "Name") + " | Carga " + P(o, "EstimatedChargeRemaining") + "%");

        S("Segurança");
        foreach (var o in Q("root\\Microsoft\\Windows\\Defender", "SELECT * FROM MSFT_MpComputerStatus"))
        {
            A("Defender - versão do produto", P(o, "AMProductVersion")); A("Defender - engine", P(o, "AMEngineVersion"));
            A("Defender - serviço", P(o, "AMServiceVersion")); A("Defender - assinaturas", P(o, "AntivirusSignatureVersion"));
            A("Defender - assinaturas atualizadas", Dt(P(o, "AntivirusSignatureLastUpdated")));
            A("Defender - proteção em tempo real", (bool)(P(o, "RealTimeProtectionEnabled") ?? false) ? "Ativada" : "Desativada");
            A("Defender - último scan rápido", Dt(P(o, "QuickScanEndTime")));
        }
        foreach (var o in Q("root\\SecurityCenter2", "SELECT * FROM AntiVirusProduct")) A("Antivírus registrado", P(o, "displayName"));
        foreach (var o in Q("root\\SecurityCenter2", "SELECT * FROM FirewallProduct")) A("Firewall de terceiros", P(o, "displayName"));
        bool tpm = false;
        foreach (var o in Q("root\\cimv2\\Security\\MicrosoftTpm", "SELECT * FROM Win32_Tpm")) { tpm = true; A("TPM", "Versão " + P(o, "SpecVersion") + " | Ativado: " + P(o, "IsEnabled_InitialValue")); }
        if (!tpm) A("TPM", "Não detectado (ou exige executar como administrador)");
        var uac = Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        A("UAC", uac == null ? "N/D" : (Convert.ToInt32(uac) == 1 ? "Ativado" : "Desativado"));

        S("Software Base");
        var rel = Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
        A(".NET Framework 4.x", Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Version") + (rel != null ? " (release " + rel + ")" : ""));
        try { var dn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\shared\Microsoft.NETCore.App"); if (Directory.Exists(dn)) A(".NET (Core) runtimes", string.Join(", ", Directory.GetDirectories(dn).Select(Path.GetFileName))); } catch { }
        A("PowerShell 5", Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine", "PowerShellVersion"));
        try { var e = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application"); if (Directory.Exists(e)) A("Microsoft Edge", string.Join(", ", Directory.GetDirectories(e).Select(Path.GetFileName).Where(x => char.IsDigit(x[0])))); } catch { }
        foreach (var k in new[] { "x64", "x86" }) { var v = Reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + k, "Version"); A("VC++ Redist 2015-2022 " + k, v); }
        var hf = Q("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering").ToList();
        A("Atualizações instaladas (KB)", hf.Count); if (hf.Count > 0) A("KBs", string.Join(", ", hf.Select(h => (string)P(h, "HotFixID"))));

        S("Drivers principais");
        foreach (var o in Q("SELECT DeviceName, DriverVersion, DriverDate, DeviceClass, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass='DISPLAY' OR DeviceClass='NET' OR DeviceClass='MEDIA' OR DeviceClass='HDC' OR DeviceClass='SCSIADAPTER' OR DeviceClass='SYSTEM' AND DriverProviderName<>'Microsoft'"))
        {
            if (P(o, "DeviceName") == null) continue;
            A("[" + P(o, "DeviceClass") + "] " + P(o, "DeviceName"), P(o, "DriverVersion") + " | " + Dt(P(o, "DriverDate")) + " | " + P(o, "DriverProviderName"));
        }
    }
}

class Loader : Control
{
    float ang = 0; System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 16 };
    public Loader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(13, 13, 15); t.Tick += (s, e) => { ang = (ang + 7) % 360; Invalidate(); }; t.Start();
        VisibleChanged += (s, e) => t.Enabled = Visible;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int d = 64; var r = new Rectangle(Width / 2 - d / 2, Height / 2 - d / 2 - 20, d, d);
        using (var p = new Pen(Color.FromArgb(40, 40, 48), 6)) g.DrawEllipse(p, r);
        using (var p = new Pen(Color.FromArgb(0, 170, 255), 6) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawArc(p, r, ang, 100);
        using (var f = PCInfo.F(11f, false))
            TextRenderer.DrawText(g, "Identificando o hardware do PC...", f, new Rectangle(0, r.Bottom + 18, Width, 30), Color.FromArgb(160, 160, 170), TextFormatFlags.HorizontalCenter);
    }
}

// Janela flutuante: status da internet (ping 8.8.8.8 / 1.1.1.1 a cada 2 s)
class NetMonitor : Form
{
    bool? online = null; long ms = 0; bool busy = false; float ph = 0; DateTime since = DateTime.Now;
    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 2000 }, anim = new System.Windows.Forms.Timer { Interval = 33 };
    static Color GBG = Color.FromArgb(30, 110, 72), RBG = Color.FromArgb(165, 42, 48), NBG = Color.FromArgb(50, 50, 58);
    public NetMonitor()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        Size = new Size(460, 128); BackColor = NBG; DoubleBuffered = true;
        var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        try { using (var gp = PCInfo_Round(new RectangleF(0, 0, Width, Height), 16)) Region = new Region(gp); } catch { }
        t.Tick += (s, e) => Check(); t.Start(); Check();
        anim.Tick += (s, e) => { ph = (ph + 0.033f) % 1f; Invalidate(); }; anim.Start();
        VisibleChanged += (s, e) => anim.Enabled = Visible;
        MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) Hide(); };
    }
    static System.Drawing.Drawing2D.GraphicsPath PCInfo_Round(RectangleF r, float d)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    void Check()
    {
        if (busy) return; busy = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = false; long rt = 0;
            foreach (var h in new[] { "8.8.8.8", "1.1.1.1" })
            {
                try { using (var p = new Ping()) { var r = p.Send(h, 1500); if (r.Status == IPStatus.Success) { ok = true; rt = r.RoundtripTime; break; } } } catch { }
            }
            try { BeginInvoke((Action)(() => { if (online != ok) since = DateTime.Now; online = ok; ms = rt; busy = false; BackColor = ok ? GBG : RBG; })); } catch { busy = false; }
        });
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var W = Color.White; float cx = 64, cy = 64;
        if (online == true)
        {
            // anel pulsando + ondas de Wi-Fi acendendo em sequência
            float rr = 30 + ph * 26; int a = (int)(150 * (1 - ph));
            using (var p = new Pen(Color.FromArgb(a, 180, 255, 210), 3)) g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
            using (var b = new SolidBrush(Color.FromArgb(45, 255, 255, 255))) g.FillEllipse(b, cx - 34, cy - 34, 68, 68);
            float by = cy + 14;
            using (var b = new SolidBrush(W)) g.FillEllipse(b, cx - 4.5f, by - 4.5f, 9, 9);
            for (int k = 1; k <= 3; k++)
            {
                float lit = (ph * 4) % 4; int al = lit >= k ? 255 : 90; float r = k * 11;
                using (var p = new Pen(Color.FromArgb(al, W), 5) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawArc(p, cx - r, by - r, r * 2, r * 2, 225, 90);
            }
        }
        else if (online == false)
        {
            // Wi-Fi apagado cortado + "piscar" de alerta
            float pulse = (float)(0.5 + 0.5 * Math.Sin(ph * Math.PI * 2));
            using (var b = new SolidBrush(Color.FromArgb((int)(30 + 40 * pulse), 255, 255, 255))) g.FillEllipse(b, cx - 36, cy - 36, 72, 72);
            float by = cy + 14;
            using (var b = new SolidBrush(Color.FromArgb(110, W))) g.FillEllipse(b, cx - 4.5f, by - 4.5f, 9, 9);
            for (int k = 1; k <= 3; k++) { float r = k * 11;
                using (var p = new Pen(Color.FromArgb(110, W), 5) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawArc(p, cx - r, by - r, r * 2, r * 2, 225, 90); }
            using (var p = new Pen(Color.FromArgb((int)(170 + 85 * pulse), W), 6) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawLine(p, cx - 26, cy - 24, cx + 26, cy + 28);
        }
        else using (var p = new Pen(Color.FromArgb(200, W), 5)) g.DrawArc(p, cx - 22, cy - 22, 44, 44, ph * 360, 100);

        string a1 = online == null ? "Verificando conexão..." : online.Value ? "Online" : "Sem internet";
        var dur = DateTime.Now - since; string d = dur.TotalHours >= 1 ? (int)dur.TotalHours + " h " + dur.Minutes + " min" : dur.TotalMinutes >= 1 ? (int)dur.TotalMinutes + " min" : (int)dur.TotalSeconds + " s";
        string a2 = online == true ? "Internet conectada  ·  latência " + ms + " ms" : online == false ? "Conexão perdida  ·  tentando reconectar..." : "";
        string a3 = online == null ? "" : (online.Value ? "Estável há " : "Fora do ar há ") + d;
        using (var f1 = PCInfo.F(20f, true)) TextRenderer.DrawText(g, a1, f1, new Point(124, 16), W);
        using (var f2 = PCInfo.F(11f, false)) TextRenderer.DrawText(g, a2, f2, new Point(126, 58), Color.FromArgb(235, 255, 255, 255));
        using (var f3 = PCInfo.F(9.5f, false)) TextRenderer.DrawText(g, a3, f3, new Point(126, 86), Color.FromArgb(200, 255, 255, 255));
    }
}
