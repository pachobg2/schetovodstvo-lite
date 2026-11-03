using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SchetovodstvoLite;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        int wi = Array.IndexOf(args, "--wait-pid");   // started by the updater: let the old instance finish closing first
        if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out var wp)) { try { Process.GetProcessById(wp).WaitForExit(20000); } catch { } }
        var where = (Path.GetDirectoryName(Environment.ProcessPath) ?? "").ToLowerInvariant();
        var id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(where)))[..12];
        using var mutex = new Mutex(true, @"Local\SchetovodstvoLite.SingleInstance." + id, out bool first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SchetovodstvoLite.Show." + id);
        if (!first) { show.Set(); return; }   // already running: just bring that window to the front
        Updater.CleanUp();
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        var t = new Thread(() => { while (show.WaitOne()) { try { form.BeginInvoke(() => { if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate(); }); } catch { return; } } }) { IsBackground = true };
        t.Start();
        Application.Run(form);
    }
}

class Config
{
    public string PdfDir { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int W { get; set; }
    public int H { get; set; }
    public bool Max { get; set; }
}

class MainForm : Form
{
    const int KeepBackups = 40;
    static readonly string ExeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
    static readonly string TestRoot = Environment.GetEnvironmentVariable("SCHETLITE_TEST_ROOT"); // tests only: keeps real data untouched
    static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SchetovodstvoLite");
    static readonly string Docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string DataDir, DataFile, ConfigFile;
    string BackupDir => Path.Combine(DataDir, "backups");

    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(243, 244, 246) };
    Config cfg = new();
    DateTime lastBak = DateTime.MinValue;
    bool closing;

