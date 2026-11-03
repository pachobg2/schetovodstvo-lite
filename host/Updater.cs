using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SchetovodstvoLite
{
    /// <summary>Updates over the internet from the GitHub releases of pachobg2/schetovodstvo-lite.
    /// Release assets: schetovodstvo-lite.exe + SHA256SUMS.txt. The download is only used when its SHA-256 matches the sums file of the same release.</summary>
    static class Updater
    {
        const string Api = "https://api.github.com/repos/pachobg2/schetovodstvo-lite/releases/latest";
        const string ExeAsset = "schetovodstvo-lite.exe", SumsAsset = "SHA256SUMS.txt";
        // tests only (together with SCHETLITE_TEST_ROOT): point the updater at a local fake release
        static readonly string TestUrl = Environment.GetEnvironmentVariable("SCHETLITE_TEST_ROOT") != null ? Environment.GetEnvironmentVariable("SCHETLITE_UPDATE_URL") : null;
        static readonly HttpClient Http = MakeHttp();
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;

        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("schetovodstvo-lite-updater");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        public static Version Current
        {
            get { var v = typeof(Updater).Assembly.GetName().Version; return new Version(v.Major, v.Minor, Math.Max(v.Build, 0)); }
        }

        sealed class Release { public Version Ver; public string Notes, Page, ExeUrl, SumsUrl; public long Size; }

        static bool Trusted(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (TestUrl != null && u.IsLoopback) return true;
            return u.Scheme == Uri.UriSchemeHttps && (u.Host == "github.com" || u.Host.EndsWith(".github.com") || u.Host.EndsWith(".githubusercontent.com"));
        }

        static string Str(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) && o != null ? Convert.ToString(o) : null; }

        static async Task<Release> Fetch()
        {
            var j = Js.DeserializeObject(await Http.GetStringAsync(TestUrl ?? Api)) as Dictionary<string, object>;
            var tag = (Str(j, "tag_name") ?? "").Trim().TrimStart('v', 'V');
            Version ver;
            if (!Version.TryParse(tag, out ver)) throw new Exception("Невалиден номер на версия в GitHub: " + tag);
            var r = new Release { Ver = ver, Notes = Str(j, "body") ?? "", Page = Str(j, "html_url") ?? "" };
            var assets = j.ContainsKey("assets") ? j["assets"] as IEnumerable : null;
            if (assets != null)
                foreach (Dictionary<string, object> a in assets)
                {
                    var name = Str(a, "name"); var url = Str(a, "browser_download_url");
                    if (name == ExeAsset) { r.ExeUrl = url; long sz; if (long.TryParse(Str(a, "size"), out sz)) r.Size = sz; }
                    else if (name == SumsAsset) r.SumsUrl = url;
                }
            return r;
        }

        public static async Task<object> Check()
        {
            var r = await Fetch();
            var cur = Current;
            return new { current = cur.ToString(), latest = r.Ver.ToString(), newer = r.Ver > cur, notes = r.Notes, page = r.Page, ready = r.ExeUrl != null && r.SumsUrl != null, sizeMb = Math.Round(r.Size / 1048576.0, 1) };
        }

        /// <summary>Downloads, verifies and swaps the exe, then starts the new one (which waits for this process to end). The caller closes the app afterwards.</summary>
        public static async Task Install(Action<int> progress)
        {
            var r = await Fetch();
            if (r.Ver <= Current) throw new Exception("Вече имате най-новата версия.");
            if (r.ExeUrl == null || r.SumsUrl == null) throw new Exception("Версията в GitHub няма нужните файлове (schetovodstvo-lite.exe и SHA256SUMS.txt).");
            if (!Trusted(r.ExeUrl) || !Trusted(r.SumsUrl)) throw new Exception("Адресът за изтегляне не е от GitHub — прекратявам.");

            string want = null;
            foreach (var line in (await Http.GetStringAsync(r.SumsUrl)).Split('\n'))
            {
                var p = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && p[1] == ExeAsset) { want = p[0].ToLowerInvariant(); break; }
            }
            if (want == null || want.Length != 64) throw new Exception("В SHA256SUMS.txt няма контролна сума за schetovodstvo-lite.exe.");

            var exe = ExePath; var dir = Path.GetDirectoryName(exe);
            var tmp = Path.Combine(dir, "schetovodstvo-lite.update.tmp"); var old = exe + ".old";
            try { File.WriteAllBytes(tmp, new byte[0]); }
            catch { throw new Exception("Няма право за запис в папката на програмата: " + dir); }

            try
            {
                using (var resp = await Http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? r.Size;
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var dst = File.Create(tmp))
                    {
                        var buf = new byte[81920]; long got = 0; int last = -1, n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n); got += n;
                            int pct = total > 0 ? (int)(got * 100 / total) : 0;
                            if (pct != last) { last = pct; progress(pct); }
                        }
                    }
                }
                string have;
                using (var fs = File.OpenRead(tmp)) have = BitConverter.ToString(SHA256.Create().ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (have != want) throw new Exception("Контролната сума на изтегления файл не съвпада — файлът не е използван.");
                using (var fs = File.OpenRead(tmp)) if (fs.Length < 100000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new Exception("Изтегленият файл не е валидна програма.");
            }
            catch { try { File.Delete(tmp); } catch { } throw; }

            // a running exe cannot be overwritten but it can be renamed: old -> .old, new -> exe
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); try { File.Delete(tmp); } catch { } throw; }
            try { Process.Start(new ProcessStartInfo(exe, "--wait-pid " + Process.GetCurrentProcess().Id) { UseShellExecute = false, WorkingDirectory = dir }); }
            catch { try { File.Delete(exe); File.Move(old, exe); } catch { } throw new Exception("Новата версия не се стартира; върната е старата."); }
        }

        /// <summary>Leftovers of a finished update.</summary>
        public static void CleanUp()
        {
            try
            {
                foreach (var f in new[] { ExePath + ".old", Path.Combine(Path.GetDirectoryName(ExePath), "schetovodstvo-lite.update.tmp") }) if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }
    }
}
