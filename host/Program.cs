using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SchetovodstvoLite
{
    static class Program
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);

        static readonly Assembly Self = typeof(Program).Assembly;

        // Everything the app needs is inside this one exe: the managed WebView2 DLLs and the native loader are embedded resources.
        static Assembly ResolveEmbedded(object sender, ResolveEventArgs a)
        {
            var name = new AssemblyName(a.Name).Name;
            using (var s = Self.GetManifestResourceStream("dep/" + name + ".dll"))
            {
                if (s == null) return null;
                var b = new byte[s.Length]; s.Read(b, 0, b.Length);
                return Assembly.Load(b);
            }
        }

        static void PreloadNative()
        {
            try
            {
                using (var s = Self.GetManifestResourceStream("dep/WebView2Loader.dll"))
                {
                    if (s == null) return;
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchetovodstvoLite", "native", Self.GetName().Version.ToString());
                    Directory.CreateDirectory(dir);
                    var f = Path.Combine(dir, "WebView2Loader.dll");
                    if (!File.Exists(f) || new FileInfo(f).Length != s.Length)
                        using (var o = File.Create(f)) s.CopyTo(o);
                    LoadLibrary(f);   // the managed DLL then finds the already-loaded module by name
                }
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch { }   // TLS 1.2 for GitHub
            Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run(string[] args)
        {
            int wi = Array.IndexOf(args, "--wait-pid");   // started by the updater: let the old instance finish closing first
            int wp;
            if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out wp)) { try { Process.GetProcessById(wp).WaitForExit(20000); } catch { } }
            var where = (Path.GetDirectoryName(Updater.ExePath) ?? "").ToLowerInvariant();
            var hash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(where));
            var id = BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            bool first;
            using (var mutex = new Mutex(true, @"Local\SchetovodstvoLite.SingleInstance." + id, out first))
            {
                var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SchetovodstvoLite.Show." + id);
                if (!first) { show.Set(); return; }   // already running: bring that window to the front
                Updater.CleanUp();
                PreloadNative();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm();
                var t = new Thread(() => { while (show.WaitOne()) { try { form.BeginInvoke((Action)(() => { if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate(); })); } catch { return; } } }) { IsBackground = true };
                t.Start();
                Application.Run(form);
            }
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
        static readonly string ExeDir = Path.GetDirectoryName(Updater.ExePath);
        static readonly string TestRoot = Environment.GetEnvironmentVariable("SCHETLITE_TEST_ROOT"); // tests only: keeps real data untouched
        static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SchetovodstvoLite");
        static readonly string Docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string DataDir, DataFile, ConfigFile;
        string BackupDir { get { return Path.Combine(DataDir, "backups"); } }

        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(243, 244, 246) };
        Config cfg = new Config();
        DateTime lastBak = DateTime.MinValue;
        bool closing;

        public MainForm()
        {
            Text = "Счетоводство Lite";
            ResolveDataDir();
            try { cfg = Js.Deserialize<Config>(File.ReadAllText(ConfigFile)) ?? new Config(); } catch { cfg = new Config(); }
            try { Icon = Icon.ExtractAssociatedIcon(Updater.ExePath); } catch { }
            var sc = DeviceDpi / 96f;
            MinimumSize = new Size((int)(900 * sc), (int)(600 * sc));
            var area = Screen.PrimaryScreen.WorkingArea;
            var w = Math.Min(cfg.W > 0 ? Math.Max(cfg.W, MinimumSize.Width) : (int)(1240 * sc), area.Width);
            var h = Math.Min(cfg.H > 0 ? Math.Max(cfg.H, MinimumSize.Height) : (int)(900 * sc), area.Height);
            StartPosition = FormStartPosition.Manual;
            var onScreen = cfg.X >= 0 && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(cfg.X + 60, cfg.Y + 20)));
            Bounds = onScreen ? new Rectangle(cfg.X, cfg.Y, w, h) : new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
            if (cfg.Max) WindowState = FormWindowState.Maximized;
            Controls.Add(web);
            AutoBackupDaily();
        }

        // EVERYTHING lives in one folder next to the exe: Schetovodstvo-Lite-data\ = data.json (+.bak), backups\, config.json.
        // If the exe's folder is read-only the same layout is used in %AppData%\SchetovodstvoLite.
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

        static readonly Assembly Asm = typeof(MainForm).Assembly;

        void OnWebResource(object s, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var env = web.CoreWebView2.Environment;
            var name = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
            if (name.Length == 0) name = "index.html";
            using (var src = name.Contains("..") ? null : Asm.GetManifestResourceStream("web/" + name))
            {
                if (src == null) { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); return; }
                var ms = new MemoryStream(); src.CopyTo(ms); ms.Position = 0;
                string mime;
                switch (Path.GetExtension(name).ToLowerInvariant())
                {
                    case ".html": mime = "text/html; charset=utf-8"; break;
                    case ".js": mime = "application/javascript; charset=utf-8"; break;
                    case ".png": mime = "image/png"; break;
                    case ".ico": mime = "image/x-icon"; break;
                    default: mime = "application/octet-stream"; break;
                }
                e.Response = env.CreateWebResourceResponse(ms, 200, "OK", "Content-Type: " + mime + "\r\nCache-Control: no-store");
            }
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
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(DataFile) && (DateTime.Now - lastBak).TotalMinutes > 2) { File.Copy(DataFile, DataFile + ".bak", true); lastBak = DateTime.Now; }
            if (File.Exists(DataFile)) File.Delete(DataFile);
            File.Move(tmp, DataFile);
        }

        string BackupNow(string json)
        {
            Directory.CreateDirectory(BackupDir);
            var path = Path.Combine(BackupDir, "schetovodstvolite_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
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
                var today = "schetovodstvolite_" + DateTime.Now.ToString("yyyyMMdd") + "_";
                if (new DirectoryInfo(BackupDir).GetFiles(today + "*.json").Length == 0)
                { File.Copy(DataFile, Path.Combine(BackupDir, "schetovodstvolite_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json")); Prune(); }
            }
            catch { }
        }

        void SaveConfig() { try { File.WriteAllText(ConfigFile, Js.Serialize(cfg)); } catch { } }

        static string SafeName(string n) { return string.Concat((n ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)); }

        /* ---------------- bridge ---------------- */
        async void OnMessage(object s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = Js.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>; } catch { return; }
            if (msg == null) return;
            object idObj; msg.TryGetValue("id", out idObj);
            int? id = idObj == null ? (int?)null : Convert.ToInt32(idObj);
            object cmdObj; msg.TryGetValue("cmd", out cmdObj);
            string cmd = cmdObj as string;
            object arg; msg.TryGetValue("arg", out arg);
            var argd = arg as Dictionary<string, object>;
            object result = null; string error = null; bool restartAfter = false;
            try
            {
                switch (cmd)
                {
                    case "load": result = File.Exists(DataFile) ? File.ReadAllText(DataFile) : null; break;
                    case "save": SaveData((string)arg); break;
                    case "backup": result = BackupNow((string)arg); break;
                    case "listBackups":
                        result = new DirectoryInfo(BackupDir).GetFiles("schetovodstvolite_*.json").OrderByDescending(f => f.Name)
                            .Select(f => new { name = f.Name, when = f.LastWriteTime.ToString("dd.MM.yyyy HH:mm"), kb = (int)Math.Ceiling(f.Length / 1024.0) }).ToList();
                        break;
                    case "readBackup":
                        {
                            var name = Path.GetFileName((string)arg);   // only files inside backups\
                            if (!name.StartsWith("schetovodstvolite_") || !name.EndsWith(".json")) throw new Exception("Невалидно име на копие.");
                            result = File.ReadAllText(Path.Combine(BackupDir, name));
                            break;
                        }
                    case "about":
                        result = new { version = Updater.Current.ToString(), exe = Updater.ExePath, dataDir = DataDir, webview2 = web.CoreWebView2.Environment.BrowserVersionString };
                        break;
                    case "openDataDir":
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DataDir + "\"") { UseShellExecute = true });
                        break;
                    case "pdfs":   // one PDF per language: ask for the folder once, sub-folder per invoice month
                        {
                            var files = (argd != null && argd.ContainsKey("files") ? argd["files"] as IEnumerable : null) ?? new object[0];
                            var initial = !string.IsNullOrEmpty(cfg.PdfDir) && Directory.Exists(cfg.PdfDir) ? cfg.PdfDir : Path.Combine(Docs, "Фактури");
                            Directory.CreateDirectory(initial);
                            string dir;
                            if (TestRoot != null) dir = TestRoot;
                            else
                            {
                                using (var dlg = new FolderBrowserDialog { Description = "В коя папка да се запишат PDF файловете?", SelectedPath = initial, ShowNewFolderButton = true })
                                {
                                    if (dlg.ShowDialog(this) != DialogResult.OK) break;
                                    dir = dlg.SelectedPath; cfg.PdfDir = dir; SaveConfig();
                                }
                            }
                            var sub = SafeName(argd != null && argd.ContainsKey("folder") ? argd["folder"] as string : "").Trim().TrimEnd('.');
                            if (sub.Length > 0) { dir = Path.Combine(dir, sub); Directory.CreateDirectory(dir); }
                            var saved = new List<string>();
                            foreach (Dictionary<string, object> f in files)
                            {
                                object o;
                                var nm = SafeName(f.TryGetValue("name", out o) ? o as string : null); if (nm.Length == 0) nm = "invoice.pdf";
                                var lang = f.TryGetValue("lang", out o) && (o as string) == "en" ? "en" : "bg";
                                var target = Path.Combine(dir, nm);
                                if (TestRoot == null && File.Exists(target) && MessageBox.Show(this, "Файлът „" + nm + "“ вече съществува. Да го заменя ли?", "Счетоводство Lite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) continue;
                                var docTitle = f.TryGetValue("title", out o) ? o as string : null;   // the PDF's title (shown by PDF viewers)
                                if (!string.IsNullOrWhiteSpace(docTitle)) await web.CoreWebView2.ExecuteScriptAsync("document.title=" + Js.Serialize(docTitle));
                                await web.CoreWebView2.ExecuteScriptAsync("document.body.classList.add('p-" + lang + "');window.__printing&&window.__printing(true)");
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
                                finally { await web.CoreWebView2.ExecuteScriptAsync("document.body.classList.remove('p-" + lang + "');window.__printing&&window.__printing(false)"); }
                            }
                            result = saved;
                            break;
                        }
                    case "updateCheck": result = await Updater.Check(); break;
                    case "updateInstall":
                        await Updater.Install(pct => { try { web.CoreWebView2.PostWebMessageAsJson(Js.Serialize(new { ev = "upd", pct = pct })); } catch { } });
                        restartAfter = true;
                        break;
                    default: error = "unknown command " + cmd; break;
                }
            }
            catch (Exception ex) { error = ex.Message; }
            if (id != null)
            {
                try { web.CoreWebView2.PostWebMessageAsJson(Js.Serialize(new Dictionary<string, object> { { "id", id.Value }, { "result", result }, { "error", error } })); } catch { }
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
                        var j = Js.DeserializeObject(t.Result) as string;
                        if (!string.IsNullOrEmpty(j)) SaveData(j);
                    }
                }
            }
            catch { }
            Close();
        }
    }
}