    public MainForm()
    {
        Text = "Счетоводство Lite";
        ResolveDataDir();
        try { cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile)) ?? new Config(); } catch { cfg = new Config(); }
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        var sc = DeviceDpi / 96f;
        MinimumSize = new Size((int)(900 * sc), (int)(600 * sc));
        var area = Screen.PrimaryScreen!.WorkingArea;
        var w = Math.Min(cfg.W > 0 ? Math.Max(cfg.W, MinimumSize.Width) : (int)(1240 * sc), area.Width);
        var h = Math.Min(cfg.H > 0 ? Math.Max(cfg.H, MinimumSize.Height) : (int)(900 * sc), area.Height);
        StartPosition = FormStartPosition.Manual;
        var onScreen = cfg.X >= 0 && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(cfg.X + 60, cfg.Y + 20)));
        Bounds = onScreen ? new Rectangle(cfg.X, cfg.Y, w, h) : new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
        if (cfg.Max) WindowState = FormWindowState.Maximized;
        Controls.Add(web);
        AutoBackupDaily();
    }

    // EVERYTHING lives in one folder next to the exe: Schetovodstvo-data\ = data.json (+.bak), backups\, config.json.
    // Copy the exe together with that folder to move the app. If the exe's folder is read-only the same layout is used in %AppData%\Schetovodstvo.
    void ResolveDataDir()
    {
        if (TestRoot != null) DataDir = Path.Combine(TestRoot, "data");
        else
        {
            try
            {
                var pd = Path.Combine(ExeDir, "Schetovodstvo-Lite-data");
                Directory.CreateDirectory(pd);
                var probe = Path.Combine(pd, ".write-test"); File.WriteAllText(probe, "x"); File.Delete(probe);
                DataDir = pd;
            }
            catch { DataDir = AppDataDir; }
        }
        Directory.CreateDirectory(DataDir); Directory.CreateDirectory(Path.Combine(DataDir, "backups"));
        DataFile = Path.Combine(DataDir, "data.json"); ConfigFile = Path.Combine(DataDir, "config.json");
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var wv = TestRoot != null ? Path.Combine(TestRoot, "wv2") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchetovodstvoLite", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, wv);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не може да се стартира Microsoft Edge WebView2 Runtime.\r\n\r\nИнсталирайте го от https://go.microsoft.com/fwlink/p/?LinkId=2124703\r\n\r\n" + ex.Message, "Счетоводство Lite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            closing = true; Close(); return;
        }
        var core = web.CoreWebView2;
        // the whole web app is embedded in this exe and served from memory at https://schetovodstvo-lite.app/
        core.AddWebResourceRequestedFilter("https://schetovodstvo-lite.app/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResource;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (s, a) => { a.Handled = true; OpenExternal(a.Uri); };
        core.NavigationStarting += (s, a) => { if (!a.Uri.StartsWith("https://schetovodstvo-lite.app/")) { a.Cancel = true; OpenExternal(a.Uri); } };
        core.Navigate("https://schetovodstvo-lite.app/index.html");
    }

    static readonly System.Reflection.Assembly Asm = typeof(MainForm).Assembly;

    void OnWebResource(object s, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = web.CoreWebView2.Environment;
        var name = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
        if (name.Length == 0) name = "index.html";
        using var src = name.Contains("..") ? null : Asm.GetManifestResourceStream("web/" + name);
        if (src == null) { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); return; }
        var ms = new MemoryStream(); src.CopyTo(ms); ms.Position = 0;
        var mime = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
        e.Response = env.CreateWebResourceResponse(ms, 200, "OK", "Content-Type: " + mime + "\r\nCache-Control: no-store");
    }

    static void OpenExternal(string uri)
    {
        if (uri.StartsWith("http://") || uri.StartsWith("https://"))
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    /* ---------------- data ---------------- */
    void SaveData(string json)
    {
        Directory.CreateDirectory(DataDir);
        var tmp = DataFile + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        if (File.Exists(DataFile) && (DateTime.Now - lastBak).TotalMinutes > 2) { File.Copy(DataFile, DataFile + ".bak", true); lastBak = DateTime.Now; }
        File.Move(tmp, DataFile, true);
    }

    string BackupNow(string json)
    {
        Directory.CreateDirectory(BackupDir);
        var path = Path.Combine(BackupDir, $"schetovodstvolite_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        Prune();
        return path;
    }

    void Prune()
    {
        var files = new DirectoryInfo(BackupDir).GetFiles("schetovodstvolite_*.json").OrderByDescending(f => f.Name).ToList();
        foreach (var f in files.Skip(KeepBackups)) try { f.Delete(); } catch { }
    }

    void AutoBackupDaily()
    {
        try
        {
            if (!File.Exists(DataFile)) return;
            Directory.CreateDirectory(BackupDir);
            var today = $"schetovodstvolite_{DateTime.Now:yyyyMMdd}_";
            if (new DirectoryInfo(BackupDir).GetFiles(today + "*.json").Length == 0)
            { File.Copy(DataFile, Path.Combine(BackupDir, $"schetovodstvolite_{DateTime.Now:yyyyMMdd_HHmmss}.json")); Prune(); }
        }
        catch { }
    }

    void SaveConfig() { try { File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg)); } catch { } }

    /* ---------------- bridge ---------------- */
    async void OnMessage(object s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        int? id = msg?["id"]?.GetValue<int>();
        string cmd = msg?["cmd"]?.GetValue<string>();
        var arg = msg?["arg"];
        object result = null; string error = null; bool restartAfter = false;
        try
        {
            switch (cmd)
            {
                case "load": result = File.Exists(DataFile) ? File.ReadAllText(DataFile) : null; break;
                case "save": SaveData(arg.GetValue<string>()); break;
                case "backup": result = BackupNow(arg.GetValue<string>()); break;
                case "listBackups":
                    result = new DirectoryInfo(BackupDir).GetFiles("schetovodstvolite_*.json").OrderByDescending(f => f.Name)
                        .Select(f => new { name = f.Name, when = f.LastWriteTime.ToString("dd.MM.yyyy HH:mm"), kb = (int)Math.Ceiling(f.Length / 1024.0) }).ToList();
                    break;
                case "readBackup":
                    {
                        var name = Path.GetFileName(arg.GetValue<string>());   // only files inside backups\
                        if (!name.StartsWith("schetovodstvolite_") || !name.EndsWith(".json")) throw new Exception("Невалидно име на копие.");
                        result = File.ReadAllText(Path.Combine(BackupDir, name));
                        break;
                    }
                case "about":
                    result = new
                    {
                        version = typeof(MainForm).Assembly.GetName().Version is Version av ? $"{av.Major}.{av.Minor}.{av.Build}" : "?",
                        exe = Environment.ProcessPath,
                        dataDir = DataDir,
                        webview2 = web.CoreWebView2.Environment.BrowserVersionString
                    };
                    break;
                case "openDataDir":
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDir}\"") { UseShellExecute = true });
                    break;
                case "pdfs":   // one PDF per language: ask for the folder once, sub-folder per invoice month
                    {
                        var files = arg?["files"]?.AsArray() ?? new JsonArray();
                        var initial = !string.IsNullOrEmpty(cfg.PdfDir) && Directory.Exists(cfg.PdfDir) ? cfg.PdfDir : Path.Combine(Docs, "Фактури");
                        Directory.CreateDirectory(initial);
                        string dir;
                        if (TestRoot != null) dir = TestRoot;
                        else
                        {
                            using var dlg = new FolderBrowserDialog { Description = "В коя папка да се запишат PDF файловете?", UseDescriptionForTitle = true, SelectedPath = initial, ShowNewFolderButton = true };
                            if (dlg.ShowDialog(this) != DialogResult.OK) break;
                            dir = dlg.SelectedPath; cfg.PdfDir = dir; SaveConfig();
                        }
                        var sub = string.Concat((arg?["folder"]?.GetValue<string>() ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
                        if (sub.Length > 0) { dir = Path.Combine(dir, sub); Directory.CreateDirectory(dir); }
                        var saved = new List<string>();
                        foreach (var f in files)
                        {
                            var nm = string.Concat((f?["name"]?.GetValue<string>() ?? "invoice.pdf").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                            var lang = f?["lang"]?.GetValue<string>() == "en" ? "en" : "bg";
                            var target = Path.Combine(dir, nm);
                            if (TestRoot == null && File.Exists(target) && MessageBox.Show(this, $"Файлът „{nm}“ вече съществува. Да го заменя ли?", "Счетоводство Lite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) continue;
                            var docTitle = f?["title"]?.GetValue<string>();   // the PDF's title (shown by PDF viewers)
                            if (!string.IsNullOrWhiteSpace(docTitle)) await web.CoreWebView2.ExecuteScriptAsync("document.title=" + JsonSerializer.Serialize(docTitle));
                            await web.CoreWebView2.ExecuteScriptAsync($"document.body.classList.add('p-{lang}');window.__printing&&window.__printing(true)");
                            try
                            {
                                await Task.Delay(250);
                                var ps = web.CoreWebView2.Environment.CreatePrintSettings();
                                ps.Orientation = CoreWebView2PrintOrientation.Portrait;
                                ps.PageWidth = 8.27; ps.PageHeight = 11.69;
                                ps.MarginTop = ps.MarginBottom = ps.MarginLeft = ps.MarginRight = 0;
                                ps.ShouldPrintBackgrounds = true; ps.ShouldPrintHeaderAndFooter = false;
                                if (!await web.CoreWebView2.PrintToPdfAsync(target, ps)) throw new Exception("PDF не беше създаден: " + nm);
                                saved.Add(target);
                            }
                            finally { await web.CoreWebView2.ExecuteScriptAsync($"document.body.classList.remove('p-{lang}');window.__printing&&window.__printing(false)"); }
                        }
                        result = saved;
                        break;
                    }
                case "updateCheck": result = await Updater.Check(); break;
                case "updateInstall":
                    await Updater.Install(pct => { try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { ev = "upd", pct })); } catch { } });
                    restartAfter = true;
                    break;
                default: error = "unknown command " + cmd; break;
            }
        }
        catch (Exception ex) { error = ex.Message; }
        if (id != null)
        {
            try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result, error })); } catch { }
        }
        if (restartAfter) { await Task.Delay(400); Close(); }   // the new version is already starting; closing saves the state first
    }

    /* ---------------- shutdown: flush the latest state to disk ---------------- */
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (closing) { base.OnFormClosing(e); return; }
        e.Cancel = true; closing = true;
        try
        {
            cfg.Max = WindowState == FormWindowState.Maximized;
            var b = cfg.Max ? RestoreBounds : Bounds;
            cfg.X = b.X; cfg.Y = b.Y; cfg.W = b.Width; cfg.H = b.Height; SaveConfig();
            if (web.CoreWebView2 != null)
            {
                var t = web.CoreWebView2.ExecuteScriptAsync("window.__state ? window.__state() : null");
                if (await Task.WhenAny(t, Task.Delay(2500)) == t && t.Result != "null")
                {
                    var j = JsonSerializer.Deserialize<string>(t.Result);
                    if (!string.IsNullOrEmpty(j)) SaveData(j);
                }
            }
        }
        catch { }
        Close();
    }
}
