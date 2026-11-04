// SlideDock v4 - dock rounded yang sepenuhnya menggantikan taskbar Windows.
// Compiler: C# 5 bawaan .NET Framework 4.8 (tanpa SDK tambahan).
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using DR = System.Drawing;

// target .NET 4.8 agar WPF memakai skala per-monitor (tajam di monitor 125% maupun 100%)
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace SlideDock
{
    // ================= data =================
    class WinInfo { public IntPtr H; public string Title; public string Exe; public uint Pid; public string Cls; public string Aumid; }

    class DockItem
    {
        public string Key;
        public string Name;
        public string Exe;
        public string Lnk;
        public string Aumid;
        public bool Pinned;
        public IntPtr IconWin;
        public bool Active;
        public List<IntPtr> Wins = new List<IntPtr>();
    }

    class AppEntry { public string Name; public string Target; public bool Desktop; }

    class PinItem { public string Target; public string Name; }

    static class Store
    {
        public static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlideDock");
        static readonly string HiddenFile = System.IO.Path.Combine(Dir, "hidden.txt");

        public static string[] DesktopDirs()
        {
            return new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
        }

        public static string PinnedDir()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        }

        public static HashSet<string> Hidden()
        {
            HashSet<string> h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(HiddenFile))
                foreach (string l in File.ReadAllLines(HiddenFile)) if (l.Trim().Length > 0) h.Add(l.Trim());
            return h;
        }

        public static void Hide(string target)
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(HiddenFile, target + Environment.NewLine);
        }
    }

    // ----- identitas app: versi (dari file VERSION saat build) & mode Microsoft Store -----
    static class AppInfo
    {
        public const string Repo = "hubble99/slidedock-windows";
        public static readonly Version Ver = Norm(typeof(AppInfo).Assembly.GetName().Version);
        public static string Version { get { return Ver.ToString(3); } }
        public static string ReleasesPage { get { return "https://github.com/" + Repo + "/releases"; } }

        public static Version Norm(Version v) { return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build)); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref int len, StringBuilder name);
        static bool? packaged;

        // dipasang dari Microsoft Store (MSIX): update diurus Store, mulai bersama Windows lewat StartupTask
        public static bool Packaged
        {
            get
            {
                if (packaged == null)
                {
                    try { int len = 0; packaged = GetCurrentPackageFullName(ref len, null) != 15700; }   // 15700 = bukan paket
                    catch { packaged = false; }
                }
                return packaged.Value;
            }
        }
    }

    // ----- update otomatis dari GitHub Releases (versi biasa, bukan versi Store) -----
    class UpdateInfo { public Version Ver; public string Tag, Page, ExeUrl, ShaUrl; public List<string> Notes = new List<string>(); }

    static class Updater
    {
        public static UpdateInfo Available;              // null = sudah versi terbaru / belum dicek
        public static bool Busy, Checking;
        public static DateTime LastCheck = DateTime.MinValue;
        public static string LastError;
        public static Action Changed;                    // dipanggil di thread UI setiap status berubah
        public static bool AutoCheck { get { return Prefs.Get("autoupdate", 1) != 0; } set { Prefs.Set("autoupdate", value ? 1 : 0); } }
        public static bool Enabled { get { return !AppInfo.Packaged; } }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "SlideDock/" + AppInfo.Version;
            return wc;
        }

        static UpdateInfo Fetch()
        {
            string json;
            using (WebClient wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                wc.Encoding = Encoding.UTF8;
                json = wc.DownloadString("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
            }
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
            js.MaxJsonLength = 20000000;
            Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(json);
            string tag = d.ContainsKey("tag_name") ? d["tag_name"] as string : null;
            Version v;
            if (tag == null || !System.Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            UpdateInfo u = new UpdateInfo();
            u.Ver = AppInfo.Norm(v);
            u.Tag = tag;
            u.Page = d.ContainsKey("html_url") ? d["html_url"] as string : AppInfo.ReleasesPage;
            u.Notes = Summary(d.ContainsKey("body") ? d["body"] as string : null);
            System.Collections.IEnumerable assets = d.ContainsKey("assets") ? d["assets"] as System.Collections.IEnumerable : null;
            if (assets != null)
                foreach (object o in assets)
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = a["name"] as string, url = a["browser_download_url"] as string;
                    if (name == "SlideDock.exe") u.ExeUrl = url;
                    else if (name == "SlideDock.exe.sha256") u.ShaUrl = url;
                }
            return u;
        }

        // ringkasan rilis: butir "- ..." dari catatan rilis (ditulis CI dari pesan commit rilis), tanpa format Markdown
        static List<string> Summary(string body)
        {
            List<string> l = new List<string>();
            if (string.IsNullOrEmpty(body)) return l;
            foreach (string raw in body.Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith("- ")) l.Add(raw.Substring(2).Trim());
                else if (l.Count > 0 && raw.StartsWith("  ") && raw.Trim().Length > 0) l[l.Count - 1] += " " + raw.Trim();
                else if (l.Count > 0 && raw.Trim().Length == 0) break;     // butir berikutnya = bagian lain (daftar otomatis GitHub)
            }
            for (int i = 0; i < l.Count; i++)
            {
                string t = Regex.Replace(l[i], @"\[([^\]]+)\]\([^)]*\)", "$1");
                l[i] = t.Replace("**", "").Replace("`", "");
            }
            return l.Take(8).ToList();
        }

        // cek rilis terbaru di thread latar; done(pesan) di thread UI (pesan null = berhasil)
        public static void Check(Dispatcher ui, Action<string> done)
        {
            if (!Enabled || Checking) return;
            Checking = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo u = null;
                string err = null;
                try { u = Fetch(); } catch (Exception ex) { err = ex.Message; }
                ui.BeginInvoke(new Action(delegate
                {
                    Checking = false;
                    LastCheck = DateTime.Now;
                    LastError = err;
                    if (err == null) Available = u != null && u.ExeUrl != null && u.Ver > AppInfo.Ver ? u : null;
                    if (Changed != null) Changed();
                    if (done != null) done(err);
                }));
            });
        }

        // unduh versi baru, cek sidik jari SHA-256, lalu tukar file .exe. File .exe yang sedang berjalan boleh
        // diganti namanya di Windows: SlideDock.exe -> SlideDock.exe.old, file baru -> SlideDock.exe, lalu restart.
        public static void Install(Dispatcher ui, Action restart, Action<string> fail)
        {
            UpdateInfo u = Available;
            if (u == null || Busy) return;
            Busy = true;
            if (Changed != null) Changed();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string fresh = exe + ".new", old = exe + ".old";
                string err = null;
                try
                {
                    using (WebClient wc = Client()) wc.DownloadFile(u.ExeUrl, fresh);
                    byte[] data = File.ReadAllBytes(fresh);
                    if (data.Length < 100000 || data[0] != 'M' || data[1] != 'Z') throw new Exception("The downloaded file is not a valid app.");
                    if (u.ShaUrl != null)
                    {
                        string expect;
                        using (WebClient wc = Client()) expect = wc.DownloadString(u.ShaUrl).Trim().Split(' ', '\t', '\r', '\n')[0];
                        string got;
                        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                            got = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
                        if (!string.Equals(expect, got, StringComparison.OrdinalIgnoreCase)) throw new Exception("The download was damaged (checksum mismatch).");
                    }
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(exe, old);
                    try { File.Move(fresh, exe); }
                    catch { File.Move(old, exe); throw; }            // gagal: kembalikan file lama
                }
                catch (UnauthorizedAccessException) { err = "SlideDock can't update itself in this folder."; }
                catch (Exception ex) { err = ex.Message; }
                if (err != null) { try { if (File.Exists(fresh)) File.Delete(fresh); } catch { } }
                ui.BeginInvoke(new Action(delegate
                {
                    Busy = false;
                    if (err == null) { restart(); return; }
                    if (Changed != null) Changed();
                    fail(err);
                }));
            });
        }

        // sisa update sebelumnya
        public static void Cleanup()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                if (File.Exists(exe + ".new")) File.Delete(exe + ".new");
            }
            catch { }
        }
    }

    // versi Microsoft Store: "Start with Windows" memakai StartupTask milik paket (bukan Task Scheduler)
    static class PackagedStartup
    {
        const string Id = "SlideDockStartup";

        public static bool On()
        {
            try
            {
                global::Windows.ApplicationModel.StartupTask st = Notifs.Wait(global::Windows.ApplicationModel.StartupTask.GetAsync(Id));
                return st.State == global::Windows.ApplicationModel.StartupTaskState.Enabled
                    || st.State == global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                global::Windows.ApplicationModel.StartupTask st = Notifs.Wait(global::Windows.ApplicationModel.StartupTask.GetAsync(Id));
                if (on) Notifs.Wait(st.RequestEnableAsync()); else st.Disable();
            }
            catch { }
        }
    }

    // pengaturan dock kecil-kecil (key=angka) di %APPDATA%\SlideDock\prefs.txt
    static class Prefs
    {
        static readonly string FileP = System.IO.Path.Combine(Store.Dir, "prefs.txt");
        static Dictionary<string, int> map;

        static void Load()
        {
            if (map != null) return;
            map = new Dictionary<string, int>();
            try
            {
                if (!File.Exists(FileP)) return;
                foreach (string l in File.ReadAllLines(FileP))
                {
                    int eq = l.IndexOf('=');
                    int v;
                    if (eq > 0 && int.TryParse(l.Substring(eq + 1).Trim(), out v)) map[l.Substring(0, eq).Trim()] = v;
                }
            }
            catch { }
        }

        public static int Get(string key, int def)
        {
            Load();
            int v;
            return map.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, int v)
        {
            Load();
            map[key] = v;
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.WriteAllLines(FileP, map.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
            catch { }
        }

        // 0 = mati, 1 = halus, 2 = kuat
        public static int Magnify { get { return Get("magnify", 2); } set { Set("magnify", value); } }
        public static bool Notch { get { return Get("notch", 1) != 0; } set { Set("notch", value ? 1 : 0); } }
        // 0 = otomatis (menurut ukuran layar), 1 = kecil, 2 = sedang, 3 = besar
        public static int DockSize { get { return Get("docksize", 0); } set { Set("docksize", value); } }
        public static bool DockMove { get { return Get("dockmove", 0) != 0; } set { Set("dockmove", value ? 1 : 0); } }
    }

    static class Launcher
    {
        public static void Open(string target)
        {
            try
            {
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start("explorer.exe", target);
                    return;
                }
                ProcessStartInfo psi = new ProcessStartInfo(target);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open:\n" + target + "\n\n" + ex.Message, "SlideDock");
            }
        }
    }

    // Daftar app: shortcut Desktop (langsung dari folder) + semua app terpasang (termasuk app Microsoft Store)
    static class Catalog
    {
        static List<AppEntry> all;
        static DateTime loadedAt = DateTime.MinValue;

        public static List<AppEntry> Desktop()
        {
            List<AppEntry> l = new List<AppEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in Store.DesktopDirs())
            {
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.GetFiles(d))
                {
                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                    string n = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (!seen.Add(n)) continue;
                    AppEntry e = new AppEntry();
                    e.Name = n;
                    e.Target = f;
                    e.Desktop = true;
                    l.Add(e);
                }
            }
            return l.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static List<AppEntry> All()
        {
            if (all == null) Load();
            else if ((DateTime.Now - loadedAt).TotalSeconds > 120) LoadAsync(null);   // segarkan di latar, pakai daftar lama dulu
            return all;
        }

        public static bool Ready { get { return all != null; } }

        // memuat daftar app di thread latar (STA, untuk COM Shell). done dipanggil di thread UI.
        static bool loading;
        static readonly List<Action> waiters = new List<Action>();
        public static void LoadAsync(Action done)
        {
            System.Windows.Threading.Dispatcher ui = Application.Current != null ? Application.Current.Dispatcher : null;
            lock (waiters)
            {
                if (done != null) waiters.Add(done);
                if (loading) return;
                loading = true;
            }
            Thread t = new Thread(delegate()
            {
                try { Load(); } catch { }
                List<Action> l;
                lock (waiters) { loading = false; l = new List<Action>(waiters); waiters.Clear(); }
                if (ui != null) foreach (Action a in l) ui.BeginInvoke(a);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        static void Load()
        {
            List<AppEntry> l = new List<AppEntry>();
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                object shell = Activator.CreateInstance(t);
                object folder = t.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
                object items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
                int count = (int)items.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    object it = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, new object[] { i });
                    if (it == null) continue;
                    string name = (string)it.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, it, null);
                    string path = (string)it.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, it, null);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;
                    if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name)) continue;
                    AppEntry e = new AppEntry();
                    e.Name = name;
                    e.Target = "shell:AppsFolder\\" + path;
                    l.Add(e);
                }
            }
            catch { }
            all = l.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            loadedAt = DateTime.Now;
        }
    }

    static class PinStore
    {
        static readonly string FileP = System.IO.Path.Combine(Store.Dir, "pins.txt");
        static List<PinItem> cache;
        static DateTime cacheStamp = DateTime.MinValue;

        public static List<PinItem> Load()
        {
            DateTime stamp = File.Exists(FileP) ? File.GetLastWriteTimeUtc(FileP) : DateTime.MinValue;
            if (cache != null && stamp == cacheStamp) return cache;
            cacheStamp = stamp;
            List<PinItem> l = new List<PinItem>();
            if (File.Exists(FileP))
            {
                foreach (string line in File.ReadAllLines(FileP))
                {
                    int i = line.IndexOf('|');
                    if (i <= 0) continue;
                    PinItem p = new PinItem();
                    p.Target = line.Substring(0, i);
                    p.Name = line.Substring(i + 1);
                    l.Add(p);
                }
            }
            else
            {
                l = Seed();
                Save(l);
            }
            cache = l;
            return l;
        }

        public static void Save(List<PinItem> l)
        {
            Directory.CreateDirectory(Store.Dir);
            File.WriteAllLines(FileP, l.Select(p => p.Target + "|" + p.Name).ToArray());
            cache = l;
            cacheStamp = File.GetLastWriteTimeUtc(FileP);
        }

        public static void Add(string target, string name)
        {
            List<PinItem> l = new List<PinItem>(Load());
            if (l.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase))) return;
            PinItem n = new PinItem();
            n.Target = target;
            n.Name = name;
            l.Add(n);
            Save(l);
        }

        // urutan baru dari dock (ikon pin digeser): pin yang tidak disebut tetap ikut, di belakang
        public static void Reorder(List<string> targets)
        {
            List<PinItem> l = Load();
            List<PinItem> n = new List<PinItem>();
            foreach (string t in targets)
            {
                PinItem p = l.FirstOrDefault(x => string.Equals(x.Target, t, StringComparison.OrdinalIgnoreCase));
                if (p != null && !n.Contains(p)) n.Add(p);
            }
            foreach (PinItem p in l) if (!n.Contains(p)) n.Add(p);
            if (n.SequenceEqual(l)) return;
            Save(n);
        }

        public static void Remove(string target)
        {
            Save(Load().Where(p => !string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        // Pin yang menunjuk ke file yang sudah dihapus ikut dibuang
        public static void Prune()
        {
            List<PinItem> l = Load();
            List<PinItem> keep = l.Where(p => p.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                                           || File.Exists(p.Target) || Directory.Exists(p.Target)).ToList();
            if (keep.Count != l.Count) Save(keep);
        }

        static List<PinItem> Seed()
        {
            List<PinItem> l = new List<PinItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string pd = Store.PinnedDir();
            if (Directory.Exists(pd))
            {
                foreach (string f in Directory.GetFiles(pd, "*.lnk"))
                {
                    PinItem p = new PinItem();
                    p.Target = f;
                    p.Name = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (seen.Add(p.Target)) l.Add(p);
                }
            }
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"))
                {
                    byte[] b = k == null ? null : k.GetValue("FavoritesResolve") as byte[];
                    if (b != null)
                    {
                        string s = Encoding.Unicode.GetString(b);
                        foreach (Match m in Regex.Matches(s, @"[A-Za-z0-9_.\-]+_[a-z0-9]{13}![A-Za-z0-9.]+"))
                        {
                            PinItem p = new PinItem();
                            p.Target = "shell:AppsFolder\\" + m.Value;
                            string nm = Icons.ShellName(p.Target);
                            p.Name = string.IsNullOrEmpty(nm) ? m.Value : nm;
                            if (seen.Add(p.Target)) l.Add(p);
                        }
                    }
                }
            }
            catch { }
            return l;
        }
    }

    // ================= native =================
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    delegate IntPtr LowLevelProc(int code, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        [PreserveSig] int Commit();
    }

    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        public const uint VK_SPACE = 0x20;

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelProc cb, IntPtr mod, uint tid);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("shell32.dll")] public static extern int SHGetSpecialFolderLocation(IntPtr h, int csidl, out IntPtr pidl);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attrs, ref SHFILEINFO fi, uint cb, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr h, string root, uint flags);

        // ----- identitas app (AUMID) + nama -----
        [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint len, StringBuilder sb);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        static readonly Dictionary<IntPtr, string> aumidCache = new Dictionary<IntPtr, string>();
        static readonly Dictionary<string, string> descCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<IntPtr, DateTime> aumidMiss = new Dictionary<IntPtr, DateTime>();
        static readonly Dictionary<long, string> exeCache = new Dictionary<long, string>();

        public static string AumidOf(IntPtr h, uint pid)
        {
            string a;
            if (aumidCache.TryGetValue(h, out a)) return a;
            DateTime miss;
            if (aumidMiss.TryGetValue(h, out miss) && (DateTime.Now - miss).TotalSeconds < 10) return null;   // jangan tanya ulang tiap detik
            a = WindowAumid(h);
            if (string.IsNullOrEmpty(a)) a = ProcessAumid(pid);
            if (string.IsNullOrEmpty(a))
            {
                if (aumidMiss.Count > 500) aumidMiss.Clear();
                aumidMiss[h] = DateTime.Now;
                return null;
            }
            if (aumidCache.Count > 500) aumidCache.Clear();
            aumidCache[h] = a;
            return a;
        }

        // lokasi program per proses tidak berubah selama proses hidup: simpan sementara
        public static string ExeOfCached(uint pid, IntPtr h)
        {
            long k = ((long)pid << 32) ^ h.ToInt64();
            string e;
            if (exeCache.TryGetValue(k, out e)) return e;
            e = ExeOf(pid);
            if (exeCache.Count > 800) exeCache.Clear();
            exeCache[k] = e;
            return e;
        }

        static string WindowAumid(IntPtr h)
        {
            try
            {
                Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                IPropertyStore st;
                if (SHGetPropertyStoreForWindow(h, ref iid, out st) != 0 || st == null) return null;
                try
                {
                    PROPERTYKEY k = new PROPERTYKEY();
                    k.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                    k.pid = 5;
                    PROPVARIANT v;
                    if (st.GetValue(ref k, out v) != 0) return null;
                    string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null;
                    PropVariantClear(ref v);
                    return s;
                }
                finally { Marshal.ReleaseComObject(st); }
            }
            catch { return null; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        static readonly Dictionary<string, string> lnkAumidCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ID app yang tersimpan di file shortcut (.lnk)
        public static string LnkAumid(string path)
        {
            string a;
            if (lnkAumidCache.TryGetValue(path, out a)) return a;
            a = null;
            try
            {
                Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                IPropertyStore st;
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out st) == 0 && st != null)
                {
                    try
                    {
                        PROPERTYKEY k = new PROPERTYKEY();
                        k.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                        k.pid = 5;
                        PROPVARIANT v;
                        if (st.GetValue(ref k, out v) == 0)
                        {
                            if (v.vt == 31) a = Marshal.PtrToStringUni(v.p);
                            PropVariantClear(ref v);
                        }
                    }
                    finally { Marshal.ReleaseComObject(st); }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(a)) a = null;
            lnkAumidCache[path] = a;
            return a;
        }

        static string ProcessAumid(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                uint len = 512;
                StringBuilder sb = new StringBuilder((int)len);
                return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        // ID app dari daftar "Semua app" kadang berupa path: "{GUID-folder}\Vendor\app.exe"
        public static string ExpandAppPath(string id)
        {
            try
            {
                Match m = Regex.Match(id, @"^\{([0-9A-Fa-f\-]{36})\}\\(.+)$");
                if (m.Success)
                {
                    Guid g = new Guid(m.Groups[1].Value);
                    IntPtr p;
                    if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) == 0)
                    {
                        string root = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        string full = System.IO.Path.Combine(root, m.Groups[2].Value);
                        if (File.Exists(full)) return full;
                    }
                    return null;
                }
                if (id.Length > 3 && id[1] == ':' && File.Exists(id)) return id;
            }
            catch { }
            return null;
        }

        public static string DescOf(string exe)
        {
            if (exe == null) return null;
            string d;
            lock (descCache) { if (descCache.TryGetValue(exe, out d)) return d; }
            try
            {
                FileVersionInfo fi = FileVersionInfo.GetVersionInfo(exe);
                d = !string.IsNullOrEmpty(fi.FileDescription) ? fi.FileDescription : fi.ProductName;
            }
            catch { d = null; }
            if (string.IsNullOrEmpty(d)) d = System.IO.Path.GetFileNameWithoutExtension(exe);
            lock (descCache) descCache[exe] = d;
            return d;
        }

        public static string ExeOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int n = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref n)) return sb.ToString();
            }
            finally { CloseHandle(h); }
            return null;
        }

        public static List<WinInfo> Windows(uint ownPid)
        {
            List<WinInfo> res = new List<WinInfo>();
            EnumWindowsProc cb = delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, 4) != IntPtr.Zero) return true;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                if ((ex & WS_EX_NOACTIVATE) != 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                StringBuilder sb = new StringBuilder(len + 1);
                GetWindowText(h, sb, sb.Capacity);
                StringBuilder cs = new StringBuilder(256);
                GetClassName(h, cs, 256);
                string cls = cs.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == ownPid) return true;
                WinInfo w = new WinInfo();
                w.H = h; w.Title = sb.ToString(); w.Pid = pid; w.Cls = cls; w.Exe = ExeOfCached(pid, h); w.Aumid = AumidOf(h, pid);
                if (w.Aumid == null && w.Exe != null && w.Exe.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase))
                    w.Aumid = "Microsoft.Windows.Explorer";
                res.Add(w);
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            return res;
        }
    }

    // ================= shortcut + ikon =================
    static class ShellUtil
    {
        static readonly Dictionary<string, string[]> cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public static string[] ResolveLnk(string path)
        {
            string[] r;
            if (cache.TryGetValue(path, out r)) return r;
            string target = null, icon = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { path });
                target = (string)lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null);
                string il = (string)lnk.GetType().InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, lnk, null);
                if (!string.IsNullOrEmpty(il))
                {
                    string ip = Environment.ExpandEnvironmentVariables(il.Split(',')[0].Trim('"'));
                    if (ip.Length > 0 && File.Exists(ip)) icon = ip;
                }
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) target = null;
            }
            catch { }
            r = new string[] { target, icon };
            cache[path] = r;
            return r;
        }
    }

    static class Icons
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        static ImageSource Extract(string p)
        {
            try
            {
                using (DR.Icon ic = DR.Icon.ExtractAssociatedIcon(p))
                {
                    if (ic == null) return null;
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
            }
            catch { return null; }
        }

        public static ImageSource For(string target)
        {
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return FromShell(target);
            return FromPath(target);
        }

        public static ImageSource FromPath(string path)
        {
            ImageSource c;
            if (cache.TryGetValue(path, out c)) return c;
            string src = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string[] r = ShellUtil.ResolveLnk(path);
                if (r[1] != null) src = r[1];
                else if (r[0] != null) src = r[0];
            }
            ImageSource img = Extract(src);
            if (img == null && src != path) img = Extract(path);
            cache[path] = img;
            return img;
        }

        static ImageSource ShellIcon(string shellPath, out string name)
        {
            name = null;
            try
            {
                IntPtr pidl;
                uint o;
                if (Native.SHParseDisplayName(shellPath, IntPtr.Zero, out pidl, 0, out o) != 0) return null;
                try
                {
                    SHFILEINFO fi = new SHFILEINFO();
                    Native.SHGetFileInfo(pidl, 0, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 0x8 | 0x100 | 0x200);
                    name = fi.szDisplayName;
                    if (fi.hIcon == IntPtr.Zero) return null;
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    Native.DestroyIcon(fi.hIcon);
                    bs.Freeze();
                    return bs;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            catch { return null; }
        }

        public static ImageSource FromShell(string shellPath)
        {
            ImageSource c;
            if (cache.TryGetValue(shellPath, out c)) return c;
            string n;
            ImageSource r = ShellIcon(shellPath, out n);
            cache[shellPath] = r;
            return r;
        }

        static readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ShellName(string shellPath)
        {
            string n;
            if (nameCache.TryGetValue(shellPath, out n)) return n;
            ShellIcon(shellPath, out n);
            nameCache[shellPath] = n;
            return n;
        }

        public static ImageSource FromWindow(IntPtr h)
        {
            try
            {
                IntPtr r;
                Native.SendMessageTimeout(h, 0x7F, (IntPtr)1, IntPtr.Zero, 2, 60, out r);
                if (r == IntPtr.Zero) Native.SendMessageTimeout(h, 0x7F, (IntPtr)2, IntPtr.Zero, 2, 60, out r);
                if (r == IntPtr.Zero) r = Native.GetClassLongPtr(h, -14);
                if (r == IntPtr.Zero) return null;
                BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(r, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            catch { return null; }
        }

        public static ImageSource RecycleBin()
        {
            try
            {
                IntPtr pidl;
                if (Native.SHGetSpecialFolderLocation(IntPtr.Zero, 0x0A, out pidl) != 0) return null;
                try
                {
                    SHFILEINFO fi = new SHFILEINFO();
                    Native.SHGetFileInfo(pidl, 0, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 0x8 | 0x100);
                    if (fi.hIcon == IntPtr.Zero) return null;
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    Native.DestroyIcon(fi.hIcon);
                    bs.Freeze();
                    return bs;
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            catch { return null; }
        }
    }

    // ================= tombol Windows + kirim pintasan =================
    static class WinKeyHook
    {
        public static Action OnWinTap;
        public static Func<bool> Enabled;
        public static bool Active = true;
        static IntPtr hook = IntPtr.Zero;
        static LowLevelProc proc;
        static bool winHeld, comboUsed;
        static uint winVk;
        static int winDownAt;
        static readonly IntPtr Marker = (IntPtr)0x53444B31;

        public static void Install()
        {
            if (hook != IntPtr.Zero) return;
            proc = Callback;
            hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(null), 0);
        }

        public static void Uninstall()
        {
            if (hook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        static INPUT Key(ushort vk, bool up)
        {
            INPUT i = new INPUT();
            i.type = 1;
            i.u.ki.wVk = vk;
            i.u.ki.dwFlags = up ? 2u : 0u;
            i.u.ki.dwExtraInfo = Marker;
            return i;
        }

        static void InjectWinDown(uint vk)
        {
            INPUT[] a = new INPUT[] { Key((ushort)vk, false) };
            Native.SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
        }

        // Kirim Win+<huruf> (mis. Win+S = Search, Win+A = Quick Settings, Win+N = notifikasi)
        public static void SendWinCombo(ushort vk)
        {
            INPUT[] a = new INPUT[] { Key(0x5B, false), Key(vk, false), Key(vk, true), Key(0x5B, true) };
            Native.SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT)));
        }

        // Win sendirian = buka dock. Win + tombol lain tetap bekerja normal.
        static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Active && (Enabled == null || Enabled()))
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool ours = (k.flags & 0x10) != 0 && k.dwExtraInfo == Marker;
                if (!ours)
                {
                    int w = wParam.ToInt32();
                    bool down = w == 0x100 || w == 0x104;
                    bool up = w == 0x101 || w == 0x105;
                    bool isWin = k.vkCode == 0x5B || k.vkCode == 0x5C;
                    if (winHeld && !isWin && Environment.TickCount - winDownAt > 5000) winHeld = false;
                    if (isWin)
                    {
                        if (down)
                        {
                            if (!winHeld) { winHeld = true; comboUsed = false; winVk = k.vkCode; winDownAt = Environment.TickCount; }
                            return (IntPtr)1;
                        }
                        if (up && winHeld)
                        {
                            winHeld = false;
                            if (!comboUsed)
                            {
                                Action a = OnWinTap;
                                if (a != null) Application.Current.Dispatcher.BeginInvoke((Delegate)a);
                                return (IntPtr)1;
                            }
                        }
                    }
                    else if (winHeld && down && !comboUsed)
                    {
                        comboUsed = true;
                        InjectWinDown(winVk);
                    }
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
    }

    // ================= menu klik kanan: tutup saat klik di mana pun di luar menu =================
    static class MenuCloser
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

        static IntPtr hook = IntPtr.Zero;
        static LowLevelProc proc;
        static readonly List<ContextMenu> open = new List<ContextMenu>();

        public static void Install()
        {
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(delegate(object s, RoutedEventArgs e)
            {
                ContextMenu m = s as ContextMenu;
                if (m != null && !open.Contains(m)) open.Add(m);
            }));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(delegate(object s, RoutedEventArgs e)
            {
                open.Remove(s as ContextMenu);
            }));
            proc = Callback;
            hook = Native.SetWindowsHookEx(14, proc, Native.GetModuleHandle(null), 0);   // WH_MOUSE_LL
        }

        public static void CloseAll()
        {
            foreach (ContextMenu cm in open.ToArray()) cm.IsOpen = false;
        }

        public static void Uninstall()
        {
            if (hook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        static IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && open.Count > 0)
            {
                int m = w.ToInt32();
                if (m == 0x201 || m == 0x204 || m == 0x207)   // klik kiri / kanan / tengah
                {
                    MSLLHOOKSTRUCT s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                    int x = s.x, y = s.y;
                    Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (ContextMenu cm in open.ToArray())
                            if (!Inside(cm, x, y)) cm.IsOpen = false;
                    }));
                }
            }
            return Native.CallNextHookEx(hook, code, w, l);
        }

        static bool Inside(ContextMenu cm, int x, int y)
        {
            HwndSource src = PresentationSource.FromVisual(cm) as HwndSource;
            if (src == null) return false;
            RECT r;
            if (!GetWindowRect(src.Handle, out r)) return false;
            return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
        }
    }

    // ================= taskbar Windows: disembunyikan total =================
    static class TaskbarCtl
    {
        public static bool HideEnabled = true;
        static int prev = -1;

        static List<IntPtr> Trays()
        {
            List<IntPtr> l = new List<IntPtr>();
            IntPtr t = Native.FindWindow("Shell_TrayWnd", null);
            if (t != IntPtr.Zero) l.Add(t);
            IntPtr s = IntPtr.Zero;
            while ((s = Native.FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) l.Add(s);
            return l;
        }

        static void SetAutoHide(bool on)
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = tray;
            if (prev < 0)
            {
                // pengaturan asli taskbar disimpan sekali ke file: auto-hide sengaja dibiarkan aktif saat Windows
                // dimatikan, jadi keadaan "sekarang" belum tentu pengaturan asli Anda
                prev = LoadOriginal();
                if (prev < 0) { prev = (int)Native.SHAppBarMessage(4, ref d); SaveOriginal(prev); }
            }
            d.lParam = (IntPtr)(on ? (prev | 1) : prev);
            Native.SHAppBarMessage(0xA, ref d);
        }

        static readonly string OrigFile = System.IO.Path.Combine(Store.Dir, "taskbar-original.txt");
        static int LoadOriginal()
        {
            try { int v; if (File.Exists(OrigFile) && int.TryParse(File.ReadAllText(OrigFile).Trim(), out v)) return v; } catch { }
            return -1;
        }
        static void SaveOriginal(int v)
        {
            try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(OrigFile, v.ToString()); } catch { }
        }

        // Windows dimatikan / restart / sign out: biarkan auto-hide aktif agar saat login berikutnya taskbar lama
        // sudah tersembunyi sebelum SlideDock berjalan. Hanya jendela taskbar yang ditampilkan lagi.
        public static void KeepHiddenForNextLogin()
        {
            if (Releasing) return;
            SetAutoHide(true);
        }

        // SlideDock dilepas untuk seterusnya (uninstall, atau Windows menutup app untuk uninstall/update paket):
        // jangan sisakan auto-hide untuk login berikutnya
        public static bool Releasing;

        // kembalikan taskbar Windows seperti sebelum SlideDock dipakai: semua taskbar tampil, auto-hide &
        // pengaturan lain sesuai aslinya (tanpa catatan asli: auto-hide dimatikan, bawaan Windows)
        public static void RestoreDefaults()
        {
            Releasing = true;
            HideEnabled = false;
            ShowAll();
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                APPBARDATA d = new APPBARDATA();
                d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                d.hWnd = tray;
                int orig = prev >= 0 ? prev : LoadOriginal();
                if (orig < 0) orig = (int)Native.SHAppBarMessage(4, ref d) & ~1;
                d.lParam = (IntPtr)orig;
                Native.SHAppBarMessage(0xA, ref d);
            }
            try { if (File.Exists(OrigFile)) File.Delete(OrigFile); } catch { }
        }

        public static void Apply()
        {
            if (Releasing) return;
            SetAutoHide(true);
            if (HideEnabled) foreach (IntPtr t in Trays()) Native.ShowWindow(t, 0);
        }

        public static void ShowAll()
        {
            foreach (IntPtr t in Trays()) Native.ShowWindow(t, 5);
        }

        static int lastAutoHideCheck;

        public static void Enforce()
        {
            if (!HideEnabled) return;
            // bila auto-hide terlepas (mis. Explorer dimulai ulang), Windows menyisakan ruang taskbar: pasang lagi
            if (Environment.TickCount - lastAutoHideCheck > 2000)
            {
                lastAutoHideCheck = Environment.TickCount;
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero)
                {
                    APPBARDATA d = new APPBARDATA();
                    d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                    d.hWnd = tray;
                    int st = (int)Native.SHAppBarMessage(4, ref d);
                    if ((st & 1) == 0) SetAutoHide(true);
                }
            }
            foreach (IntPtr t in Trays()) if (Native.IsWindowVisible(t)) Native.ShowWindow(t, 0);
        }

        public static void Restore()
        {
            ShowAll();
            if (prev < 0) return;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = tray;
            d.lParam = (IntPtr)prev;
            Native.SHAppBarMessage(0xA, ref d);
        }
    }

    // ================= posisi dock: bawah / kiri / kanan =================
    static class Layout
    {
        public static int Side;   // 0 = bawah, 1 = kiri, 2 = kanan
        public static bool V { get { return Side != 0; } }
        static readonly string FileP = System.IO.Path.Combine(Store.Dir, "position.txt");

        // titik tumpu animasi: tepi yang menempel ke layar
        public static Point Origin { get { return Side == 1 ? new Point(0, 0.5) : Side == 2 ? new Point(1, 0.5) : new Point(0.5, 1); } }

        public static void Load()
        {
            try
            {
                int v;
                if (File.Exists(FileP) && int.TryParse(File.ReadAllText(FileP).Trim(), out v) && v >= 0 && v <= 2) Side = v;
            }
            catch { }
        }

        public static void Save(int v)
        {
            Side = v;
            try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(FileP, v.ToString()); } catch { }
        }
    }

    // ================= gaya bersama =================
    static class UI
    {
        public static readonly FontFamily Fluent = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily Text = new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");
        public static string Theme = "glass";

        public static Border Card(double radius, double margin)
        {
            Border card = new Border();
            card.Margin = new Thickness(margin);
            card.CornerRadius = new CornerRadius(radius);
            card.Background = new SolidColorBrush(Color.FromArgb(0xF8, 0x1B, 0x1B, 0x21));
            card.BorderBrush = UI.Ink(0x40);
            card.BorderThickness = new Thickness(1);
            card.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black };
            return card;
        }

        public static TextBlock Glyph(string code, double size)
        {
            TextBlock t = new TextBlock();
            t.Text = code;
            t.FontFamily = Fluent;
            t.FontSize = size;
            t.Foreground = UI.Fg;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }

        // gaya menu klik kanan untuk seluruh app: gelap, sudut membulat
        public static void ApplyMenuStyle(Application app)
        {
            string ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
            bool lt = Light;
            string mBg = lt ? "#FAF8F8FA" : "#FA1E1E24", mEdge = lt ? "#26000000" : "#40FFFFFF", mFg = lt ? "#16161C" : "White";
            string mHi = lt ? "#16000000" : "#30FFFFFF", tBg = lt ? "#F8FAFAFC" : "#F2202026", sh = lt ? "0.22" : "0.5";
            string th1 = lt ? "#40000000" : "#50FFFFFF", th2 = lt ? "#70000000" : "#90FFFFFF", th3 = lt ? "#90000000" : "#B0FFFFFF";
            string menuXaml =
                "<Style " + ns + " TargetType='ContextMenu'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='HasDropShadow' Value='False'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>" +
                "<Border Background='" + mBg + "' BorderBrush='" + mEdge + "' BorderThickness='1' CornerRadius='12' Padding='5' Margin='10'>" +
                "<Border.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='3' Opacity='" + sh + "'/></Border.Effect>" +
                "<StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>" +
                "</Border></ControlTemplate></Setter.Value></Setter></Style>";
            string itemXaml =
                "<Style " + ns + " TargetType='MenuItem'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='Foreground' Value='" + mFg + "'/>" +
                "<Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI Variable, Segoe UI'/>" +
                "<Setter Property='FontSize' Value='13'/>" +
                "<Setter Property='Cursor' Value='Hand'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>" +
                "<Border x:Name='bd' Background='Transparent' CornerRadius='8' Padding='12,8,22,8' MinWidth='170'>" +
                "<ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'>" +
                "<Setter TargetName='bd' Property='Background' Value='" + mHi + "'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></Setter.Value></Setter></Style>";
            // tooltip gelap membulat, senada dengan dock (bukan kotak putih bawaan Windows)
            string tipXaml =
                "<Style " + ns + " TargetType='ToolTip'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='HasDropShadow' Value='False'/>" +
                "<Setter Property='Foreground' Value='" + mFg + "'/>" +
                "<Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI Variable, Segoe UI'/>" +
                "<Setter Property='FontSize' Value='12'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'>" +
                "<Border Background='" + tBg + "' BorderBrush='" + mEdge + "' BorderThickness='1' CornerRadius='8' Padding='10,5,10,6' Margin='8'>" +
                "<Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='2' Opacity='" + sh + "'/></Border.Effect>" +
                "<ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>";
            // scrollbar tipis & gelap (bukan scrollbar putih bawaan Windows)
            string thumb = "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Border x:Name='t' CornerRadius='3' Background='" + th1 + "'/>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='t' Property='Background' Value='" + th2 + "'/></Trigger>" +
                "<Trigger Property='IsDragging' Value='True'><Setter TargetName='t' Property='Background' Value='" + th3 + "'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>";
            string barXaml =
                "<Style " + ns + " TargetType='ScrollBar'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='Background' Value='Transparent'/>" +
                "<Setter Property='Width' Value='8'/><Setter Property='MinWidth' Value='8'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
                "<Border Background='Transparent' Padding='2,2,1,2'><Track x:Name='PART_Track' IsDirectionReversed='True'>" + thumb + "</Track></Border>" +
                "</ControlTemplate></Setter.Value></Setter>" +
                "<Style.Triggers><Trigger Property='Orientation' Value='Horizontal'>" +
                "<Setter Property='Width' Value='Auto'/><Setter Property='MinWidth' Value='0'/><Setter Property='Height' Value='8'/><Setter Property='MinHeight' Value='8'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
                "<Border Background='Transparent' Padding='2,2,2,1'><Track x:Name='PART_Track' IsDirectionReversed='False'>" + thumb + "</Track></Border>" +
                "</ControlTemplate></Setter.Value></Setter></Trigger></Style.Triggers></Style>";
            try { app.Resources[typeof(ScrollBar)] = System.Windows.Markup.XamlReader.Parse(barXaml); } catch { }
            try
            {
                app.Resources[typeof(ContextMenu)] = System.Windows.Markup.XamlReader.Parse(menuXaml);
                app.Resources[typeof(MenuItem)] = System.Windows.Markup.XamlReader.Parse(itemXaml);
                app.Resources[typeof(ToolTip)] = System.Windows.Markup.XamlReader.Parse(tipXaml);
            }
            catch { }
        }

        // ---- tema: "glass" (Glass 1, kaca gelap), "glass2" (Glass 2, kaca putih, teks hitam), "dark" ----
        public static bool Light { get { return Theme == "glass2"; } }
        static bool themeApplied;

        // tema tersimpan; "aurora" (versi lama) menjadi Glass 2
        public static string NormTheme(string t)
        {
            t = (t ?? "").Trim();
            if (t == "aurora") return "glass2";
            return t == "dark" || t == "glass2" ? t : "glass";
        }

        public static string SavedTheme()
        {
            try
            {
                string f = System.IO.Path.Combine(Store.Dir, "theme.txt");
                if (File.Exists(f)) return NormTheme(File.ReadAllText(f));
            }
            catch { }
            return "glass";
        }

        // warna teks & garis yang ikut tema: putih di Glass 1 / Dark, hitam di Glass 2.
        // Satu brush per tingkat transparansi dipakai bersama, jadi ganti tema langsung mengubah semua yang sudah tampil.
        static readonly Dictionary<byte, SolidColorBrush> inks = new Dictionary<byte, SolidColorBrush>();
        public static Color InkColor(byte a) { return Light ? Color.FromArgb(a, 0x16, 0x16, 0x1C) : Color.FromArgb(a, 255, 255, 255); }
        public static SolidColorBrush Ink(byte a)
        {
            SolidColorBrush b;
            if (!inks.TryGetValue(a, out b)) { b = new SolidColorBrush(InkColor(a)); inks[a] = b; }
            return b;
        }
        public static SolidColorBrush Fg { get { return Ink(0xFF); } }

        // teks berwarna biru: biru terang di latar gelap, biru tua di latar putih
        static SolidColorBrush accentInk;
        static Color AccentInkColor() { return Light ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF); }
        public static SolidColorBrush AccentInk { get { if (accentInk == null) accentInk = new SolidColorBrush(AccentInkColor()); return accentInk; } }

        public static void SetTheme(string t)
        {
            t = NormTheme(t);
            if (themeApplied && t == Theme) return;
            themeApplied = true;
            Theme = t;
            foreach (KeyValuePair<byte, SolidColorBrush> kv in inks) kv.Value.Color = InkColor(kv.Key);
            if (accentInk != null) accentInk.Color = AccentInkColor();
            if (Application.Current != null) ApplyMenuStyle(Application.Current);
        }

        // tooltip dock: muncul tepat di atas ikon (atau di sampingnya untuk dock kiri/kanan), bukan di bawah kursor
        public static ToolTip Tip(FrameworkElement el, object content)
        {
            ToolTip tt = new ToolTip();
            tt.Content = content;
            tt.Placement = PlacementMode.Custom;
            tt.CustomPopupPlacementCallback = DockTipPlacement;
            el.ToolTip = tt;
            ToolTipService.SetInitialShowDelay(el, 300);
            return tt;
        }

        static CustomPopupPlacement[] DockTipPlacement(Size popup, Size target, Point offset)
        {
            const double g = 2;      // tooltip punya margin 8 untuk bayangan: jarak tampak ~6
            Point p = Layout.Side == 1 ? new Point(target.Width - g, (target.Height - popup.Height) / 2)
                    : Layout.Side == 2 ? new Point(-popup.Width + g, (target.Height - popup.Height) / 2)
                    : new Point((target.Width - popup.Width) / 2, -popup.Height + g);
            return new CustomPopupPlacement[] { new CustomPopupPlacement(p, PopupPrimaryAxis.None) };
        }

        [StructLayout(LayoutKind.Sequential)] struct MPOINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(MPOINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);

        // skala tampilan monitor (1.25 = 125%)
        public static double ScaleOf(WF.Screen sc)
        {
            try
            {
                MPOINT p;
                p.X = sc.Bounds.Left + sc.Bounds.Width / 2;
                p.Y = sc.Bounds.Top + sc.Bounds.Height / 2;
                uint dx, dy;
                if (GetDpiForMonitor(MonitorFromPoint(p, 2), 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch { }
            return 1;
        }

        public static void Hover(Border b, byte alpha)
        {
            b.MouseEnter += delegate { b.Background = UI.Ink(alpha); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
        }
    }

    // baterai sebagai cincin: lingkaran putih terisi sesuai persen, angka di tengah.
    // Petir biru saat mengisi daya, merah saat 20% ke bawah (dan tidak mengisi).
    class BatteryRing : Grid
    {
        readonly System.Windows.Shapes.Ellipse track = new System.Windows.Shapes.Ellipse();
        readonly System.Windows.Shapes.Path arc = new System.Windows.Shapes.Path();
        readonly TextBlock num = new TextBlock();
        readonly TextBlock bolt;
        readonly double d, th;
        int lastPct = -1;
        bool lastCharging;

        public BatteryRing(double size)
        {
            d = size;
            th = Math.Max(2, Math.Round(size * 0.11 * 2) / 2);
            Width = d;
            Height = d;
            track.Stroke = UI.Ink(0x40);
            track.StrokeThickness = th;
            arc.StrokeThickness = th;
            arc.StrokeStartLineCap = PenLineCap.Round;
            arc.StrokeEndLineCap = PenLineCap.Round;
            num.Foreground = UI.Fg;
            num.FontFamily = UI.Text;
            num.FontWeight = FontWeights.SemiBold;
            num.HorizontalAlignment = HorizontalAlignment.Center;
            num.VerticalAlignment = VerticalAlignment.Center;
            num.TextAlignment = TextAlignment.Center;
            num.Margin = new Thickness(0, 0, 0, size * 0.03);
            // penanda mengisi daya: petir biru di pojok kanan bawah cincin (bayangan tipis agar tetap jelas di atas garis cincin)
            double bs = Math.Max(10, Math.Min(17, Math.Round(size * 0.48)));
            bolt = UI.Glyph("\uE945", bs);
            bolt.Foreground = new SolidColorBrush(Blue);
            bolt.HorizontalAlignment = HorizontalAlignment.Right;
            bolt.VerticalAlignment = VerticalAlignment.Bottom;
            bolt.Margin = new Thickness(0, 0, -bs * 0.38, -bs * 0.2);
            bolt.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9, Color = Color.FromRgb(0x10, 0x10, 0x16) };
            bolt.Visibility = Visibility.Collapsed;
            Children.Add(track);
            Children.Add(arc);
            Children.Add(num);
            Children.Add(bolt);
            Set(100, false);
        }

        public static readonly Color Blue = Color.FromRgb(0x60, 0xCD, 0xFF);
        public static readonly Color Red = Color.FromRgb(0xFF, 0x60, 0x5C);

        public void Set(int pct, bool charging)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            if (pct == lastPct && charging == lastCharging) return;
            lastPct = pct;
            lastCharging = charging;
            num.Text = pct.ToString();
            bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            num.FontSize = d * (pct >= 100 ? 0.33 : 0.4);
            arc.Stroke = !charging && pct <= 20 ? (Brush)new SolidColorBrush(Red) : UI.Fg;
            double r = (d - th) / 2, c = d / 2;
            if (pct >= 100) { arc.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
            if (pct <= 0) { arc.Data = null; return; }
            double a = 2 * Math.PI * pct / 100.0;
            PathFigure f = new PathFigure();
            f.StartPoint = new Point(c, c - r);
            f.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));
            PathGeometry g = new PathGeometry();
            g.Figures.Add(f);
            arc.Data = g;
        }
    }

    // jam animasi: waktu bertambah sesuai frame, tetapi satu frame yang terlambat (sistem sibuk) dihitung
    // paling lama ~2 frame. Hasilnya animasi tidak pernah "melompat" — paling-paling sedikit lebih lambat.
    class AnimClock
    {
        readonly Stopwatch sw = Stopwatch.StartNew();
        double last, t;
        public double Tick()
        {
            double now = sw.Elapsed.TotalMilliseconds;
            double dt = now - last;
            last = now;
            t += Math.Min(dt, 34);
            return t;
        }
    }

    // ================= jendela popup (panel Start & menu daya) =================
    class Popover : Window
    {
        const double SlideDistance = 30;
        bool closing;
        int openGen;
        DateTime shownAt = DateTime.MinValue;
        double anchorBottom = -1;
        public static double SideGap = 80;
        protected bool FixedSize;          // ukuran tidak berubah setelah tampil: cukup tangkap area di belakangnya saja

        // dock di kiri/kanan: popup muncul di samping dock, tegak di tengah layar
        // posisi dock di layar (DIP). Dock bisa digeser, jadi popup mengikuti dock, bukan tengah layar.
        public static Rect DockSpan = Rect.Empty;
        // dock kiri/kanan: titik tengah tegak yang diinginkan (mis. ikon/jam yang diklik); NaN = tengah dock
        protected double SideAnchor = double.NaN;

        // dock di bawah: posisi kiri popup agar tengahnya sejajar dengan cx (NaN = tengah dock), tetap di layar
        protected static double AlongX(double w, double cx)
        {
            if (double.IsNaN(cx)) cx = DockSpan.IsEmpty ? Scr.Left + Scr.Width / 2 : DockSpan.Left + DockSpan.Width / 2;
            return Math.Max(Scr.Left + 8, Math.Min(Scr.Right - 8 - w, cx - w / 2));
        }

        Point SidePoint(Size s)
        {
            double c = !double.IsNaN(SideAnchor) ? SideAnchor : DockSpan.IsEmpty ? Scr.Top + Scr.Height / 2 : DockSpan.Top + DockSpan.Height / 2;
            double y = Math.Max(Scr.Top + 8, Math.Min(Scr.Bottom - 8 - s.Height, c - s.Height / 2));
            double x = Layout.Side == 1 ? Scr.Left + SideGap : Scr.Right - SideGap - s.Width;
            return new Point(x, y);
        }
        protected List<Border> Cards;
        public static Rect Scr = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        double anchorRight = -1;
        protected bool AnchorRight;
        protected bool AnchorCenter;
        double anchorCenter = -1;
        public DateTime HiddenAt = DateTime.MinValue;

        public bool IsOpen { get { return IsVisible && !closing; } }
        public bool JustHidden { get { return (DateTime.Now - HiddenAt).TotalMilliseconds < 400; } }

        public Popover()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Deactivated += delegate
            {
                if (IsOpen && (DateTime.Now - shownAt).TotalMilliseconds > 300) HidePanel();
            };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; }
            };
            SizeChanged += delegate
            {
                if (!IsOpen || anchorBottom < 0) return;
                if (slidePending)
                {
                    // ukuran berubah sebelum animasi buka dimulai (mis. isi baru dimuat saat dibuka ulang):
                    // perbarui tujuan animasi, jangan meluncur ke posisi lama yang dihitung dari ukuran lama
                    Point np = Layout.V ? SidePoint(new Size(ActualWidth, ActualHeight)) : AnchoredPoint(slideTarget.X);
                    slideTarget = np;
                    Left = np.X;
                    Top = np.Y;
                    UpdateLayout();
                    ApplySurface(false);
                    Point f = SlideOffset(np, SlideDistance);
                    MoveNow(f.X, f.Y);
                    return;
                }
                BeginAnimation(Window.TopProperty, null);
                ++glideGen;                   // ukuran berubah di tengah animasi: langsung ke posisi akhir
                if (Layout.V)
                {
                    Point sp2 = SidePoint(new Size(ActualWidth, ActualHeight));
                    Left = sp2.X;
                    Top = sp2.Y;
                    curPos = sp2;
                    UpdateLayout();
                    ApplySurface(false);
                    return;
                }
                PlaceAnchored();
            };
        }

        Point AnchoredPoint(double left)
        {
            if (anchorRight > 0) left = Math.Max(0, anchorRight - ActualWidth);
            if (anchorCenter > 0) left = Math.Max(Scr.Left + 8, Math.Min(anchorCenter - ActualWidth / 2, Scr.Right - 8 - ActualWidth));
            return new Point(left, anchorBottom - ActualHeight);
        }

        bool slidePending;                 // popup tampil, animasi buka belum mulai
        Point slideTarget;

        void PlaceAnchored()
        {
            Top = anchorBottom - ActualHeight;
            if (anchorRight > 0) Left = Math.Max(0, anchorRight - ActualWidth);
            if (anchorCenter > 0) Left = Math.Max(Scr.Left + 8, Math.Min(anchorCenter - ActualWidth / 2, Scr.Right - 8 - ActualWidth));
            curPos = new Point(Left, Top);
            if (slidePending) slideTarget = curPos;
            UpdateLayout();
            ApplySurface(false);
        }

        // popup yang sedang terbuka pindah ke tengah titik lain (mis. ikon dock lain yang diklik)
        protected void Reanchor(Point center)
        {
            if (!IsOpen) return;
            BeginAnimation(Window.TopProperty, null);
            ++glideGen;
            SideAnchor = center.Y;
            if (Layout.V)
            {
                Point sp = SidePoint(new Size(ActualWidth, ActualHeight));
                Left = sp.X;
                Top = sp.Y;
                curPos = sp;
                if (slidePending) slideTarget = sp;
                UpdateLayout();
                ApplySurface(false);
                return;
            }
            if (anchorCenter < 0) return;
            anchorCenter = center.X;
            PlaceAnchored();
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint op);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint op);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);

        // tema Glass: latar di belakang popup diambil, diperkecil, lalu diburamkan (kaca buram)
        BitmapSource capImg;
        int capX, capY, capW, capH;

        void ApplySurface() { ApplySurface(true); }

        void ApplySurface(bool recapture)
        {
            List<Border> cards = Cards;
            if (cards == null)
            {
                Border c0 = Content as Border;
                if (c0 == null) return;
                cards = new List<Border> { c0 };
            }
            bool dark = UI.Theme == "dark";
            bool light = UI.Light;
            try
            {
                PresentationSource ps = PresentationSource.FromVisual(this);
                double s = ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1;
                Point wtl = PointToScreen(new Point(0, 0));
                int ww = (int)(ActualWidth * s), wh = (int)(ActualHeight * s);
                if (ww <= 0 || wh <= 0) return;
                if (recapture || capImg == null)
                {
                    // area tangkapan lebih luas dari jendela agar tetap pas bila popup melebar/meninggi
                    int pad = 40, extraTop = FixedSize ? 0 : 500, extraSide = FixedSize ? 0 : 420;
                    System.Drawing.Rectangle vs = WF.SystemInformation.VirtualScreen;   // semua monitor
                    int x0 = Math.Max(vs.Left, (int)wtl.X - pad - extraSide);
                    int y0 = Math.Max(vs.Top, (int)wtl.Y - pad - extraTop);
                    int x1 = Math.Min(vs.Right, (int)wtl.X + ww + pad + extraSide);
                    int y1 = Math.Min(vs.Bottom, (int)wtl.Y + wh + pad);
                    if (x1 - x0 <= 0 || y1 - y0 <= 0) return;
                    capImg = Frost(x0, y0, x1 - x0, y1 - y0);
                    capX = x0; capY = y0; capW = x1 - x0; capH = y1 - y0;
                }
                // latar tetap ditangkap di tema Dark: bila tema diganti saat popup terbuka, kaca langsung siap
                if (dark)
                {
                    foreach (Border c in cards)
                    {
                        c.Background = new SolidColorBrush(Color.FromArgb(0xF8, 0x1B, 0x1B, 0x21));
                        c.BorderBrush = UI.Ink(0x40);
                    }
                    return;
                }
                foreach (Border card in cards)
                {
                    Point tl = card.PointToScreen(new Point(0, 0));
                    int w = (int)(card.ActualWidth * s), h = (int)(card.ActualHeight * s);
                    if (w <= 0 || h <= 0) continue;
                    ImageBrush ib = new ImageBrush(capImg);
                    ib.Stretch = Stretch.Fill;
                    ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                    ib.Viewbox = new Rect((tl.X - capX) / capW, (tl.Y - capY) / capH, (double)w / capW, (double)h / capH);
                    Rect unit = new Rect(0, 0, 1, 1);
                    DrawingGroup dg = new DrawingGroup();
                    dg.Children.Add(new GeometryDrawing(ib, null, new RectangleGeometry(unit)));
                    // Glass 1: lapisan gelap tipis; Glass 2: lapisan putih tebal (teks hitam tetap jelas)
                    Color tint = light ? Color.FromArgb(0x84, 0xF6, 0xF6, 0xF8) : Color.FromArgb(0x6E, 0x14, 0x14, 0x1C);
                    dg.Children.Add(new GeometryDrawing(new SolidColorBrush(tint), null, new RectangleGeometry(unit)));
                    // aksen putih tipis: kabut putih merata + kilau dari atas yang turun pelan
                    dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)), null, new RectangleGeometry(unit)));
                    LinearGradientBrush gl = new LinearGradientBrush();
                    gl.StartPoint = new Point(0, 0);
                    gl.EndPoint = new Point(0, 1);
                    gl.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 255, 255, 255), 0));
                    gl.GradientStops.Add(new GradientStop(Color.FromArgb(0x0C, 255, 255, 255), 0.35));
                    gl.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7));
                    dg.Children.Add(new GeometryDrawing(gl, null, new RectangleGeometry(unit)));
                    DrawingBrush db = new DrawingBrush(dg);
                    db.Stretch = Stretch.Fill;
                    card.Background = db;
                    // tepi lebih terang di atas (seperti cahaya mengenai kaca), memudar ke bawah
                    LinearGradientBrush edge = new LinearGradientBrush();
                    edge.StartPoint = new Point(0, 0);
                    edge.EndPoint = new Point(0, 1);
                    edge.GradientStops.Add(new GradientStop(Color.FromArgb(0xA8, 255, 255, 255), 0));
                    edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x58, 255, 255, 255), 0.4));
                    edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, 255, 255, 255), 1));
                    if (light)
                    {
                        // kaca putih: tepi atas putih terang, sisi lain garis abu tipis agar kartu tidak menyatu dengan latar
                        edge = new LinearGradientBrush();
                        edge.StartPoint = new Point(0, 0);
                        edge.EndPoint = new Point(0, 1);
                        edge.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0));
                        edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 0.5));
                        edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0, 0, 0), 1));
                    }
                    card.BorderBrush = edge;
                }
            }
            catch { }
        }

        // tema diganti saat popup terbuka: gambar ulang permukaannya
        public void RefreshSurface()
        {
            if (IsOpen) ApplySurface(false);
        }
        public static BitmapSource Frost(int x, int y, int w, int h)
        {
            // salin layar apa adanya (tanpa CAPTUREBLT yang lambat & membuat kursor berkedip),
            // lalu perkecil 1/5 di memori: jauh lebih cepat daripada memperkecil langsung dari layar
            int sw = Math.Max(1, w / 5), sh = Math.Max(1, h / 5);
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr mem2 = CreateCompatibleDC(screen);
            IntPtr big = CreateCompatibleBitmap(screen, w, h);
            IntPtr bmp = CreateCompatibleBitmap(screen, sw, sh);
            IntPtr old = SelectObject(mem, big);
            IntPtr old2 = SelectObject(mem2, bmp);
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020);
            SetStretchBltMode(mem2, 4);                // HALFTONE: hasil kecil tetap halus
            SetBrushOrgEx(mem2, 0, 0, IntPtr.Zero);
            StretchBlt(mem2, 0, 0, sw, sh, mem, 0, 0, w, h, 0x00CC0020);
            SelectObject(mem, old);
            SelectObject(mem2, old2);
            BitmapSource small;
            try
            {
                BitmapSource raw = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // StretchBlt mengosongkan kanal alpha (semua piksel transparan) -> kaca tidak terlihat.
                // Paksa setiap piksel buram (alpha 255) agar gambar latar kaca tampil.
                int stride = sw * 4;
                byte[] px = new byte[stride * sh];
                raw.CopyPixels(px, stride, 0);
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                small = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, px, stride);
            }
            finally { DeleteObject(bmp); DeleteObject(big); DeleteDC(mem); DeleteDC(mem2); ReleaseDC(IntPtr.Zero, screen); }
            Image im = new Image();
            im.Source = small;
            im.Stretch = Stretch.Fill;
            im.Width = sw;
            im.Height = sh;
            im.Effect = new BlurEffect { Radius = 8, KernelType = KernelType.Gaussian };
            im.Measure(new Size(sw, sh));
            im.Arrange(new Rect(0, 0, sw, sh));
            RenderTargetBitmap rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(im);
            rtb.Freeze();
            return rtb;
        }

        protected void SlideIn(Func<Size, Point> place)
        {
            MenuCloser.CloseAll();
            closing = false;
            ++glideGen;                       // hentikan animasi tutup yang mungkin masih berjalan
            BeginAnimation(Window.TopProperty, null);
            BeginAnimation(Window.OpacityProperty, null);
            Opacity = 0;
            Show();
            UpdateLayout();
            Point p = place(new Size(ActualWidth, ActualHeight));
            if (Layout.V) p = SidePoint(new Size(ActualWidth, ActualHeight));
            Left = p.X;
            double target = p.Y;
            Top = target;
            UpdateLayout();
            Left = p.X;          // ulang: bila popup pindah ke monitor dengan skala lain
            Top = target;
            UpdateLayout();
            anchorBottom = target + ActualHeight;
            anchorRight = AnchorRight ? Left + ActualWidth : -1;
            anchorCenter = AnchorCenter ? Left + ActualWidth / 2 : -1;
            ApplySurface();
            shownAt = DateTime.Now;
            // geser masuk dari arah dock (bawah, atau samping untuk dock kiri/kanan), tanpa fade:
            // memudarkan jendela transparan memaksa seluruh panel digambar ulang tiap frame, menggeser jendela murah.
            Point from = SlideOffset(p, SlideDistance);
            slideTarget = p;
            slidePending = true;
            MoveNow(from.X, from.Y);          // frame pertama sudah di titik awal (tidak melompat dari posisi akhir)
            Opacity = 1;
            Activate();
            // frame pertama setelah tampil butuh ~100 ms untuk digambar: mulai geser SETELAH frame itu jadi,
            // supaya animasi tidak "terlewat" separuh lalu tersentak di akhir.
            int ticks = 0, gen = ++openGen;
            EventHandler onFrame = null;
            onFrame = delegate
            {
                if (++ticks < 2) return;
                CompositionTarget.Rendering -= onFrame;
                if (gen != openGen) return;
                slidePending = false;
                if (closing || !IsVisible) return;
                Glide(SlideOffset(slideTarget, SlideDistance), slideTarget, 260, false, null);
            };
            CompositionTarget.Rendering += onFrame;
        }

        // titik awal/akhir geser: dari arah dock
        static Point SlideOffset(Point p, double d)
        {
            if (Layout.Side == 1) return new Point(p.X - d, p.Y);
            if (Layout.Side == 2) return new Point(p.X + d, p.Y);
            return new Point(p.X, p.Y + d);
        }

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        double DeviceScale()
        {
            PresentationSource ps = PresentationSource.FromVisual(this);
            return ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1;
        }

        // pindahkan jendela langsung (piksel layar, x & y sekaligus) tanpa melewati sistem layout WPF
        void MoveNow(double x, double y)
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            curPos = new Point(x, y);
            if (h == IntPtr.Zero) { Left = x; Top = y; return; }
            double k = DeviceScale();
            SetWindowPos(h, IntPtr.Zero, (int)Math.Round(x * k), (int)Math.Round(y * k), 0, 0, 0x0001 | 0x0004 | 0x0010);
        }

        // gerak halus: posisi dihitung dari waktu yang benar-benar berlalu tiap frame layar,
        // jadi tetap mulus & selesai tepat waktu walau ada frame yang terlambat
        int glideGen;
        Point curPos;
        void Glide(Point from, Point to, int ms, bool accelerate, Action done)
        {
            int gen = ++glideGen;
            Stopwatch sw = Stopwatch.StartNew();
            AnimClock clock = new AnimClock();
            EventHandler step = null;
            step = delegate
            {
                if (gen != glideGen) { CompositionTarget.Rendering -= step; return; }
                double t = Math.Min(1, clock.Tick() / ms);
                double e = accelerate ? t * t * t : 1 - Math.Pow(1 - t, 3);      // ease-in (tutup) / ease-out (buka)
                MoveNow(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e);
                if (t < 1) return;
                CompositionTarget.Rendering -= step;
                Left = to.X;
                Top = to.Y;
                if (done != null) done();
            };
            CompositionTarget.Rendering += step;
        }

        public void HidePanel()
        {
            if (!IsOpen) return;
            HiddenAt = DateTime.Now;
            closing = true;
            slidePending = false;
            ++openGen;
            BeginAnimation(Window.TopProperty, null);
            BeginAnimation(Window.OpacityProperty, null);
            // geser turun (atau kembali ke arah dock) lalu sembunyikan
            Point cur = curPos;
            Glide(cur, SlideOffset(cur, SlideDistance), 170, true, delegate
            {
                if (!closing) return;            // sudah dibuka lagi di tengah animasi
                // jendela transparan menyimpan gambar terakhirnya & menampilkannya lagi sesaat ketika dibuka ulang.
                // Kosongkan dulu (opacity 0), tunggu frame itu tergambar, baru sembunyikan.
                Opacity = 0;
                int ticks = 0;
                EventHandler onFrame = null;
                onFrame = delegate
                {
                    if (++ticks < 3) return;
                    CompositionTarget.Rendering -= onFrame;
                    if (!closing) return;
                    Hide();
                    closing = false;
                };
                CompositionTarget.Rendering += onFrame;
            });
        }
    }

    // ----- daftar Start (hanya app yang Anda tambahkan) -----
    static class StartStore
    {
        static readonly string FileP = System.IO.Path.Combine(Store.Dir, "start.txt");
        static readonly string RbFlag = System.IO.Path.Combine(Store.Dir, "start-rb.flag");
        public const string RecycleTarget = "shell:RecycleBinFolder";

        public static List<PinItem> Load()
        {
            List<PinItem> l = new List<PinItem>();
            if (!File.Exists(FileP)) return l;
            foreach (string line in File.ReadAllLines(FileP))
            {
                int i = line.IndexOf('|');
                if (i <= 0) continue;
                PinItem p = new PinItem();
                p.Target = line.Substring(0, i);
                p.Name = line.Substring(i + 1);
                if (p.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(p.Target) || Directory.Exists(p.Target))
                    l.Add(p);   // shortcut yang sudah dihapus otomatis hilang
            }
            // Recycle Bin jadi item Start biasa (bisa diseret); disisipkan sekali di posisi pertama
            if (!File.Exists(RbFlag))
            {
                if (!l.Any(x => string.Equals(x.Target, RecycleTarget, StringComparison.OrdinalIgnoreCase)))
                {
                    PinItem rb = new PinItem();
                    rb.Target = RecycleTarget;
                    rb.Name = "Recycle Bin";
                    l.Insert(0, rb);
                    Save(l);
                }
                try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(RbFlag, "1"); } catch { }
            }
            return l;
        }

        static void Save(List<PinItem> l)
        {
            Directory.CreateDirectory(Store.Dir);
            File.WriteAllLines(FileP, l.Select(p => p.Target + "|" + p.Name).ToArray());
        }

        public static void Swap(string a, string b)
        {
            List<PinItem> l = Load();
            int i = l.FindIndex(p => string.Equals(p.Target, a, StringComparison.OrdinalIgnoreCase));
            int j = l.FindIndex(p => string.Equals(p.Target, b, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || j < 0 || i == j) return;
            PinItem tmp = l[i];
            l[i] = l[j];
            l[j] = tmp;
            Save(l);
        }

        public static bool Contains(string target)
        {
            return Load().Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase));
        }

        public static void Add(string target, string name)
        {
            List<PinItem> l = Load();
            if (l.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase))) return;
            PinItem n = new PinItem();
            n.Target = target;
            n.Name = name;
            l.Add(n);
            Save(l);
        }

        public static void Remove(string target)
        {
            Save(Load().Where(p => !string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase)).ToList());
        }
    }

    // ----- cari file lewat indeks Windows Search -----
    static class FileSearch
    {
        public static List<string> Query(string q, int max)
        {
            try
            {
                string clean = q.Replace("'", "''").Replace("%", "").Replace("_", " ");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
                string sql = "SELECT TOP " + max + " System.ItemPathDisplay FROM SYSTEMINDEX WHERE SCOPE='file:" + home +
                             "' AND System.ItemNameDisplay LIKE '%" + clean + "%' ORDER BY System.DateModified DESC";
                List<string> l = new List<string>();
                using (OleDbConnection c = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"))
                {
                    c.Open();
                    using (OleDbCommand cmd = new OleDbCommand(sql, c))
                    using (OleDbDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            object v = r[0];
                            if (v != null && v != DBNull.Value) l.Add(v.ToString());
                        }
                    }
                }
                return l;
            }
            catch { return null; }
        }
    }

    // ----- info sistem: volume, kecerahan, Wi-Fi, baterai, disk -----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float l, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float l);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float l);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float l, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float l);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float l);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }

    static class SysInfo
    {
        static IAudioEndpointVolume vol;

        static IAudioEndpointVolume Vol()
        {
            if (vol != null) return vol;
            try
            {
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return null;
                vol = (IAudioEndpointVolume)o;
            }
            catch { vol = null; }
            return vol;
        }

        public static void ResetAudio() { vol = null; }

        public static double Volume()
        {
            IAudioEndpointVolume v = Vol();
            float f;
            if (v == null || v.GetMasterVolumeLevelScalar(out f) != 0) return -1;
            return f;
        }

        public static void SetVolume(double x)
        {
            IAudioEndpointVolume v = Vol();
            if (v == null) return;
            Guid g = Guid.Empty;
            v.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, x)), ref g);
            if (x > 0.001) v.SetMute(false, ref g);
        }

        public static bool Muted()
        {
            IAudioEndpointVolume v = Vol();
            bool m = false;
            if (v != null) v.GetMute(out m);
            return m;
        }

        public static void SetMute(bool m)
        {
            IAudioEndpointVolume v = Vol();
            if (v == null) return;
            Guid g = Guid.Empty;
            v.SetMute(m, ref g);
        }

        public static int Brightness()
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    new System.Management.ManagementScope(@"root\WMI"), new System.Management.SelectQuery("WmiMonitorBrightness")))
                {
                    foreach (System.Management.ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
                }
            }
            catch { }
            return -1;
        }

        public static void SetBrightness(int v)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    new System.Management.ManagementScope(@"root\WMI"), new System.Management.SelectQuery("WmiMonitorBrightnessMethods")))
                {
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Max(0, Math.Min(100, v)) });
                        break;
                    }
                }
            }
            catch { }
        }

        public static string Ssid()
        {
            List<WifiNet> nat = Wifi.NativeNetworks();
            if (nat != null)
            {
                WifiNet c = nat.FirstOrDefault(x => x.Connected);
                return c != null ? c.Ssid : null;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show interfaces");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    Match m = Regex.Match(o, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            catch { }
            return null;
        }

        public static string BatteryText()
        {
            WF.PowerStatus ps = WF.SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) != 0 || ps.BatteryLifePercent > 1.0f) return "No battery";
            int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
            return pct + "%" + (ps.PowerLineStatus == WF.PowerLineStatus.Online ? " (charging)" : "");
        }

        // ikon baterai sesuai isi (0-100% dalam 11 tingkat), versi petir saat mengisi daya
        public static string BatteryGlyph()
        {
            WF.PowerStatus ps = WF.SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) != 0 || ps.BatteryLifePercent > 1.0f) return "\uE83E";
            int lvl = Math.Max(0, Math.Min(10, (int)Math.Round(ps.BatteryLifePercent * 10)));
            if (ps.PowerLineStatus == WF.PowerLineStatus.Online)
                return lvl == 10 ? "\uEA93" : lvl == 9 ? "\uE83E" : ((char)(0xE85A + lvl)).ToString();
            return lvl == 10 ? "\uE83F" : ((char)(0xE850 + lvl)).ToString();
        }

        public static string DiskText()
        {
            try
            {
                DriveInfo d = new DriveInfo("C");
                return (d.AvailableFreeSpace / 1073741824.0).ToString("0") + " GB free of " + (d.TotalSize / 1073741824.0).ToString("0") + " GB";
            }
            catch { return "-"; }
        }
    }

    // ----- slider datar bergaya dock -----
    class FlatSlider : Grid
    {
        public Action<double> Changed;
        public Action<double> Committed;
        double val;
        readonly Border track = new Border();
        readonly Border fill = new Border();
        readonly Border thumb = new Border();
        readonly bool vertical;

        public FlatSlider() : this(false) { }

        // vertical = slider tegak berbentuk pil (terisi dari bawah), untuk popup di samping dock kiri/kanan
        public FlatSlider(bool vertical)
        {
            this.vertical = vertical;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Brush accent = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            if (vertical)
            {
                Width = 46;
                track.Background = UI.Ink(0x26);
                fill.Background = accent;
                fill.VerticalAlignment = VerticalAlignment.Bottom;
                track.Child = fill;
                Children.Add(track);
                SizeChanged += delegate { Layout(); };
                MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { CaptureMouse(); FromMouse(e); };
                MouseMove += delegate(object s, MouseEventArgs e) { if (IsMouseCaptured) FromMouse(e); };
                MouseLeftButtonUp += delegate
                {
                    if (!IsMouseCaptured) return;
                    ReleaseMouseCapture();
                    if (Committed != null) Committed(val);
                };
                return;
            }
            Height = 26;
            track.Height = 4;
            track.CornerRadius = new CornerRadius(2);
            track.Background = UI.Ink(0x50);
            track.VerticalAlignment = VerticalAlignment.Center;
            track.Margin = new Thickness(8, 0, 8, 0);
            fill.Height = 4;
            fill.CornerRadius = new CornerRadius(2);
            fill.Background = accent;
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.VerticalAlignment = VerticalAlignment.Center;
            fill.Margin = new Thickness(8, 0, 0, 0);
            thumb.Width = 16;
            thumb.Height = 16;
            thumb.CornerRadius = new CornerRadius(8);
            thumb.Background = Brushes.White;
            thumb.BorderBrush = accent;
            thumb.BorderThickness = new Thickness(3);
            thumb.HorizontalAlignment = HorizontalAlignment.Left;
            thumb.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(track);
            Children.Add(fill);
            Children.Add(thumb);
            SizeChanged += delegate { Layout(); };
            MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { CaptureMouse(); FromMouse(e); };
            MouseMove += delegate(object s, MouseEventArgs e) { if (IsMouseCaptured) FromMouse(e); };
            MouseLeftButtonUp += delegate
            {
                if (!IsMouseCaptured) return;
                ReleaseMouseCapture();
                if (Committed != null) Committed(val);
            };
        }

        public double Value
        {
            get { return val; }
            set { val = Math.Max(0, Math.Min(1, value)); Layout(); }
        }

        void FromMouse(MouseEventArgs e)
        {
            if (vertical)
            {
                if (ActualHeight <= 0) return;
                Value = 1 - e.GetPosition(this).Y / ActualHeight;
                if (Changed != null) Changed(val);
                return;
            }
            double w = ActualWidth - 16;
            if (w <= 0) return;
            Value = (e.GetPosition(this).X - 8) / w;
            if (Changed != null) Changed(val);
        }

        void Layout()
        {
            if (vertical)
            {
                track.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 14, 14);
                fill.Height = ActualHeight * val;
                return;
            }
            double w = Math.Max(0, ActualWidth - 16);
            fill.Width = w * val;
            thumb.Margin = new Thickness(w * val, 0, 0, 0);
        }
    }

    // ----- aksi daya -----
    static class Power
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        static void RunCmd(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "SlideDock"); }
        }

        // Laptop ini memakai Modern Standby (S0): mematikan layar = masuk sleep.
        public static void Sleep()
        {
            Thread.Sleep(400);
            SendMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
        }

        public static void Hibernate() { RunCmd("shutdown.exe", "/h"); }
        public static void Restart()
        {
            if (UpdatePending() && InitiateWithUpdates(0x4)) return;     // SHUTDOWN_RESTART
            RunCmd("shutdown.exe", "/r /t 0");
        }

        public static void Shutdown()
        {
            if (UpdatePending() && InitiateWithUpdates(0x8)) return;     // SHUTDOWN_POWEROFF
            RunCmd("shutdown.exe", "/s /t 0");
        }

        // ada update Windows yang menunggu restart? (penanda resmi Windows Update / servicing)
        public static bool UpdatePending()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                    if (k != null) return true;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                    if (k != null) return true;
            }
            catch { }
            return false;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TOKEN_PRIV { public int Count; public long Luid; public int Attr; }
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIV ns, int len, IntPtr prev, IntPtr ret);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint InitiateShutdown(string machine, string message, uint grace, uint flags, uint reason);

        // sama seperti "Update and restart/shut down" di Start Windows
        static bool InitiateWithUpdates(uint mode)
        {
            try
            {
                IntPtr tok;
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out tok))
                {
                    TOKEN_PRIV tp = new TOKEN_PRIV();
                    tp.Count = 1;
                    LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid);
                    tp.Attr = 2;                                              // SE_PRIVILEGE_ENABLED
                    AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    Native.CloseHandle(tok);
                }
                uint flags = mode | 0x40;                                      // SHUTDOWN_INSTALL_UPDATES
                uint reason = 0x80000000 | 0x00020000 | 0x00000003;           // terencana, OS, upgrade
                return InitiateShutdown(null, null, 0, flags, reason) == 0;
            }
            catch { return false; }
        }
    }

    // ----- elemen bersama untuk Start & Search -----
    static class Tiles
    {
        public static TextBlock Header(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Foreground = UI.Ink(0xB0);
            t.FontSize = 12.5;
            t.FontWeight = FontWeights.SemiBold;
            t.FontFamily = UI.Text;
            t.Margin = new Thickness(8, 10, 0, 4);
            return t;
        }

        public static TextBlock Note(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Foreground = UI.Ink(0x90);
            t.FontSize = 13;
            t.FontFamily = UI.Text;
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(8, 4, 8, 6);
            return t;
        }

        public static Border App(string name, ImageSource icon, Action click)
        {
            Border tile = new Border();
            tile.Width = 92;
            tile.Height = 88;
            tile.Margin = new Thickness(2);
            tile.CornerRadius = new CornerRadius(14);
            tile.Background = Brushes.Transparent;
            tile.Cursor = Cursors.Hand;
            tile.ToolTip = name;
            UI.Hover(tile, 0x30);
            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            Image img = new Image();
            img.Width = 36;
            img.Height = 36;
            img.Source = icon;
            img.Margin = new Thickness(0, 0, 0, 6);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            TextBlock tb = new TextBlock();
            tb.Text = name;
            tb.Foreground = UI.Fg;
            tb.FontSize = 11.5;
            tb.FontFamily = UI.Text;
            tb.TextAlignment = TextAlignment.Center;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.MaxWidth = 84;
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(img);
            sp.Children.Add(tb);
            tile.Child = sp;
            tile.MouseLeftButtonUp += delegate { click(); };
            return tile;
        }

        public static Border Row(string glyph, string title, string sub, ImageSource icon, Action click)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(10, 7, 10, 7);
            b.Margin = new Thickness(2, 1, 2, 1);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            UI.Hover(b, 0x30);
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            FrameworkElement ic;
            if (icon != null)
            {
                Image img = new Image();
                img.Source = icon;
                img.Width = 22;
                img.Height = 22;
                ic = img;
            }
            else ic = UI.Glyph(glyph ?? "", 16);
            ic.Width = 24;
            row.Children.Add(ic);
            StackPanel txt = new StackPanel();
            txt.Margin = new Thickness(10, 0, 0, 0);
            TextBlock t = new TextBlock();
            t.Text = title;
            t.Foreground = UI.Fg;
            t.FontSize = 13.5;
            t.FontFamily = UI.Text;
            txt.Children.Add(t);
            if (!string.IsNullOrEmpty(sub))
            {
                TextBlock s = new TextBlock();
                s.Text = sub;
                s.Foreground = UI.Ink(0x90);
                s.FontSize = 11;
                s.FontFamily = UI.Text;
                s.TextTrimming = TextTrimming.CharacterEllipsis;
                s.MaxWidth = 560;
                txt.Children.Add(s);
            }
            row.Children.Add(txt);
            b.Child = row;
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        public static void Menu(FrameworkElement el, params object[] labelsAndActions)
        {
            ContextMenu cm = new ContextMenu();
            for (int i = 0; i + 1 < labelsAndActions.Length; i += 2)
            {
                MenuItem mi = new MenuItem();
                mi.Header = labelsAndActions[i];
                Action a = (Action)labelsAndActions[i + 1];
                mi.Click += delegate { a(); };
                cm.Items.Add(mi);
            }
            el.ContextMenu = cm;
        }

        // aman untuk path yang sangat panjang (> 260 karakter)
        public static string SafeName(string path)
        {
            int i = path.LastIndexOfAny(new char[] { '\\', '/' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        public static string SafeDir(string path)
        {
            int i = path.LastIndexOfAny(new char[] { '\\', '/' });
            return i > 0 ? path.Substring(0, i) : "";
        }

        public static void OpenLocation(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }

    // ----- Search: cari app, file, web (seperti Search di taskbar lama) -----
    class SearchWindow : Popover
    {
        readonly TextBox search = new TextBox();
        readonly TextBlock hint = new TextBlock();
        readonly StackPanel content = new StackPanel();
        readonly ScrollViewer sv = new ScrollViewer();
        readonly DispatcherTimer fileTimer = new DispatcherTimer();
        StackPanel fileList;
        List<AppEntry> visibleApps = new List<AppEntry>();
        int searchSeq;

        public SearchWindow()
        {
            SizeToContent = SizeToContent.Manual;
            Width = 740;
            Height = 540;

            Border card = UI.Card(24, 16);
            Grid root = new Grid();
            root.Margin = new Thickness(18, 16, 18, 14);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border sBox = new Border();
            sBox.CornerRadius = new CornerRadius(14);
            sBox.Background = UI.Ink(0x26);
            sBox.Padding = new Thickness(12, 9, 14, 9);
            sBox.Margin = new Thickness(0, 0, 0, 8);
            Grid sGrid = new Grid();
            sGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock lens = UI.Glyph("", 15);
            lens.Opacity = 0.7;
            lens.Margin = new Thickness(0, 0, 10, 0);
            sGrid.Children.Add(lens);
            search.Background = Brushes.Transparent;
            search.BorderThickness = new Thickness(0);
            search.Foreground = UI.Fg;
            search.CaretBrush = UI.Fg;
            search.FontSize = 15;
            search.FontFamily = UI.Text;
            hint.Text = "Search apps, files, or the web";
            hint.Foreground = UI.Ink(0x99);
            hint.FontSize = 15;
            hint.FontFamily = UI.Text;
            hint.IsHitTestVisible = false;
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(search, 1);
            Grid.SetColumn(hint, 1);
            sGrid.Children.Add(search);
            sGrid.Children.Add(hint);
            sBox.Child = sGrid;
            root.Children.Add(sBox);

            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = content;
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);
            card.Child = root;
            Content = card;

            fileTimer.Interval = TimeSpan.FromMilliseconds(300);
            fileTimer.Tick += delegate { fileTimer.Stop(); StartFileSearch(); };
            search.TextChanged += delegate
            {
                hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Rebuild();
                fileTimer.Stop();
                if (search.Text.Trim().Length > 1) fileTimer.Start();
            };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                string q = search.Text.Trim();
                if (visibleApps.Count > 0) { HidePanel(); Launcher.Open(visibleApps[0].Target); }
                else if (q.Length > 0) { HidePanel(); WebSearch(q); }
            };
        }

        static void WebSearch(string q)
        {
            Launcher.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q));
        }

        public void Prewarm() { Catalog.LoadAsync(null); }   // di latar: tidak membekukan dock saat app baru jalan

        public void ShowPanel(double dockTop)
        {
            if (IsOpen) return;
            search.Text = "";
            Rebuild();
            SideAnchor = double.NaN;
            SlideIn(delegate(Size s) { return new Point(AlongX(s.Width, double.NaN), dockTop - s.Height + 6); });
            search.Focus();
        }

        void Rebuild()
        {
            content.Children.Clear();
            fileList = null;
            string q = search.Text.Trim();
            if (q.Length == 0)
            {
                visibleApps = new List<AppEntry>();
                content.Children.Add(Tiles.Note("Type to search apps, your files, or the web."));
                return;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            visibleApps = Catalog.Desktop().Concat(Catalog.All())
                .Where(e => e.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(e => e.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Where(e => seen.Add(e.Name))
                .Take(18).ToList();

            content.Children.Add(Tiles.Header("App"));
            if (visibleApps.Count == 0) content.Children.Add(Tiles.Note("No matching apps."));
            else
            {
                WrapPanel w = new WrapPanel();
                foreach (AppEntry a in visibleApps)
                {
                    AppEntry e = a;
                    Border t = Tiles.App(e.Name, Icons.For(e.Target), delegate { HidePanel(); Launcher.Open(e.Target); });
                    Tiles.Menu(t,
                        "Add to Start", (Action)delegate { StartStore.Add(e.Target, e.Name); },
                        "Pin to dock", (Action)delegate { PinStore.Add(e.Target, e.Name); });
                    w.Children.Add(t);
                }
                content.Children.Add(w);
            }

            content.Children.Add(Tiles.Header("File"));
            fileList = new StackPanel();
            fileList.Children.Add(Tiles.Note(q.Length < 2 ? "Type at least 2 letters to search files." : "Searching files..."));
            content.Children.Add(fileList);

            content.Children.Add(Tiles.Header("Web"));
            content.Children.Add(Tiles.Row("", "Search the web for \"" + q + "\"", null, null, delegate { HidePanel(); WebSearch(q); }));
        }

        void StartFileSearch()
        {
            string q = search.Text.Trim();
            if (q.Length < 2 || fileList == null) return;
            int seq = ++searchSeq;
            StackPanel target = fileList;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> res = FileSearch.Query(q, 8);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (seq != searchSeq || target != fileList) return;
                    target.Children.Clear();
                    if (res == null) target.Children.Add(Tiles.Note("Windows file search is not available."));
                    else if (res.Count == 0) target.Children.Add(Tiles.Note("No matching files."));
                    else foreach (string p in res)
                    {
                        string path = p;
                        Border row = Tiles.Row(null, Tiles.SafeName(path), Tiles.SafeDir(path),
                            Icons.FromPath(path), delegate { HidePanel(); Launcher.Open(path); });
                        Tiles.Menu(row, "Open file location", (Action)delegate { Tiles.OpenLocation(path); });
                        target.Children.Add(row);
                    }
                }));
            });
        }
    }

    // jendela kecil tembus-klik yang membawa ikon saat diseret
    class GhostWin : Window
    {
        public GhostWin(UIElement content) : this(content, 84, 84) { }

        public GhostWin(UIElement content, double winW, double winH)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Width = winW;
            Height = winH;
            FrameworkElement fe = content as FrameworkElement;
            if (fe != null) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
            Content = content;
            SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | 0x20 | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);   // 0x20 = klik tembus
            };
        }
    }

    // ----- Start: app pilihan Anda (70%) + info & daya (30%) -----
    // satu baris di Dock settings: pilihan (Choices), saklar (Toggle) atau tautan (Click)
    class DockSetting
    {
        public string Section, Label;
        public string[] Choices;
        public bool Toggle;
        public Func<int> Get;
        public Action<int> Set;
        public Action Click;
        public bool Input;                     // kotak teks + Save (mis. link kalender)
        public string Placeholder;
        public Action<string> SetText;
    }

    class StartWindow : Popover
    {
        static readonly CultureInfo Id = new CultureInfo("en-US");
        readonly WrapPanel grid = new WrapPanel();
        readonly TextBlock leftTitle = new TextBlock();
        readonly TextBlock addText = new TextBlock();
        readonly TextBox filter = new TextBox();
        readonly Border filterBox = new Border();
        readonly ScrollViewer sv = new ScrollViewer();
        readonly TextBlock greet = new TextBlock();
        readonly TextBlock clock = new TextBlock();
        readonly TextBlock date = new TextBlock();
        readonly TextBlock wifiInfo = new TextBlock();
        readonly TextBlock batInfo = new TextBlock();
        readonly TextBlock batGlyph = UI.Glyph("\uE83F", 14);
        readonly TextBlock diskInfo = new TextBlock();
        readonly DispatcherTimer clockTimer = new DispatcherTimer();
        bool addMode;
        readonly WrapPanel trayWrap = new WrapPanel();

        // Dock settings: menggantikan daftar app (kiri) & tombol daya (kanan bawah) sampai "Done" diklik.
        // Tiap pilihan langsung berlaku.
        public Func<List<DockSetting>> SettingsProvider;
        bool settingsMode;
        Grid headRow;
        readonly ScrollViewer settingsScroll = new ScrollViewer();
        readonly StackPanel settingsList = new StackPanel();
        UniformGrid powerGrid;
        readonly Border doneBtn = new Border();
        Border dockSettingsRow;

        public StartWindow()
        {
            SizeToContent = SizeToContent.Manual;
            Width = 840;
            Height = 560;
            FixedSize = true;

            Border card = UI.Card(24, 16);
            Grid root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

            // ---- kiri: app ----
            Grid left = new Grid();
            left.Margin = new Thickness(18, 16, 12, 14);
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid head = new Grid();
            headRow = head;
            head.Margin = new Thickness(6, 0, 0, 8);
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            leftTitle.Foreground = UI.Fg;
            leftTitle.FontSize = 16;
            leftTitle.FontWeight = FontWeights.SemiBold;
            leftTitle.FontFamily = UI.Text;
            leftTitle.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(leftTitle);
            Border addBtn = new Border();
            addBtn.CornerRadius = new CornerRadius(10);
            addBtn.Padding = new Thickness(12, 6, 12, 6);
            addBtn.Background = UI.Ink(0x26);
            addBtn.Cursor = Cursors.Hand;
            addText.Foreground = UI.Fg;
            addText.FontSize = 12.5;
            addText.FontFamily = UI.Text;
            addBtn.Child = addText;
            addBtn.MouseEnter += delegate { addBtn.Background = UI.Ink(0x40); };
            addBtn.MouseLeave += delegate { addBtn.Background = UI.Ink(0x26); };
            addBtn.MouseLeftButtonUp += delegate
            {
                addMode = !addMode;
                filter.Text = "";
                Rebuild();
                if (addMode) filter.Focus();
            };
            Grid.SetColumn(addBtn, 1);
            head.Children.Add(addBtn);
            left.Children.Add(head);

            filterBox.CornerRadius = new CornerRadius(12);
            filterBox.Background = UI.Ink(0x20);
            filterBox.Padding = new Thickness(12, 7, 12, 7);
            filterBox.Margin = new Thickness(4, 0, 4, 8);
            filter.Background = Brushes.Transparent;
            filter.BorderThickness = new Thickness(0);
            filter.Foreground = UI.Fg;
            filter.CaretBrush = UI.Fg;
            filter.FontSize = 13.5;
            filter.FontFamily = UI.Text;
            filter.ToolTip = "Filter apps";
            filterBox.Child = filter;
            filter.TextChanged += delegate { if (addMode) Rebuild(); };
            Grid.SetRow(filterBox, 1);
            left.Children.Add(filterBox);

            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = grid;
            // lebar tile mengikuti lebar area: kolom mengisi penuh, tanpa sisa ruang kosong di kanan
            sv.ScrollChanged += delegate(object s, ScrollChangedEventArgs e) { if (e.ViewportWidthChange != 0) FitTiles(); };
            Grid.SetRow(sv, 2);
            left.Children.Add(sv);
            settingsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            settingsScroll.Content = settingsList;
            // scroll bawaan (3 baris = 48 px per putaran) terasa terlalu cepat di daftar pengaturan: 20 px per putaran
            settingsScroll.PreviewMouseWheel += delegate(object o, MouseWheelEventArgs e)
            {
                e.Handled = true;
                settingsScroll.ScrollToVerticalOffset(settingsScroll.VerticalOffset - e.Delta / 120.0 * 20);
            };
            settingsList.Margin = new Thickness(6, 0, 14, 8);
            settingsScroll.Visibility = Visibility.Collapsed;
            Grid.SetRowSpan(settingsScroll, 3);
            left.Children.Add(settingsScroll);
            root.Children.Add(left);

            // ---- kanan: info & daya ----
            Border right = new Border();
            right.CornerRadius = new CornerRadius(0, 23, 23, 0);
            right.Background = UI.Ink(0x14);
            right.BorderBrush = UI.Ink(0x22);
            right.BorderThickness = new Thickness(1, 0, 0, 0);
            right.Padding = new Thickness(18, 20, 18, 16);
            Grid.SetColumn(right, 1);
            DockPanel rp = new DockPanel();

            StackPanel power = new StackPanel();
            DockPanel.SetDock(power, Dock.Bottom);
            TextBlock pt = new TextBlock();
            pt.Visibility = Visibility.Collapsed;
            pt.Foreground = UI.Ink(0xB0);
            pt.FontSize = 12;
            pt.FontWeight = FontWeights.SemiBold;
            pt.FontFamily = UI.Text;
            pt.Margin = new Thickness(2, 0, 0, 6);
            power.Children.Add(pt);
            UniformGrid pg = new UniformGrid();
            pg.Columns = 4;
            pg.Children.Add(PowerButton("", "Sleep", Power.Sleep));
            pg.Children.Add(PowerButton("", "Hibernate", Power.Hibernate));
            restartBtn = (StackPanel)PowerButton("", "Restart", Power.Restart);
            pg.Children.Add(restartBtn);
            shutdownBtn = (StackPanel)PowerButton("", "Shut down", Power.Shutdown);
            pg.Children.Add(shutdownBtn);
            power.Children.Add(pg);
            powerGrid = pg;
            doneBtn.CornerRadius = new CornerRadius(12);
            doneBtn.Height = 40;
            doneBtn.Cursor = Cursors.Hand;
            doneBtn.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            doneBtn.MouseEnter += delegate { doneBtn.Background = new SolidColorBrush(Color.FromRgb(0x7D, 0xD8, 0xFF)); };
            doneBtn.MouseLeave += delegate { doneBtn.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)); };
            TextBlock dt = new TextBlock();
            dt.Text = "Done";
            dt.FontSize = 13.5;
            dt.FontWeight = FontWeights.SemiBold;
            dt.FontFamily = UI.Text;
            dt.Foreground = Brushes.Black;
            dt.HorizontalAlignment = HorizontalAlignment.Center;
            dt.VerticalAlignment = VerticalAlignment.Center;
            doneBtn.Child = dt;
            doneBtn.Visibility = Visibility.Collapsed;
            doneBtn.MouseLeftButtonUp += delegate { SetSettingsMode(false); };
            power.Children.Add(doneBtn);
            rp.Children.Add(power);

            StackPanel info = new StackPanel();
            greet.Foreground = UI.Ink(0xC0);
            greet.FontSize = 13;
            greet.FontFamily = UI.Text;
            clock.Foreground = UI.Fg;
            clock.FontSize = 40;
            clock.FontWeight = FontWeights.SemiBold;
            clock.FontFamily = UI.Text;
            clock.Margin = new Thickness(0, 2, 0, 0);
            date.Foreground = UI.Ink(0xC0);
            date.FontSize = 12.5;
            date.FontFamily = UI.Text;
            date.TextWrapping = TextWrapping.Wrap;
            // pojok kanan atas: PC settings, Dock settings & Task Manager cukup sebagai ikon
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            greet.VerticalAlignment = VerticalAlignment.Center;
            greet.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(greet);
            StackPanel icons = new StackPanel();
            icons.Orientation = Orientation.Horizontal;
            icons.Margin = new Thickness(0, -6, -6, 0);
            top.Margin = new Thickness(0, 0, 0, 2);
            Border pcBtn = IconBtn("\uE713", "PC settings");
            UI.Hover(pcBtn, 0x30);
            pcBtn.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open("ms-settings:"); };
            icons.Children.Add(pcBtn);
            dockSettingsRow = IconBtn("\uE771", "Dock settings");
            dockSettingsRow.MouseEnter += delegate { if (!settingsMode) dockSettingsRow.Background = UI.Ink(0x30); };
            dockSettingsRow.MouseLeave += delegate { if (!settingsMode) dockSettingsRow.Background = Brushes.Transparent; };
            dockSettingsRow.MouseLeftButtonUp += delegate { SetSettingsMode(!settingsMode); };
            icons.Children.Add(dockSettingsRow);
            Border tmBtn = IconBtn("\uE9D9", "Task Manager (Ctrl+Shift+Esc)");
            UI.Hover(tmBtn, 0x30);
            tmBtn.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open("taskmgr.exe"); };
            icons.Children.Add(tmBtn);
            Grid.SetColumn(icons, 1);
            top.Children.Add(icons);
            // di luar area gulir: tidak ikut terpotong (sebelumnya kotak hover terpotong di atas & kanan)
            DockPanel.SetDock(top, Dock.Top);
            rp.Children.Add(top);
            info.Children.Add(clock);
            info.Children.Add(date);
            BuildUpdateBar();
            info.Children.Add(updateBar);
            Border sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(0, 16, 0, 12);
            sep.Background = UI.Ink(0x26);
            info.Children.Add(sep);
            info.Children.Add(InfoRow("", wifiInfo, null));
            info.Children.Add(InfoRow("", batInfo, batGlyph));
            info.Children.Add(InfoRow("", diskInfo, null));
            TextBlock trayTitle = new TextBlock();
            trayTitle.Text = "Running in background";
            trayTitle.Foreground = UI.Ink(0xB0);
            trayTitle.FontSize = 12;
            trayTitle.FontWeight = FontWeights.SemiBold;
            trayTitle.FontFamily = UI.Text;
            trayTitle.Margin = new Thickness(0, 14, 0, 4);
            info.Children.Add(trayTitle);
            trayWrap.MaxWidth = 220;
            trayWrap.Margin = new Thickness(-6, 0, 0, 0);
            info.Children.Add(trayWrap);
            // banyak app di latar belakang: kolom info bisa digulir, tidak pernah menimpa tombol daya / Done
            ScrollViewer isv = new ScrollViewer();
            isv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            isv.Content = info;
            isv.Margin = new Thickness(0, 0, 0, 10);
            rp.Children.Add(isv);

            right.Child = rp;
            root.Children.Add(right);
            dragLayer.IsHitTestVisible = false;
            Grid.SetColumnSpan(dragLayer, 2);
            root.Children.Add(dragLayer);
            PreviewMouseMove += delegate(object s, MouseEventArgs e) { DragUpdate(e); };
            PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { if (DragEnd()) e.Handled = true; };
            LostMouseCapture += delegate(object s, MouseEventArgs e) { if (e.OriginalSource == this && dragging && !ending) { pressed = false; ResetDrag(); } };
            card.Child = root;
            Content = card;

            clockTimer.Interval = TimeSpan.FromSeconds(1);
            clockTimer.Tick += delegate { UpdateClock(); };
            IsVisibleChanged += delegate { if (IsVisible) clockTimer.Start(); else clockTimer.Stop(); };
        }

        // ----- banner "Update available" di kolom kanan Start -----
        public Action OnUpdate;
        readonly Border updateBar = new Border();
        readonly TextBlock updateText = new TextBlock();
        readonly TextBlock updateNotes = new TextBlock();
        readonly Border updateBtn = new Border();
        readonly TextBlock updateBtnText = new TextBlock();

        void BuildUpdateBar()
        {
            updateBar.CornerRadius = new CornerRadius(12);
            updateBar.Margin = new Thickness(0, 14, 0, 0);
            updateBar.Padding = new Thickness(12, 9, 9, 9);
            updateBar.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x60, 0xCD, 0xFF));
            updateBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x60, 0xCD, 0xFF));
            updateBar.BorderThickness = new Thickness(1);
            updateBar.Visibility = Visibility.Collapsed;
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            updateNotes.Foreground = UI.Ink(0xC8);
            updateNotes.FontSize = 11.5;
            updateNotes.FontFamily = UI.Text;
            updateNotes.TextWrapping = TextWrapping.Wrap;
            updateNotes.LineHeight = 16;
            updateNotes.Margin = new Thickness(0, 7, 0, 1);
            updateNotes.Visibility = Visibility.Collapsed;
            updateText.Foreground = UI.Fg;
            updateText.FontSize = 12.5;
            updateText.FontFamily = UI.Text;
            updateText.TextWrapping = TextWrapping.Wrap;
            updateText.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(updateText);
            updateBtn.CornerRadius = new CornerRadius(8);
            updateBtn.Padding = new Thickness(12, 5, 12, 6);
            updateBtn.Margin = new Thickness(8, 0, 0, 0);
            updateBtn.Cursor = Cursors.Hand;
            updateBtn.VerticalAlignment = VerticalAlignment.Center;
            updateBtn.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            updateBtnText.Text = "Update";
            updateBtnText.FontSize = 12.5;
            updateBtnText.FontWeight = FontWeights.SemiBold;
            updateBtnText.FontFamily = UI.Text;
            updateBtnText.Foreground = Brushes.Black;
            updateBtn.Child = updateBtnText;
            updateBtn.MouseLeftButtonUp += delegate { if (OnUpdate != null) OnUpdate(); };
            Grid.SetColumn(updateBtn, 1);
            g.Children.Add(updateBtn);
            StackPanel ub = new StackPanel();
            ub.Children.Add(g);
            ub.Children.Add(updateNotes);
            updateBar.Child = ub;
        }

        // dipanggil setiap status update berubah
        public void RefreshUpdate()
        {
            UpdateInfo u = Updater.Available;
            updateBar.Visibility = u != null ? Visibility.Visible : Visibility.Collapsed;
            if (u != null)
            {
                updateText.Text = Updater.Busy ? "Downloading SlideDock " + u.Ver.ToString(3) + "..." : "SlideDock " + u.Ver.ToString(3) + " is available";
                updateBtn.Visibility = Updater.Busy ? Visibility.Collapsed : Visibility.Visible;
                updateNotes.Text = string.Join("\n", u.Notes.Select(x => "\u2022  " + x).ToArray());
                updateNotes.Visibility = u.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (settingsMode && IsOpen) RenderSettings();
        }

        static Border IconBtn(string glyph, string tip)
        {
            Border b = new Border();
            b.Width = 32;
            b.Height = 30;
            b.Margin = new Thickness(2, 0, 0, 0);
            b.CornerRadius = new CornerRadius(8);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = tip;
            b.Child = UI.Glyph(glyph, 14);
            return b;
        }

        FrameworkElement InfoRow(string glyph, TextBlock text, TextBlock icon)
        {
            StackPanel r = new StackPanel();
            r.Orientation = Orientation.Horizontal;
            r.Margin = new Thickness(0, 4, 0, 4);
            TextBlock g = icon ?? UI.Glyph(glyph, 14);
            g.Text = glyph;
            g.Width = 20;
            g.Opacity = 0.85;
            text.Foreground = UI.Fg;
            text.FontSize = 12.5;
            text.FontFamily = UI.Text;
            text.TextWrapping = TextWrapping.Wrap;
            text.MaxWidth = 180;
            text.Margin = new Thickness(10, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            r.Children.Add(g);
            r.Children.Add(text);
            return r;
        }

        StackPanel restartBtn, shutdownBtn;

        // ada update menunggu: tombol diberi lingkaran oranye & label "Update & ..."
        void UpdatePowerLabels()
        {
            bool up = Power.UpdatePending();
            MarkUpdate(restartBtn, up, "Update & restart", "Restart");
            MarkUpdate(shutdownBtn, up, "Update & shut down", "Shut down");
        }

        static void MarkUpdate(StackPanel sp, bool up, string upLabel, string normal)
        {
            if (sp == null || sp.Children.Count < 2) return;
            Border b = sp.Children[0] as Border;
            TextBlock t = sp.Children[1] as TextBlock;
            if (b != null)
            {
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA3, 0x3A));
                b.BorderThickness = new Thickness(up ? 2 : 0);
            }
            if (t != null)
            {
                t.Text = up ? upLabel : normal;
                t.Foreground = up ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x66)) : UI.Ink(0xC0);
            }
            sp.ToolTip = up ? "Windows updates will be installed" : normal;
        }

        FrameworkElement PowerButton(string glyph, string label, Action act)
        {
            StackPanel sp = new StackPanel();
            sp.Cursor = Cursors.Hand;
            sp.ToolTip = label;
            Border b = new Border();
            b.Width = 40;
            b.Height = 40;
            b.CornerRadius = new CornerRadius(20);
            b.Background = UI.Ink(0x22);
            b.Child = UI.Glyph(glyph, 15);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            sp.MouseEnter += delegate { b.Background = UI.Ink(0x45); };
            sp.MouseLeave += delegate { b.Background = UI.Ink(0x22); };
            TextBlock t = new TextBlock();
            t.Text = label;
            t.Foreground = UI.Ink(0xC0);
            t.FontSize = 10;
            t.FontFamily = UI.Text;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.TextAlignment = TextAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxWidth = 58;
            t.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(b);
            sp.Children.Add(t);
            sp.MouseLeftButtonUp += delegate
            {
                HidePanel();
                Dispatcher.BeginInvoke((Delegate)act, DispatcherPriority.Background);
            };
            return sp;
        }

        void UpdateClock()
        {
            DateTime now = DateTime.Now;
            int h = now.Hour;
            greet.Text = h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
            clock.Text = now.ToString("HH:mm");
            string d = now.ToString("dddd, d MMMM yyyy", Id);
            date.Text = d.Length > 0 ? char.ToUpper(d[0]) + d.Substring(1) : d;
        }

        // matikan app latar belakang: coba tutup baik-baik dulu, lalu paksa berhenti
        void QuitApp(string exe, string name)
        {
            MessageBoxResult ok = MessageBox.Show("Quit " + name + "?\n\nUnsaved work in this app may be lost.", "SlideDock",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
            int denied = 0;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string pe = Native.ExeOf((uint)p.Id);
                    if (pe == null || !string.Equals(pe, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.CloseMainWindow() || !p.WaitForExit(1500)) p.Kill();
                }
                catch { denied++; }
                finally { p.Dispose(); }
            }
            if (denied > 0)
                MessageBox.Show(name + " could not be closed completely (it may need administrator rights).", "SlideDock",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(900);
            t.Tick += delegate { t.Stop(); if (IsVisible) FillTray(); };
            t.Start();
        }

        // daftar app latar dibaca di thread lain: panel Start tidak menunggu (sebelumnya ~90 ms tiap buka)
        string traySig;
        bool trayBusy;
        void FillTray()
        {
            if (trayBusy) return;
            trayBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<TrayEntry> list;
                try { list = TrayApps.Running(); } catch { list = new List<TrayEntry>(); }
                Dispatcher.BeginInvoke(new Action(delegate { trayBusy = false; ShowTray(list); }), DispatcherPriority.Background);
            });
        }

        void ShowTray(List<TrayEntry> list)
        {
            string sig = string.Join("|", list.Select(x => x.Exe + ":" + x.Tip).ToArray());
            if (sig == traySig && trayWrap.Children.Count > 0) return;   // tidak berubah: jangan gambar ulang
            traySig = sig;
            trayWrap.Children.Clear();
            foreach (TrayEntry t in list)
            {
                TrayEntry e = t;
                if (e.Icon == null) e.Icon = Icons.FromPath(e.Exe);
                Border b = new Border();
                b.Width = 34;
                b.Height = 34;
                b.Margin = new Thickness(1);
                b.CornerRadius = new CornerRadius(9);
                b.Background = Brushes.Transparent;
                b.Cursor = Cursors.Hand;
                b.ToolTip = e.Tip;
                UI.Hover(b, 0x30);
                Image img = new Image();
                img.Source = e.Icon;
                img.Width = 18;
                img.Height = 18;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                b.Child = img;
                b.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open(e.Exe); };
                string appName = Native.DescOf(e.Exe);
                Tiles.Menu(b,
                    "Open " + appName, (Action)delegate { HidePanel(); Launcher.Open(e.Exe); },
                    "Quit " + appName, (Action)delegate { QuitApp(e.Exe, appName); },
                    "Open file location", (Action)delegate { HidePanel(); Tiles.OpenLocation(e.Exe); });
                trayWrap.Children.Add(b);
            }
            if (trayWrap.Children.Count == 0)
            {
                TextBlock n = new TextBlock();
                n.Text = "None";
                n.Foreground = UI.Ink(0x90);
                n.FontSize = 12;
                n.FontFamily = UI.Text;
                n.Margin = new Thickness(6, 2, 0, 0);
                trayWrap.Children.Add(n);
            }
        }

        void FillInfo()
        {
            UpdateClock();
            batInfo.Text = "Battery " + SysInfo.BatteryText();
            batGlyph.Text = SysInfo.BatteryGlyph();
            diskInfo.Text = "Disk C: " + SysInfo.DiskText();
            UpdatePowerLabels();
            FillTray();
            wifiInfo.Text = "Wi-Fi: checking...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string ssid = SysInfo.Ssid();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    wifiInfo.Text = ssid != null ? "Wi-Fi: " + ssid : "Wi-Fi not connected";
                }));
            });
        }

        void SetSettingsMode(bool on)
        {
            settingsMode = on;
            Visibility apps = on ? Visibility.Collapsed : Visibility.Visible;
            headRow.Visibility = apps;
            filterBox.Visibility = apps;
            sv.Visibility = apps;
            powerGrid.Visibility = apps;
            settingsScroll.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            doneBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            dockSettingsRow.Background = on ? UI.Ink(0x30) : Brushes.Transparent;
            if (on) { RenderSettings(); settingsScroll.ScrollToTop(); }
            else if (addMode) { addMode = false; filter.Text = ""; Rebuild(); }
        }

        public void ShowSettings(double dockTop)
        {
            if (IsOpen) { SetSettingsMode(true); return; }
            ShowPanel(dockTop);
            SetSettingsMode(true);
        }

        // gambar ulang daftar pengaturan (dipanggil setelah tiap perubahan agar pilihan aktif ikut berpindah)
        public void RenderSettings()
        {
            settingsList.Children.Clear();
            TextBlock title = new TextBlock();
            title.Text = "Dock settings";
            title.Foreground = UI.Fg;
            title.FontSize = 16;
            title.FontWeight = FontWeights.SemiBold;
            title.FontFamily = UI.Text;
            title.Margin = new Thickness(0, 6, 0, 2);
            settingsList.Children.Add(title);
            TextBlock hint = new TextBlock();
            hint.Text = "Changes apply right away.";
            hint.Foreground = UI.Ink(0x90);
            hint.FontSize = 12;
            hint.FontFamily = UI.Text;
            settingsList.Children.Add(hint);
            if (SettingsProvider == null) return;
            string section = null;
            foreach (DockSetting s in SettingsProvider())
            {
                if (s.Section != section)
                {
                    section = s.Section;
                    TextBlock h = new TextBlock();
                    h.Text = section.ToUpperInvariant();
                    h.Foreground = UI.Ink(0x90);
                    h.FontSize = 11;
                    h.FontWeight = FontWeights.SemiBold;
                    h.FontFamily = UI.Text;
                    h.Margin = new Thickness(0, 18, 0, 6);
                    settingsList.Children.Add(h);
                }
                settingsList.Children.Add(SettingRow(s));
            }
        }

        FrameworkElement SettingRow(DockSetting s)
        {
            if (s.Input) return InputRow(s);
            Border row = new Border();
            row.CornerRadius = new CornerRadius(12);
            row.Background = UI.Ink(0x14);
            row.Padding = new Thickness(14, 8, 8, 8);
            row.Margin = new Thickness(0, 0, 0, 4);
            row.MinHeight = 46;
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = new TextBlock();
            label.Text = s.Label;
            label.Foreground = UI.Fg;
            label.FontSize = 13;
            label.FontFamily = UI.Text;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Children.Add(label);
            row.Child = g;
            if (s.Click != null)
            {
                // tautan: seluruh baris bisa diklik
                row.Cursor = Cursors.Hand;
                row.MouseEnter += delegate { row.Background = UI.Ink(0x2A); };
                row.MouseLeave += delegate { row.Background = UI.Ink(0x14); };
                TextBlock chev = UI.Glyph("\uE76C", 11);
                chev.Opacity = 0.7;
                chev.Margin = new Thickness(0, 0, 6, 0);
                Grid.SetColumn(chev, 1);
                g.Children.Add(chev);
                Action click = s.Click;
                row.MouseLeftButtonUp += delegate { click(); };
                return row;
            }
            if (!s.Toggle && s.Choices == null)
            {
                label.Foreground = UI.Ink(0xB0);   // baris info saja
                label.TextWrapping = TextWrapping.Wrap;
                label.FontSize = 12;
                return row;
            }
            int cur = s.Get != null ? s.Get() : 0;
            FrameworkElement ctl;
            if (s.Toggle) ctl = Switch(cur != 0, delegate { s.Set(cur != 0 ? 0 : 1); RenderSettings(); });
            else
            {
                Border seg = new Border();
                seg.CornerRadius = new CornerRadius(10);
                seg.Background = UI.Ink(0x1E);
                seg.Padding = new Thickness(3);
                StackPanel sp = new StackPanel();
                sp.Orientation = Orientation.Horizontal;
                for (int i = 0; i < s.Choices.Length; i++)
                {
                    int idx = i;
                    bool on = idx == cur;
                    Border b = new Border();
                    b.CornerRadius = new CornerRadius(8);
                    b.Padding = new Thickness(12, 5, 12, 6);
                    b.Cursor = on ? Cursors.Arrow : Cursors.Hand;
                    b.Background = on ? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)) : Brushes.Transparent;
                    if (!on) UI.Hover(b, 0x26);
                    TextBlock bt = new TextBlock();
                    bt.Text = s.Choices[i];
                    bt.FontSize = 12.5;
                    bt.FontFamily = UI.Text;
                    bt.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    bt.Foreground = on ? Brushes.Black : UI.Fg;
                    b.Child = bt;
                    b.MouseLeftButtonUp += delegate { if (idx == cur) return; s.Set(idx); RenderSettings(); };
                    sp.Children.Add(b);
                }
                seg.Child = sp;
                ctl = seg;
            }
            ctl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ctl, 1);
            g.Children.Add(ctl);
            return row;
        }

        FrameworkElement InputRow(DockSetting s)
        {
            Border row = new Border();
            row.CornerRadius = new CornerRadius(12);
            row.Background = UI.Ink(0x14);
            row.Padding = new Thickness(14, 10, 10, 10);
            row.Margin = new Thickness(0, 0, 0, 4);
            StackPanel col = new StackPanel();
            TextBlock label = new TextBlock();
            label.Text = s.Label;
            label.Foreground = UI.Fg;
            label.FontSize = 13;
            label.FontFamily = UI.Text;
            label.Margin = new Thickness(0, 0, 0, 8);
            col.Children.Add(label);
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Border box = new Border();
            box.CornerRadius = new CornerRadius(8);
            box.Background = UI.Ink(0x22);
            box.Padding = new Thickness(8, 5, 8, 5);
            Grid inner = new Grid();
            TextBox tb = new TextBox();
            tb.Background = Brushes.Transparent;
            tb.BorderThickness = new Thickness(0);
            tb.Foreground = UI.Fg;
            tb.CaretBrush = UI.Fg;
            tb.FontSize = 12.5;
            tb.FontFamily = UI.Text;
            TextBlock hint = new TextBlock();
            hint.Text = s.Placeholder ?? "";
            hint.Foreground = UI.Ink(0x70);
            hint.FontSize = 12.5;
            hint.FontFamily = UI.Text;
            hint.IsHitTestVisible = false;
            hint.Margin = new Thickness(2, 0, 0, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.TextChanged += delegate { hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            inner.Children.Add(tb);
            inner.Children.Add(hint);
            box.Child = inner;
            g.Children.Add(box);
            Border save = new Border();
            save.CornerRadius = new CornerRadius(8);
            save.Padding = new Thickness(14, 5, 14, 6);
            save.Margin = new Thickness(8, 0, 0, 0);
            save.Cursor = Cursors.Hand;
            save.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            TextBlock st = new TextBlock();
            st.Text = "Save";
            st.FontSize = 12.5;
            st.FontWeight = FontWeights.SemiBold;
            st.FontFamily = UI.Text;
            st.Foreground = Brushes.Black;
            save.Child = st;
            Action go = delegate { if (tb.Text.Trim().Length > 0 && s.SetText != null) s.SetText(tb.Text); };
            save.MouseLeftButtonUp += delegate { go(); };
            tb.KeyDown += delegate(object o, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; go(); } };
            Grid.SetColumn(save, 1);
            g.Children.Add(save);
            col.Children.Add(g);
            row.Child = col;
            return row;
        }

        public void RefreshSettings() { if (settingsMode && IsOpen) RenderSettings(); }

        static FrameworkElement Switch(bool on, Action toggle)
        {
            Border b = new Border();
            b.Width = 42;
            b.Height = 22;
            b.Margin = new Thickness(0, 0, 6, 0);
            b.CornerRadius = new CornerRadius(11);
            b.Cursor = Cursors.Hand;
            b.Background = on ? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)) : UI.Ink(0x40);
            Border knob = new Border();
            knob.Width = 16;
            knob.Height = 16;
            knob.CornerRadius = new CornerRadius(8);
            knob.Background = on ? Brushes.Black : UI.Fg;
            knob.Margin = new Thickness(3);
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            b.Child = knob;
            b.MouseLeftButtonUp += delegate { toggle(); };
            return b;
        }

        public void ShowPanel(double dockTop)
        {
            if (IsOpen) return;
            addMode = false;
            filter.Text = "";
            if (settingsMode) SetSettingsMode(false);
            Rebuild();
            FillInfo();
            sv.ScrollToTop();
            SideAnchor = double.NaN;
            SlideIn(delegate(Size s) { return new Point(AlongX(s.Width, double.NaN), dockTop - s.Height + 6); });
        }

        public void RefreshIfOpen()
        {
            if (IsOpen) Rebuild();
        }

        // dipanggil saat app baru jalan (waktu senggang): ikon, daftar app latar, jendela & efek kaca disiapkan
        // lebih dulu agar Start pertama kali dibuka tidak lambat
        public void Prewarm()
        {
            try
            {
                Rebuild();
                FillTray();
                bool act = ShowActivated;
                ShowActivated = false;
                Opacity = 0;
                Left = -20000; Top = -20000;
                Show();
                UpdateLayout();
                Hide();
                ShowActivated = act;
                Frost(0, 0, 64, 64);
            }
            catch { }
        }

        // ----- susun ulang: seret ikon ke ikon lain untuk tukar tempat -----
        readonly Canvas dragLayer = new Canvas();
        Border dragSrc, hoverTile, ghost;
        GhostWin ghostWin;
        // sorotan ikon tujuan digambar di jendela kecil terpisah (seperti ghost), bukan di panel Start:
        // panel Start yang besar tidak perlu digambar ulang selama menyeret -> gerakan tetap ~60 fps
        GhostWin hlWin;
        ScaleTransform hlScale;
        Point downPt;
        bool pressed, dragging, ending;

        void EnableSwap(Border t, string target)
        {
            t.Tag = target;
            t.RenderTransformOrigin = new Point(0.5, 0.5);
            t.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                pressed = true;
                dragSrc = t;
                downPt = e.GetPosition(dragLayer);
            };
        }

        static ImageSource IconOfTile(Border tile)
        {
            StackPanel sp = tile.Child as StackPanel;
            if (sp == null || sp.Children.Count == 0) return null;
            Image im = sp.Children[0] as Image;
            return im != null ? im.Source : null;
        }

        Border MakeGhost(Border tile)
        {
            Border g = new Border();
            g.Width = 68;
            g.Height = 68;
            g.CornerRadius = new CornerRadius(18);
            g.Background = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
            g.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255));
            g.BorderThickness = new Thickness(1);
            g.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black };
            g.IsHitTestVisible = false;
            Image im = new Image();
            im.Width = 42;
            im.Height = 42;
            im.Source = IconOfTile(tile);
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
            g.Child = im;
            ScaleTransform st = new ScaleTransform(0.8, 0.8);
            g.RenderTransformOrigin = new Point(0.5, 0.5);
            g.RenderTransform = st;
            DoubleAnimation pop = new DoubleAnimation(1.1, TimeSpan.FromMilliseconds(140));
            pop.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            return g;
        }

        void MakeHighlightWin(Border tile)
        {
            double w = tile.ActualWidth > 0 ? tile.ActualWidth : 92, h = tile.ActualHeight > 0 ? tile.ActualHeight : 88;
            Border box = new Border();
            box.Width = w;
            box.Height = h;
            box.CornerRadius = new CornerRadius(14);
            box.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0xCD, 0xFF));
            box.BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x60, 0xCD, 0xFF));
            box.BorderThickness = new Thickness(1.5);
            box.IsHitTestVisible = false;
            hlScale = new ScaleTransform(1, 1);
            box.RenderTransformOrigin = new Point(0.5, 0.5);
            box.RenderTransform = hlScale;
            hlWin = new GhostWin(box, w + 12, h + 12);
            hlWin.Left = -30000;
            hlWin.Top = -30000;
            hlWin.Show();                    // dibuat sebelum ghost: ghost tetap di atasnya
        }

        void Highlight(Border b)
        {
            if (hlWin == null) return;
            IntPtr hw = new WindowInteropHelper(hlWin).Handle;
            if (b == null) { Native.SetWindowPos(hw, IntPtr.Zero, -30000, -30000, 0, 0, 0x0001 | 0x0004 | 0x0010); return; }
            // satu kali pindah (tanpa animasi) dalam piksel layar: tidak ada frame tambahan yang bersaing dengan ikon yang diseret
            Point sp = b.PointToScreen(new Point(0, 0));
            PresentationSource ps = PresentationSource.FromVisual(this);
            double k = ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1;
            Native.SetWindowPos(hw, IntPtr.Zero, (int)Math.Round(sp.X - 6 * k), (int)Math.Round(sp.Y - 6 * k), 0, 0, 0x0001 | 0x0004 | 0x0010);
        }

        void DragUpdate(MouseEventArgs e)
        {
            if (!pressed || dragSrc == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { pressed = false; return; }
            Point p = e.GetPosition(dragLayer);
            if (!dragging)
            {
                if (Math.Abs(p.X - downPt.X) < 6 && Math.Abs(p.Y - downPt.Y) < 6) return;
                dragging = true;         // tandai dulu: CaptureMouse bisa memicu event gerak lagi
                CaptureMouse();
                dragSrc.Opacity = 0.25;
                MakeHighlightWin(dragSrc);
                ghost = MakeGhost(dragSrc);
                ghostWin = new GhostWin(ghost);
                Point w0 = e.GetPosition(this);
                ghostWin.Left = Left + w0.X - 42;
                ghostWin.Top = Top + w0.Y - 42;
                ghostWin.Show();
            }
            if (ghost == null || ghostWin == null) return;
            // geser ghost dengan satu panggilan (x & y sekaligus, piksel layar) agar tidak ada langkah setengah-jalan
            Point sp = PointToScreen(e.GetPosition(this));
            PresentationSource ps = PresentationSource.FromVisual(this);
            double k = ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1;
            IntPtr gh = new WindowInteropHelper(ghostWin).Handle;
            if (gh != IntPtr.Zero)
                Native.SetWindowPos(gh, IntPtr.Zero, (int)Math.Round(sp.X - 42 * k), (int)Math.Round(sp.Y - 42 * k), 0, 0, 0x0001 | 0x0004 | 0x0010);
            Border over = null;
            foreach (UIElement c in grid.Children)
            {
                Border b = c as Border;
                if (b == null || !(b.Tag is string) || b == dragSrc) continue;
                Point tl = b.TranslatePoint(new Point(0, 0), dragLayer);
                if (new Rect(tl, b.RenderSize).Contains(p)) { over = b; break; }
            }
            if (over != hoverTile)
            {
                hoverTile = over;
                Highlight(hoverTile);
            }
        }

        bool DragEnd()
        {
            pressed = false;
            if (!dragging) return false;
            Border src = dragSrc, dst = hoverTile;
            ending = true;
            ReleaseMouseCapture();
            ending = false;
            ResetDrag();
            if (src != null && dst != null) AnimateSwap(src, dst);
            return true;
        }

        void ResetDrag()
        {
            dragLayer.Children.Clear();
            if (ghostWin != null) { ghostWin.Close(); ghostWin = null; }
            if (hlWin != null) { hlWin.Close(); hlWin = null; }
            ghost = null;
            if (dragSrc != null) dragSrc.Opacity = 1;
            dragging = false;
            dragSrc = null;
            hoverTile = null;
        }

        void AnimateSwap(Border a, Border b)
        {
            Point pa = a.TranslatePoint(new Point(0, 0), dragLayer);
            Point pb = b.TranslatePoint(new Point(0, 0), dragLayer);
            TranslateTransform ta = new TranslateTransform();
            TranslateTransform tb = new TranslateTransform();
            a.RenderTransform = ta;
            b.RenderTransform = tb;
            Panel.SetZIndex(a, 10);
            Duration d = TimeSpan.FromMilliseconds(240);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            DoubleAnimation ax = new DoubleAnimation(0, pb.X - pa.X, d) { EasingFunction = ease };
            DoubleAnimation ay = new DoubleAnimation(0, pb.Y - pa.Y, d) { EasingFunction = ease };
            DoubleAnimation bx = new DoubleAnimation(0, pa.X - pb.X, d) { EasingFunction = ease };
            DoubleAnimation by = new DoubleAnimation(0, pa.Y - pb.Y, d) { EasingFunction = ease };
            string sa = (string)a.Tag, sb = (string)b.Tag;
            ax.Completed += delegate { StartStore.Swap(sa, sb); Rebuild(); };
            ta.BeginAnimation(TranslateTransform.XProperty, ax);
            ta.BeginAnimation(TranslateTransform.YProperty, ay);
            tb.BeginAnimation(TranslateTransform.XProperty, bx);
            tb.BeginAnimation(TranslateTransform.YProperty, by);
        }
        Border RecycleTile()
        {
            Border t = Tiles.App("Recycle Bin", Icons.RecycleBin(), delegate { HidePanel(); Launcher.Open("shell:RecycleBinFolder"); });
            Tiles.Menu(t,
                "Open", (Action)delegate { HidePanel(); Launcher.Open("shell:RecycleBinFolder"); },
                "Empty Recycle Bin...", (Action)delegate { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0); },
                "Remove from Start", (Action)delegate { StartStore.Remove(StartStore.RecycleTarget); Rebuild(); });
            return t;
        }

        void FitTiles()
        {
            double w = sv.ViewportWidth > 0 ? sv.ViewportWidth : sv.ActualWidth;
            if (w <= 0) return;
            int cols = Math.Max(1, (int)(w / 96));                 // 96 = lebar tile minimum + margin
            double tw = Math.Floor(w / cols) - 4;
            foreach (UIElement c in grid.Children)
            {
                Border b = c as Border;
                if (b == null) continue;
                b.Width = tw;
                StackPanel sp = b.Child as StackPanel;
                Grid wrap = b.Child as Grid;                       // tile dengan centang (mode Add apps)
                if (sp == null && wrap != null && wrap.Children.Count > 0) sp = wrap.Children[0] as StackPanel;
                if (sp != null && sp.Children.Count > 1) { TextBlock tb = sp.Children[1] as TextBlock; if (tb != null) tb.MaxWidth = tw - 8; }
            }
        }

        void Rebuild()
        {
            try { RebuildItems(); } finally { FitTiles(); }
        }

        void RebuildItems()
        {
            grid.Children.Clear();
            List<PinItem> items = StartStore.Load();
            if (!addMode)
            {
                leftTitle.Text = "Start";

                addText.Text = "+  Add apps";
                filterBox.Visibility = Visibility.Collapsed;
                foreach (PinItem p in items)
                {
                    PinItem e = p;
                    if (string.Equals(e.Target, StartStore.RecycleTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        Border rb = RecycleTile();
                        EnableSwap(rb, e.Target);
                        grid.Children.Add(rb);
                        continue;
                    }
                    Border t = Tiles.App(e.Name, Icons.For(e.Target), delegate { HidePanel(); Launcher.Open(e.Target); });
                    EnableSwap(t, e.Target);
                    if (e.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                        Tiles.Menu(t,
                            "Remove from Start", (Action)delegate { StartStore.Remove(e.Target); Rebuild(); },
                            "Pin to dock", (Action)delegate { PinStore.Add(e.Target, e.Name); });
                    else
                        Tiles.Menu(t,
                            "Remove from Start", (Action)delegate { StartStore.Remove(e.Target); Rebuild(); },
                            "Pin to dock", (Action)delegate { PinStore.Add(e.Target, e.Name); },
                            "Open file location", (Action)delegate { Tiles.OpenLocation(e.Target); });
                    grid.Children.Add(t);
                }
                if (items.Count(x => !string.Equals(x.Target, StartStore.RecycleTarget, StringComparison.OrdinalIgnoreCase)) == 0)
                {
                    TextBlock n = Tiles.Note("Click \"+ Add apps\" at the top right to choose the apps you want in Start.");
                    n.Width = 420;
                    n.Margin = new Thickness(12, 30, 8, 6);
                    grid.Children.Add(n);
                }
                return;
            }

            leftTitle.Text = "Choose apps for Start";
            addText.Text = "Done";
            filterBox.Visibility = Visibility.Visible;
            int gen = ++addGen;
            if (!Catalog.Ready)
            {
                // daftar app belum siap: tampilkan pesan, muat di latar, lalu susun ulang (dock tidak membeku)
                TextBlock wait = Tiles.Note("Loading apps...");
                wait.Margin = new Thickness(12, 30, 8, 6);
                grid.Children.Add(wait);
                Catalog.LoadAsync(delegate { if (addMode && gen == addGen) Rebuild(); });
                return;
            }
            HashSet<string> inStart = new HashSet<string>(items.Select(p => p.Target), StringComparer.OrdinalIgnoreCase);
            string q = filter.Text.Trim();
            HashSet<string> seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            List<AppEntry> apps = new List<AppEntry>();
            foreach (AppEntry a in Catalog.Desktop().Concat(Catalog.All()).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!seen.Add(a.Name)) continue;
                if (q.Length > 0 && a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                apps.Add(a);
            }
            // ~200 tile sekaligus membekukan panel ~0,6 detik: tampilkan 30 pertama langsung (yang terlihat),
            // sisanya ditambahkan bertahap di latar
            int idx = 0;
            Action addBatch = null;
            addBatch = delegate
            {
                if (gen != addGen || !addMode) return;
                List<KeyValuePair<Image, string>> icons = new List<KeyValuePair<Image, string>>();
                int n = idx == 0 ? 30 : 24;
                for (; idx < apps.Count && n > 0; idx++, n--) AddAppTile(apps[idx], inStart, icons);
                FitTiles();
                LoadIconsGradually(icons, gen);
                if (idx < apps.Count) Dispatcher.BeginInvoke(addBatch, DispatcherPriority.Background);
            };
            addBatch();
        }

        void AddAppTile(AppEntry a, HashSet<string> inStart, List<KeyValuePair<Image, string>> pendingIcons)
        {
                AppEntry e = a;
                Border check = CheckBadge();
                check.Visibility = inStart.Contains(e.Target) ? Visibility.Visible : Visibility.Collapsed;
                Border t = null;
                // klik = tambahkan (centang hijau muncul), klik lagi = hapus dari Start. App tetap di daftar.
                t = Tiles.App(e.Name, null, delegate
                {
                    if (inStart.Contains(e.Target)) { StartStore.Remove(e.Target); inStart.Remove(e.Target); }
                    else { StartStore.Add(e.Target, e.Name); inStart.Add(e.Target); }
                    bool on = inStart.Contains(e.Target);
                    check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    t.ToolTip = on ? e.Name + " is in Start (click to remove)" : "Click to add " + e.Name + " to Start";
                });
                t.ToolTip = inStart.Contains(e.Target) ? e.Name + " is in Start (click to remove)" : "Click to add " + e.Name + " to Start";
                StackPanel sp = t.Child as StackPanel;
                if (sp != null)
                {
                    t.Child = null;
                    Grid g = new Grid();
                    g.Children.Add(sp);
                    g.Children.Add(check);
                    t.Child = g;
                    Image img = sp.Children.Count > 0 ? sp.Children[0] as Image : null;
                    if (img != null) pendingIcons.Add(new KeyValuePair<Image, string>(img, e.Target));
                }
                grid.Children.Add(t);
        }

        int addGen;

        // centang hijau di pojok kanan atas tile
        static Border CheckBadge()
        {
            Border b = new Border();
            b.Width = 20;
            b.Height = 20;
            b.CornerRadius = new CornerRadius(10);
            b.Background = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255));
            b.BorderThickness = new Thickness(1.5);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.Margin = new Thickness(0, 4, 10, 0);
            b.IsHitTestVisible = false;
            TextBlock g = UI.Glyph("", 10);
            g.FontWeight = FontWeights.Bold;
            b.Child = g;
            return b;
        }

        // ikon dimuat sedikit demi sedikit (maks ~12 ms per giliran) agar panel tetap responsif
        void LoadIconsGradually(List<KeyValuePair<Image, string>> list, int gen)
        {
            int i = 0;
            Action step = null;
            step = delegate
            {
                if (gen != addGen || !addMode) return;
                Stopwatch sw = Stopwatch.StartNew();
                while (i < list.Count && sw.ElapsedMilliseconds < 12)
                {
                    try { list[i].Key.Source = Icons.For(list[i].Value); } catch { }
                    i++;
                }
                if (i < list.Count) Dispatcher.BeginInvoke(step, DispatcherPriority.Background);
            };
            Dispatcher.BeginInvoke(step, DispatcherPriority.Background);
        }
    }

    // ----- Wi-Fi versi dock -----
    class WifiNet { public string Ssid; public string Auth; public int Signal; public bool Known; public bool Connected; }

    static class Wifi
    {
        static string Run(string args)
        {
            string o;
            RunCode(args, out o);
            return o;
        }

        static int RunCode(string args, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) return -1;
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr h, ref Guid iface, uint flags, IntPtr res, out IntPtr list);

        // nama keamanan yang dipakai Connect() (cocok dengan teks netsh versi Inggris)
        static string AuthName(int algo, bool secured)
        {
            switch (algo)
            {
                case 1: return secured ? "WEP" : "Open";
                case 4: return "WPA-Personal";
                case 7: return "WPA2-Personal";
                case 9: return "WPA3-Personal";
                case 10: return "OWE";
                case 3: case 6: case 8: case 11: return "WPA2-Enterprise";
                default: return secured ? "Secured" : "Open";
            }
        }

        // daftar jaringan langsung dari Windows (WLAN API). Sama di semua bahasa Windows, tidak seperti
        // teks netsh ("Signal" / "Sinyal"...). null = API tidak tersedia (mis. PC tanpa Wi-Fi).
        public static List<WifiNet> NativeNetworks()
        {
            IntPtr h = IntPtr.Zero, ifs = IntPtr.Zero;
            try
            {
                uint ver;
                if (WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return null;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out ifs) != 0) return null;
                List<WifiNet> l = new List<WifiNet>();
                int count = Marshal.ReadInt32(ifs);
                for (int i = 0; i < count; i++)
                {
                    Guid g = (Guid)Marshal.PtrToStructure(new IntPtr(ifs.ToInt64() + 8 + i * 532), typeof(Guid));
                    IntPtr nl;
                    if (WlanGetAvailableNetworkList(h, ref g, 0, IntPtr.Zero, out nl) != 0) continue;
                    try
                    {
                        int n = Marshal.ReadInt32(nl);
                        for (int k = 0; k < n; k++)
                        {
                            long b = nl.ToInt64() + 8 + (long)k * 628;        // WLAN_AVAILABLE_NETWORK = 628 byte
                            int len = Marshal.ReadInt32(new IntPtr(b + 512));
                            if (len <= 0 || len > 32) continue;                 // jaringan tersembunyi
                            byte[] raw = new byte[len];
                            Marshal.Copy(new IntPtr(b + 516), raw, 0, len);
                            WifiNet w = new WifiNet();
                            w.Ssid = Encoding.UTF8.GetString(raw);
                            w.Signal = Marshal.ReadInt32(new IntPtr(b + 604));
                            bool sec = Marshal.ReadInt32(new IntPtr(b + 608)) != 0;
                            w.Auth = AuthName(Marshal.ReadInt32(new IntPtr(b + 612)), sec);
                            int flags = Marshal.ReadInt32(new IntPtr(b + 620));
                            w.Connected = (flags & 1) != 0;                    // WLAN_AVAILABLE_NETWORK_CONNECTED
                            w.Known = (flags & 2) != 0;                        // ..._HAS_PROFILE
                            l.Add(w);
                        }
                    }
                    finally { WlanFreeMemory(nl); }
                }
                return l;
            }
            catch { return null; }
            finally
            {
                if (ifs != IntPtr.Zero) WlanFreeMemory(ifs);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
        }

        public static List<WifiNet> Scan()
        {
            List<WifiNet> nat = NativeNetworks();
            if (nat != null)
            {
                // satu SSID bisa muncul dua kali (dengan & tanpa profil tersimpan): gabungkan
                List<WifiNet> merged = nat.Where(x => x.Ssid.Length > 0).GroupBy(x => x.Ssid).Select(g =>
                {
                    WifiNet m = g.OrderByDescending(x => x.Signal).First();
                    m.Signal = g.Max(x => x.Signal);
                    m.Known = g.Any(x => x.Known);
                    m.Connected = g.Any(x => x.Connected);
                    return m;
                }).ToList();
                return merged.OrderByDescending(x => x.Connected).ThenByDescending(x => x.Signal).ToList();
            }
            return ScanNetsh();
        }

        // cadangan bila WLAN API tidak tersedia (hanya akurat di Windows berbahasa Inggris)
        static List<WifiNet> ScanNetsh()
        {
            string cur = SysInfo.Ssid();
            HashSet<string> known = new HashSet<string>();
            foreach (Match m in Regex.Matches(Run("wlan show profiles"), @"All User Profile\s*:\s*(.+)$", RegexOptions.Multiline))
                known.Add(m.Groups[1].Value.Trim());
            List<WifiNet> l = new List<WifiNet>();
            WifiNet n = null;
            foreach (string raw in Run("wlan show networks mode=bssid").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                Match ms = Regex.Match(line, @"^SSID \d+\s*:\s*(.*)$");
                if (ms.Success) { n = new WifiNet(); n.Ssid = ms.Groups[1].Value.Trim(); l.Add(n); continue; }
                if (n == null) continue;
                Match ma = Regex.Match(line, @"^\s*Authentication\s*:\s*(.+)$");
                if (ma.Success) n.Auth = ma.Groups[1].Value.Trim();
                Match sg = Regex.Match(line, @"^\s*Signal\s*:\s*(\d+)%");
                if (sg.Success) n.Signal = Math.Max(n.Signal, int.Parse(sg.Groups[1].Value));
            }
            l = l.Where(x => x.Ssid.Length > 0).GroupBy(x => x.Ssid).Select(g => g.OrderByDescending(x => x.Signal).First()).ToList();
            foreach (WifiNet x in l) { x.Known = known.Contains(x.Ssid); x.Connected = x.Ssid == cur; }
            return l.OrderByDescending(x => x.Connected).ThenByDescending(x => x.Signal).ToList();
        }

        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr res, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr h, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr res);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr h, IntPtr res);

        // minta Windows memindai ulang jaringan sekitar (seperti saat panel Wi-Fi Windows dibuka)
        public static void TriggerScan()
        {
            IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                uint ver;
                if (WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return;
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(list.ToInt64() + 8 + i * 532);
                    Guid g = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                    WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
        }

        public static bool IsOpen(WifiNet n) { return (n.Auth ?? "").ToLowerInvariant().Contains("open"); }

        // mengembalikan pesan error, atau null bila perintah sambung terkirim
        public static string Connect(WifiNet n, string password)
        {
            if (!n.Known)
            {
                string a = (n.Auth ?? "").ToLowerInvariant();
                string auth, enc;
                if (a.Contains("enterprise")) return "Enterprise networks need Network settings.";
                if (a.Contains("open")) { auth = "open"; enc = "none"; }
                else if (a.Contains("wpa3")) { auth = "WPA3SAE"; enc = "AES"; }
                else if (a.Contains("wpa2")) { auth = "WPA2PSK"; enc = "AES"; }
                else if (a.Contains("wpa")) { auth = "WPAPSK"; enc = "AES"; }
                else return "This network type is not supported here.";
                string ssid = System.Security.SecurityElement.Escape(n.Ssid);
                string key = enc == "none" ? "" :
                    "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" +
                    System.Security.SecurityElement.Escape(password ?? "") + "</keyMaterial></sharedKey>";
                string xml = "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                    "<name>" + ssid + "</name><SSIDConfig><SSID><name>" + ssid + "</name></SSID></SSIDConfig>" +
                    "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security>" +
                    "<authEncryption><authentication>" + auth + "</authentication><encryption>" + enc + "</encryption><useOneX>false</useOneX></authEncryption>" +
                    key + "</security></MSM></WLANProfile>";
                string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sd-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    File.WriteAllText(file, xml);
                    Run("wlan add profile filename=\"" + file + "\" user=current");
                }
                finally { try { File.Delete(file); } catch { } }   // file berisi kata sandi: langsung dihapus
            }
            // berhasil/tidak dilihat dari kode keluar netsh, bukan teksnya: teks "successfully" hanya ada di
            // Windows berbahasa Inggris (dulu di bahasa lain sambungan dianggap gagal & profil baru dihapus)
            string o;
            int code = RunCode("wlan connect name=\"" + n.Ssid.Replace("\"", "") + "\"", out o);
            return code == 0 ? null : (o.Trim().Length > 0 ? o.Trim() : "Could not connect to " + n.Ssid + ".");
        }

        public static void Disconnect() { Run("wlan disconnect"); }

        public static void DeleteProfile(string ssid) { Run("wlan delete profile name=\"" + ssid.Replace("\"", "") + "\""); }
    }

    class WifiPanel : StackPanel
    {
        readonly StackPanel list = new StackPanel();
        readonly TextBlock status = new TextBlock();
        List<WifiNet> last = new List<WifiNet>();
        string expanded;
        string message;
        string busySsid;                // jaringan yang sedang connect/disconnect

        public WifiPanel()
        {
            StackPanel sp = this;
            sp.Width = 300;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = new TextBlock();
            title.Text = "Wi-Fi";
            title.Foreground = UI.Fg;
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.FontFamily = UI.Text;
            title.Margin = new Thickness(4, 0, 0, 0);
            head.Children.Add(title);
            Border refresh = IconButton("", "Refresh");
            refresh.MouseLeftButtonUp += delegate { Refresh(); };
            Grid.SetColumn(refresh, 1);
            head.Children.Add(refresh);
            sp.Children.Add(head);

            status.Foreground = UI.Ink(0xB0);
            status.FontSize = 12;
            status.FontFamily = UI.Text;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(4, 2, 4, 8);

            // tinggi tetap: panel tidak memanjang saat daftar jaringan muncul, jadi popup tidak melompat
            // (dock kiri/kanan di bawah layar: dulu turun melewati layar lalu naik lagi)
            Grid body = new Grid();
            body.Height = 340;
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(status);
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = list;
            Grid.SetRow(sv, 1);
            body.Children.Add(sv);
            sp.Children.Add(body);

            Border settings = new Border();
            settings.CornerRadius = new CornerRadius(10);
            settings.Padding = new Thickness(8, 7, 8, 7);
            settings.Margin = new Thickness(0, 8, 0, 0);
            settings.Background = Brushes.Transparent;
            settings.Cursor = Cursors.Hand;
            UI.Hover(settings, 0x30);
            StackPanel sr = new StackPanel();
            sr.Orientation = Orientation.Horizontal;
            sr.Children.Add(UI.Glyph("", 13));
            TextBlock st = new TextBlock();
            st.Text = "Network settings";
            st.Foreground = UI.Fg;
            st.FontSize = 12.5;
            st.FontFamily = UI.Text;
            st.Margin = new Thickness(10, 0, 0, 0);
            sr.Children.Add(st);
            settings.Child = sr;
            settings.MouseLeftButtonUp += delegate { if (OnClose != null) OnClose(); Launcher.Open("ms-settings:network-wifi"); };
            sp.Children.Add(settings);

        }

        public Action OnClose;

        static Border IconButton(string glyph, string tip)
        {
            Border b = new Border();
            b.Width = 30;
            b.Height = 28;
            b.CornerRadius = new CornerRadius(8);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = tip;
            UI.Hover(b, 0x30);
            b.Child = UI.Glyph(glyph, 13);
            return b;
        }

        public void Start()
        {
            expanded = null;
            message = null;
            list.Children.Clear();
            status.Text = "Scanning...";
            Refresh();
        }
        void Refresh()
        {
            if (message == null) status.Text = "Scanning...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<WifiNet> nets = Wifi.Scan();
                Dispatcher.BeginInvoke(new Action(delegate { Render(nets); }));
                Wifi.TriggerScan();
                Thread.Sleep(3500);
                List<WifiNet> fresh = Wifi.Scan();
                Dispatcher.BeginInvoke(new Action(delegate { if (IsVisible) Render(fresh); }));
            });
        }

        void Render(List<WifiNet> nets)
        {
            last = nets;
            list.Children.Clear();
            WifiNet cur = nets.FirstOrDefault(n => n.Connected);
            if (message != null) { status.Text = message; message = null; }
            else if (nets.Count == 0) status.Text = "No networks found.";
            else status.Text = cur != null ? "Connected to " + cur.Ssid : "Not connected";
            foreach (WifiNet n in nets) list.Children.Add(Row(n));
        }

        // tombol bulat di samping nama jaringan: satu-satunya cara connect / disconnect
        Border LinkButton(WifiNet n)
        {
            bool busy = busySsid == n.Ssid;
            Border b = new Border();
            b.Width = 30;
            b.Height = 30;
            b.CornerRadius = new CornerRadius(15);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Cursor = busy ? Cursors.Arrow : Cursors.Hand;
            b.ToolTip = busy ? (n.Connected ? "Disconnecting..." : "Connecting...") : n.Connected ? "Disconnect" : "Connect";
            Brush normal = n.Connected ? (Brush)new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)) : UI.Ink(0x26);
            Brush hover = n.Connected ? (Brush)new SolidColorBrush(Color.FromRgb(0x7D, 0xD8, 0xFF)) : UI.Ink(0x48);
            b.Background = normal;
            if (!busy)
            {
                b.MouseEnter += delegate { b.Background = hover; };
                b.MouseLeave += delegate { b.Background = normal; };
            }
            TextBlock g = UI.Glyph(busy ? "" : "", 14);
            if (n.Connected) g.Foreground = Brushes.Black;
            b.Child = g;
            return b;
        }

        FrameworkElement Row(WifiNet n)
        {
            StackPanel outer = new StackPanel();
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(10, 6, 6, 6);
            b.Margin = new Thickness(0, 1, 0, 1);
            bool open = expanded == n.Ssid;
            b.Background = open ? UI.Ink(0x26) : Brushes.Transparent;
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string bars = n.Signal < 30 ? "" : n.Signal < 55 ? "" : n.Signal < 80 ? "" : "";
            TextBlock sig = UI.Glyph(bars, 16);
            sig.Width = 22;
            g.Children.Add(sig);
            StackPanel txt = new StackPanel();
            txt.Margin = new Thickness(10, 0, 0, 0);
            txt.VerticalAlignment = VerticalAlignment.Center;
            TextBlock name = new TextBlock();
            name.Text = n.Ssid;
            name.Foreground = UI.Fg;
            name.FontSize = 13.5;
            name.FontFamily = UI.Text;
            name.FontWeight = n.Connected ? FontWeights.SemiBold : FontWeights.Normal;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.Children.Add(name);
            TextBlock sub = new TextBlock();
            sub.Text = (n.Connected ? "Connected" : Wifi.IsOpen(n) ? "Open" : "Secured") + (n.Known && !n.Connected ? " · saved" : "") + " · " + n.Signal + "%";
            sub.Foreground = n.Connected ? (Brush)UI.AccentInk : UI.Ink(0x90);
            sub.FontSize = 11;
            sub.FontFamily = UI.Text;
            txt.Children.Add(sub);
            Grid.SetColumn(txt, 1);
            g.Children.Add(txt);
            if (!Wifi.IsOpen(n))
            {
                TextBlock lk = UI.Glyph("", 12);
                lk.Opacity = 0.6;
                lk.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(lk, 2);
                g.Children.Add(lk);
            }
            Border link = LinkButton(n);
            Grid.SetColumn(link, 3);
            g.Children.Add(link);
            b.Child = g;
            outer.Children.Add(b);

            // jaringan baru yang berkunci: kotak kata sandi muncul di bawahnya, Enter atau ikon = connect
            PasswordBox pw = null;
            if (open && !n.Connected)
            {
                StackPanel act = new StackPanel();
                act.Margin = new Thickness(42, 4, 6, 8);
                Border pwBox = new Border();
                pwBox.CornerRadius = new CornerRadius(9);
                pwBox.Background = UI.Ink(0x26);
                pwBox.Padding = new Thickness(10, 6, 10, 6);
                pw = new PasswordBox();
                pw.Background = Brushes.Transparent;
                pw.BorderThickness = new Thickness(0);
                pw.Foreground = UI.Fg;
                pw.CaretBrush = UI.Fg;
                pw.FontSize = 13;
                pw.ToolTip = "Network password";
                pwBox.Child = pw;
                act.Children.Add(pwBox);
                TextBlock hint = new TextBlock();
                hint.Text = "Type the password, then press Enter or the link icon";
                hint.Foreground = UI.Ink(0x90);
                hint.FontSize = 11;
                hint.FontFamily = UI.Text;
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(2, 5, 0, 0);
                act.Children.Add(hint);
                outer.Children.Add(act);
                PasswordBox pwRef = pw;
                pw.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Enter && pwRef.Password.Length > 0) DoConnect(n, pwRef.Password);
                    else if (e.Key == Key.Escape) { expanded = null; Render(last); e.Handled = true; }
                };
                Dispatcher.BeginInvoke(new Action(delegate { pwRef.Focus(); }), DispatcherPriority.Input);
            }

            PasswordBox pwFinal = pw;
            link.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (busySsid != null) return;
                if (n.Connected)
                {
                    busySsid = n.Ssid;
                    Render(last);
                    status.Text = "Disconnecting from " + n.Ssid + "...";
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Wifi.Disconnect();
                        Thread.Sleep(1200);
                        Dispatcher.BeginInvoke(new Action(delegate { busySsid = null; message = "Disconnected from " + n.Ssid; Refresh(); }));
                    });
                    return;
                }
                if (n.Known || Wifi.IsOpen(n)) { DoConnect(n, null); return; }
                if (pwFinal != null && pwFinal.Password.Length > 0) { DoConnect(n, pwFinal.Password); return; }
                expanded = open ? null : n.Ssid;    // minta kata sandi dulu
                Render(last);
            };
            return outer;
        }

        void DoConnect(WifiNet n, string password)
        {
            busySsid = n.Ssid;
            Render(last);
            status.Text = "Connecting to " + n.Ssid + "...";
            bool wasKnown = n.Known;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = Wifi.Connect(n, password);
                bool ok = false;
                if (err == null)
                {
                    for (int i = 0; i < 12 && !ok; i++) { Thread.Sleep(700); ok = SysInfo.Ssid() == n.Ssid; }
                }
                if (!ok && !wasKnown) Wifi.DeleteProfile(n.Ssid);   // kata sandi salah: profil baru dibuang
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    busySsid = null;
                    message = ok ? "Connected to " + n.Ssid : (err ?? "Could not connect to " + n.Ssid + ". Check the password.");
                    if (ok) expanded = null;
                    Refresh();
                }));
            });
        }
    }

    // ----- Quick Settings versi dock -----
    class QuickPop : Popover
    {
        readonly TextBlock wifiName = new TextBlock();
        readonly TextBlock batText = new TextBlock();
        readonly TextBlock volIcon = UI.Glyph("", 16);
        readonly FlatSlider bright = new FlatSlider();
        readonly FlatSlider volume = new FlatSlider();
        readonly FrameworkElement brightRow;
        readonly DispatcherTimer brightTimer = new DispatcherTimer();
        int pendingBright = -1;
        public Action OnWifi;
        readonly WifiPanel wifiPanel = new WifiPanel();
        readonly Border wifiArea = new Border();

        // satu popup, beberapa tampilan: tiap ikon dock membuka pengaturannya sendiri,
        // "all" = Quick Settings lengkap (dibuka dari tautan "Quick Settings" di bawah tiap tampilan)
        public string Mode = "all";
        readonly Border card;
        readonly FrameworkElement allView;
        readonly StackPanel wifiView = new StackPanel();
        readonly StackPanel soundView = new StackPanel();
        readonly StackPanel batteryView = new StackPanel();
        readonly Border wifiHost = new Border();
        FlatSlider volume2 = new FlatSlider();
        readonly TextBlock volIcon2 = UI.Glyph("", 16);
        readonly TextBlock volPct = new TextBlock();
        readonly BatteryRing bigRing = new BatteryRing(56);
        readonly TextBlock batTitle = new TextBlock();
        readonly TextBlock batSub = new TextBlock();
        FlatSlider bright2 = new FlatSlider();
        FrameworkElement brightRow2;
        Action<double> onBright, onBrightDone;

        void ToggleWifi()
        {
            if (wifiArea.Visibility == Visibility.Visible) { wifiArea.Visibility = Visibility.Collapsed; return; }
            wifiArea.Visibility = Visibility.Visible;
            wifiPanel.Start();
        }

        public QuickPop()
        {
            card = UI.Card(20, 14);
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(16, 16, 16, 12);
            sp.Width = 336;

            UniformGrid tiles = new UniformGrid();
            tiles.Columns = 3;
            tiles.Children.Add(Tile("", "Wi-Fi", wifiName, "slidedock:wifi"));
            tiles.Children.Add(Tile("", "Bluetooth", null, "ms-settings:bluetooth"));
            tiles.Children.Add(Tile("", "Airplane mode", null, "ms-settings:network-airplanemode"));
            tiles.Children.Add(Tile("", "Energy saver", null, "ms-settings:batterysaver"));
            tiles.Children.Add(Tile("", "Night light", null, "ms-settings:nightlight"));
            tiles.Children.Add(Tile("", "Accessibility", null, "ms-settings:easeofaccess"));
            sp.Children.Add(tiles);

            brightRow = SliderRow(UI.Glyph("", 16), bright);
            brightRow.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(brightRow);
            foreach (TextBlock vi in new TextBlock[] { volIcon, volIcon2 })
            {
                vi.Cursor = Cursors.Hand;
                vi.ToolTip = "Mute / unmute";
                vi.MouseLeftButtonUp += delegate
                {
                    SysInfo.SetMute(!SysInfo.Muted());
                    UpdateVolIcon();
                };
            }
            FrameworkElement volRow = SliderRow(volIcon, volume);
            volRow.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(volRow);

            Border sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(0, 12, 0, 8);
            sep.Background = UI.Ink(0x26);
            sp.Children.Add(sep);

            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            batText.Foreground = UI.Fg;
            batText.FontSize = 12.5;
            batText.FontFamily = UI.Text;
            batText.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(batText);
            Border gear = new Border();
            gear.Width = 32;
            gear.Height = 30;
            gear.CornerRadius = new CornerRadius(8);
            gear.Background = Brushes.Transparent;
            gear.Cursor = Cursors.Hand;
            gear.ToolTip = "Settings";
            UI.Hover(gear, 0x30);
            gear.Child = UI.Glyph("", 14);
            gear.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open("ms-settings:"); };
            Grid.SetColumn(gear, 1);
            foot.Children.Add(gear);
            sp.Children.Add(foot);

            // Quick Settings + panel Wi-Fi di sebelah kanannya
            Grid two = new Grid();
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            two.Children.Add(sp);
            wifiArea.BorderBrush = UI.Ink(0x26);
            wifiArea.BorderThickness = new Thickness(1, 0, 0, 0);
            wifiArea.Margin = new Thickness(0, 14, 0, 12);
            wifiArea.Padding = new Thickness(14, 0, 14, 0);
            wifiArea.Child = wifiPanel;
            wifiArea.Visibility = Visibility.Collapsed;
            Grid.SetColumn(wifiArea, 1);
            two.Children.Add(wifiArea);
            wifiPanel.OnClose = delegate { HidePanel(); };
            allView = two;

            BuildWifiView();
            AnchorCenter = true;
            card.Child = allView;
            Content = card;

            volume.Changed = delegate(double v) { SysInfo.SetVolume(v); volume2.Value = v; UpdateVolIcon(); };
            brightTimer.Interval = TimeSpan.FromMilliseconds(120);
            brightTimer.Tick += delegate
            {
                brightTimer.Stop();
                int v = pendingBright;
                if (v >= 0) ThreadPool.QueueUserWorkItem(delegate { SysInfo.SetBrightness(v); });
            };
            onBright = delegate(double v)
            {
                pendingBright = (int)Math.Round(v * 100);
                bright.Value = v;
                bright2.Value = v;
                if (!brightTimer.IsEnabled) brightTimer.Start();
            };
            onBrightDone = delegate(double v)
            {
                pendingBright = (int)Math.Round(v * 100);
                brightTimer.Stop();
                int x = pendingBright;
                ThreadPool.QueueUserWorkItem(delegate { SysInfo.SetBrightness(x); });
            };
            bright.Changed = onBright;
            bright.Committed = onBrightDone;
            BuildSideViews();
        }

        // ----- tampilan per ikon -----
        static TextBlock Heading(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Foreground = UI.Fg;
            t.FontSize = 15;
            t.FontWeight = FontWeights.SemiBold;
            t.FontFamily = UI.Text;
            t.Margin = new Thickness(4, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        static Border LinkButton(string glyph, string text, bool chevron)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(8, 7, 8, 7);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            UI.Hover(b, 0x30);
            StackPanel sr = new StackPanel();
            sr.Orientation = Orientation.Horizontal;
            if (glyph != null) sr.Children.Add(UI.Glyph(glyph, 13));
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Foreground = UI.Fg;
            t.FontSize = 12.5;
            t.FontFamily = UI.Text;
            t.VerticalAlignment = VerticalAlignment.Center;
            if (glyph != null) t.Margin = new Thickness(10, 0, 0, 0);
            sr.Children.Add(t);
            if (chevron)
            {
                TextBlock c = UI.Glyph("", 10);
                c.Margin = new Thickness(8, 1, 0, 0);
                c.Opacity = 0.75;
                sr.Children.Add(c);
            }
            b.Child = sr;
            return b;
        }

        // baris bawah tiap tampilan: [pengaturan terkait]  ...  [Quick Settings >]
        FrameworkElement Footer(string glyph, string text, string uri, bool stacked)
        {
            StackPanel f = new StackPanel();
            Border sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(0, 10, 0, 6);
            sep.Background = UI.Ink(0x26);
            f.Children.Add(sep);
            if (stacked)
            {
                // kolom sempit: tautan ditumpuk, isinya di tengah
                if (text != null)
                {
                    Border l2 = LinkButton(glyph, text, false);
                    ((FrameworkElement)l2.Child).HorizontalAlignment = HorizontalAlignment.Center;
                    l2.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open(uri); };
                    f.Children.Add(l2);
                }
                Border a2 = LinkButton(null, "Quick Settings", true);
                ((FrameworkElement)a2.Child).HorizontalAlignment = HorizontalAlignment.Center;
                a2.MouseLeftButtonUp += delegate { SwitchMode("all"); };
                f.Children.Add(a2);
                return f;
            }
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (text != null)
            {
                Border left = LinkButton(glyph, text, false);
                left.HorizontalAlignment = HorizontalAlignment.Left;
                left.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open(uri); };
                g.Children.Add(left);
            }
            Border all = LinkButton(null, "Quick Settings", true);
            all.MouseLeftButtonUp += delegate { SwitchMode("all"); };
            Grid.SetColumn(all, 1);
            g.Children.Add(all);
            f.Children.Add(g);
            return f;
        }

        void BuildWifiView()
        {
            wifiView.Margin = new Thickness(16, 16, 16, 10);
            wifiView.Children.Add(wifiHost);
            wifiView.Children.Add(Footer(null, null, null, false));
        }

        static void Detach(FrameworkElement e)
        {
            Panel p = e.Parent as Panel;
            if (p != null) { p.Children.Remove(e); return; }
            Decorator dc = e.Parent as Decorator;
            if (dc != null) dc.Child = null;
        }

        // dock di kiri/kanan: tampilan volume & baterai dibuat tegak (slider vertikal, isi ditumpuk)
        bool builtV;

        void BuildSideViews()
        {
            builtV = Layout.V;
            BuildSoundView();
            BuildBatteryView();
        }

        void BuildSoundView()
        {
            soundView.Children.Clear();
            Detach(volIcon2);
            Detach(volPct);
            volume2 = new FlatSlider(Layout.V);
            volume2.Changed = delegate(double v) { SysInfo.SetVolume(v); volume.Value = v; UpdateVolIcon(); };
            volPct.Foreground = UI.Ink(0xB0);
            volPct.FontSize = 12.5;
            volPct.FontFamily = UI.Text;
            volPct.VerticalAlignment = VerticalAlignment.Center;
            soundView.Margin = new Thickness(16, 16, 16, 10);
            if (Layout.V)
            {
                soundView.Width = 140;
                TextBlock h = Heading("Volume");
                h.Margin = new Thickness(0);
                h.HorizontalAlignment = HorizontalAlignment.Center;
                soundView.Children.Add(h);
                volPct.HorizontalAlignment = HorizontalAlignment.Center;
                volPct.Margin = new Thickness(0, 2, 0, 12);
                soundView.Children.Add(volPct);
                volume2.Height = 180;
                volume2.HorizontalAlignment = HorizontalAlignment.Center;
                soundView.Children.Add(volume2);
                volIcon2.Margin = new Thickness(0, 12, 0, 0);
                volIcon2.HorizontalAlignment = HorizontalAlignment.Center;
                soundView.Children.Add(volIcon2);
                soundView.Children.Add(Footer("", "Sound settings", "ms-settings:sound", true));
                return;
            }
            soundView.Width = 300;
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Heading("Volume"));
            volPct.HorizontalAlignment = HorizontalAlignment.Right;
            volPct.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(volPct, 1);
            head.Children.Add(volPct);
            soundView.Children.Add(head);
            volIcon2.Margin = new Thickness(0);
            volIcon2.HorizontalAlignment = HorizontalAlignment.Center;
            FrameworkElement row = SliderRow(volIcon2, volume2);
            row.Margin = new Thickness(0, 12, 0, 2);
            soundView.Children.Add(row);
            soundView.Children.Add(Footer("", "Sound settings", "ms-settings:sound", false));
        }

        void BuildBatteryView()
        {
            batteryView.Children.Clear();
            foreach (FrameworkElement e in new FrameworkElement[] { bigRing, batTitle, batSub }) Detach(e);
            bright2 = new FlatSlider(Layout.V);
            bright2.Changed = onBright;
            bright2.Committed = onBrightDone;
            batteryView.Margin = new Thickness(16, 16, 16, 10);
            batTitle.Foreground = UI.Fg;
            batTitle.FontSize = 20;
            batTitle.FontWeight = FontWeights.SemiBold;
            batTitle.FontFamily = UI.Text;
            batSub.Foreground = UI.Ink(0xB0);
            batSub.FontSize = 12;
            batSub.FontFamily = UI.Text;
            batSub.TextWrapping = TextWrapping.Wrap;
            if (Layout.V)
            {
                batteryView.Width = 150;
                bigRing.Margin = new Thickness(0, 4, 0, 10);
                bigRing.HorizontalAlignment = HorizontalAlignment.Center;
                batteryView.Children.Add(bigRing);
                batTitle.HorizontalAlignment = HorizontalAlignment.Center;
                batteryView.Children.Add(batTitle);
                batSub.HorizontalAlignment = HorizontalAlignment.Center;
                batSub.TextAlignment = TextAlignment.Center;
                batteryView.Children.Add(batSub);
                StackPanel br = new StackPanel();
                br.Margin = new Thickness(0, 16, 0, 0);
                bright2.Height = 140;
                bright2.HorizontalAlignment = HorizontalAlignment.Center;
                br.Children.Add(bright2);
                TextBlock sun = UI.Glyph("", 16);
                sun.Margin = new Thickness(0, 10, 0, 0);
                br.Children.Add(sun);
                brightRow2 = br;
                batteryView.Children.Add(br);
                StackPanel tl = new StackPanel();
                tl.Margin = new Thickness(-3, 12, -3, 0);
                foreach (FrameworkElement tile in new FrameworkElement[] {
                    Tile("", "Energy saver", null, "ms-settings:batterysaver"),
                    Tile("", "Power & battery", null, "ms-settings:powersleep") })
                {
                    tile.Height = 60;
                    tl.Children.Add(tile);
                }
                batteryView.Children.Add(tl);
                batteryView.Children.Add(Footer(null, null, null, true));
                return;
            }
            batteryView.Width = 300;
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bigRing.Margin = new Thickness(4, 0, 14, 0);
            bigRing.HorizontalAlignment = HorizontalAlignment.Left;
            top.Children.Add(bigRing);
            StackPanel txt = new StackPanel();
            txt.VerticalAlignment = VerticalAlignment.Center;
            batTitle.HorizontalAlignment = HorizontalAlignment.Left;
            txt.Children.Add(batTitle);
            batSub.HorizontalAlignment = HorizontalAlignment.Left;
            batSub.TextAlignment = TextAlignment.Left;
            txt.Children.Add(batSub);
            Grid.SetColumn(txt, 1);
            top.Children.Add(txt);
            batteryView.Children.Add(top);
            brightRow2 = SliderRow(UI.Glyph("", 16), bright2);
            brightRow2.Margin = new Thickness(0, 14, 0, 0);
            batteryView.Children.Add(brightRow2);
            UniformGrid tiles = new UniformGrid();
            tiles.Columns = 2;
            tiles.Margin = new Thickness(-3, 10, -3, 0);
            tiles.Children.Add(Tile("", "Energy saver", null, "ms-settings:batterysaver"));
            tiles.Children.Add(Tile("", "Power & battery", null, "ms-settings:powersleep"));
            batteryView.Children.Add(tiles);
            batteryView.Children.Add(Footer(null, null, null, false));
        }

        void SwitchMode(string m)
        {
            Mode = m;
            if (builtV != Layout.V) BuildSideViews();
            wifiArea.Child = null;
            wifiHost.Child = null;
            if (m == "wifi") wifiHost.Child = wifiPanel;
            else { wifiArea.Child = wifiPanel; wifiArea.Visibility = Visibility.Collapsed; }
            card.Child = m == "wifi" ? (UIElement)wifiView : m == "sound" ? (UIElement)soundView : m == "battery" ? (UIElement)batteryView : allView;
            LoadData();
            if (m == "wifi") wifiPanel.Start();
        }

        void LoadData()
        {
            double v = SysInfo.Volume();
            volume.Value = v < 0 ? 0 : v;
            volume2.Value = volume.Value;
            UpdateVolIcon();
            batText.Text = "Battery " + SysInfo.BatteryText();
            UpdateBattery();
            bool wantSsid = Mode == "all";
            bool wantBright = Mode == "all" || Mode == "battery";
            if (wantSsid) wifiName.Text = "...";
            if (!wantSsid && !wantBright) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string ssid = wantSsid ? SysInfo.Ssid() : null;
                int b = wantBright ? SysInfo.Brightness() : -1;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (wantSsid) wifiName.Text = ssid ?? "Not connected";
                    if (!wantBright) return;
                    Visibility vis = b < 0 ? Visibility.Collapsed : Visibility.Visible;
                    brightRow.Visibility = vis;
                    brightRow2.Visibility = vis;
                    if (b >= 0) { bright.Value = b / 100.0; bright2.Value = b / 100.0; }
                }));
            });
        }

        void UpdateBattery()
        {
            WF.PowerStatus ps = WF.SystemInformation.PowerStatus;
            bool has = (ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            if (!has)
            {
                bigRing.Visibility = Visibility.Collapsed;
                batTitle.Text = "No battery";
                batSub.Text = "Running on AC power";
                return;
            }
            bigRing.Visibility = Visibility.Visible;
            int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
            bool charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;
            bigRing.Set(pct, charging);
            batTitle.Text = pct + "%";
            if (charging) batSub.Text = pct >= 100 ? "Fully charged" : "Charging";
            else
            {
                int s = ps.BatteryLifeRemaining;
                batSub.Text = s > 0 ? "About " + (s >= 3600 ? s / 3600 + " h " : "") + (s % 3600) / 60 + " min left" : "On battery";
            }
        }

        FrameworkElement SliderRow(FrameworkElement icon, FlatSlider s)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(icon);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            return g;
        }

        FrameworkElement Tile(string glyph, string label, TextBlock sub, string uri)
        {
            Border b = new Border();
            b.Height = 72;
            b.Margin = new Thickness(3);
            b.CornerRadius = new CornerRadius(12);
            b.Background = UI.Ink(0x1E);
            b.Cursor = Cursors.Hand;
            b.MouseEnter += delegate { b.Background = UI.Ink(0x38); };
            b.MouseLeave += delegate { b.Background = UI.Ink(0x1E); };
            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(UI.Glyph(glyph, 17));
            TextBlock t = new TextBlock();
            t.Text = label;
            t.Foreground = UI.Fg;
            t.FontSize = 11.5;
            t.FontFamily = UI.Text;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(t);
            if (sub != null)
            {
                sub.Foreground = UI.Ink(0xA0);
                sub.FontSize = 10;
                sub.FontFamily = UI.Text;
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                sub.TextTrimming = TextTrimming.CharacterEllipsis;
                sub.MaxWidth = 96;
                sp.Children.Add(sub);
            }
            b.Child = sp;
            b.MouseLeftButtonUp += delegate
            {
                if (uri == "slidedock:wifi") { ToggleWifi(); return; }   // Quick Settings tetap terbuka
                HidePanel();
                Launcher.Open(uri);
            };
            return b;
        }

        void UpdateVolIcon()
        {
            bool m = SysInfo.Muted();
            string g = m ? "" : "";
            volIcon.Text = g;
            volIcon2.Text = g;
            volume.Opacity = m ? 0.5 : 1;
            volume2.Opacity = volume.Opacity;
            double v = SysInfo.Volume();
            volPct.Text = m ? "Muted" : v < 0 ? "" : Math.Round(v * 100) + "%";
        }

        // mode: "wifi", "sound", "battery" atau "all"; centerX = tengah ikon dock yang diklik
        public void ShowAt(string mode, Point center, double dockTop)
        {
            if (IsOpen) return;
            SysInfo.ResetAudio();
            SideAnchor = center.Y;
            SwitchMode(mode);
            SlideIn(delegate(Size s) { return new Point(AlongX(s.Width, center.X), dockTop - s.Height + 6); });
        }

        // sudah terbuka, ikon lain diklik: ganti isi & pindah ke atas ikon itu tanpa menutup
        public void Switch(string mode, Point center)
        {
            if (!IsOpen) return;
            SwitchMode(mode);
            UpdateLayout();
            Reanchor(center);
        }
    }

    // ----- notifikasi Windows (dibaca lewat UserNotificationListener) -----
    class NotifItem { public uint Id; public string App; public string Title; public string Body; public DateTime Time; }

    static class Notifs
    {
        static global::Windows.UI.Notifications.Management.UserNotificationListener L
        {
            get { return global::Windows.UI.Notifications.Management.UserNotificationListener.Current; }
        }

        // tunggu operasi WinRT selesai (dipanggil dari thread latar). Tanpa AsTask, supaya bisa di-build
        // dengan metadata bawaan Windows (System32\WinMetadata) tanpa perlu Windows SDK.
        public static T Wait<T>(global::Windows.Foundation.IAsyncOperation<T> op)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (op.Status == global::Windows.Foundation.AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > 10000) { op.Cancel(); throw new TimeoutException(); }
                Thread.Sleep(10);
            }
            if (op.Status != global::Windows.Foundation.AsyncStatus.Completed) throw new InvalidOperationException("WinRT call " + op.Status);
            return op.GetResults();
        }

        // null = tidak ada akses
        public static List<NotifItem> Get()
        {
            List<NotifItem> res = new List<NotifItem>();
            try
            {
                var st = Wait(L.RequestAccessAsync());
                if (st != global::Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) return null;
                var list = Wait(L.GetNotificationsAsync(global::Windows.UI.Notifications.NotificationKinds.Toast));
                foreach (var n in list)
                {
                    NotifItem it = new NotifItem();
                    it.Id = n.Id;
                    it.Time = n.CreationTime.LocalDateTime;
                    try { it.App = n.AppInfo.DisplayInfo.DisplayName; } catch { it.App = "App"; }
                    try
                    {
                        var b = n.Notification.Visual.GetBinding(global::Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                        if (b != null)
                        {
                            var els = b.GetTextElements();
                            if (els.Count > 0) it.Title = els[0].Text;
                            if (els.Count > 1) it.Body = string.Join("\n", els.Skip(1).Select(x => x.Text).ToArray());
                        }
                    }
                    catch { }
                    res.Add(it);
                }
            }
            catch { return null; }
            return res.OrderByDescending(x => x.Time).ToList();
        }

        public static void Remove(uint id) { try { L.RemoveNotification(id); } catch { } }
        public static void ClearAll() { try { L.ClearNotifications(); } catch { } }
    }

    // ----- kalender -----
    // ================= kalender: hari libur (menurut region Windows) & Google Calendar (link iCal) =================
    class CalEvent
    {
        public string Uid, Title;
        public DateTime Start, End;            // waktu lokal. Acara seharian: End = hari SETELAH hari terakhir
        public bool AllDay, Cancelled;
        public int Kind;                       // 0 = acara pribadi, 1 = libur nasional, 2 = perayaan
        public string Rrule;
        public List<DateTime> ExDates = new List<DateTime>();
        public DateTime? RecurrenceId;
    }

    class DayItem { public string Title; public int Kind; public DateTime Start, End; public bool AllDay; }

    // pembaca file iCal (.ics): cukup untuk kalender Google/Outlook/iCloud, termasuk acara berulang yang umum
    static class Ics
    {
        public static List<CalEvent> Parse(string text, bool holidays)
        {
            List<CalEvent> all = new List<CalEvent>();
            if (string.IsNullOrEmpty(text)) return all;
            text = text.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
            CalEvent e = null;
            string desc = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line == "BEGIN:VEVENT") { e = new CalEvent(); desc = null; continue; }
                if (line == "END:VEVENT")
                {
                    if (e != null && e.Start != DateTime.MinValue)
                    {
                        if (e.End <= e.Start) e.End = e.AllDay ? e.Start.AddDays(1) : e.Start;
                        // hari libur Google: "perayaan" punya deskripsi dua baris ("...untuk menyembunyikan..."),
                        // libur nasional satu baris. Sama di semua bahasa.
                        e.Kind = !holidays ? 0 : (desc != null && desc.Contains("\n") ? 2 : 1);
                        all.Add(e);
                    }
                    e = null;
                    continue;
                }
                if (e == null) continue;
                int colon = ValueStart(line);
                if (colon < 0) continue;
                string head = line.Substring(0, colon), value = line.Substring(colon + 1);
                int semi = head.IndexOf(';');
                string name = (semi < 0 ? head : head.Substring(0, semi)).ToUpperInvariant();
                string pars = semi < 0 ? "" : head.Substring(semi + 1);
                bool allDay;
                DateTime d;
                switch (name)
                {
                    case "SUMMARY": e.Title = Unescape(value); break;
                    case "DESCRIPTION": desc = Unescape(value); break;
                    case "UID": e.Uid = value; break;
                    case "STATUS": e.Cancelled = value.Trim().ToUpperInvariant() == "CANCELLED"; break;
                    case "RRULE": e.Rrule = value; break;
                    case "DTSTART": if (ParseDate(pars, value, out d, out allDay)) { e.Start = d; e.AllDay = allDay; } break;
                    case "DTEND": if (ParseDate(pars, value, out d, out allDay)) e.End = d; break;
                    case "DURATION": { TimeSpan ts; if (ParseDuration(value, out ts)) e.End = e.Start + ts; } break;
                    case "RECURRENCE-ID": if (ParseDate(pars, value, out d, out allDay)) e.RecurrenceId = d; break;
                    case "EXDATE":
                        foreach (string part in value.Split(','))
                            if (ParseDate(pars, part, out d, out allDay)) e.ExDates.Add(d);
                        break;
                }
            }
            // acara berulang yang satu kejadiannya diubah/dibatalkan: kejadian asli dikecualikan dari pola
            Dictionary<string, CalEvent> masters = new Dictionary<string, CalEvent>();
            foreach (CalEvent x in all) if (x.Rrule != null && x.Uid != null && !x.RecurrenceId.HasValue) masters[x.Uid] = x;
            List<CalEvent> res = new List<CalEvent>();
            foreach (CalEvent x in all)
            {
                CalEvent m;
                if (x.RecurrenceId.HasValue && x.Uid != null && masters.TryGetValue(x.Uid, out m)) m.ExDates.Add(x.RecurrenceId.Value);
                if (!x.Cancelled) res.Add(x);
            }
            return res;
        }

        static int ValueStart(string line)
        {
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') q = !q;
                else if (line[i] == ':' && !q) return i;
            }
            return -1;
        }

        static string Unescape(string v)
        {
            return v.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
        }

        static bool ParseDuration(string v, out TimeSpan ts)
        {
            ts = TimeSpan.Zero;
            Match m = Regex.Match(v.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
            if (!m.Success) return false;
            Func<int, int> g = delegate(int i) { return m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0; };
            ts = new TimeSpan(g(2) * 7 + g(3), g(4), g(5), g(6));
            if (m.Groups[1].Value == "-") ts = ts.Negate();
            return true;
        }

        // nama zona waktu IANA (dipakai Google) -> zona Windows
        static readonly Dictionary<string, string> Iana = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Asia/Jakarta", "SE Asia Standard Time" }, { "Asia/Pontianak", "SE Asia Standard Time" },
            { "Asia/Makassar", "Singapore Standard Time" }, { "Asia/Jayapura", "Tokyo Standard Time" },
            { "Asia/Bangkok", "SE Asia Standard Time" }, { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" }, { "Asia/Saigon", "SE Asia Standard Time" },
            { "Asia/Singapore", "Singapore Standard Time" }, { "Asia/Kuala_Lumpur", "Singapore Standard Time" }, { "Asia/Manila", "Singapore Standard Time" },
            { "Asia/Shanghai", "China Standard Time" }, { "Asia/Hong_Kong", "China Standard Time" }, { "Asia/Taipei", "Taipei Standard Time" },
            { "Asia/Tokyo", "Tokyo Standard Time" }, { "Asia/Seoul", "Korea Standard Time" }, { "Asia/Kolkata", "India Standard Time" }, { "Asia/Calcutta", "India Standard Time" },
            { "Asia/Dubai", "Arabian Standard Time" }, { "Asia/Riyadh", "Arab Standard Time" }, { "Asia/Karachi", "Pakistan Standard Time" }, { "Asia/Dhaka", "Bangladesh Standard Time" },
            { "Europe/London", "GMT Standard Time" }, { "Europe/Dublin", "GMT Standard Time" }, { "Europe/Lisbon", "GMT Standard Time" },
            { "Europe/Paris", "Romance Standard Time" }, { "Europe/Brussels", "Romance Standard Time" }, { "Europe/Madrid", "Romance Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" }, { "Europe/Amsterdam", "W. Europe Standard Time" }, { "Europe/Rome", "W. Europe Standard Time" },
            { "Europe/Vienna", "W. Europe Standard Time" }, { "Europe/Zurich", "W. Europe Standard Time" }, { "Europe/Stockholm", "W. Europe Standard Time" },
            { "Europe/Warsaw", "Central European Standard Time" }, { "Europe/Prague", "Central Europe Standard Time" }, { "Europe/Athens", "GTB Standard Time" },
            { "Europe/Istanbul", "Turkey Standard Time" }, { "Europe/Moscow", "Russian Standard Time" }, { "Europe/Kiev", "FLE Standard Time" }, { "Europe/Kyiv", "FLE Standard Time" },
            { "America/New_York", "Eastern Standard Time" }, { "America/Toronto", "Eastern Standard Time" }, { "America/Chicago", "Central Standard Time" },
            { "America/Denver", "Mountain Standard Time" }, { "America/Phoenix", "US Mountain Standard Time" }, { "America/Los_Angeles", "Pacific Standard Time" },
            { "America/Vancouver", "Pacific Standard Time" }, { "America/Mexico_City", "Central Standard Time (Mexico)" }, { "America/Sao_Paulo", "E. South America Standard Time" },
            { "America/Argentina/Buenos_Aires", "Argentina Standard Time" }, { "America/Bogota", "SA Pacific Standard Time" }, { "America/Lima", "SA Pacific Standard Time" },
            { "Australia/Sydney", "AUS Eastern Standard Time" }, { "Australia/Melbourne", "AUS Eastern Standard Time" }, { "Australia/Brisbane", "E. Australia Standard Time" },
            { "Australia/Perth", "W. Australia Standard Time" }, { "Pacific/Auckland", "New Zealand Standard Time" },
            { "Africa/Cairo", "Egypt Standard Time" }, { "Africa/Johannesburg", "South Africa Standard Time" }, { "Africa/Lagos", "W. Central Africa Standard Time" },
            { "Africa/Nairobi", "E. Africa Standard Time" }, { "UTC", "UTC" }, { "Etc/UTC", "UTC" }, { "GMT", "UTC" }
        };

        static TimeZoneInfo Zone(string tzid)
        {
            if (string.IsNullOrEmpty(tzid)) return null;
            tzid = tzid.Trim('"');
            string win;
            try { return TimeZoneInfo.FindSystemTimeZoneById(Iana.TryGetValue(tzid, out win) ? win : tzid); }
            catch { return null; }
        }

        static bool ParseDate(string pars, string value, out DateTime d, out bool allDay)
        {
            d = DateTime.MinValue;
            allDay = false;
            value = value.Trim();
            if (value.Length == 8 || pars.IndexOf("VALUE=DATE", StringComparison.OrdinalIgnoreCase) >= 0 && value.Length == 8)
            {
                allDay = true;
                return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
            }
            bool utc = value.EndsWith("Z");
            if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return false;
            if (utc) { d = DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime(); d = DateTime.SpecifyKind(d, DateTimeKind.Unspecified); return true; }
            Match tz = Regex.Match(pars, @"TZID=(""[^""]*""|[^;:]*)", RegexOptions.IgnoreCase);
            TimeZoneInfo z = tz.Success ? Zone(tz.Groups[1].Value) : null;
            if (z != null)
            {
                try { d = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), z, TimeZoneInfo.Local), DateTimeKind.Unspecified); }
                catch { }
            }
            return true;                                         // tanpa zona: waktu "mengambang" = waktu lokal
        }

        static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        // kejadian acara yang bersinggungan dengan [from, to)
        public static IEnumerable<KeyValuePair<DateTime, DateTime>> Occurrences(CalEvent e, DateTime from, DateTime to)
        {
            TimeSpan dur = e.End - e.Start;
            if (e.Rrule == null)
            {
                if (e.Start < to && (e.End > from || e.Start >= from)) yield return new KeyValuePair<DateTime, DateTime>(e.Start, e.End);
                yield break;
            }
            Dictionary<string, string> r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in e.Rrule.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) r[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            string freq = r.ContainsKey("FREQ") ? r["FREQ"].ToUpperInvariant() : "";
            int interval = 1, count = 0;
            if (r.ContainsKey("INTERVAL")) int.TryParse(r["INTERVAL"], out interval);
            if (interval < 1) interval = 1;
            if (r.ContainsKey("COUNT")) int.TryParse(r["COUNT"], out count);
            DateTime until = DateTime.MaxValue;
            if (r.ContainsKey("UNTIL"))
            {
                DateTime u; bool ad;
                if (ParseDate("", r["UNTIL"], out u, out ad)) until = ad ? u.AddDays(1).AddTicks(-1) : u;
            }
            List<KeyValuePair<int, int>> byDay = new List<KeyValuePair<int, int>>();    // (urutan, hari) mis. -1FR
            if (r.ContainsKey("BYDAY"))
                foreach (string bd in r["BYDAY"].Split(','))
                {
                    string s = bd.Trim().ToUpperInvariant();
                    if (s.Length < 2) continue;
                    int dow = Array.IndexOf(DayCodes, s.Substring(s.Length - 2));
                    int ord = 0;
                    if (s.Length > 2) int.TryParse(s.Substring(0, s.Length - 2), out ord);
                    if (dow >= 0) byDay.Add(new KeyValuePair<int, int>(ord, dow));
                }
            List<int> byMonthDay = new List<int>(), byMonth = new List<int>();
            if (r.ContainsKey("BYMONTHDAY")) foreach (string s in r["BYMONTHDAY"].Split(',')) { int v; if (int.TryParse(s, out v)) byMonthDay.Add(v); }
            if (r.ContainsKey("BYMONTH")) foreach (string s in r["BYMONTH"].Split(',')) { int v; if (int.TryParse(s, out v)) byMonth.Add(v); }

            int n = 0;
            TimeSpan tod = e.Start.TimeOfDay;
            for (int p = 0; p < 5000; p++)
            {
                List<DateTime> cands = new List<DateTime>();
                if (freq == "DAILY") cands.Add(e.Start.AddDays((double)p * interval));
                else if (freq == "WEEKLY")
                {
                    DateTime week = e.Start.Date.AddDays(-(((int)e.Start.DayOfWeek + 6) % 7)).AddDays(7.0 * p * interval);
                    if (byDay.Count == 0) cands.Add(week.AddDays(((int)e.Start.DayOfWeek + 6) % 7) + tod);
                    else foreach (KeyValuePair<int, int> bd in byDay) cands.Add(week.AddDays((bd.Value + 6) % 7) + tod);
                }
                else if (freq == "MONTHLY")
                {
                    DateTime m = new DateTime(e.Start.Year, e.Start.Month, 1).AddMonths(p * interval);
                    if (byMonth.Count == 0 || byMonth.Contains(m.Month)) cands.AddRange(InMonth(m, e.Start, byDay, byMonthDay));
                }
                else if (freq == "YEARLY")
                {
                    int y = e.Start.Year + p * interval;
                    if (y > 9998) break;
                    List<int> months = byMonth.Count > 0 ? byMonth : new List<int> { e.Start.Month };
                    foreach (int mo in months) cands.AddRange(InMonth(new DateTime(y, mo, 1), e.Start, byDay, byMonthDay));
                }
                else yield break;
                cands.Sort();
                foreach (DateTime c in cands)
                {
                    if (c < e.Start) continue;
                    if (c > until) yield break;
                    n++;
                    if (count > 0 && n > count) yield break;
                    if (c >= to) yield break;
                    bool ex = e.ExDates.Any(x => e.AllDay ? x.Date == c.Date : Math.Abs((x - c).TotalMinutes) < 1);
                    if (!ex && c + dur > from) yield return new KeyValuePair<DateTime, DateTime>(c, c + dur);
                }
            }
        }

        // tanggal-tanggal kejadian dalam satu bulan menurut BYDAY / BYMONTHDAY (atau tanggal yang sama dengan awal acara)
        static List<DateTime> InMonth(DateTime month, DateTime start, List<KeyValuePair<int, int>> byDay, List<int> byMonthDay)
        {
            List<DateTime> l = new List<DateTime>();
            int dim = DateTime.DaysInMonth(month.Year, month.Month);
            TimeSpan tod = start.TimeOfDay;
            if (byMonthDay.Count > 0)
            {
                foreach (int md in byMonthDay)
                {
                    int day = md > 0 ? md : dim + md + 1;
                    if (day >= 1 && day <= dim) l.Add(new DateTime(month.Year, month.Month, day) + tod);
                }
            }
            else if (byDay.Count > 0)
            {
                foreach (KeyValuePair<int, int> bd in byDay)
                {
                    List<int> days = new List<int>();
                    for (int day = 1; day <= dim; day++) if ((int)new DateTime(month.Year, month.Month, day).DayOfWeek == bd.Value) days.Add(day);
                    if (bd.Key == 0) foreach (int day in days) l.Add(new DateTime(month.Year, month.Month, day) + tod);
                    else
                    {
                        int idx = bd.Key > 0 ? bd.Key - 1 : days.Count + bd.Key;
                        if (idx >= 0 && idx < days.Count) l.Add(new DateTime(month.Year, month.Month, days[idx]) + tod);
                    }
                }
            }
            else if (start.Day <= dim) l.Add(new DateTime(month.Year, month.Month, start.Day) + tod);
            return l;
        }
    }

    static class CalData
    {
        public static Action Changed;                    // dipanggil di thread UI saat data berubah
        static List<CalEvent> holidays = new List<CalEvent>(), personal = new List<CalEvent>();
        static readonly string Dir = System.IO.Path.Combine(Store.Dir, "calendar");
        static bool busy;
        public static string PersonalError;
        public static DateTime PersonalSynced = DateTime.MinValue;

        public static bool HolidaysOn { get { return Prefs.Get("holidays", 1) != 0; } set { Prefs.Set("holidays", value ? 1 : 0); } }
        public static bool HolidayLocal { get { return Prefs.Get("holidaylocal", 1) != 0; } set { Prefs.Set("holidaylocal", value ? 1 : 0); } }
        public static bool RedSundays { get { return Prefs.Get("redsunday", 1) != 0; } set { Prefs.Set("redsunday", value ? 1 : 0); } }

        // kalender hari libur Google per negara: (nama kalender, bahasa lokal). Diverifikasi satu per satu.
        // Negara lain memakai kode negaranya langsung (format baru Google) dengan nama dalam bahasa Inggris.
        static readonly Dictionary<string, string[]> Feeds = new Dictionary<string, string[]>
        {
            { "ID", new[] { "indonesian", "id" } }, { "MY", new[] { "malaysia", "ms" } }, { "SG", new[] { "singapore", null } },
            { "PH", new[] { "philippines", "fil" } }, { "TH", new[] { "th", "th" } }, { "VN", new[] { "vietnamese", "vi" } },
            { "JP", new[] { "japanese", "ja" } }, { "KR", new[] { "south_korea", "ko" } }, { "CN", new[] { "china", "zh-CN" } },
            { "TW", new[] { "taiwan", "zh-TW" } }, { "HK", new[] { "hong_kong", "zh-TW" } }, { "IN", new[] { "indian", "hi" } },
            { "BD", new[] { "bd", "bn" } }, { "PK", new[] { "pk", "ur" } }, { "LK", new[] { "lk", "si" } }, { "NP", new[] { "np", "ne" } },
            { "MM", new[] { "mm", "my" } }, { "KH", new[] { "kh", "km" } }, { "LA", new[] { "la", "lo" } }, { "BN", new[] { "bn", null } },
            { "AU", new[] { "australian", null } }, { "NZ", new[] { "new_zealand", null } }, { "US", new[] { "usa", null } },
            { "CA", new[] { "canadian", null } }, { "GB", new[] { "uk", null } }, { "IE", new[] { "irish", null } },
            { "DE", new[] { "german", "de" } }, { "AT", new[] { "austrian", "de" } }, { "CH", new[] { "ch", "de" } },
            { "FR", new[] { "french", "fr" } }, { "BE", new[] { "be", "nl" } }, { "NL", new[] { "dutch", "nl" } },
            { "IT", new[] { "italian", "it" } }, { "ES", new[] { "spain", "es" } }, { "PT", new[] { "portuguese", "pt-PT" } },
            { "BR", new[] { "brazilian", "pt-BR" } }, { "MX", new[] { "mexican", "es" } }, { "AR", new[] { "ar", "es" } },
            { "CL", new[] { "cl", "es" } }, { "CO", new[] { "co", "es" } }, { "PE", new[] { "pe", "es" } },
            { "PL", new[] { "polish", "pl" } }, { "CZ", new[] { "czech", "cs" } }, { "SK", new[] { "slovak", "sk" } },
            { "HU", new[] { "hungarian", "hu" } }, { "RO", new[] { "romanian", "ro" } }, { "BG", new[] { "bulgarian", "bg" } },
            { "HR", new[] { "croatian", "hr" } }, { "SI", new[] { "slovenian", "sl" } }, { "GR", new[] { "greek", "el" } },
            { "TR", new[] { "turkish", "tr" } }, { "RU", new[] { "russian", "ru" } }, { "UA", new[] { "ukrainian", "uk" } },
            { "SE", new[] { "swedish", "sv" } }, { "NO", new[] { "norwegian", "no" } }, { "DK", new[] { "danish", "da" } },
            { "FI", new[] { "finnish", "fi" } }, { "LV", new[] { "latvian", "lv" } }, { "LT", new[] { "lithuanian", "lt" } },
            { "SA", new[] { "saudiarabian", "ar" } }, { "AE", new[] { "ae", "ar" } }, { "EG", new[] { "eg", "ar" } },
            { "QA", new[] { "qa", "ar" } }, { "KW", new[] { "kw", "ar" } }, { "MA", new[] { "ma", "ar" } },
            { "IL", new[] { "jewish", "iw" } }, { "ZA", new[] { "sa", null } }, { "NG", new[] { "ng", null } }, { "KE", new[] { "ke", null } }
        };

        [DllImport("kernel32.dll")] static extern int GetUserGeoID(int geoClass);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetGeoInfo(int location, int geoType, StringBuilder data, int len, int langId);

        // negara dari Settings > Time & language > Language & region > "Country or region"
        // (bukan format tanggal: laptop bisa berformat US tapi negaranya Indonesia)
        public static string Country
        {
            get
            {
                try
                {
                    int geo = GetUserGeoID(16);                 // GEOCLASS_NATION
                    StringBuilder sb = new StringBuilder(8);
                    if (geo != -1 && GetGeoInfo(geo, 4, sb, sb.Capacity, 0) > 0 && sb.Length == 2) return sb.ToString().ToUpperInvariant();   // GEO_ISO2
                }
                catch { }
                try { return RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant(); } catch { return "US"; }
            }
        }

        public static string CountryName
        {
            get { try { return new RegionInfo(Country).EnglishName; } catch { return Country; } }
        }

        // bahasa lokal negara ini (null = hanya bahasa Inggris)
        public static string LocalLanguage
        {
            get { string[] f; return Feeds.TryGetValue(Country, out f) ? f[1] : null; }
        }

        static string HolidayUrl(out string file)
        {
            string cc = Country;
            string[] f;
            string slug = Feeds.TryGetValue(cc, out f) ? f[0] : cc.ToLowerInvariant();
            string lang = HolidayLocal && f != null && f[1] != null ? f[1] : "en";
            file = System.IO.Path.Combine(Dir, "holidays-" + cc + "-" + lang + ".ics");
            return "https://calendar.google.com/calendar/ical/" + lang + "." + slug + "%23holiday%40group.v.calendar.google.com/public/basic.ics";
        }

        // ----- link iCal pribadi (Google Calendar "Secret address in iCal format"): disimpan terenkripsi (DPAPI) -----
        static string UrlFile { get { return System.IO.Path.Combine(Dir, "personal-url.dat"); } }
        static string PersonalFile { get { return System.IO.Path.Combine(Dir, "personal.ics"); } }

        public static string PersonalUrl
        {
            get
            {
                try
                {
                    if (!File.Exists(UrlFile)) return null;
                    byte[] b = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(UrlFile), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(b);
                }
                catch { return null; }
            }
        }

        static void SaveUrl(string url)
        {
            Directory.CreateDirectory(Dir);
            if (url == null) { if (File.Exists(UrlFile)) File.Delete(UrlFile); return; }
            File.WriteAllBytes(UrlFile, System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(url), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }

        static string Download(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "SlideDock/" + AppInfo.Version;
                wc.Encoding = Encoding.UTF8;
                string s = wc.DownloadString(url);
                if (s.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("That link isn't an iCal calendar.");
                return s;
            }
        }

        static bool Stale(string file, TimeSpan age) { return !File.Exists(file) || DateTime.Now - File.GetLastWriteTime(file) > age; }

        // muat dari cache, unduh yang sudah lama (libur: 7 hari, pribadi: 10 menit), lalu kabari UI
        public static void Refresh(Dispatcher ui, bool force)
        {
            if (busy) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<CalEvent> h = new List<CalEvent>(), mine = new List<CalEvent>();
                string err = null;
                bool synced = false;
                try
                {
                    Directory.CreateDirectory(Dir);
                    if (HolidaysOn)
                    {
                        string file;
                        string url = HolidayUrl(out file);
                        if (force || Stale(file, TimeSpan.FromDays(7)))
                            try { File.WriteAllText(file, Download(url), Encoding.UTF8); } catch { }   // offline: pakai cache
                        if (File.Exists(file)) h = Ics.Parse(File.ReadAllText(file, Encoding.UTF8), true);
                    }
                    string purl = PersonalUrl;
                    if (purl != null)
                    {
                        if (force || Stale(PersonalFile, TimeSpan.FromMinutes(10)))
                        {
                            try { File.WriteAllText(PersonalFile, Download(purl), Encoding.UTF8); synced = true; }
                            catch (Exception ex) { err = ex.Message; }
                        }
                        if (File.Exists(PersonalFile)) mine = Ics.Parse(File.ReadAllText(PersonalFile, Encoding.UTF8), false);
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                ui.BeginInvoke(new Action(delegate
                {
                    busy = false;
                    holidays = h;
                    personal = mine;
                    PersonalError = err;
                    if (synced) PersonalSynced = DateTime.Now;
                    else if (PersonalSynced == DateTime.MinValue && File.Exists(PersonalFile)) PersonalSynced = File.GetLastWriteTime(PersonalFile);
                    if (Changed != null) Changed();
                }));
            });
        }

        // simpan link kalender pribadi setelah dicek bisa dibaca; done(pesan error atau null) di thread UI
        public static void Connect(string url, Dispatcher ui, Action<string> done)
        {
            url = (url ?? "").Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.Substring(9);
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                done("Paste the iCal link (it starts with https://).");
                return;
            }
            string u = url;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    string ics = Download(u);
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(PersonalFile, ics, Encoding.UTF8);
                    SaveUrl(u);
                }
                catch (Exception ex) { err = ex.Message; }
                ui.BeginInvoke(new Action(delegate
                {
                    if (err == null) { PersonalSynced = DateTime.Now; PersonalError = null; Refresh(ui, false); }
                    done(err);
                }));
            });
        }

        public static void Disconnect(Dispatcher ui)
        {
            try { SaveUrl(null); if (File.Exists(PersonalFile)) File.Delete(PersonalFile); } catch { }
            PersonalSynced = DateTime.MinValue;
            PersonalError = null;
            Refresh(ui, false);
        }

        // semua hari libur & acara per tanggal dalam rentang [from, to)
        public static Dictionary<DateTime, List<DayItem>> Range(DateTime from, DateTime to)
        {
            Dictionary<DateTime, List<DayItem>> map = new Dictionary<DateTime, List<DayItem>>();
            foreach (List<CalEvent> src in new List<CalEvent>[] { HolidaysOn ? holidays : new List<CalEvent>(), personal })
                foreach (CalEvent e in src)
                {
                    IEnumerable<KeyValuePair<DateTime, DateTime>> occ;
                    try { occ = Ics.Occurrences(e, from, to).ToList(); } catch { continue; }
                    foreach (KeyValuePair<DateTime, DateTime> o in occ)
                    {
                        // acara beberapa hari: masuk ke setiap harinya
                        DateTime last = e.AllDay ? o.Value.Date.AddDays(-1) : (o.Value > o.Key ? o.Value.AddTicks(-1).Date : o.Key.Date);
                        if (last < o.Key.Date) last = o.Key.Date;
                        for (DateTime d = o.Key.Date; d <= last && d < to; d = d.AddDays(1))
                        {
                            if (d < from.Date) continue;
                            List<DayItem> l;
                            if (!map.TryGetValue(d, out l)) { l = new List<DayItem>(); map[d] = l; }
                            if (e.Kind > 0 && l.Any(x => x.Kind == e.Kind && x.Title == e.Title)) continue;   // duplikat
                            l.Add(new DayItem { Title = e.Title ?? "(no title)", Kind = e.Kind, Start = o.Key, End = o.Value, AllDay = e.AllDay });
                        }
                    }
                }
            foreach (List<DayItem> l in map.Values)
                l.Sort(delegate(DayItem a, DayItem b)
                {
                    int ka = a.Kind == 0 ? 2 : a.Kind - 1, kb = b.Kind == 0 ? 2 : b.Kind - 1;     // libur, perayaan, lalu acara
                    if (ka != kb) return ka.CompareTo(kb);
                    if (a.AllDay != b.AllDay) return a.AllDay ? -1 : 1;
                    return a.Start.CompareTo(b.Start);
                });
            return map;
        }
    }

    class CalendarPop : Popover
    {
        public Action OnNotifications;
        readonly StackPanel notifList = new StackPanel();
        readonly TextBlock clearAll = new TextBlock();

        void LoadNotifs()
        {
            notifList.Children.Clear();
            notifList.Children.Add(Tiles.Note("Loading..."));
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<NotifItem> items = Notifs.Get();
                Dispatcher.BeginInvoke(new Action(delegate { RenderNotifs(items); }));
            });
        }

        void RenderNotifs(List<NotifItem> items)
        {
            notifList.Children.Clear();
            clearAll.Visibility = items != null && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (items == null) { notifList.Children.Add(Tiles.Note("Notifications are not available.")); return; }
            if (items.Count == 0) { notifList.Children.Add(Tiles.Note("No new notifications")); return; }
            foreach (NotifItem n in items.Take(20))
            {
                NotifItem it = n;
                Border c = new Border();
                c.CornerRadius = new CornerRadius(12);
                c.Background = UI.Ink(0x1E);
                c.Padding = new Thickness(12, 9, 8, 10);
                c.Margin = new Thickness(0, 0, 0, 6);
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel tx = new StackPanel();
                TextBlock app = new TextBlock();
                app.Text = it.App + "  \u00B7  " + When(it.Time);
                app.Foreground = UI.Ink(0xA0);
                app.FontSize = 11;
                app.FontFamily = UI.Text;
                tx.Children.Add(app);
                if (!string.IsNullOrEmpty(it.Title))
                {
                    TextBlock ti = new TextBlock();
                    ti.Text = it.Title;
                    ti.Foreground = UI.Fg;
                    ti.FontSize = 13;
                    ti.FontWeight = FontWeights.SemiBold;
                    ti.FontFamily = UI.Text;
                    ti.TextTrimming = TextTrimming.CharacterEllipsis;
                    ti.Margin = new Thickness(0, 3, 0, 0);
                    tx.Children.Add(ti);
                }
                if (!string.IsNullOrEmpty(it.Body))
                {
                    TextBlock bo = new TextBlock();
                    bo.Text = it.Body;
                    bo.Foreground = UI.Ink(0xD0);
                    bo.FontSize = 12;
                    bo.FontFamily = UI.Text;
                    bo.TextWrapping = TextWrapping.Wrap;
                    bo.TextTrimming = TextTrimming.CharacterEllipsis;
                    bo.MaxHeight = 34;
                    bo.Margin = new Thickness(0, 2, 0, 0);
                    tx.Children.Add(bo);
                }
                g.Children.Add(tx);
                Border x = new Border();
                x.Width = 24;
                x.Height = 24;
                x.CornerRadius = new CornerRadius(6);
                x.Background = Brushes.Transparent;
                x.Cursor = Cursors.Hand;
                x.ToolTip = "Dismiss";
                x.VerticalAlignment = VerticalAlignment.Top;
                UI.Hover(x, 0x30);
                x.Child = UI.Glyph("\uE711", 10);
                x.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; DismissCard(c, it.Id); };
                Grid.SetColumn(x, 1);
                g.Children.Add(x);
                c.Child = g;
                notifList.Children.Add(c);
            }
        }

        // hapus satu notifikasi: hanya kartunya yang memudar & hilang, daftar lain tidak dimuat ulang
        void DismissCard(Border c, uint id)
        {
            c.IsHitTestVisible = false;
            ThreadPool.QueueUserWorkItem(delegate { Notifs.Remove(id); });
            DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += delegate
            {
                notifList.Children.Remove(c);
                if (!notifList.Children.OfType<Border>().Any()) ShowEmpty();
            };
            c.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        void ShowEmpty()
        {
            notifList.Children.Clear();
            notifList.Children.Add(Tiles.Note("No new notifications"));
            clearAll.Visibility = Visibility.Collapsed;
        }

        static string When(DateTime t)
        {
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm");
            return t.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        }
        static readonly CultureInfo Id = new CultureInfo("en-US");
        readonly TextBlock title = new TextBlock();
        readonly TextBlock monthText = new TextBlock();
        readonly UniformGrid days = new UniformGrid();
        DateTime month;

        // tanggal yang diklik + panel hari itu (hari libur, perayaan, acara Google)
        DateTime selected = DateTime.Today;
        Dictionary<DateTime, List<DayItem>> monthItems = new Dictionary<DateTime, List<DayItem>>();
        readonly TextBlock dayTitle = new TextBlock();
        readonly TextBlock daySub = new TextBlock();
        readonly StackPanel dayList = new StackPanel();
        readonly TextBlock todayBtn = new TextBlock();
        public Action OnCalendarSettings;
        static readonly Brush HolidayRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        static readonly Brush Muted = UI.Ink(0x99);

        // data hari libur/acara berubah (selesai diunduh): gambar ulang bila sedang tampil
        public void DataChanged() { if (IsOpen) Render(); }

        public CalendarPop()
        {
            Border card = UI.Card(20, 14);
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(16, 14, 16, 12);
            sp.Width = 300;
            DockPanel nsp = new DockPanel();
            nsp.Width = 300;
            nsp.LastChildFill = true;

            Grid nh = new Grid();
            nh.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nh.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock nTitle = new TextBlock();
            nTitle.Text = "Notifications";
            nTitle.Foreground = UI.Fg;
            nTitle.FontSize = 15;
            nTitle.FontWeight = FontWeights.SemiBold;
            nTitle.FontFamily = UI.Text;
            nTitle.VerticalAlignment = VerticalAlignment.Center;
            nh.Children.Add(nTitle);
            clearAll.Text = "Clear all";
            clearAll.Foreground = UI.AccentInk;
            clearAll.FontSize = 12;
            clearAll.FontFamily = UI.Text;
            clearAll.Cursor = Cursors.Hand;
            clearAll.VerticalAlignment = VerticalAlignment.Center;
            clearAll.MouseLeftButtonUp += delegate { ThreadPool.QueueUserWorkItem(delegate { Notifs.ClearAll(); }); ShowEmpty(); };
            Grid.SetColumn(clearAll, 1);
            nh.Children.Add(clearAll);
            nh.Margin = new Thickness(2, 0, 2, 8);
            DockPanel.SetDock(nh, Dock.Top);
            nsp.Children.Add(nh);
            ScrollViewer nsv = new ScrollViewer();
            nsv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            nsv.MaxHeight = 300;
            nsv.Content = notifList;
            Border nsep = new Border();
            nsep.Height = 1;
            nsep.Margin = new Thickness(0, 10, 0, 12);
            nsep.Background = UI.Ink(0x26);

            title.Foreground = UI.Fg;
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.FontFamily = UI.Text;
            title.Margin = new Thickness(2, 0, 0, 10);
            sp.Children.Add(title);

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            todayBtn.Text = "Today";
            todayBtn.Foreground = Accent;
            todayBtn.FontSize = 12;
            todayBtn.FontFamily = UI.Text;
            todayBtn.Cursor = Cursors.Hand;
            todayBtn.VerticalAlignment = VerticalAlignment.Center;
            todayBtn.Margin = new Thickness(0, 0, 8, 0);
            todayBtn.MouseLeftButtonUp += delegate
            {
                selected = DateTime.Today;
                month = new DateTime(selected.Year, selected.Month, 1);
                Render();
            };
            Grid.SetColumn(todayBtn, 1);
            head.Children.Add(todayBtn);
            monthText.Foreground = UI.Fg;
            monthText.FontSize = 13.5;
            monthText.FontFamily = UI.Text;
            monthText.VerticalAlignment = VerticalAlignment.Center;
            monthText.Margin = new Thickness(2, 0, 0, 0);
            head.Children.Add(monthText);
            FrameworkElement prev = NavButton("\uE76B", -1);
            FrameworkElement next = NavButton("\uE76C", 1);
            Grid.SetColumn(prev, 2);
            Grid.SetColumn(next, 3);
            head.Children.Add(prev);
            head.Children.Add(next);
            sp.Children.Add(head);

            UniformGrid wd = new UniformGrid();
            wd.Columns = 7;
            wd.Margin = new Thickness(0, 8, 0, 2);
            foreach (string d in new string[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
            {
                TextBlock t = new TextBlock();
                t.Text = d;
                t.Foreground = UI.Ink(0x90);
                t.FontSize = 11;
                t.FontFamily = UI.Text;
                t.TextAlignment = TextAlignment.Center;
                wd.Children.Add(t);
            }
            sp.Children.Add(wd);
            days.Columns = 7;
            days.Rows = 6;
            sp.Children.Add(days);
            sp.Children.Add(BuildDayPanel());

            Border notif = new Border();
            notif.CornerRadius = new CornerRadius(10);
            notif.Padding = new Thickness(10, 7, 10, 7);
            notif.Margin = new Thickness(0, 10, 0, 0);
            notif.Background = Brushes.Transparent;
            notif.Cursor = Cursors.Hand;
            UI.Hover(notif, 0x30);
            StackPanel nr = new StackPanel();
            nr.Orientation = Orientation.Horizontal;
            nr.Children.Add(UI.Glyph("\uEA8F", 14));
            TextBlock nt = new TextBlock();
            nt.Text = "Open notification center";
            nt.Foreground = UI.Fg;
            nt.FontSize = 13;
            nt.FontFamily = UI.Text;
            nt.Margin = new Thickness(10, 0, 0, 0);
            nr.Children.Add(nt);
            notif.Child = nr;
            notif.MouseLeftButtonUp += delegate { HidePanel(); if (OnNotifications != null) OnNotifications(); };
            DockPanel.SetDock(notif, Dock.Bottom);
            nsp.Children.Add(notif);
            nsp.Children.Add(nsv);

            // dua panel: Notifications (kiri) + Calendar (kanan)
            card.Child = sp;
            card.Margin = new Thickness(6, 14, 14, 14);
            Border ncard = UI.Card(20, 14);
            ncard.Margin = new Thickness(14, 14, 6, 14);
            nsp.Margin = new Thickness(14, 14, 14, 10);
            ncard.Child = nsp;
            ncard.VerticalAlignment = VerticalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
            two.Children.Add(ncard);
            two.Children.Add(card);
            calCard = card;
            notifCard = ncard;
            notifScroll = nsv;
            Cards = new List<Border> { ncard, card };
            Content = two;
            ArrangeCards();
            // notifikasi banyak + layar pendek: perpendek daftar notifikasi (tetap bisa digulir) sampai panel muat
            SizeChanged += delegate
            {
                double over = ActualHeight - (Popover.Scr.Height - 16);
                if (over > 1 && notifScroll.ActualHeight > 90) notifScroll.MaxHeight = Math.Max(90, notifScroll.ActualHeight - over);
            };
        }

        readonly Grid two = new Grid();
        Border calCard, notifCard;
        ScrollViewer notifScroll;
        bool? arrangedV;
        double notifMax = 300;

        // dock bawah: Notifications (kiri) + Calendar (kanan).
        // dock kiri/kanan: Calendar (atas) + Notifications (bawah) agar rapi di samping dock;
        // tapi di layar yang kurang tinggi (< 980) tetap berdampingan supaya tidak melewati layar.
        void ArrangeCards()
        {
            bool v = Layout.V && Popover.Scr.Height >= 980;
            if (arrangedV == v) return;
            arrangedV = v;
            two.RowDefinitions.Clear();
            two.ColumnDefinitions.Clear();
            if (v)
            {
                two.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                two.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(calCard, 0); Grid.SetRow(calCard, 0);
                Grid.SetColumn(notifCard, 0); Grid.SetRow(notifCard, 1);
                calCard.Margin = new Thickness(14, 14, 14, 6);
                notifCard.Margin = new Thickness(14, 6, 14, 14);
                notifMax = 220;                       // jaga total tinggi tetap muat di layar
            }
            else
            {
                two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(calCard, 0); Grid.SetColumn(calCard, 1);
                Grid.SetRow(notifCard, 0); Grid.SetColumn(notifCard, 0);
                calCard.Margin = new Thickness(6, 14, 14, 14);
                notifCard.Margin = new Thickness(14, 14, 6, 14);
                notifMax = 300;
            }
            notifScroll.MaxHeight = notifMax;
        }

        FrameworkElement BuildDayPanel()
        {
            StackPanel p = new StackPanel();
            Border sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(0, 10, 0, 10);
            sep.Background = UI.Ink(0x26);
            p.Children.Add(sep);
            dayTitle.Foreground = UI.Fg;
            dayTitle.FontSize = 13.5;
            dayTitle.FontWeight = FontWeights.SemiBold;
            dayTitle.FontFamily = UI.Text;
            dayTitle.Margin = new Thickness(2, 0, 0, 0);
            p.Children.Add(dayTitle);
            daySub.Foreground = Muted;
            daySub.FontSize = 11.5;
            daySub.FontFamily = UI.Text;
            daySub.Margin = new Thickness(2, 1, 0, 4);
            p.Children.Add(daySub);
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.MaxHeight = 128;
            sv.Content = dayList;
            p.Children.Add(sv);

            Grid actions = new Grid();
            actions.Margin = new Thickness(0, 10, 0, 0);
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock open = new TextBlock();
            open.Text = "Open in Google Calendar";
            open.Foreground = Accent;
            open.FontSize = 12;
            open.FontFamily = UI.Text;
            open.Cursor = Cursors.Hand;
            open.VerticalAlignment = VerticalAlignment.Center;
            open.Margin = new Thickness(2, 0, 0, 0);
            open.MouseLeftButtonUp += delegate
            {
                HidePanel();
                Launcher.Open("https://calendar.google.com/calendar/r/day/" + selected.Year + "/" + selected.Month + "/" + selected.Day);
            };
            actions.Children.Add(open);
            Border add = new Border();
            add.CornerRadius = new CornerRadius(8);
            add.Padding = new Thickness(11, 4, 12, 5);
            add.Cursor = Cursors.Hand;
            add.Background = Accent;
            add.ToolTip = "New event on this day in Google Calendar";
            TextBlock at = new TextBlock();
            at.Text = "+ Event";
            at.FontSize = 12;
            at.FontWeight = FontWeights.SemiBold;
            at.FontFamily = UI.Text;
            at.Foreground = Brushes.Black;
            add.Child = at;
            add.MouseLeftButtonUp += delegate
            {
                HidePanel();
                string d0 = selected.ToString("yyyyMMdd", CultureInfo.InvariantCulture), d1 = selected.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                Launcher.Open("https://calendar.google.com/calendar/r/eventedit?dates=" + d0 + "/" + d1);
            };
            Grid.SetColumn(add, 1);
            actions.Children.Add(add);
            p.Children.Add(actions);
            return p;
        }

        static string Relative(DateTime d)
        {
            int n = (int)(d.Date - DateTime.Today).TotalDays;
            if (n == 0) return "Today";
            if (n == 1) return "Tomorrow";
            if (n == -1) return "Yesterday";
            return n > 0 ? "In " + n + " days" : -n + " days ago";
        }

        void RenderDay()
        {
            dayTitle.Text = selected.ToString("dddd, d MMMM yyyy", Id);
            daySub.Text = Relative(selected);
            dayList.Children.Clear();
            List<DayItem> items;
            if (!monthItems.TryGetValue(selected.Date, out items) || items.Count == 0)
            {
                TextBlock none = new TextBlock();
                none.Text = "No holidays or events";
                none.Foreground = Muted;
                none.FontSize = 12;
                none.FontFamily = UI.Text;
                none.Margin = new Thickness(2, 4, 0, 0);
                dayList.Children.Add(none);
                if (CalData.PersonalUrl == null)
                {
                    TextBlock link = new TextBlock();
                    link.Text = "Show my Google Calendar events here";
                    link.Foreground = Accent;
                    link.FontSize = 12;
                    link.FontFamily = UI.Text;
                    link.Cursor = Cursors.Hand;
                    link.Margin = new Thickness(2, 4, 0, 0);
                    link.MouseLeftButtonUp += delegate { if (OnCalendarSettings != null) OnCalendarSettings(); };
                    dayList.Children.Add(link);
                }
                return;
            }
            foreach (DayItem it in items)
            {
                Grid row = new Grid();
                row.Margin = new Thickness(2, 5, 0, 0);
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Border bar = new Border();
                bar.Width = 3;
                bar.CornerRadius = new CornerRadius(1.5);
                bar.Margin = new Thickness(0, 1, 9, 1);
                bar.Background = it.Kind == 1 ? HolidayRed : it.Kind == 2 ? Muted : Accent;
                row.Children.Add(bar);
                StackPanel tx = new StackPanel();
                TextBlock title = new TextBlock();
                title.Text = it.Title;
                title.Foreground = UI.Fg;
                title.FontSize = 12.5;
                title.FontFamily = UI.Text;
                title.TextWrapping = TextWrapping.Wrap;
                tx.Children.Add(title);
                TextBlock sub = new TextBlock();
                sub.Foreground = Muted;
                sub.FontSize = 11;
                sub.FontFamily = UI.Text;
                if (it.Kind == 1) sub.Text = "Public holiday";
                else if (it.Kind == 2) sub.Text = "Observance";
                else if (it.AllDay) sub.Text = "All day";
                else if (it.Start.Date == it.End.Date || it.End <= it.Start) sub.Text = it.Start.ToString("HH:mm") + (it.End > it.Start ? " \u2013 " + it.End.ToString("HH:mm") : "");
                else sub.Text = it.Start.ToString("d MMM HH:mm", Id) + " \u2013 " + it.End.ToString("d MMM HH:mm", Id);
                tx.Children.Add(sub);
                Grid.SetColumn(tx, 1);
                row.Children.Add(tx);
                dayList.Children.Add(row);
            }
        }

        FrameworkElement NavButton(string glyph, int delta)
        {
            Border b = new Border();
            b.Width = 30;
            b.Height = 28;
            b.CornerRadius = new CornerRadius(8);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            UI.Hover(b, 0x30);
            b.Child = UI.Glyph(glyph, 12);
            b.MouseLeftButtonUp += delegate { month = month.AddMonths(delta); Render(); };
            return b;
        }

        void Render()
        {
            monthText.Text = Cap(month.ToString("MMMM yyyy", Id));
            days.Children.Clear();
            DateTime first = new DateTime(month.Year, month.Month, 1);
            int offset = ((int)first.DayOfWeek + 6) % 7;
            DateTime start = first.AddDays(-offset);
            DateTime today = DateTime.Today;
            monthItems = CalData.Range(start, start.AddDays(42));
            todayBtn.Visibility = month == new DateTime(today.Year, today.Month, 1) && selected == today ? Visibility.Collapsed : Visibility.Visible;
            for (int i = 0; i < 42; i++)
            {
                DateTime d = start.AddDays(i);
                List<DayItem> items;
                monthItems.TryGetValue(d, out items);
                bool holiday = items != null && items.Any(x => x.Kind == 1);
                bool red = holiday || (CalData.RedSundays && d.DayOfWeek == DayOfWeek.Sunday);
                bool isToday = d == today, isSel = d == selected;

                Border c = new Border();
                c.Width = 32;
                c.Height = 32;
                c.Margin = new Thickness(3, 2, 3, 2);
                c.CornerRadius = new CornerRadius(16);
                c.Cursor = Cursors.Hand;
                c.Background = isToday ? Accent : Brushes.Transparent;
                if (isSel && !isToday) { c.BorderBrush = Accent; c.BorderThickness = new Thickness(1.5); }
                Grid g = new Grid();
                TextBlock t = new TextBlock();
                t.Text = d.Day.ToString();
                t.FontSize = 12.5;
                t.FontFamily = UI.Text;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                if (isToday)
                {
                    t.Foreground = Brushes.Black;
                    t.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    t.Foreground = red ? HolidayRed : UI.Fg;
                    if (holiday) t.FontWeight = FontWeights.SemiBold;
                }
                g.Children.Add(t);
                // titik kecil: merah = libur nasional, abu-abu = perayaan, biru = acara Google
                if (items != null && items.Count > 0)
                {
                    StackPanel dots = new StackPanel();
                    dots.Orientation = Orientation.Horizontal;
                    dots.HorizontalAlignment = HorizontalAlignment.Center;
                    dots.VerticalAlignment = VerticalAlignment.Bottom;
                    dots.Margin = new Thickness(0, 0, 0, 3);
                    foreach (int kind in new int[] { 1, 2, 0 })
                    {
                        if (!items.Any(x => x.Kind == kind)) continue;
                        System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse();
                        dot.Width = 4;
                        dot.Height = 4;
                        dot.Margin = new Thickness(0.75, 0, 0.75, 0);
                        dot.Fill = kind == 1 ? HolidayRed : kind == 2 ? (isToday ? new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) : Muted)
                                 : (isToday ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C)) : Accent);
                        dots.Children.Add(dot);
                    }
                    g.Children.Add(dots);
                }
                if (d.Month != month.Month) g.Opacity = 0.35;
                c.Child = g;
                if (!isToday)
                {
                    c.MouseEnter += delegate { c.Background = UI.Ink(0x22); };
                    c.MouseLeave += delegate { c.Background = Brushes.Transparent; };
                }
                DateTime day = d;
                c.MouseLeftButtonUp += delegate
                {
                    selected = day;
                    if (day.Month != month.Month || day.Year != month.Year) month = new DateTime(day.Year, day.Month, 1);
                    Render();
                };
                days.Children.Add(c);
            }
            RenderDay();
        }

        static string Cap(string s)
        {
            return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);
        }

        public void ShowAt(Point clock, double dockTop)
        {
            if (IsOpen) return;
            SideAnchor = clock.Y;
            month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            selected = DateTime.Today;
            CalData.Refresh(Dispatcher, false);
            arrangedV = null;                         // layar bisa berbeda dari sebelumnya
            ArrangeCards();
            notifScroll.MaxHeight = notifMax;
            title.Text = Cap(DateTime.Today.ToString("dddd, d MMMM yyyy", Id));
            ArrangeCards();
            LoadNotifs();
            Render();
            AnchorCenter = true;
            SlideIn(delegate(Size s) { return new Point(AlongX(s.Width, clock.X), dockTop - s.Height + 6); });
        }
    }

    // ----- ikon tray (diambil dari daftar ikon tray Windows di registry) -----
    class TrayEntry { public string Exe; public string Tip; public ImageSource Icon; }

    static class TrayApps
    {
        static ImageSource Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BitmapDecoder d = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame f = d.Frames[0];
                    f.Freeze();
                    return f;
                }
            }
            catch { return null; }
        }

        static string Clean(string s)
        {
            if (s == null) return null;
            StringBuilder sb = new StringBuilder();
            foreach (char c in s) if (c < '‪' || c > '‮') sb.Append(c);
            return sb.ToString().Trim();
        }

        public static List<TrayEntry> Running()
        {
            HashSet<string> run = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string e = Native.ExeOf((uint)p.Id);
                    if (e != null) run.Add(e);
                }
                catch { }
                finally { p.Dispose(); }
            }

            List<TrayEntry> l = new List<TrayEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (k == null) return l;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        using (Microsoft.Win32.RegistryKey s = k.OpenSubKey(sub))
                        {
                            if (s == null) continue;
                            string raw = s.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrEmpty(raw)) continue;
                            string exe = raw.StartsWith("{") ? Native.ExpandAppPath(raw) : raw;
                            if (exe == null) continue;
                            string fn = System.IO.Path.GetFileName(exe).ToLowerInvariant();
                            if (fn == "explorer.exe" || fn == "rundll32.exe" || fn == "slidedock.exe") continue;
                            if (!run.Contains(exe) || !seen.Add(exe)) continue;
                            TrayEntry t = new TrayEntry();
                            t.Exe = exe;
                            string tip = Clean(s.GetValue("InitialTooltip") as string);
                            t.Tip = string.IsNullOrEmpty(tip) ? Native.DescOf(exe) : tip;
                            t.Icon = Decode(s.GetValue("IconSnapshot") as byte[]);
                            if (t.Icon == null && Application.Current != null && Application.Current.Dispatcher.CheckAccess()) t.Icon = Icons.FromPath(exe);
                            l.Add(t);
                        }
                    }
                }
            }
            catch { }
            return l;
        }
    }

    class TrayPop : Popover
    {
        readonly WrapPanel wrap = new WrapPanel();
        readonly TextBlock empty = new TextBlock();

        public TrayPop()
        {
            Border card = UI.Card(16, 14);
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(8);
            wrap.MaxWidth = 6 * 38;
            sp.Children.Add(wrap);
            empty.Text = "No tray apps running.";
            empty.Foreground = UI.Ink(0x90);
            empty.FontSize = 12.5;
            empty.FontFamily = UI.Text;
            empty.Margin = new Thickness(6, 4, 6, 4);
            sp.Children.Add(empty);
            card.Child = sp;
            Content = card;
        }

        void Fill()
        {
            wrap.Children.Clear();
            List<TrayEntry> list = TrayApps.Running();
            empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (TrayEntry t in list)
            {
                TrayEntry e = t;
                Border b = new Border();
                b.Width = 36;
                b.Height = 36;
                b.Margin = new Thickness(1);
                b.CornerRadius = new CornerRadius(9);
                b.Background = Brushes.Transparent;
                b.Cursor = Cursors.Hand;
                b.ToolTip = e.Tip;
                UI.Hover(b, 0x30);
                Image img = new Image();
                img.Source = e.Icon;
                img.Width = 18;
                img.Height = 18;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                b.Child = img;
                b.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open(e.Exe); };
                Tiles.Menu(b, "Open " + e.Tip, (Action)delegate { HidePanel(); Launcher.Open(e.Exe); });
                wrap.Children.Add(b);
            }
        }

        public void ShowAt(double left, double dockTop)
        {
            if (IsOpen) return;
            Fill();
            SlideIn(delegate(Size s)
            {
                double x = Math.Min(Popover.Scr.Right - s.Width, Math.Max(Popover.Scr.Left, left - s.Width / 2 + 23));
                return new Point(x, dockTop - s.Height + 6);
            });
        }
    }

    // ================= preview jendela saat hover ikon app yang berjalan =================
    class PreviewWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }
        [StructLayout(LayoutKind.Sequential)] struct PPOINT { public int X, Y; }

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out PPOINT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        IntPtr hwnd = IntPtr.Zero;
        readonly StackPanel cards = new StackPanel();
        readonly List<IntPtr> thumbs = new List<IntPtr>();
        readonly List<KeyValuePair<IntPtr, FrameworkElement>> slots = new List<KeyValuePair<IntPtr, FrameworkElement>>();
        readonly DispatcherTimer watch = new DispatcherTimer();
        Rect anchor;
        double centerX, bottomY;
        bool away;
        DateTime awayAt;
        ImageSource appIcon;
        public Rect Scr = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        public double ScrScale = 1;
        public double SideX, CenterY;
        public string Key;

        public PreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26));
            cards.Orientation = Orientation.Horizontal;
            cards.Margin = new Thickness(6);
            Content = cards;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
                int round = 2;                                   // sudut membulat Windows 11
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                int border = UI.Theme == "glass2" ? 0x00C8C4C0 : UI.Theme == "glass" ? 0x00A8A4A0 : 0x00504A48;   // garis tepi (BGR): terang untuk Glass
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            };
            SizeChanged += delegate { Place(); ApplyGlass(); UpdateThumbs(); };
            watch.Interval = TimeSpan.FromMilliseconds(120);
            watch.Tick += delegate { CheckCursor(); };

            // seret file ke atas salah satu pratinjau & tahan sebentar: jendela itu naik ke depan,
            // file tetap terseret sehingga bisa langsung dilepas di app tersebut
            AllowDrop = true;
            dragTimer.Interval = TimeSpan.FromMilliseconds(450);
            dragTimer.Tick += delegate
            {
                dragTimer.Stop();
                IntPtr w = dragWin;
                SetDragCard(null);
                if (w == IntPtr.Zero || !IsWindow(w)) return;
                WinRaise.Raise(w);
                HidePreview();
            };
            DragEnter += delegate(object s, DragEventArgs e) { DragOverCards(e); };
            DragOver += delegate(object s, DragEventArgs e) { DragOverCards(e); };
            DragLeave += delegate(object s, DragEventArgs e)
            {
                Point q = e.GetPosition(this);
                if (q.X < 0 || q.Y < 0 || q.X >= ActualWidth || q.Y >= ActualHeight) SetDragCard(null);   // benar-benar keluar jendela
            };
            Drop += delegate(object s, DragEventArgs e) { e.Effects = DragDropEffects.None; e.Handled = true; SetDragCard(null); };
        }

        readonly DispatcherTimer dragTimer = new DispatcherTimer();
        Border dragCard;
        IntPtr dragWin = IntPtr.Zero;

        void DragOverCards(DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;          // pratinjau bukan tempat menjatuhkan file
            e.Handled = true;
            Point q = e.GetPosition(this);
            Border hit = null;
            foreach (UIElement c in cards.Children)
            {
                Border card = c as Border;
                if (card == null || !(card.Tag is IntPtr)) continue;
                Point o = card.TranslatePoint(new Point(0, 0), this);
                if (new Rect(o, card.RenderSize).Contains(q)) { hit = card; break; }
            }
            SetDragCard(hit);
        }

        void SetDragCard(Border card)
        {
            if (card == dragCard) return;
            if (dragCard != null) dragCard.Background = Brushes.Transparent;
            dragCard = card;
            dragTimer.Stop();
            dragWin = card != null ? (IntPtr)card.Tag : IntPtr.Zero;
            if (card == null) return;
            card.Background = UI.Ink(0x40);
            dragTimer.Start();
        }

        public bool Showing { get { return IsVisible && Key != null; } }

        public void ShowFor(string key, List<IntPtr> wins, ImageSource icon, Rect anchorPx, double cx, double bottom)
        {
            Key = key;
            anchor = anchorPx;
            centerX = cx;
            bottomY = bottom;
            appIcon = icon;
            away = false;
            Build(wins);
            if (!IsVisible)
            {
                CaptureStrip();      // ambil latar sebelum panel tampil (agar panel sendiri tidak ikut terekam)
                Opacity = 0;
                Show();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            }
            UpdateLayout();
            Place();
            ApplyGlass();
            UpdateThumbs();
            watch.Start();
        }

        // ----- tema Glass: latar buram seperti dock & popup lain -----
        BitmapSource cap;
        double capX, capY, capW, capH, capScale = 1;

        void CaptureStrip()
        {
            cap = null;
            if (UI.Theme == "dark") return;
            try
            {
                capScale = ScrScale;
                if (Layout.V)
                {
                    // dock di samping: ambil kolom latar di sebelah dock
                    double colX = Layout.Side == 1 ? SideX : SideX - 680;
                    int cx0 = (int)(Math.Max(Scr.Left, colX) * capScale);
                    int cx1 = (int)(Math.Min(Scr.Right, colX + 680) * capScale);
                    int cy0 = (int)(Scr.Top * capScale), cy1 = (int)(Scr.Bottom * capScale);
                    if (cx1 - cx0 <= 0 || cy1 - cy0 <= 0) return;
                    cap = Popover.Frost(cx0, cy0, cx1 - cx0, cy1 - cy0);
                    capX = cx0; capY = cy0; capW = cx1 - cx0; capH = cy1 - cy0;
                    return;
                }
                int sx = (int)(Scr.Left * capScale);
                int sw = (int)(Scr.Width * capScale);
                int y1 = (int)(bottomY * capScale);
                int y0 = Math.Max((int)(Scr.Top * capScale), y1 - (int)(460 * capScale));
                if (y1 - y0 <= 0 || sw <= 0) return;
                cap = Popover.Frost(sx, y0, sw, y1 - y0);
                capX = sx; capY = y0; capW = sw; capH = y1 - y0;
            }
            catch { cap = null; }
        }

        void ApplyGlass()
        {
            if (!IsVisible) return;
            if (cap == null || UI.Theme == "dark")
            {
                Background = new SolidColorBrush(UI.Light ? Color.FromRgb(0xF3, 0xF3, 0xF5) : Color.FromRgb(0x20, 0x20, 0x26));
                return;
            }
            double s = capScale;
            Rect unit = new Rect(0, 0, 1, 1);
            ImageBrush ib = new ImageBrush(cap);
            ib.Stretch = Stretch.Fill;
            ib.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
            ib.Viewbox = new Rect((Left * s - capX) / capW, (Top * s - capY) / capH, ActualWidth * s / capW, ActualHeight * s / capH);
            DrawingGroup dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(ib, null, new RectangleGeometry(unit)));
            dg.Children.Add(new GeometryDrawing(new SolidColorBrush(UI.Light ? Color.FromArgb(0x84, 0xF6, 0xF6, 0xF8) : Color.FromArgb(0x6E, 0x14, 0x14, 0x1C)), null, new RectangleGeometry(unit)));
            LinearGradientBrush gl = new LinearGradientBrush();
            gl.StartPoint = new Point(0, 0);
            gl.EndPoint = new Point(0, 1);
            gl.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 255, 255, 255), 0));
            gl.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.5));
            dg.Children.Add(new GeometryDrawing(gl, null, new RectangleGeometry(unit)));
            DrawingBrush db = new DrawingBrush(dg);
            db.Stretch = Stretch.Fill;
            Background = db;
        }

        public void HidePreview()
        {
            watch.Stop();
            dragTimer.Stop();
            dragCard = null;
            dragWin = IntPtr.Zero;
            ClearThumbs();
            Key = null;
            if (IsVisible) Hide();
        }

        static string TitleOf(IntPtr w)
        {
            int len = Native.GetWindowTextLength(w);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 1);
            Native.GetWindowText(w, sb, sb.Capacity);
            return sb.ToString();
        }

        void Build(List<IntPtr> wins)
        {
            ClearThumbs();
            cards.Children.Clear();
            slots.Clear();
            foreach (IntPtr w in wins)
            {
                IntPtr win = w;
                Border card = new Border();
                card.Width = 224;
                card.Margin = new Thickness(3);
                card.Padding = new Thickness(8, 6, 6, 8);
                card.CornerRadius = new CornerRadius(10);
                card.Background = Brushes.Transparent;
                card.Cursor = Cursors.Hand;
                card.Tag = win;
                card.MouseEnter += delegate { card.Background = UI.Ink(0x2D); };
                card.MouseLeave += delegate { card.Background = Brushes.Transparent; };

                StackPanel col = new StackPanel();
                Grid head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Image ic = new Image();
                ic.Source = appIcon;
                ic.Width = 16;
                ic.Height = 16;
                ic.VerticalAlignment = VerticalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(ic, BitmapScalingMode.HighQuality);
                head.Children.Add(ic);
                TextBlock title = new TextBlock();
                title.Text = TitleOf(win);
                title.Foreground = UI.Fg;
                title.FontSize = 12;
                title.FontFamily = UI.Text;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.VerticalAlignment = VerticalAlignment.Center;
                title.Margin = new Thickness(8, 0, 6, 0);
                Grid.SetColumn(title, 1);
                head.Children.Add(title);
                Border x = new Border();
                x.Width = 24;
                x.Height = 22;
                x.CornerRadius = new CornerRadius(6);
                x.Background = Brushes.Transparent;
                x.ToolTip = "Close window";
                x.Child = UI.Glyph("", 9);
                x.MouseEnter += delegate { x.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)); };
                x.MouseLeave += delegate { x.Background = Brushes.Transparent; };
                x.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    Native.PostMessage(win, 0x10, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE: tutup jendela ini saja
                    x.IsEnabled = false;
                    card.Opacity = 0.4;
                    DispatcherTimer chk = new DispatcherTimer();
                    chk.Interval = TimeSpan.FromMilliseconds(450);
                    chk.Tick += delegate
                    {
                        chk.Stop();
                        if (IsWindow(win) && Native.IsWindowVisible(win)) { card.Opacity = 1; x.IsEnabled = true; return; }   // app menolak/menanyakan simpan
                        List<IntPtr> rest = slots.Select(sl => sl.Key).Where(h => h != win && IsWindow(h)).ToList();
                        if (rest.Count == 0) { HidePreview(); return; }
                        Build(rest);
                        UpdateLayout();
                        Place();
                        UpdateThumbs();
                    };
                    chk.Start();
                };
                Grid.SetColumn(x, 2);
                head.Children.Add(x);
                col.Children.Add(head);

                Border slot = new Border();
                slot.Width = 208;
                slot.Height = 124;
                slot.Margin = new Thickness(0, 7, 0, 0);
                slot.CornerRadius = new CornerRadius(6);
                slot.Background = UI.Ink(0x14);
                Image big = new Image();
                big.Source = appIcon;
                big.Width = 34;
                big.Height = 34;
                big.Opacity = 0.5;
                slot.Child = big;      // tampil bila pratinjau tidak tersedia (mis. jendela diminimize)
                col.Children.Add(slot);
                card.Child = col;
                card.MouseLeftButtonUp += delegate
                {
                    if (Native.IsIconic(win)) Native.ShowWindow(win, 9);
                    Native.SetForegroundWindow(win);
                    HidePreview();
                };
                cards.Children.Add(card);
                slots.Add(new KeyValuePair<IntPtr, FrameworkElement>(win, slot));
            }
        }

        void Place()
        {
            if (!IsVisible) return;
            if (Layout.V)
            {
                double sx = Layout.Side == 1 ? SideX + 10 : SideX - ActualWidth - 10;
                double sy = Math.Max(Scr.Top + 4, Math.Min(Scr.Bottom - ActualHeight - 4, CenterY - ActualHeight / 2));
                Left = sx; Top = sy;
                Left = sx; Top = sy;
                return;
            }
            double x = centerX - ActualWidth / 2;
            x = Math.Max(Scr.Left + 4, Math.Min(Scr.Right - ActualWidth - 4, x));
            Left = x;
            Top = bottomY - ActualHeight - 10;
            Left = x;                                  // ulang setelah skala monitor diterapkan
            Top = bottomY - ActualHeight - 10;
        }

        void ClearThumbs()
        {
            foreach (IntPtr th in thumbs) DwmUnregisterThumbnail(th);
            thumbs.Clear();
        }

        void UpdateThumbs()
        {
            if (hwnd == IntPtr.Zero || !IsVisible) return;
            ClearThumbs();
            PresentationSource ps = PresentationSource.FromVisual(this);
            double s = ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1;
            foreach (KeyValuePair<IntPtr, FrameworkElement> sl in slots)
            {
                IntPtr th;
                if (DwmRegisterThumbnail(hwnd, sl.Key, out th) != 0) continue;
                thumbs.Add(th);
                PSIZE sz;
                DwmQueryThumbnailSourceSize(th, out sz);
                if (sz.x <= 0 || sz.y <= 0) continue;
                Point p = sl.Value.TransformToAncestor(this).Transform(new Point(0, 0));
                double bw = sl.Value.ActualWidth * s, bh = sl.Value.ActualHeight * s;
                double sc = Math.Min(1, Math.Min(bw / sz.x, bh / sz.y));
                int w = (int)(sz.x * sc), h = (int)(sz.y * sc);
                int x0 = (int)(p.X * s + (bw - w) / 2), y0 = (int)(p.Y * s + (bh - h) / 2);
                DWM_THUMBNAIL_PROPERTIES pr = new DWM_THUMBNAIL_PROPERTIES();
                pr.dwFlags = 0x1 | 0x4 | 0x8 | 0x10;             // tujuan, opacity, visible, tanpa bingkai luar
                pr.rcDestination.left = x0;
                pr.rcDestination.top = y0;
                pr.rcDestination.right = x0 + w;
                pr.rcDestination.bottom = y0 + h;
                pr.opacity = 255;
                pr.fVisible = true;
                pr.fSourceClientAreaOnly = false;
                DwmUpdateThumbnailProperties(th, ref pr);
                Border b = sl.Value as Border;
                if (b != null && b.Child != null) b.Child.Visibility = Visibility.Hidden;
            }
        }

        void CheckCursor()
        {
            PPOINT p;
            RECT r;
            if (!GetCursorPos(out p) || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r)) return;
            bool inside = anchor.Contains(new Point(p.X, p.Y))
                || (p.X >= r.left - 6 && p.X <= r.right + 6 && p.Y >= r.top - 6 && p.Y <= r.bottom + 14);
            if (inside) { away = false; return; }
            if (!away) { away = true; awayAt = DateTime.Now; return; }
            if ((DateTime.Now - awayAt).TotalMilliseconds > 300) HidePreview();
        }
    }

    // jendela dinaikkan ke paling depan walau SlideDock bukan app yang aktif (mis. saat Explorer sedang
    // menyeret file). Untuk menjatuhkan file, jendela tujuan cukup terlihat paling depan, tidak perlu aktif.
    static class WinRaise
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void Raise(IntPtr w)
        {
            if (Native.IsIconic(w)) Native.ShowWindow(w, 4);       // SW_SHOWNOACTIVATE: pulihkan tanpa merebut fokus
            const uint f = 0x0001 | 0x0002 | 0x0010 | 0x0040;      // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
            SetWindowPos(w, new IntPtr(-1), 0, 0, 0, 0, f);        // HWND_TOPMOST: naik ke paling depan...
            SetWindowPos(w, new IntPtr(-2), 0, 0, 0, 0, f);        // ...HWND_NOTOPMOST: lalu tidak lagi "selalu di atas"
        }
    }

    // ================= dock =================
    class DockWindow : Window
    {
        public Action OnToggle, OnDoubleClick, OnFullscreen, OnSearch, OnPower, OnClock, OnTray;
        public Action<string> OnQuick;      // "wifi", "sound" atau "battery": ikon status mana yang diklik
        public bool HotkeyOk;
        public double SurfaceTop
        {
            get
            {
                if (fullCard == null || fullCard.ActualHeight <= 0) return Top + ActualHeight - 70;
                return Top + fullCard.TranslatePoint(new Point(0, 0), this).Y;
            }
        }
        public Func<bool> AnyPopupOpen;
        public Action OnPopup;
        public void HidePreview() { previewTimer.Stop(); preview.HidePreview(); }
        const bool ReserveSpace = false;   // false = app terbuka penuh layar, dock melayang di atasnya
        Border fullCard;
        Border notch;
        readonly Image notchIcon = new Image();
        readonly StackPanel notchApps = new StackPanel();
        readonly BatteryRing notchRing = new BatteryRing(20);
        Border notchSep;
        readonly DispatcherTimer collapseTimer = new DispatcherTimer();
        bool compact, appActive;
        string activeKey = "";
        public bool FullscreenNow { get { return hiddenForFullscreen; } }

        const double CardMargin = 10;
        readonly StackPanel itemsPanel = new StackPanel();
        readonly TextBlock clockTime = new TextBlock();
        readonly TextBlock clockDate = new TextBlock();
        readonly Image binImage = new Image();
        readonly TextBlock netGlyph = UI.Glyph("\uE701", 15);
        readonly TextBlock volGlyph = UI.Glyph("\uE767", 15);
        readonly BatteryRing batRing = new BatteryRing(25);
        readonly TextBlock netTip = new TextBlock(), volTip = new TextBlock(), batTip = new TextBlock();
        Border statusTile, netTile, volTile, batTile;
        string netKind = "none", ssidCache;
        bool ssidBusy;
        Border powerTile;
        Border clockTile;
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        string signature = "";
        bool suppressUp, hiddenForFullscreen, appBarOn;
        int tick;
        IntPtr hwnd = IntPtr.Zero;

        public Rect ScreenDip;
        public bool IsPrimary;
        public double ScreenScale = 1;
        public double SurfaceExtent { get { return (fullCard != null && fullCard.ActualWidth > 0 ? fullCard.ActualWidth * DockScale : 70) + 10; } }
        public string Device;

        public DockWindow(Rect scr, bool primary)
        {
            ScreenDip = scr;
            IsPrimary = primary;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            Width = Layout.V ? 160 : scr.Width;
            Height = Layout.V ? scr.Height : 150;                       // ruang tetap: dock & notch bisa ber-morph tanpa jendela melompat
            AllowDrop = true;
            PreviewMouseDown += delegate { MenuCloser.CloseAll(); };

            Border card = UI.Card(22, CardMargin);
            StackPanel row = new StackPanel();
            row.Orientation = Layout.V ? Orientation.Vertical : Orientation.Horizontal;
            row.Margin = Layout.V ? new Thickness(6, 8, 6, 8) : new Thickness(8, 6, 8, 6);

            row.Children.Add(MakeStart());
            row.Children.Add(GlyphTile("\uE721", 18, "Search apps, files, or the web", delegate { if (OnSearch != null) OnSearch(); }));
            row.Children.Add(MakeSep());
            itemsPanel.Orientation = Layout.V ? Orientation.Vertical : Orientation.Horizontal;
            itemsPanel.Background = Brushes.Transparent;
            itemsPanel.MouseMove += delegate(object s, MouseEventArgs e) { Magnify(Layout.V ? e.GetPosition(itemsPanel).Y : e.GetPosition(itemsPanel).X); };
            itemsPanel.MouseLeave += delegate { Magnify(double.NaN); };
            row.Children.Add(itemsPanel);
            row.Children.Add(MakeSep());
            row.Children.Add(MakeStatus());
            row.Children.Add(MakeClock());

            card.Child = row;
            fullCard = card;
            // klik kanan area kosong dock = menu SlideDock (pengganti ikon tray yang ikut tersembunyi)
            AttachDockMenu(card);
            BuildNotch();
            rootGrid = new Grid();
            // lapisan bentuk transisi: satu bentuk yang melebar/menyusut antara notch & dock penuh
            morphLayer = new Canvas();
            morphLayer.IsHitTestVisible = false;
            morphPath = new System.Windows.Shapes.Path();
            morphGloss = new System.Windows.Shapes.Path();
            morphPath.Visibility = Visibility.Collapsed;
            morphGloss.Visibility = Visibility.Collapsed;
            morphLayer.Children.Add(morphPath);
            morphLayer.Children.Add(morphGloss);
            rootGrid.Children.Add(morphLayer);
            rootGrid.Children.Add(card);
            rootGrid.Children.Add(notch);
            Content = rootGrid;
            ApplySize();
            SetupDrag(card, false);
            SetupDrag(notch, true);
            PreviewMouseMove += delegate(object s, MouseEventArgs e) { DragMove(e); };
            notch.SizeChanged += delegate { if (!dragging) SetSlide(slideCur); };
            card.SizeChanged += delegate { if (!dragging) ApplySlide(false); };
            SizeChanged += delegate { if (!dragging) ApplySlide(false); };
            LoadTheme();
            StyleSurfaces();
            // cek posisi kursor tiap 150 ms: dekat dock = buka penuh, menjauh 0,7 detik = kembali ke notch
            previewTimer.Interval = TimeSpan.FromMilliseconds(380);
            previewTimer.Tick += delegate { ShowPreview(); };
            collapseTimer.Interval = TimeSpan.FromMilliseconds(150);
            collapseTimer.Tick += delegate { HoverPoll(); };
            collapseTimer.Start();

            card.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)
                {
                    suppressUp = true;
                    if (OnDoubleClick != null) OnDoubleClick();
                }
            };
            card.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (suppressUp) { suppressUp = false; return; }
                if (OnToggle != null) OnToggle();
            };
            PreviewDragEnter += delegate(object s, DragEventArgs e) { DragHover(e); };
            PreviewDragOver += delegate(object s, DragEventArgs e) { DragHover(e); };
            DragLeave += delegate(object s, DragEventArgs e)
            {
                Point q = e.GetPosition(this);
                if (q.X < 0 || q.Y < 0 || q.X >= ActualWidth || q.Y >= ActualHeight) { dragHoverEl = null; previewTimer.Stop(); }
            };
            Drop += delegate(object s, DragEventArgs e)
            {
                dragHoverEl = null;
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                foreach (string f in files) PinStore.Add(f, System.IO.Path.GetFileNameWithoutExtension(f));
                signature = "";
                UpdateItems();
            };

            SourceInitialized += OnSourceInit;
            ContentRendered += delegate { SetupAppBar(); };
            SizeChanged += delegate { Reposition(); };
            DpiChanged += delegate { Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Background); };
            Closed += delegate { RemoveAppBar(); };

            tickTimer.Interval = TimeSpan.FromSeconds(1);
            tickTimer.Tick += delegate { Tick(); };
            tickTimer.Start();
            UpdateStatus();
            Tick();
        }
        readonly DispatcherTimer tickTimer = new DispatcherTimer();


        // ----- ukuran dock: kecil / sedang / besar, atau otomatis menurut ukuran layar -----
        Grid rootGrid;
        public double DockScale = 1;

        public static double ScaleFor(int size, Rect scr)
        {
            if (size == 1) return 0.8;
            if (size == 2) return 0.9;
            if (size == 3) return 1.0;
            // otomatis: layar kecil (mis. laptop 15" dengan skala 125%) memakai dock yang lebih kecil
            double h = Layout.V ? scr.Width : scr.Height;
            return h < 800 ? 0.8 : h < 1000 ? 0.9 : 1.0;
        }

        public void ApplySize()
        {
            DockScale = ScaleFor(Prefs.DockSize, ScreenDip);
            rootGrid.LayoutTransform = Math.Abs(DockScale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(DockScale, DockScale);
            Dispatcher.BeginInvoke(new Action(delegate { ApplySlide(false); }), DispatcherPriority.Loaded);
        }

        // ----- geser dock di sepanjang tepinya (bawah: kiri-kanan, kiri/kanan: atas-bawah) -----
        // Satu posisi (pecahan -1..1) dipakai bersama: notch digeser sampai pojok = -1/1, dock penuh pun
        // menempel di ujung yang sama, jadi saat dibuka dock "tumbuh" dari tempat notch berada.
        // Notch maupun dock penuh sama-sama bisa digeser.
        readonly TranslateTransform cardShift = new TranslateTransform();
        readonly TranslateTransform notchShift = new TranslateTransform();
        double slideTarget, slideCur;          // pecahan -1..1
        bool slideLoop, dragArmed, dragging;
        Point dragStart;
        double dragFrom, dragRange;
        FrameworkElement dragEl;
        readonly Stopwatch slideWatch = new Stopwatch();

        string SlideKey { get { return "slide" + Layout.Side; } }

        // jarak geser maksimum dari tengah (satuan dalam dock, sebelum skala) agar tetap di dalam layar
        double RangeOf(FrameworkElement e)
        {
            if (e == null || DockScale <= 0) return 0;
            double win = (Layout.V ? ActualHeight : ActualWidth) / DockScale;
            double size = Layout.V ? e.ActualHeight : e.ActualWidth;
            if (size <= 0) return 0;
            return Math.Max(0, (win - size) / 2 - 8);
        }

        public void ApplySlide(bool animate)
        {
            double frac = Prefs.DockMove ? Prefs.Get(SlideKey, 0) / 1000.0 : 0;
            slideTarget = Math.Max(-1, Math.Min(1, frac));
            if (animate) { StartSlideLoop(); return; }
            slideCur = slideTarget;
            SetSlide(slideCur);
        }

        void SetSlide(double f)
        {
            double c = f * RangeOf(fullCard), n = f * RangeOf(notch);
            if (Layout.V) { cardShift.X = 0; cardShift.Y = c; notchShift.X = 0; notchShift.Y = n; }
            else { cardShift.X = c; cardShift.Y = 0; notchShift.X = n; notchShift.Y = 0; }
        }

        void StartSlideLoop()
        {
            if (slideLoop) return;
            slideLoop = true;
            slideWatch.Restart();
            CompositionTarget.Rendering += SlideStep;
        }

        // tiap frame layar (60 fps): mendekati target dengan halus, dihitung dari waktu yang berlalu
        void SlideStep(object sender, EventArgs e)
        {
            double dt = Math.Min(50, slideWatch.Elapsed.TotalMilliseconds);
            slideWatch.Restart();
            double d = slideTarget - slideCur;
            if (!dragging && Math.Abs(d) < 0.0005)
            {
                slideCur = slideTarget;
                SetSlide(slideCur);
                CompositionTarget.Rendering -= SlideStep;
                slideLoop = false;
                return;
            }
            slideCur += d * (1 - Math.Pow(1 - 0.35, dt / 16.7));
            SetSlide(slideCur);
        }

        // bagian yang bisa diklik (ikon, jam, status, app di notch) tidak memulai geser; area kosong bisa
        static bool IsInteractive(DependencyObject o, FrameworkElement stop)
        {
            while (o != null && o != stop)
            {
                FrameworkElement fe = o as FrameworkElement;
                if (fe != null && fe.Cursor == Cursors.Hand) return true;
                o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
            }
            return false;
        }

        void SetupDrag(FrameworkElement el, bool isNotch)
        {
            el.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (!Prefs.DockMove || e.ClickCount > 1 || compact != isNotch) return;
                if (IsInteractive(e.OriginalSource as DependencyObject, el)) return;
                dragArmed = true;
                dragEl = el;
                dragStart = e.GetPosition(this);
                dragFrom = slideTarget;
            };
            el.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (dragging) e.Handled = true;               // digeser = bukan klik (tidak membuka Start / dock)
                EndDrag();
            };
            el.LostMouseCapture += delegate { if (dragging) EndDrag(); };
        }

        void DragMove(MouseEventArgs e)
        {
            if (!dragArmed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
            Point pos = e.GetPosition(this);
            double delta = Layout.V ? pos.Y - dragStart.Y : pos.X - dragStart.X;
            if (!dragging)
            {
                if (Math.Abs(delta) < 5) return;              // ambang kecil: klik biasa tidak ikut menggeser
                dragRange = RangeOf(dragEl);
                if (dragRange <= 0) { dragArmed = false; return; }
                dragging = true;
                dragEl.CaptureMouse();
                HidePreview();
                Magnify(double.NaN);
                MenuCloser.CloseAll();
                EndPeek(true);                                // menggeser notch tidak membuka dock
            }
            slideTarget = Math.Max(-1, Math.Min(1, dragFrom + delta / DockScale / dragRange));
            StartSlideLoop();
        }

        void EndDrag()
        {
            bool was = dragging;
            dragArmed = false;
            dragging = false;
            if (dragEl != null && dragEl.IsMouseCaptured) dragEl.ReleaseMouseCapture();
            if (!was) return;
            lastNear = DateTime.Now;
            Prefs.Set(SlideKey, (int)Math.Round(slideTarget * 1000));
            if (OnDockMoved != null) OnDockMoved(this);
        }

        public Action<DockWindow> OnDockMoved;

        // ----- notch: dock mengecil saat ada app aktif (mirip Poni, tapi di bawah) -----
        // ----- tema: Glass (kaca buram) / Dark; bentuk menempel ke tepi bawah layar -----
        public string Theme = "glass";
        static readonly string ThemeFile = System.IO.Path.Combine(Store.Dir, "theme.txt");

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)]
        struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WinCompAttrData d);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

        void LoadTheme()
        {
            try
            {
                if (File.Exists(ThemeFile))
                {
                    string s = File.ReadAllText(ThemeFile).Trim();
                    Theme = UI.NormTheme(s);
                }
            }
            catch { }
        }

        public void ApplyTheme(string th)
        {
            th = UI.NormTheme(th);
            Theme = th;
            try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(ThemeFile, th); } catch { }
            StyleSurfaces();
            ApplyBlur();
        }

        // bentuk menempel ke bawah: sudut atas membulat, kaki kiri-kanan melebar dengan lengkung cekung
        const double Flare = 12;
        bool shaped;
        System.Windows.Shapes.Path fullShape, fullGloss, notchShape, notchGloss;

        static Geometry FlareGeometry(double W, double H, double r, double f)
        {
            if (W < 2 * (f + r) || H < r + f) return Geometry.Empty;
            string inv = "F0 M 0,{1} C {7},{1} {2},{8} {2},{3} L {2},{4} C {2},{9} {10},0 {5},0 L {11},0 C {12},0 {13},{9} {13},{4} L {13},{3} C {13},{8} {14},{1} {0},{1} Z";
            Func<double, string> n = delegate(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); };
            string d = string.Format(inv,
                n(W), n(H), n(f), n(H - f), n(r), n(f + r),
                "", n(f * 0.55), n(H - f * 0.45), n(r * 0.45), n(f + r * 0.45),
                n(W - f - r), n(W - f - r * 0.45), n(W - f), n(W - f * 0.55));
            return Geometry.Parse(d);
        }

        // bentuk yang sama, diputar agar menempel ke tepi kiri atau kanan layar
        static Geometry ShapeFor(Size s, double radius)
        {
            if (!Layout.V) return FlareGeometry(s.Width, s.Height, radius, Flare);
            Geometry g = FlareGeometry(s.Height, s.Width, radius, Flare).CloneCurrentValue();
            Matrix m = Layout.Side == 1 ? new Matrix(0, 1, -1, 0, s.Width, 0) : new Matrix(0, 1, 1, 0, 0, 0);
            g.Transform = new MatrixTransform(m);
            return g;
        }

        void WrapWithShape(Border b, double radius, out System.Windows.Shapes.Path shape, out System.Windows.Shapes.Path gloss)
        {
            UIElement child = b.Child;
            b.Child = null;
            Grid g = new Grid();
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            System.Windows.Shapes.Path gl = new System.Windows.Shapes.Path();
            gl.IsHitTestVisible = false;
            // Canvas tidak ikut menentukan ukuran -> bentuk mengikuti isi, bukan sebaliknya
            Canvas layer = new Canvas();
            layer.Children.Add(p);
            layer.Children.Add(gl);
            g.Children.Add(layer);
            g.Children.Add(child);
            g.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                Geometry geo = ShapeFor(e.NewSize, radius);
                p.Data = geo;
                gl.Data = geo;
            };
            b.Child = g;
            shape = p;
            gloss = gl;
        }

        void StyleSurfaces()
        {
            if (!shaped)
            {
                shaped = true;
                foreach (Border b in new Border[] { fullCard, notch })
                {
                    b.Margin = new Thickness(0);
                    b.Padding = new Thickness(0);
                    b.HorizontalAlignment = Layout.Side == 1 ? HorizontalAlignment.Left : Layout.Side == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                    b.VerticalAlignment = Layout.V ? VerticalAlignment.Center : VerticalAlignment.Bottom;
                    b.RenderTransformOrigin = Layout.Origin;
                    b.RenderTransform = b == notch ? notchShift : cardShift;
                    b.Background = null;
                    b.BorderThickness = new Thickness(0);
                    b.CornerRadius = new CornerRadius(0);
                    b.Effect = null;
                    FrameworkElement c = b.Child as FrameworkElement;
                    if (c != null)
                    {
                        if (Layout.Side == 1) c.Margin = b == notch ? new Thickness(8, 16 + Flare, 7, 18 + Flare) : new Thickness(4, 8 + Flare, 6, 8 + Flare);
                        else if (Layout.Side == 2) c.Margin = b == notch ? new Thickness(7, 16 + Flare, 8, 18 + Flare) : new Thickness(6, 8 + Flare, 4, 8 + Flare);
                        else c.Margin = b == notch ? new Thickness(16 + Flare, 7, 18 + Flare, 8) : new Thickness(8 + Flare, 6, 8 + Flare, 4);
                    }
                }
                FrameworkElement nc = notch.Child as FrameworkElement;
                if (nc != null) notchContentBase = nc.Margin;
                WrapWithShape(fullCard, 20, out fullShape, out fullGloss);
                WrapWithShape(notch, 14, out notchShape, out notchGloss);
            }
            UI.SetTheme(Theme);
            bool light = Theme == "glass2";
            bool glass = Theme == "glass" || light;
            foreach (System.Windows.Shapes.Path p in new System.Windows.Shapes.Path[] { fullShape, notchShape })
            {
                if (light)
                {
                    // Glass 2: kaca putih dominan, garis tepi abu tipis
                    p.Fill = new SolidColorBrush(Color.FromArgb(0x96, 0xF6, 0xF6, 0xF8));
                    p.Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0));
                }
                else
                {
                    p.Fill = new SolidColorBrush(glass ? Color.FromArgb(0x5C, 0x14, 0x14, 0x1C) : Color.FromArgb(0xF4, 0x1B, 0x1B, 0x21));
                    p.Stroke = new SolidColorBrush(Color.FromArgb((byte)(glass ? 0x80 : 0x40), 255, 255, 255));
                }
                p.StrokeThickness = 1;
            }
            foreach (System.Windows.Shapes.Path gl in new System.Windows.Shapes.Path[] { fullGloss, notchGloss })
            {
                LinearGradientBrush g = new LinearGradientBrush();
                g.StartPoint = Layout.Side == 1 ? new Point(1, 0) : new Point(0, 0);                       // kilau dari tepi luar
                g.EndPoint = Layout.Side == 1 ? new Point(0, 0) : Layout.Side == 2 ? new Point(1, 0) : new Point(0, 1);
                g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(light ? 0x70 : glass ? 0x30 : 0x00), 255, 255, 255), 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6));
                gl.Fill = g;
            }
        }
        void ApplyBlur()
        {
            if (hwnd == IntPtr.Zero) return;
            AccentPolicy a = new AccentPolicy();
            a.AccentState = 0;      // tanpa blur Windows: kaca digambar sendiri agar benar-benar transparan
            a.AccentFlags = 2;
            a.GradientColor = 0;
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(a, p, false);
                WinCompAttrData d = new WinCompAttrData();
                d.Attribute = 19;                           // WCA_ACCENT_POLICY
                d.Data = p;
                d.SizeOfData = size;
                SetWindowCompositionAttribute(hwnd, ref d);
            }
            catch { }
            finally { Marshal.FreeHGlobal(p); }
            UpdateRegion();
        }

        // bentuk jendela: sudut atas membulat, sisi bawah rata menempel ke layar
        void UpdateRegion()
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowRgn(hwnd, IntPtr.Zero, true);   // bentuk kini digambar oleh Path, tidak perlu region
            if (hwnd != IntPtr.Zero) return;
            double s = Scale();
            RECT wr;
            if (!GetWindowRect(hwnd, out wr)) return;
            int w = wr.right - wr.left;
            int h = wr.bottom - wr.top;
            if (w <= 0 || h <= 0) return;
            int d = (int)Math.Round((compact ? 14 : 20) * s * 2);
            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + d + 1, d, d);
            SetWindowRgn(hwnd, rgn, true);
        }

        void BuildNotch()
        {
            notch = new Border();
            notch.Margin = new Thickness(10, 10, 10, 4);
            notch.CornerRadius = new CornerRadius(15);
            notch.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x12, 0x12, 0x16));
            notch.BorderBrush = UI.Ink(0x40);
            notch.BorderThickness = new Thickness(1);
            notch.Padding = new Thickness(10, 5, 12, 5);
            notch.HorizontalAlignment = HorizontalAlignment.Center;
            notch.VerticalAlignment = VerticalAlignment.Bottom;
            notch.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black };
            notch.Opacity = 0;
            notch.IsHitTestVisible = false;
            StackPanel sp = new StackPanel();
            sp.Orientation = Layout.V ? Orientation.Vertical : Orientation.Horizontal;
            notchIcon.Width = 20;
            notchIcon.Height = 20;
            RenderOptions.SetBitmapScalingMode(notchIcon, BitmapScalingMode.HighQuality);
            notchApps.Orientation = Layout.V ? Orientation.Vertical : Orientation.Horizontal;
            notchApps.HorizontalAlignment = HorizontalAlignment.Center;
            notchApps.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(notchApps);
            Border dot = new Border();
            dot.Width = Layout.V ? 14 : 1;
            dot.Height = Layout.V ? 1 : 14;
            dot.Margin = Layout.V ? new Thickness(0, 8, 0, 8) : new Thickness(10, 0, 10, 0);
            dot.Background = UI.Ink(0x40);
            notchSep = dot;
            notch.Cursor = Cursors.Hand;
            // klik bagian notch yang bukan ikon app = langsung buka dock penuh (tanpa menunggu jeda)
            notch.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.Handled || !compact) return;
                e.Handled = true;
                lastNear = DateTime.Now;
                EndPeek(false);
                SetCompact(false);
            };
            sp.Children.Add(dot);
            notchRing.HorizontalAlignment = HorizontalAlignment.Center;
            notchRing.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(notchRing);
            notch.Child = sp;
        }

        // ----- transisi notch <-> dock penuh (gaya Dynamic Island) -----
        // SATU bentuk latar yang ukurannya berubah mulus dari notch ke dock (atau sebaliknya), digambar ulang
        // tiap frame layar dari waktu yang benar-benar berlalu. Isi (ikon/teks) tidak ikut "ditarik":
        // isi lama memudar cepat, isi baru muncul dengan sedikit membesar, ukurannya tetap proporsional.
        Canvas morphLayer;
        System.Windows.Shapes.Path morphPath, morphGloss;
        int morphGen;
        bool morphing;
        Rect morphRect;
        double morphRadius;

        Rect RectIn(FrameworkElement e)
        {
            Point p = e.TranslatePoint(new Point(0, 0), morphLayer);
            return new Rect(p, new Size(e.ActualWidth, e.ActualHeight));
        }

        static ScaleTransform ScaleOfContent(UIElement e)
        {
            ScaleTransform st = e.RenderTransform as ScaleTransform;
            if (st == null || st.IsFrozen) { st = new ScaleTransform(1, 1); e.RenderTransform = st; }
            e.RenderTransformOrigin = Layout.Origin;
            return st;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        void SetCompact(bool c)
        {
            if (c == compact) return;
            if (c) ResetPeek();
            compact = c;
            Border show = c ? notch : fullCard;
            Border hide = c ? fullCard : notch;
            show.IsHitTestVisible = true;
            hide.IsHitTestVisible = false;
            Grid sg = show.Child as Grid, hg = hide.Child as Grid;
            bool ready = sg != null && hg != null && sg.Children.Count > 1 && hg.Children.Count > 1 && morphPath != null
                         && show.ActualWidth > 0 && hide.ActualWidth > 0;
            // awal: transparansi isi mengikuti keadaan terakhir (bisa sedang di tengah transisi)
            double sStart = show.Opacity < 0.01 ? 0 : (ready ? sg.Children[1].Opacity : 1);
            double hStart = hide.Opacity < 0.01 ? 0 : (ready ? hg.Children[1].Opacity : 1);
            foreach (Border b in new Border[] { show, hide })
            {
                b.BeginAnimation(UIElement.OpacityProperty, null);
                b.Opacity = 1;
            }
            if (!ready)
            {
                hide.Opacity = 0;
                return;
            }
            UIElement sLayer = sg.Children[0], sContent = sg.Children[1];
            UIElement hLayer = hg.Children[0], hContent = hg.Children[1];
            sLayer.Opacity = 0;                   // latar masing-masing disembunyikan; bentuk transisi yang tampil
            hLayer.Opacity = 0;
            Rect from = morphing ? morphRect : RectIn(hide);
            Rect to = RectIn(show);
            double rFrom = morphing ? morphRadius : (c ? 20 : 14), rTo = c ? 14 : 20;
            morphPath.Fill = fullShape.Fill;
            morphPath.Stroke = fullShape.Stroke;
            morphPath.StrokeThickness = fullShape.StrokeThickness;
            morphGloss.Fill = fullGloss.Fill;
            morphPath.Visibility = Visibility.Visible;
            morphGloss.Visibility = Visibility.Visible;
            ScaleTransform sScale = ScaleOfContent(sContent), hScale = ScaleOfContent(hContent);
            sContent.Opacity = sStart;
            int gen = ++morphGen;
            morphing = true;
            double ms = c ? 320 : 420;
            Stopwatch sw = Stopwatch.StartNew();
            AnimClock clock = new AnimClock();
            EventHandler step = null;
            step = delegate
            {
                if (gen != morphGen) { CompositionTarget.Rendering -= step; return; }
                double t = Clamp01(clock.Tick() / ms);
                double e;
                if (c) e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;          // menyusut: ease-in-out
                else { double k = 1.15, u = t - 1; e = 1 + (k + 1) * u * u * u + k * u * u; } // melebar: sedikit memantul
                Rect r = new Rect(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e,
                                  Math.Max(1, from.Width + (to.Width - from.Width) * e), Math.Max(1, from.Height + (to.Height - from.Height) * e));
                double rad = rFrom + (rTo - rFrom) * Clamp01(e);
                morphRect = r;
                morphRadius = rad;
                Geometry geo = ShapeFor(r.Size, rad);
                morphPath.Data = geo;
                morphGloss.Data = geo;
                Canvas.SetLeft(morphPath, r.X); Canvas.SetTop(morphPath, r.Y);
                Canvas.SetLeft(morphGloss, r.X); Canvas.SetTop(morphGloss, r.Y);
                double fin = Clamp01((t - 0.25) / 0.75); fin = 1 - (1 - fin) * (1 - fin);     // isi baru muncul setelah bentuk mulai berubah
                double fout = Clamp01(t / 0.35);                                                 // isi lama hilang cepat
                sContent.Opacity = sStart + (1 - sStart) * fin;
                hContent.Opacity = hStart * (1 - fout);
                double ss = 0.92 + 0.08 * fin, hs = 1 - 0.06 * fout;
                sScale.ScaleX = ss; sScale.ScaleY = ss;
                hScale.ScaleX = hs; hScale.ScaleY = hs;
                if (t < 1) return;
                CompositionTarget.Rendering -= step;
                morphing = false;
                sLayer.Opacity = 1;
                sContent.Opacity = 1;
                sScale.ScaleX = 1; sScale.ScaleY = 1;
                hide.Opacity = 0;
                morphPath.Visibility = Visibility.Collapsed;
                morphGloss.Visibility = Visibility.Collapsed;
            };
            CompositionTarget.Rendering += step;
        }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        struct POINT { public int X, Y; }
        DateTime lastNear = DateTime.Now;

        bool CursorNear()
        {
            if (hwnd == IntPtr.Zero) return false;
            POINT p;
            if (!GetCursorPos(out p)) return false;
            FrameworkElement s = compact ? (FrameworkElement)notch : fullCard;
            if (s == null || s.ActualWidth <= 0) return false;
            Point tl = s.PointToScreen(new Point(0, 0));
            Point br = s.PointToScreen(new Point(s.ActualWidth, s.ActualHeight));
            return p.X >= tl.X - 4 && p.X <= br.X + 4 && p.Y >= tl.Y - 6 && p.Y <= br.Y + 8;
        }

        // ----- 3. menu klik kanan: tutup saat klik di luar menu -----
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        bool MenuOpen()
        {
            bool clicked = (GetAsyncKeyState(0x01) & 0x8001) != 0 || (GetAsyncKeyState(0x02) & 0x8001) != 0;
            bool open = false;
            foreach (UIElement c in itemsPanel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                if (fe == null || fe.ContextMenu == null || !fe.ContextMenu.IsOpen) continue;
                if (clicked && !fe.ContextMenu.IsMouseOver && !fe.IsMouseOver) fe.ContextMenu.IsOpen = false;
                else open = true;
            }
            return open;
        }

        void CloseMenus()
        {
            foreach (UIElement c in itemsPanel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                if (fe != null && fe.ContextMenu != null && fe.ContextMenu.IsOpen) fe.ContextMenu.IsOpen = false;
            }
        }

        // kursor mendekati notch: notch memanjang sedikit lalu terus pelan seperti "loading"; bila kursor
        // bertahan selama PeekDwell baru berubah jadi dock penuh. Kursor yang hanya lewat tidak membuka dock,
        // dan selama jeda app di notch bisa langsung diklik (klik = jeda diulang dari awal).
        const double PeekDwell = 750;
        DateTime peekSince = DateTime.MinValue;
        POINT peekStartAt, peekHoldAt;
        bool peekHold;

        static double Moved(POINT a, POINT b) { return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)); }

        void HoverPoll()
        {
            if (hiddenForFullscreen) return;
            if (dragArmed || dragging || reOn || reSettling) { lastNear = DateTime.Now; return; }
            bool menu = MenuOpen();
            bool popup = AnyPopupOpen != null && AnyPopupOpen();
            if (preview.Showing && previewFromNotch && compact)     // pratinjau dari notch: notch tetap kecil
            {
                lastNear = DateTime.Now;
                EndPeek(true);
                return;
            }
            if (popup || menu || preview.Showing)                   // dock penuh hanya saat ada interaksi; selain itu notch
            {
                lastNear = DateTime.Now;
                EndPeek(false);
                SetCompact(false);
                return;
            }
            if (CursorNear())
            {
                lastNear = DateTime.Now;
                if (compact)
                {
                    // di atas ikon app notch: pratinjau/klik app, bukan membuka dock (loading hanya di bagian kosong).
                    // Status hanya berubah bila KURSOR yang bergerak: saat notch membesar ikon bisa bergeser ke bawah
                    // kursor yang diam, dan itu tidak boleh membatalkan loading (sebelumnya notch jadi besar-kecil terus).
                    POINT cp;
                    GetCursorPos(out cp);
                    bool overApp = CursorOverNotchApp();
                    if (peekSince == DateTime.MinValue)
                    {
                        if (overApp) return;
                        if (peekHold && Moved(cp, peekHoldAt) < 6) return;   // baru dibatalkan: tunggu kursor bergerak dulu
                        peekHold = false;
                        peekSince = DateTime.Now;
                        peekStartAt = cp;
                        Peek(true);
                        collapseTimer.Interval = TimeSpan.FromMilliseconds(40);   // cek lebih sering selama jeda
                    }
                    else if (overApp && Moved(cp, peekStartAt) >= 6)
                    {
                        EndPeek(true);                              // kursor sengaja pindah ke ikon app
                        peekHold = true;
                        peekHoldAt = cp;
                        return;
                    }
                    if ((DateTime.Now - peekSince).TotalMilliseconds < PeekDwell) return;
                }
                EndPeek(false);
                SetCompact(false);
                return;
            }
            EndPeek(true);                                          // kursor pergi sebelum jeda habis: notch kembali
            peekHold = false;
            if (!Prefs.Notch) SetCompact(false);                       // notch dimatikan: dock selalu penuh
            else if ((DateTime.Now - lastNear).TotalMilliseconds > 700) SetCompact(true);
        }

        void EndPeek(bool shrink)
        {
            if (peekSince == DateTime.MinValue) return;
            peekSince = DateTime.MinValue;
            collapseTimer.Interval = TimeSpan.FromMilliseconds(150);
            if (shrink) Peek(false);
        }

        // jarak notch memanjang (menjauh dari tepi layar), 0..1 dianimasikan
        const double PeekGrow = 10;        // menjauh dari tepi layar
        const double PeekWiden = 20;       // ke masing-masing samping
        double peekAmt;
        int peekGen;
        Thickness notchContentBase;

        FrameworkElement NotchContent()
        {
            Grid g = notch.Child as Grid;
            return g != null && g.Children.Count > 1 ? g.Children[1] as FrameworkElement : null;
        }

        void ApplyPeek(double a)
        {
            peekAmt = a;
            FrameworkElement c = NotchContent();
            if (c == null) return;
            Thickness b = notchContentBase;
            double d = PeekGrow * a, w = PeekWiden * a;
            // notch digeser ke pinggir (f = -1..1): pelebaran lebih banyak ke arah tengah layar, sehingga tepi
            // luar tetap di pojok DAN ikon app tidak bergeser (sebelumnya ikon bisa pindah ke bawah kursor)
            double f = Math.Max(-1, Math.Min(1, slideCur));
            double lo = w * (1 + f), hi = w * (1 - f);          // sisi kiri/atas, sisi kanan/bawah
            // isi tetap di tempat; bentuk notch memanjang ke dalam layar dan melebar ke kedua sisi
            if (Layout.Side == 1) c.Margin = new Thickness(b.Left, b.Top + lo, b.Right + d, b.Bottom + hi);
            else if (Layout.Side == 2) c.Margin = new Thickness(b.Left + d, b.Top + lo, b.Right, b.Bottom + hi);
            else c.Margin = new Thickness(b.Left + lo, b.Top + d, b.Right + hi, b.Bottom);
        }

        void Peek(bool on)
        {
            double from = peekAmt, to = on ? 1 : 0;
            if (Math.Abs(from - to) < 0.001) return;
            int gen = ++peekGen;
            // membuka: cepat ke 70% (terasa responsif), lalu pelan sampai penuh tepat saat jeda habis
            double ms = on ? PeekDwell : 150, quick = 160 / PeekDwell;
            AnimClock clock = new AnimClock();
            EventHandler step = null;
            step = delegate
            {
                if (gen != peekGen) { CompositionTarget.Rendering -= step; return; }
                double k = Clamp01(clock.Tick() / ms);
                double e;
                if (!on) e = 1 - Math.Pow(1 - k, 3);
                else if (k < quick) { double u = k / quick; e = 0.7 * (1 - (1 - u) * (1 - u)); }
                else e = 0.7 + 0.3 * (k - quick) / (1 - quick);
                ApplyPeek(from + (to - from) * e);
                if (k >= 1) CompositionTarget.Rendering -= step;
            };
            CompositionTarget.Rendering += step;
        }

        // langsung ke ukuran normal (tanpa animasi): dipakai sebelum dock kembali menjadi notch
        void ResetPeek()
        {
            ++peekGen;
            if (peekAmt != 0) { ApplyPeek(0); notch.UpdateLayout(); }
        }
        static ImageSource IconOf(DockItem it)
        {
            ImageSource src = null;
            if (it.Lnk != null) src = Icons.For(it.Lnk);
            if (src == null && it.Aumid != null) src = Icons.FromShell("shell:AppsFolder\\" + it.Aumid);
            if (src == null && it.Exe != null) src = Icons.FromPath(it.Exe);
            if (src == null && it.IconWin != IntPtr.Zero) src = Icons.FromWindow(it.IconWin);
            return src;
        }

        // efek membesar seperti Dock Mac: dihitung per frame (60 fps)
        double magX = double.NaN;
        bool magLoop;

        void Magnify(double x)
        {
            magX = x;
            if (!magLoop)
            {
                magLoop = true;
                CompositionTarget.Rendering += MagStep;
            }
        }

        void MagStep(object sender, EventArgs e)
        {
            bool moving = false;
            int mag = Prefs.Magnify;                                  // 0 mati, 1 halus, 2 kuat
            double peak = mag == 0 ? 0 : mag == 1 ? 0.22 : 0.5, sigma = mag == 1 ? 44 : 38;
            foreach (UIElement c in itemsPanel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                if (fe == null) continue;
                ScaleTransform st = fe.Tag as ScaleTransform;
                if (st == null) continue;
                double target = 1;
                if (!double.IsNaN(magX) && peak > 0)
                {
                    double cx = Layout.V ? fe.TranslatePoint(new Point(0, fe.ActualHeight / 2), itemsPanel).Y : fe.TranslatePoint(new Point(fe.ActualWidth / 2, 0), itemsPanel).X;
                    // kurva lonceng yang lebih tajam: ikon di bawah kursor ~1.5x, tetangga ~1.2x, dua ikon jauhnya hampir normal
                    double d = Math.Abs(magX - cx);
                    target = 1 + peak * Math.Exp(-(d * d) / (2 * sigma * sigma));
                }
                double cur = st.ScaleX;
                double nv = cur + (target - cur) * 0.22;
                if (Math.Abs(target - nv) < 0.002) nv = target; else moving = true;
                st.ScaleX = nv;
                st.ScaleY = nv;
                Panel.SetZIndex(fe, fe == reTile && (reOn || reSettling) ? 1000 : (int)(nv * 100));   // ikon yang diseret di atas
            }
            if (!moving)
            {
                CompositionTarget.Rendering -= MagStep;
                magLoop = false;
            }
        }
        // ----- elemen -----
        FrameworkElement MakeSep()
        {
            Border b = new Border();
            b.Width = Layout.V ? 28 : 1;
            b.Height = Layout.V ? 1 : 28;
            b.Margin = Layout.V ? new Thickness(0, 6, 0, 6) : new Thickness(6, 0, 6, 0);
            b.Background = UI.Ink(0x40);
            return b;
        }

        Border NewTile() { return NewTile(true); }

        Border NewTile(bool hover)
        {
            Border t = new Border();
            t.MinWidth = 46;
            t.Height = 50;
            t.Margin = Layout.V ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0);
            t.CornerRadius = new CornerRadius(12);
            t.Background = Brushes.Transparent;
            if (hover)
            {
                t.Cursor = Cursors.Hand;
                UI.Hover(t, 0x30);
            }
            return t;
        }
        Border GlyphTile(string glyph, double size, string tip, Action click)
        {
            Border t = NewTile();
            t.Child = UI.Glyph(glyph, size);
            UI.Tip(t, tip);
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return t;
        }

        readonly Border updateDot = new Border();
        public void SetUpdateDot(bool on) { updateDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed; }

        FrameworkElement MakeStart()
        {
            Border t = NewTile();
            UniformGrid g = new UniformGrid();
            g.Rows = 2;
            g.Columns = 2;
            g.Width = 22;
            g.Height = 22;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Center;
            for (int i = 0; i < 4; i++)
            {
                Border d = new Border();
                d.Margin = new Thickness(1);
                d.CornerRadius = new CornerRadius(1.5);
                d.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
                g.Children.Add(d);
            }
            Grid sg = new Grid();
            sg.Children.Add(g);
            updateDot.Width = 8;
            updateDot.Height = 8;
            updateDot.CornerRadius = new CornerRadius(4);
            updateDot.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            updateDot.BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1C));
            updateDot.BorderThickness = new Thickness(1.5);
            updateDot.HorizontalAlignment = HorizontalAlignment.Right;
            updateDot.VerticalAlignment = VerticalAlignment.Top;
            updateDot.Margin = new Thickness(0, 9, 7, 0);
            updateDot.Visibility = Visibility.Collapsed;
            updateDot.IsHitTestVisible = false;
            sg.Children.Add(updateDot);
            t.Child = sg;
            UI.Tip(t, "Start  ·  right-click for SlideDock options");
            AttachDockMenu(t);
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (OnToggle != null) OnToggle(); };
            return t;
        }

        FrameworkElement MakeBin()
        {
            Border t = NewTile();
            binImage.Width = 30;
            binImage.Height = 30;
            RenderOptions.SetBitmapScalingMode(binImage, BitmapScalingMode.HighQuality);
            t.Child = binImage;
            UI.Tip(t, "Recycle Bin");
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Launcher.Open("shell:RecycleBinFolder"); };
            ContextMenu cm = new ContextMenu();
            MenuItem open = new MenuItem();
            open.Header = "Open";
            open.Click += delegate { Launcher.Open("shell:RecycleBinFolder"); };
            MenuItem empty = new MenuItem();
            empty.Header = "Empty Recycle Bin...";
            empty.Click += delegate { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0); };
            cm.Items.Add(open);
            cm.Items.Add(empty);
            t.ContextMenu = cm;
            UpdateBin();
            return t;
        }

        // Wi-Fi, volume, baterai: masing-masing punya sorot, tooltip satu nama, dan pengaturannya sendiri saat diklik
        FrameworkElement MakeStatus()
        {
            statusTile = NewTile(false);
            StackPanel sp = new StackPanel();
            sp.Orientation = Layout.V ? Orientation.Vertical : Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            if (Layout.V)
            {
                statusTile.Height = double.NaN;
                statusTile.Padding = new Thickness(0, 4, 0, 4);
            }
            else statusTile.Padding = new Thickness(2, 0, 2, 0);
            netGlyph.Width = 20;
            netGlyph.TextAlignment = TextAlignment.Center;
            volGlyph.Width = 20;
            volGlyph.TextAlignment = TextAlignment.Center;
            netTile = StatusPart(netGlyph, "wifi", netTip);
            volTile = StatusPart(volGlyph, "sound", volTip);
            batTile = StatusPart(batRing, "battery", batTip);
            netTile.ToolTipOpening += delegate { RefreshSsid(); };
            // scroll di ikon volume = naik/turun 2% per langkah, seperti tray Windows
            volTile.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                e.Handled = true;
                double v = SysInfo.Volume();
                if (v < 0) return;
                v = Math.Round((v + e.Delta / 120.0 * 0.02) * 50) / 50;
                SysInfo.SetVolume(Math.Max(0, Math.Min(1, v)));
                UpdateVolume();
            };
            sp.Children.Add(netTile);
            sp.Children.Add(volTile);
            sp.Children.Add(batTile);
            statusTile.Child = sp;
            // celah di antara ikon tidak membuka Start
            statusTile.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            return statusTile;
        }

        Border StatusPart(FrameworkElement content, string mode, TextBlock tip)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            if (Layout.V)
            {
                b.Width = 38;
                b.MinHeight = 34;
                b.Padding = new Thickness(0, 5, 0, 5);
                b.Margin = new Thickness(0, 1, 0, 1);
            }
            else
            {
                b.Height = 38;
                b.MinWidth = 34;
                b.Padding = new Thickness(5, 0, 5, 0);
                b.Margin = new Thickness(1, 0, 1, 0);
            }
            UI.Hover(b, 0x30);
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;
            b.Child = content;
            UI.Tip(b, tip);
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (OnQuick != null) OnQuick(mode); };
            return b;
        }

        // tengah ikon status (koordinat layar) agar popup muncul tepat di atasnya
        public Point StatusCenter(string mode)
        {
            Border t = mode == "wifi" ? netTile : mode == "sound" ? volTile : mode == "battery" ? batTile : statusTile;
            if (t == null || t.ActualWidth <= 0) t = statusTile;
            return CenterOnScreen(t);
        }

        // tengah elemen dock dalam koordinat layar (DIP), sudah termasuk skala & geser dock
        Point CenterOnScreen(FrameworkElement e)
        {
            Point p = e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), this);
            return new Point(Left + p.X, Top + p.Y);
        }

        public Point ClockCenter() { return CenterOnScreen(clockTile); }

        // kotak dock penuh di layar (DIP): patokan posisi popup
        public Rect SurfaceRect()
        {
            if (fullCard == null || fullCard.ActualWidth <= 0) return Rect.Empty;
            Point a = fullCard.TranslatePoint(new Point(0, 0), this);
            Point b = fullCard.TranslatePoint(new Point(fullCard.ActualWidth, fullCard.ActualHeight), this);
            return new Rect(new Point(Left + a.X, Top + a.Y), new Point(Left + b.X, Top + b.Y));
        }
        Border trayTile;
        readonly PreviewWindow preview = new PreviewWindow();
        readonly DispatcherTimer previewTimer = new DispatcherTimer();
        DockItem pendingItem;
        FrameworkElement pendingTile;
        bool previewFromNotch;
        FrameworkElement dragHoverEl;

        // ikon app (dock penuh atau notch) di titik p (koordinat jendela)
        FrameworkElement AppAt(Point p)
        {
            Panel panel = compact ? (Panel)notchApps : itemsPanel;
            foreach (UIElement c in panel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                if (fe == null || !(fe.DataContext is DockItem) || !fe.IsVisible) continue;
                Point o = fe.TranslatePoint(new Point(0, 0), this);
                Point b = fe.TranslatePoint(new Point(fe.ActualWidth, fe.ActualHeight), this);
                if (new Rect(o, b).Contains(p)) return fe;
            }
            return null;
        }

        bool CursorOverNotchApp()
        {
            POINT c;
            if (!compact || hwnd == IntPtr.Zero || !GetCursorPos(out c)) return false;
            try { return AppAt(PointFromScreen(new Point(c.X, c.Y))) != null; }
            catch { return false; }
        }

        // file diseret dari Explorer ke atas ikon app: tampilkan pratinjau jendelanya (tanpa membuka dock)
        void DragHover(DragEventArgs e)
        {
            FrameworkElement el = AppAt(e.GetPosition(this));
            if (el == null)
            {
                if (dragHoverEl != null) { dragHoverEl = null; previewTimer.Stop(); }
                return;                                   // area lain: perilaku lama (lepas = sematkan ke dock)
            }
            e.Effects = DragDropEffects.None;             // ikon app bukan tempat menjatuhkan file
            e.Handled = true;
            if (el == dragHoverEl) return;
            dragHoverEl = el;
            DockItem it = el.DataContext as DockItem;
            if (it == null || it.Wins.Count == 0) { previewTimer.Stop(); return; }
            pendingItem = it;
            pendingTile = el;
            if (preview.Showing) ShowPreview(); else { previewTimer.Stop(); previewTimer.Start(); }
        }

        static string ItemKey(DockItem it) { return (it.Lnk ?? "") + "|" + (it.Key ?? ""); }

        void ShowPreview()
        {
            previewTimer.Stop();
            DockItem it = pendingItem;
            FrameworkElement t = pendingTile;
            if (it == null || t == null || it.Wins.Count == 0 || !t.IsVisible) return;
            bool fromNotch = notch.IsAncestorOf(t);
            if (fromNotch != compact) return;             // notch sudah berubah jadi dock (atau sebaliknya)
            FrameworkElement surface = fromNotch ? (FrameworkElement)notch : fullCard;
            Point tl = t.PointToScreen(new Point(0, 0));
            Point br = t.PointToScreen(new Point(t.ActualWidth, t.ActualHeight));
            Point mid = t.TranslatePoint(new Point(t.ActualWidth / 2, t.ActualHeight / 2), this);
            preview.Scr = ScreenDip;
            preview.ScrScale = Scale();
            preview.CenterY = Top + mid.Y;
            Point sl = surface.TranslatePoint(new Point(0, 0), this);
            Point sr = surface.TranslatePoint(new Point(surface.ActualWidth, 0), this);
            preview.SideX = Layout.Side == 1 ? Left + sr.X : Left + sl.X;
            previewFromNotch = fromNotch;
            preview.ShowFor(ItemKey(it), new List<IntPtr>(it.Wins), IconOf(it), new Rect(tl, br), Left + mid.X, Top + sl.Y);
        }

        public Func<List<KeyValuePair<string, Action>>> MenuProvider;

        void AttachDockMenu(FrameworkElement el)
        {
            ContextMenu cm = new ContextMenu();
            cm.Items.Add(new MenuItem { Header = "SlideDock" });   // isi awal: WPF tidak membuka menu yang kosong
            el.ContextMenu = cm;
            el.ContextMenuOpening += delegate(object s, ContextMenuEventArgs e)
            {
                cm.Items.Clear();
                if (MenuProvider == null) return;
                foreach (KeyValuePair<string, Action> kv in MenuProvider())
                {
                    if (kv.Key == null)
                    {
                        Border line = new Border();
                        line.Height = 1;
                        line.Margin = new Thickness(10, 4, 10, 4);
                        line.Background = UI.Ink(0x30);
                        line.IsHitTestVisible = false;
                        cm.Items.Add(line);
                        continue;
                    }
                    MenuItem mi = new MenuItem();
                    mi.Header = kv.Key;
                    Action a = kv.Value;
                    mi.Click += delegate { a(); };
                    cm.Items.Add(mi);
                }
            };
        }

        public double TrayLeft()
        {
            if (trayTile == null) return Left + ActualWidth / 2;          // tile tray tidak lagi dipasang di dock
            Point p = trayTile.TranslatePoint(new Point(0, 0), this);
            return Left + p.X;
        }

        public double ClockRight()
        {
            Point p = clockTile.TranslatePoint(new Point(clockTile.ActualWidth, 0), this);
            return Left + p.X;
        }

        FrameworkElement MakeClock()
        {
            Border t = NewTile();
            clockTile = t;
            t.Padding = new Thickness(8, 0, 8, 0);
            if (Layout.V) { t.Height = double.NaN; t.Padding = new Thickness(2, 8, 2, 8); clockDate.FontSize = 9.5; }
            StackPanel clock = new StackPanel();
            clock.VerticalAlignment = VerticalAlignment.Center;
            clockTime.Foreground = UI.Fg;
            clockTime.FontSize = 13.5;
            clockTime.FontWeight = FontWeights.SemiBold;
            clockTime.FontFamily = UI.Text;
            clockTime.TextAlignment = TextAlignment.Center;
            clockDate.Foreground = UI.Ink(0xB0);
            clockDate.FontSize = 10.5;
            clockDate.FontFamily = UI.Text;
            clockDate.TextAlignment = TextAlignment.Center;
            clock.Children.Add(clockTime);
            clock.Children.Add(clockDate);
            t.Child = clock;
            UI.Tip(t, "Calendar");
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (OnClock != null) OnClock(); };
            return t;
        }

        void UpdateBin()
        {
            ImageSource s = Icons.RecycleBin();
            if (s != null) binImage.Source = s;
        }

        // ----- status: internet, volume, baterai -----
        static string NetKind()
        {
            bool eth = false;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("loopback")
                        || d.Contains("wsl") || d.Contains("vethernet") || d.Contains("bluetooth")) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "wifi";
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) eth = true;
                }
            }
            catch { }
            return eth ? "eth" : "none";
        }

        void UpdateStatus()
        {
            string kind = NetKind();
            if (kind != "wifi") ssidCache = null;
            else if (netKind != "wifi") ssidCache = null;     // baru tersambung: nama jaringan dibaca ulang
            netKind = kind;
            netGlyph.Text = kind == "eth" ? "" : "";
            netGlyph.Opacity = kind == "none" ? 0.35 : 1;
            UpdateNetTip();

            WF.PowerStatus ps = WF.SystemInformation.PowerStatus;
            bool hasBat = (ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            Visibility bv = hasBat ? Visibility.Visible : Visibility.Collapsed;
            batTile.Visibility = bv;
            notchRing.Visibility = bv;
            if (notchSep != null) notchSep.Visibility = bv;
            if (hasBat)
            {
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;
                batRing.Set(pct, charging);
                notchRing.Set(pct, charging);
                batTip.Text = "Battery " + pct + "%" + (charging ? (pct >= 100 ? " · fully charged" : " · charging") : "");
            }
            UpdateVolume();
        }

        void UpdateNetTip()
        {
            netTip.Text = netKind == "wifi" ? "Wi-Fi" + (string.IsNullOrEmpty(ssidCache) ? "" : " · " + ssidCache)
                        : netKind == "eth" ? "Ethernet" : "Not connected";
        }

        // nama Wi-Fi dibaca lewat netsh (lambat): di latar belakang, saat tooltip mulai tampil
        void RefreshSsid()
        {
            if (ssidBusy || netKind != "wifi") return;
            ssidBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string s = SysInfo.Ssid();
                Dispatcher.BeginInvoke(new Action(delegate { ssidBusy = false; ssidCache = s; UpdateNetTip(); }));
            });
        }

        // ikon volume mengikuti level & mute
        void UpdateVolume()
        {
            double v = SysInfo.Volume();
            if (v < 0) { volGlyph.Text = ""; volTip.Text = "Volume"; return; }
            bool m = SysInfo.Muted();
            int p = (int)Math.Round(v * 100);
            volGlyph.Text = m ? "" : p == 0 ? "" : p < 34 ? "" : p < 67 ? "" : "";
            volTip.Text = m ? "Muted" : "Volume " + p + "%";
        }

        // ----- app pin + berjalan -----
        FrameworkElement MakeItem(DockItem it)
        {
            Border t = NewTile();
            t.Width = 48;
            t.MouseEnter += delegate { t.Background = Brushes.Transparent; };   // tanpa kotak abu-abu, cukup efek membesar
            // hover sebentar di app yang berjalan = tampilkan preview semua jendelanya
            t.MouseEnter += delegate
            {
                if (it.Wins.Count == 0) return;
                pendingItem = it;
                pendingTile = t;
                if (preview.Showing) ShowPreview();
                else { previewTimer.Stop(); previewTimer.Start(); }
            };
            t.MouseLeave += delegate { previewTimer.Stop(); };
            t.PreviewMouseDown += delegate { previewTimer.Stop(); preview.HidePreview(); };
            t.DataContext = it;
            Grid g = new Grid();
            Image img = new Image();
            img.Width = 30;
            img.Height = 30;
            img.Margin = Layout.Side == 1 ? new Thickness(4, 0, 0, 0) : Layout.Side == 2 ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 0, 4);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            ImageSource src = null;

            if (it.Lnk != null) src = Icons.For(it.Lnk);
            if (src == null && it.Aumid != null) src = Icons.FromShell("shell:AppsFolder\\" + it.Aumid);
            if (src == null && it.Exe != null) src = Icons.FromPath(it.Exe);
            if (src == null && it.IconWin != IntPtr.Zero) src = Icons.FromWindow(it.IconWin);
            img.Source = src;
            ScaleTransform zoom = new ScaleTransform(1, 1);
            img.RenderTransformOrigin = Layout.Origin;
            img.RenderTransform = zoom;
            t.Tag = zoom;
            g.Children.Add(img);

            if (it.Wins.Count > 0)
            {
                Border dot = new Border();
                dot.Height = 3.5;
                dot.Width = it.Active ? 16 : 5;
                dot.CornerRadius = new CornerRadius(2);
                dot.Background = it.Active ? (Brush)new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)) : UI.Ink(0xB0);
                dot.VerticalAlignment = VerticalAlignment.Bottom;
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                dot.Margin = new Thickness(0, 0, 0, 1);
                if (Layout.V)
                {
                    // titik penanda di sisi yang menempel ke tepi layar
                    dot.Width = 3.5;
                    dot.Height = it.Active ? 16 : 5;
                    dot.VerticalAlignment = VerticalAlignment.Center;
                    dot.HorizontalAlignment = Layout.Side == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                    dot.Margin = Layout.Side == 1 ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
                }
                g.Children.Add(dot);
            }
            t.Child = g;
            UI.Tip(t, it.Name);
            t.RenderTransform = new TranslateTransform();      // dipakai saat ikon pin diurutkan ulang
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Activate(it); };
            if (it.Pinned)
            {
                t.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { ReorderPress(t, e); };
                t.PreviewMouseMove += delegate(object s, MouseEventArgs e) { ReorderMove(t, e); };
                t.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { if (ReorderRelease(t)) e.Handled = true; };
                t.LostMouseCapture += delegate { if (reOn && reTile == t) ReorderRelease(t); };
                t.ContextMenuOpening += delegate(object s, ContextMenuEventArgs e) { if (reOn || reSettling) e.Handled = true; };
            }

            ContextMenu cm = new ContextMenu();
            if (it.Lnk != null)
            {
                MenuItem op = new MenuItem();
                op.Header = it.Wins.Count > 0 ? "Open new window" : "Open";
                op.Click += delegate { Launcher.Open(it.Lnk); };
                cm.Items.Add(op);
            }
            if (it.Wins.Count > 0)
            {
                MenuItem cl = new MenuItem();
                cl.Header = it.Wins.Count > 1 ? "Close all windows" : "Close window";
                cl.Click += delegate { foreach (IntPtr w in it.Wins) Native.PostMessage(w, 0x10, IntPtr.Zero, IntPtr.Zero); };
                cm.Items.Add(cl);
            }
            if (it.Pinned)
            {
                MenuItem un = new MenuItem();
                un.Header = "Unpin from dock";
                un.Click += delegate { PinStore.Remove(it.Lnk); signature = ""; UpdateItems(); };
                cm.Items.Add(un);
            }
            else if (it.Aumid != null || it.Exe != null)
            {
                MenuItem pn = new MenuItem();
                pn.Header = "Pin to dock";
                string pinTarget = it.Aumid != null ? "shell:AppsFolder\\" + it.Aumid : it.Exe;
                pn.Click += delegate { PinStore.Add(pinTarget, it.Name); signature = ""; UpdateItems(); };
                cm.Items.Add(pn);
            }
            t.ContextMenu = cm;
            return t;
        }

        // ----- urutkan ikon pin: tekan lalu geser kiri-kanan (atas-bawah di dock samping) -----
        // Ikon yang diseret mengikuti kursor, ikon pin lain bergeser memberi tempat. Semua gerakan dihitung
        // tiap frame layar (CompositionTarget.Rendering, 60 fps) dari waktu yang berlalu, jadi tetap halus.
        // Saat dilepas ikon meluncur ke tempatnya, lalu urutan disimpan ke pins.txt.
        Border reTile;                       // ikon pin yang ditekan / diseret
        FrameworkElement[] rePins;           // semua ikon pin saat mulai seret (urutan lama)
        double[] reSlots;                    // posisi awal tiap ikon pin di sepanjang dock
        bool rePressed, reOn, reSettling, reLoop;
        Point reDown;
        int reFrom, reTo;
        double reGrab, reCursor;
        readonly Stopwatch reWatch = new Stopwatch();
        const double ReLift = 4;             // ikon yang diseret sedikit terangkat dari tepi layar

        static double Along(Point p) { return Layout.V ? p.Y : p.X; }

        void ReorderPress(Border t, MouseButtonEventArgs e)
        {
            if (reSettling) FinishReorder();
            rePressed = e.ClickCount == 1 && !compact;
            reTile = t;
            reDown = e.GetPosition(itemsPanel);
        }

        void ReorderMove(Border t, MouseEventArgs e)
        {
            if (!rePressed || reTile != t) return;
            if (e.LeftButton != MouseButtonState.Pressed) { if (reOn) ReorderRelease(t); rePressed = false; return; }
            double a = Along(e.GetPosition(itemsPanel));
            if (!reOn)
            {
                if (Math.Abs(a - Along(reDown)) < 6) return;      // ambang kecil: klik biasa tetap membuka app
                if (!StartReorder(t)) { rePressed = false; return; }
            }
            reCursor = a;
            UpdateReorderTarget();
            StartReorderLoop();
        }

        bool StartReorder(Border t)
        {
            List<FrameworkElement> pins = new List<FrameworkElement>();
            foreach (UIElement c in itemsPanel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                DockItem it = fe == null ? null : fe.DataContext as DockItem;
                if (it != null && it.Pinned && fe.RenderTransform is TranslateTransform) pins.Add(fe);
            }
            int idx = pins.IndexOf(t);
            if (idx < 0 || pins.Count < 2) return false;
            rePins = pins.ToArray();
            reSlots = new double[rePins.Length];
            for (int i = 0; i < rePins.Length; i++)
            {
                Vector v = VisualTreeHelper.GetOffset(rePins[i]);           // posisi tata letak, tanpa geseran
                reSlots[i] = Layout.V ? v.Y : v.X;
            }
            reFrom = reTo = idx;
            reGrab = Along(reDown);
            reOn = true;
            t.CaptureMouse();
            previewTimer.Stop();
            preview.HidePreview();
            CloseMenus();
            foreach (FrameworkElement fe in rePins)
            {
                ToolTip tt = fe.ToolTip as ToolTip;
                if (tt != null) tt.IsOpen = false;
                ToolTipService.SetIsEnabled(fe, false);
            }
            Panel.SetZIndex(t, 1000);
            return true;
        }

        // posisi ikon yang diseret (dibatasi di antara ikon pin pertama & terakhir) dan slot tujuannya
        double ReorderOffset()
        {
            int n = reSlots.Length;
            double lo = reSlots[0] - reSlots[reFrom], hi = reSlots[n - 1] - reSlots[reFrom];
            return Math.Max(lo, Math.Min(hi, reCursor - reGrab));
        }

        void UpdateReorderTarget()
        {
            double c = reSlots[reFrom] + ReorderOffset();
            int best = reFrom;
            for (int i = 0; i < reSlots.Length; i++)
                if (Math.Abs(reSlots[i] - c) < Math.Abs(reSlots[best] - c)) best = i;
            reTo = best;
        }

        // geseran tujuan ikon pin ke-i di sepanjang dock
        double ReorderShift(int i)
        {
            if (i == reFrom) return reOn ? ReorderOffset() : reSlots[reTo] - reSlots[reFrom];
            if (reFrom < reTo && i > reFrom && i <= reTo) return reSlots[i - 1] - reSlots[i];
            if (reTo < reFrom && i >= reTo && i < reFrom) return reSlots[i + 1] - reSlots[i];
            return 0;
        }

        void StartReorderLoop()
        {
            if (reLoop) return;
            reLoop = true;
            reWatch.Restart();
            CompositionTarget.Rendering += ReorderStep;
        }

        void ReorderStep(object sender, EventArgs e)
        {
            if (rePins == null) { CompositionTarget.Rendering -= ReorderStep; reLoop = false; return; }
            double dt = Math.Min(50, reWatch.Elapsed.TotalMilliseconds);
            reWatch.Restart();
            double k = 1 - Math.Pow(1 - 0.3, dt / 16.7);          // sama cepat di 60 Hz maupun 144 Hz
            double lift = Layout.Side == 1 ? ReLift : -ReLift;    // menjauh dari tepi layar
            bool moving = reOn;
            for (int i = 0; i < rePins.Length; i++)
            {
                TranslateTransform tt = rePins[i].RenderTransform as TranslateTransform;
                if (tt == null) continue;
                double target = ReorderShift(i);
                double cur = Layout.V ? tt.Y : tt.X;
                double nv;
                if (i == reFrom && reOn) nv = target;              // ikon yang diseret menempel ke kursor
                else
                {
                    nv = cur + (target - cur) * k;
                    if (Math.Abs(target - nv) < 0.25) nv = target; else moving = true;
                }
                double up = 0;
                if (i == reFrom)
                {
                    double upTarget = reOn ? lift : 0, upCur = Layout.V ? tt.X : tt.Y;
                    up = upCur + (upTarget - upCur) * k;
                    if (Math.Abs(upTarget - up) < 0.1) up = upTarget; else moving = true;
                }
                if (Layout.V) { tt.Y = nv; tt.X = up; } else { tt.X = nv; tt.Y = up; }
            }
            if (moving) return;
            CompositionTarget.Rendering -= ReorderStep;
            reLoop = false;
            if (reSettling) FinishReorder();
        }

        bool ReorderRelease(Border t)
        {
            bool was = reOn;
            rePressed = false;
            if (!was) return false;
            reOn = false;                                         // dulu: ReleaseMouseCapture memicu LostMouseCapture
            reSettling = true;
            if (t.IsMouseCaptured) t.ReleaseMouseCapture();
            lastNear = DateTime.Now;
            StartReorderLoop();                                   // ikon meluncur ke slotnya, lalu FinishReorder
            return true;
        }

        // animasi selesai: ikon dipindah di panel (tanpa membangun ulang dock, jadi tidak berkedip) dan disimpan
        void FinishReorder()
        {
            if (!reSettling) return;
            reSettling = false;
            if (reLoop) { CompositionTarget.Rendering -= ReorderStep; reLoop = false; }
            FrameworkElement[] pins = rePins;
            Border t = reTile;
            rePins = null;
            reTile = null;
            if (pins == null) return;
            foreach (FrameworkElement fe in pins)
            {
                TranslateTransform tt = fe.RenderTransform as TranslateTransform;
                if (tt != null) { tt.X = 0; tt.Y = 0; }
                ToolTipService.SetIsEnabled(fe, true);
            }
            if (t != null) Panel.SetZIndex(t, 100);
            if (reTo == reFrom || t == null || !itemsPanel.Children.Contains(t)) return;
            int from = itemsPanel.Children.IndexOf(t), to = itemsPanel.Children.IndexOf(pins[reTo]);
            if (from < 0 || to < 0) return;
            itemsPanel.Children.RemoveAt(from);
            itemsPanel.Children.Insert(to, t);
            List<DockItem> items = new List<DockItem>();
            foreach (UIElement c in itemsPanel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                DockItem it = fe == null ? null : fe.DataContext as DockItem;
                if (it != null) items.Add(it);
            }
            try { PinStore.Reorder(items.Where(i => i.Pinned).Select(i => i.Lnk).ToList()); } catch { }
            signature = SigOf(items);                             // dock ini sudah sesuai; layar lain ikut di Tick
            Magnify(itemsPanel.IsMouseOver ? Along(Mouse.GetPosition(itemsPanel)) : double.NaN);
        }

        void Activate(DockItem it)
        {
            if (it.Wins.Count == 0)
            {
                if (it.Lnk != null) Launcher.Open(it.Lnk);
                return;
            }
            IntPtr fg = Native.GetForegroundWindow();
            int idx = it.Wins.IndexOf(fg);
            IntPtr target;
            if (idx >= 0)
            {
                if (it.Wins.Count == 1) { Native.ShowWindow(fg, 6); return; }
                target = it.Wins[(idx + 1) % it.Wins.Count];
            }
            else target = it.Wins[0];
            if (Native.IsIconic(target)) Native.ShowWindow(target, 9);
            Native.SetForegroundWindow(target);
        }

        static DockItem MatchPinByTitle(List<DockItem> list, string title)
        {
            foreach (DockItem p in list)
                if (p.Pinned && !string.IsNullOrEmpty(p.Name) && title.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;
            return null;
        }

        static DockItem MatchPinByDesc(List<DockItem> list, string exe)
        {
            string desc = Native.DescOf(exe);
            if (string.IsNullOrEmpty(desc)) return null;
            foreach (DockItem p in list)
                if (p.Pinned && !string.IsNullOrEmpty(p.Name) &&
                    (string.Equals(p.Name, desc, StringComparison.OrdinalIgnoreCase) || desc.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                    return p;
            return null;
        }

        static bool IsFrameHost(string exe)
        {
            return exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        }

        readonly List<string> runOrder = new List<string>();

        List<DockItem> BuildItems()
        {
            List<DockItem> pinned = new List<DockItem>();
            Dictionary<string, DockItem> byKey = new Dictionary<string, DockItem>(StringComparer.OrdinalIgnoreCase);

            foreach (PinItem p in PinStore.Load())
            {
                DockItem it = new DockItem();
                it.Lnk = p.Target;
                it.Name = p.Name;
                it.Pinned = true;
                if (p.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    string id = p.Target.Substring(17);
                    it.Aumid = id;
                    it.Exe = Native.ExpandAppPath(id);
                }
                else if (p.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    it.Exe = ShellUtil.ResolveLnk(p.Target)[0];
                    it.Aumid = Native.LnkAumid(p.Target);
                }
                else if (p.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) it.Exe = p.Target;
                pinned.Add(it);
                if (it.Aumid != null && !byKey.ContainsKey("a:" + it.Aumid)) byKey["a:" + it.Aumid] = it;
                if (it.Exe != null && !byKey.ContainsKey("e:" + it.Exe)) byKey["e:" + it.Exe] = it;
            }

            List<DockItem> running = new List<DockItem>();
            foreach (WinInfo w in Native.Windows(ownPid))
            {
                bool frame = IsFrameHost(w.Exe);
                DockItem it = null;
                if (w.Aumid != null) byKey.TryGetValue("a:" + w.Aumid, out it);
                if (it == null && w.Exe != null && !frame) byKey.TryGetValue("e:" + w.Exe, out it);
                if (it == null && frame) it = MatchPinByTitle(pinned, w.Title);
                if (it == null && w.Exe != null && !frame) it = MatchPinByDesc(pinned, w.Exe);
                if (it == null)
                {
                    string key = w.Aumid != null ? "a:" + w.Aumid : (w.Exe != null && !frame ? "e:" + w.Exe : "t:" + w.Title);
                    it = new DockItem();
                    it.Key = key;
                    it.Aumid = w.Aumid;
                    it.Exe = frame ? null : w.Exe;
                    string nm = w.Aumid != null ? Icons.ShellName("shell:AppsFolder\\" + w.Aumid) : null;
                    if (string.IsNullOrEmpty(nm) && it.Exe != null) nm = Native.DescOf(it.Exe);
                    if (string.IsNullOrEmpty(nm)) nm = w.Title;
                    it.Name = nm;
                    byKey[key] = it;
                    running.Add(it);
                }
                it.Wins.Add(w.H);
                if (it.IconWin == IntPtr.Zero) it.IconWin = w.H;
            }

            // urutan tetap: app yang baru dibuka selalu di kanan, tidak ikut bergeser saat diklik
            foreach (DockItem it in running) if (!runOrder.Contains(it.Key)) runOrder.Add(it.Key);
            runOrder.RemoveAll(k => !running.Any(r => r.Key == k));
            running = running.OrderBy(r => runOrder.IndexOf(r.Key)).ToList();

            List<DockItem> list = pinned.Concat(running).ToList();
            IntPtr fg = Native.GetForegroundWindow();
            foreach (DockItem it in list) it.Active = it.Wins.Contains(fg);
            return list;
        }

        static string SigOf(List<DockItem> list)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DockItem it in list)
                sb.Append(it.Lnk).Append('|').Append(it.Key).Append('|').Append(it.Wins.Count).Append('|').Append(it.Active ? 1 : 0).Append(';');
            return sb.ToString();
        }

        public void UpdateItems()
        {
            List<DockItem> list = BuildItems();
            DockItem act = list.FirstOrDefault(i => i.Active);
            appActive = ScreenHasApp(list);   // layar ini tertutup app? -> notch
            UpdateNotch(list);
            if (act != null)
            {
                string k = act.Key + "|" + act.Lnk;
                if (k != activeKey) { activeKey = k; notchIcon.Source = IconOf(act); }
            }
            if (reOn || reSettling) return;              // ikon pin sedang diurutkan: dock jangan dibangun ulang
            string sig = SigOf(list);
            if (sig == signature) return;
            signature = sig;

            CloseMenus();
            itemsPanel.Children.Clear();
            foreach (DockItem it in list) itemsPanel.Children.Add(MakeItem(it));
            if (preview.Showing)
            {
                DockItem same = list.FirstOrDefault(i => ItemKey(i) == preview.Key);
                if (same == null || same.Wins.Count == 0) preview.HidePreview();
            }
        }

        string notchSig = "";

        bool OnMyScreen(IntPtr w)
        {
            if (hwnd == IntPtr.Zero) return IsPrimary;
            try { return WF.Screen.FromHandle(w).Equals(WF.Screen.FromHandle(hwnd)); } catch { return false; }
        }

        // ada jendela app (tidak diminimize) di layar ini = desktop layar ini tertutup
        bool ScreenHasApp(List<DockItem> list)
        {
            foreach (DockItem it in list)
                foreach (IntPtr w in it.Wins)
                    if (!Native.IsIconic(w) && OnMyScreen(w)) return true;
            return false;
        }

        // notch hanya menampilkan app yang jendelanya ada di layar ini
        void UpdateNotch(List<DockItem> list)
        {
            List<DockItem> mine = list.Where(i => i.Wins.Any(OnMyScreen)).ToList();
            StringBuilder sb = new StringBuilder();
            foreach (DockItem it in mine) sb.Append(ItemKey(it)).Append(it.Active ? "*" : "").Append(';');
            string sig = sb.ToString();
            if (sig == notchSig) return;
            notchSig = sig;
            notchApps.Children.Clear();
            foreach (DockItem it in mine)
            {
                Grid ng = new Grid();
                ng.Margin = Layout.V ? new Thickness(0, 3, 0, 3) : new Thickness(3, 0, 3, 0);
                UI.Tip(ng, it.Name);
                Image im = new Image();
                im.Source = IconOf(it);
                im.Width = 20;
                im.Height = 20;
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                ng.Children.Add(im);
                // app di notch bisa langsung diklik tanpa membuka dock
                ng.Background = Brushes.Transparent;
                ng.Cursor = Cursors.Hand;
                ScaleTransform zs = new ScaleTransform(1, 1);
                im.RenderTransformOrigin = new Point(0.5, 0.5);
                im.RenderTransform = zs;
                ng.MouseEnter += delegate { zs.ScaleX = 1.15; zs.ScaleY = 1.15; };
                ng.MouseLeave += delegate { zs.ScaleX = 1; zs.ScaleY = 1; };
                DockItem target = it;
                ng.DataContext = it;
                // hover ikon app di notch = pratinjau jendelanya, seperti di dock penuh (dock tidak terbuka)
                ng.MouseEnter += delegate
                {
                    if (target.Wins.Count == 0) return;
                    pendingItem = target;
                    pendingTile = ng;
                    if (preview.Showing) ShowPreview();
                    else { previewTimer.Stop(); previewTimer.Start(); }
                };
                ng.MouseLeave += delegate { previewTimer.Stop(); };
                ng.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    previewTimer.Stop();
                    preview.HidePreview();
                    Activate(target);
                    EndPeek(true);
                };
                if (it.Active)
                {
                    Border u = new Border();
                    u.Height = 2.5;
                    u.Width = 10;
                    u.CornerRadius = new CornerRadius(1);
                    u.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
                    u.VerticalAlignment = VerticalAlignment.Bottom;
                    u.Margin = new Thickness(0, 0, 0, -5);
                    if (Layout.V)
                    {
                        u.Width = 2.5;
                        u.Height = 10;
                        u.VerticalAlignment = VerticalAlignment.Center;
                        u.HorizontalAlignment = Layout.Side == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                        u.Margin = Layout.Side == 1 ? new Thickness(-5, 0, 0, 0) : new Thickness(0, 0, -5, 0);
                    }
                    ng.Children.Add(u);
                }
                notchApps.Children.Add(ng);
            }
        }

        public void ForceRefresh()
        {
            signature = "";
            UpdateItems();
        }

        // ----- timer -----
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

        // fullscreen sungguhan (video, game, F11) - bukan jendela yang sekadar di-maximize
        bool IsFullscreenForeground()
        {
            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0 && (state == 3 || state == 4)) return true;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd) return false;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            if (pid == ownPid) return false;
            StringBuilder cs = new StringBuilder(256);
            Native.GetClassName(fg, cs, 256);
            string cls = cs.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;
            if (IsZoomed(fg)) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            WF.Screen fgScreen = WF.Screen.FromHandle(fg);
            if (hwnd != IntPtr.Zero && !fgScreen.Equals(WF.Screen.FromHandle(hwnd))) return false;   // fullscreen di layar lain
            System.Drawing.Rectangle b = fgScreen.Bounds;
            return r.left <= b.Left && r.top <= b.Top && r.right >= b.Right && r.bottom >= b.Bottom;
        }

        void Tick()
        {
            tick++;
            DateTime now = DateTime.Now;
            clockTime.Text = now.ToString("HH:mm");
            clockDate.Text = now.ToString(Layout.V ? "dd/MM" : "dd/MM/yyyy");
            TaskbarCtl.Enforce();

            bool fs = IsFullscreenForeground();
            if (fs && !hiddenForFullscreen)
            {
                hiddenForFullscreen = true;
                if (OnFullscreen != null) OnFullscreen();
                Hide();
            }
            else if (!fs && hiddenForFullscreen)
            {
                hiddenForFullscreen = false;
                Show();
            }
            if (hiddenForFullscreen) return;
            UpdateItems();
            if (tick % 5 == 0) { UpdateBin(); UpdateStatus(); }
            else if (!compact) UpdateVolume();
        }

        // ----- posisi + AppBar -----
        public void Reposition()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(w) || double.IsNaN(h)) return;
            // koreksi bila jendela masih berada di monitor dengan skala lain (mis. baru dibuat di layar utama)
            double k = ScreenScale > 0 ? ScreenScale / Scale() : 1;
            Rect S = new Rect(ScreenDip.Left * k, ScreenDip.Top * k, ScreenDip.Width * k, ScreenDip.Height * k);
            // ukuran jendela selalu mengikuti layar
            if (Layout.V && Math.Abs(Height - S.Height) > 0.5) { Height = S.Height; return; }
            if (!Layout.V && Math.Abs(Width - S.Width) > 0.5) { Width = S.Width; return; }
            if (Layout.V)
            {
                Left = Layout.Side == 1 ? S.Left : S.Right - w;
                Top = S.Top;
            }
            else
            {
                Left = S.Left + (S.Width - w) / 2;
                Top = S.Bottom - h;
            }
            UpdateAppBar();
            UpdateRegion();
        }

        double Scale()
        {
            PresentationSource ps = PresentationSource.FromVisual(this);
            return ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1.0;
        }

        void SetupAppBar()
        {
            if (hwnd == IntPtr.Zero || appBarOn) return;
            TaskbarCtl.Apply();
            if (!ReserveSpace) return;
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = hwnd;
            d.uCallbackMessage = Native.RegisterWindowMessage("SlideDock.AppBar");
            Native.SHAppBarMessage(0, ref d);
            appBarOn = true;
            Reposition();
        }

        void UpdateAppBar()
        {
            if (!appBarOn) return;
            double scale = Scale();
            int screenW = (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale);
            int screenH = (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale);
            int top = (int)Math.Round((Top + CardMargin - 6) * scale);
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = hwnd;
            d.uEdge = 3;
            d.rc.left = 0; d.rc.right = screenW; d.rc.bottom = screenH; d.rc.top = top;
            Native.SHAppBarMessage(2, ref d);
            d.rc.top = d.rc.bottom - (screenH - top);
            Native.SHAppBarMessage(3, ref d);
        }

        void RemoveAppBar()
        {
            if (!appBarOn) return;
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = hwnd;
            Native.SHAppBarMessage(1, ref d);
            appBarOn = false;
        }

        void OnSourceInit(object s, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource src = HwndSource.FromHwnd(hwnd);
            src.AddHook(WndProc);
            HotkeyOk = IsPrimary && Native.RegisterHotKey(hwnd, 1, Native.MOD_WIN | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE);
            ApplyBlur();
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1)
            {
                handled = true;
                if (OnToggle != null) OnToggle();
            }
            return IntPtr.Zero;
        }

        // dipanggil setelah dock lama ditutup (ganti posisi): ambil alih hotkey Win+Alt+Space
        public void RetryHotkey()
        {
            if (!IsPrimary || HotkeyOk || hwnd == IntPtr.Zero) return;
            HotkeyOk = Native.RegisterHotKey(hwnd, 1, Native.MOD_WIN | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE);
        }

        protected override void OnClosed(EventArgs e)
        {
            // hentikan semua timer agar dock yang sudah ditutup tidak hidup lagi di latar
            tickTimer.Stop();
            collapseTimer.Stop();
            previewTimer.Stop();
            try { preview.HidePreview(); preview.Close(); } catch { }
            Native.UnregisterHotKey(hwnd, 1);
            base.OnClosed(e);
        }
    }

    // ================= program =================
    static class Program
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "SlideDock";

        // Mulai bersama Windows lewat Task Scheduler (tugas "SlideDock" saat login), bukan daftar Run:
        // daftar Run dijalankan Windows satu per satu setelah app lain (bisa ~1 menit), tugas login langsung jalan.
        const string TaskName = "SlideDock";
        static bool? autoStartCache;

        static int RunHidden(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        static bool AutoStartOn()
        {
            if (AppInfo.Packaged) return PackagedStartup.On();
            if (autoStartCache == null) autoStartCache = RunHidden("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0 || RunKeyOn();
            return autoStartCache.Value;
        }

        static bool RunKeyOn()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            if (AppInfo.Packaged) { PackagedStartup.Set(on); autoStartCache = null; return; }
            // dua jalur: task saat sign-in (cepat) + daftar Startup Windows sebagai cadangan bila task gagal
            // dibuat/dijalankan di PC lain (izin akun, kebijakan perusahaan, antivirus). Bila keduanya jalan,
            // yang kedua melihat SlideDock sudah berjalan lalu keluar sendiri.
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k != null)
                    {
                        if (on) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --autostart");
                        else k.DeleteValue(RunName, false);
                    }
                }
            }
            catch { }
            if (!on)
            {
                RunHidden("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F");
                autoStartCache = null;
                return;
            }
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>Start SlideDock (dock that replaces the taskbar) at sign-in.</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><AllowStartOnDemand>true</AllowStartOnDemand>" +
                "<Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority>" +
                "<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command>" +
                "<WorkingDirectory>" + System.Security.SecurityElement.Escape(System.IO.Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
            string f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slidedock-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(f, xml, Encoding.Unicode);
                int rc = RunHidden("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + f + "\" /F");
                Log("autostart task " + (rc == 0 ? "created" : "failed (" + rc + ")"));
            }
            finally { try { File.Delete(f); } catch { } }
            autoStartCache = null;
        }

        // catatan singkat kapan SlideDock mulai/berhenti dan kenapa (untuk melacak masalah saat Windows baru menyala)
        static void Log(string s)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                string f = System.IO.Path.Combine(Store.Dir, "startup.log");
                if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Log("start (pid " + Process.GetCurrentProcess().Id + (args.Length > 0 ? ", args: " + string.Join(" ", args) : "") + ")");
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log("CRASH: " + e.ExceptionObject); };
            bool created;
            using (Mutex m = new Mutex(true, "SlideDock.SingleInstance", out created))
            {
                if (!created)
                {
                    // instance lain masih berjalan (atau baru saja menutup): tunggu sebentar sebelum menyerah
                    bool got = false;
                    try { got = m.WaitOne(Array.IndexOf(args, "--restart") >= 0 ? 15000 : 4000); } catch (AbandonedMutexException) { got = true; }
                    if (!got) { Log("another SlideDock is already running - exit"); return; }
                }
                bool restarting = false;
                Updater.Cleanup();

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                UI.ApplyMenuStyle(app);
                UI.SetTheme(UI.SavedTheme());
                Layout.Load();

                StartWindow start = new StartWindow();
                SearchWindow search = new SearchWindow();
                CalendarPop cal = new CalendarPop();
                TrayPop trayPop = new TrayPop();
                QuickPop quick = new QuickPop();
                cal.OnNotifications = delegate { WinKeyHook.SendWinCombo(0x4E); };
                double sysScale;
                using (DR.Graphics g = DR.Graphics.FromHwnd(IntPtr.Zero)) sysScale = g.DpiX / 96.0;
                Func<WF.Screen, Rect> dipOf = delegate(WF.Screen sc)
                {
                    double k = UI.ScaleOf(sc);
                    return new Rect(sc.Bounds.Left / k, sc.Bounds.Top / k, sc.Bounds.Width / k, sc.Bounds.Height / k);
                };
                List<DockWindow> docks = new List<DockWindow>();
                foreach (WF.Screen sc in WF.Screen.AllScreens.OrderByDescending(x => x.Primary))
                    docks.Add(new DockWindow(dipOf(sc), sc.Primary) { Device = sc.DeviceName, ScreenScale = UI.ScaleOf(sc) });
                DockWindow dock = docks[0];
                DockWindow cur = dock;
                Action<DockWindow> wire = null;                                   // diisi di bawah
                Action<DockWindow> use = delegate(DockWindow d) { cur = d; Popover.Scr = d.ScreenDip; Popover.SideGap = d.SurfaceExtent; Popover.DockSpan = d.SurfaceRect(); };
                Func<DockWindow> dockAtCursor = delegate
                {
                    WF.Screen sc = WF.Screen.FromPoint(WF.Control.MousePosition);
                    foreach (DockWindow d in docks) if (d.Device == sc.DeviceName) return d;
                    return dock;
                };
                Popover[] pops = new Popover[] { start, search, cal, trayPop, quick };

                // tutup popup lain; klik tombol yang sama saat popup terbuka = tutup (tidak dibuka lagi)
                Func<Popover, bool> switchTo = delegate(Popover target)
                {
                    foreach (DockWindow d in docks) d.HidePreview();
                    foreach (Popover p in pops) if (p != target && p.IsOpen) p.HidePanel();
                    if (target.IsOpen) { target.HidePanel(); return false; }
                    return !target.JustHidden;
                };
                Action toggle = delegate { if (switchTo(start)) start.ShowPanel(cur.SurfaceTop); };

                Action hideAll = delegate { foreach (Popover p in pops) p.HidePanel(); };
                wire = delegate(DockWindow d)
                {
                    d.OnToggle = delegate { use(d); toggle(); };
                    d.OnDoubleClick = hideAll;
                    d.OnFullscreen = hideAll;
                    d.OnPopup = delegate { };
                    d.OnSearch = delegate { use(d); if (switchTo(search)) search.ShowPanel(d.SurfaceTop); };
                    d.OnQuick = delegate(string mode)
                    {
                        use(d);
                        Point cx = d.StatusCenter(mode);
                        // popup sudah terbuka untuk ikon lain: ganti isinya di tempat; ikon yang sama = tutup
                        if (quick.IsOpen && quick.Mode != mode) { foreach (DockWindow x in docks) x.HidePreview(); quick.Switch(mode, cx); return; }
                        if (switchTo(quick)) quick.ShowAt(mode, cx, d.SurfaceTop);
                    };
                    d.OnClock = delegate { use(d); if (switchTo(cal)) cal.ShowAt(d.ClockCenter(), d.SurfaceTop); };
                    d.OnDockMoved = delegate(DockWindow moved) { foreach (DockWindow x in docks) if (x != moved) x.ApplySlide(true); };
                    d.OnTray = delegate { use(d); if (switchTo(trayPop)) trayPop.ShowAt(d.TrayLeft(), d.SurfaceTop); };
                    d.AnyPopupOpen = delegate { if (cur != d) return false; foreach (Popover p in pops) if (p.IsOpen) return true; return false; };
                };
                foreach (DockWindow d in docks) { wire(d); d.Show(); }
                WinKeyHook.OnWinTap = delegate { use(dockAtCursor()); toggle(); };
                // tombol Windows tetap membuka Start dock, kecuali saat game fullscreen (Direct3D) / mode presentasi
                WinKeyHook.Enabled = delegate { int st; return Native.SHQueryUserNotificationState(out st) != 0 || (st != 3 && st != 4); };
                WinKeyHook.Install();
                MenuCloser.Install();
                // autostart lama (hanya task) diberi jalur cadangan daftar Startup tanpa perlu diatur ulang
                ThreadPool.QueueUserWorkItem(delegate
                {
                    // ...dan diperbarui bila file .exe dipindah ke folder lain (task/daftar lama menunjuk path lama)
                    try
                    {
                        if (AppInfo.Packaged) return;
                        string want = "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --autostart";
                        string have = null;
                        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                            if (k != null) have = k.GetValue(RunName) as string;
                        bool taskOn = RunHidden("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0;
                        if ((taskOn || have != null) && !string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) SetAutoStart(true);
                    }
                    catch { }
                });

                // pertama kali dijalankan: langsung mulai bersama Windows (sekali saja, bila dimatikan tetap mati)
                if (Prefs.Get("autostart-init", 0) == 0)
                {
                    Prefs.Set("autostart-init", 1);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        if (!AutoStartOn()) { SetAutoStart(true); Log("first run: start with Windows turned on"); }
                    });
                }

                // Desktop berubah -> panel & pin ikut berubah
                List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
                Action desktopChanged = delegate
                {
                    PinStore.Prune();
                    foreach (DockWindow d in docks) d.ForceRefresh();
                    start.RefreshIfOpen();
                };
                foreach (string d in Store.DesktopDirs())
                {
                    if (!Directory.Exists(d)) continue;
                    FileSystemWatcher w = new FileSystemWatcher(d);
                    FileSystemEventHandler h = delegate { app.Dispatcher.BeginInvoke(desktopChanged); };
                    w.Created += h;
                    w.Deleted += h;
                    w.Renamed += delegate { app.Dispatcher.BeginInvoke(desktopChanged); };
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                PinStore.Prune();
                app.Dispatcher.BeginInvoke(new Action(delegate { search.Prewarm(); }), DispatcherPriority.ApplicationIdle);
                app.Dispatcher.BeginInvoke(new Action(delegate { start.Prewarm(); }), DispatcherPriority.ApplicationIdle);

                WF.NotifyIcon tray = new WF.NotifyIcon();
                try { tray.Icon = DR.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                catch { tray.Icon = DR.SystemIcons.Application; }
                tray.Text = "SlideDock";
                tray.Visible = true;
                Action<int> changeSide = null;                                   // diisi setelah menu dock dibuat
                Action restartApp = delegate
                {
                    restarting = true;
                    try { Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "--restart"); } catch { restarting = false; return; }
                    tray.Visible = false;
                    tray.Dispose();
                    foreach (DockWindow d in docks) d.Close();
                    app.Shutdown();
                };
                WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
                menu.BackColor = DR.Color.FromArgb(30, 30, 36);
                menu.ForeColor = DR.Color.White;
                menu.Font = new DR.Font("Segoe UI", 9.5f);
                menu.Items.Add("Open Start", null, delegate { use(dock); toggle(); });
                WF.ToolStripMenuItem themeGlass = new WF.ToolStripMenuItem("Theme: Glass 1");
                WF.ToolStripMenuItem themeGlass2 = new WF.ToolStripMenuItem("Theme: Glass 2");
                WF.ToolStripMenuItem themeDark = new WF.ToolStripMenuItem("Theme: Dark");
                Action syncTheme = delegate { themeGlass.Checked = dock.Theme == "glass"; themeGlass2.Checked = dock.Theme == "glass2"; themeDark.Checked = dock.Theme == "dark"; };
                Action<string> setTheme = delegate(string th)
                {
                    foreach (DockWindow d in docks) d.ApplyTheme(th);
                    syncTheme();
                    foreach (Popover pp in pops) pp.RefreshSurface();
                };
                themeGlass.Click += delegate { setTheme("glass"); };
                themeGlass2.Click += delegate { setTheme("glass2"); };
                themeDark.Click += delegate { setTheme("dark"); };
                menu.Items.Add(themeGlass);
                menu.Items.Add(themeGlass2);
                menu.Items.Add(themeDark);
                string[] posNames = new string[] { "Bottom", "Left", "Right" };
                for (int pi = 0; pi < 3; pi++)
                {
                    int idx = pi;
                    WF.ToolStripMenuItem pm = new WF.ToolStripMenuItem("Position: " + posNames[pi]);
                    pm.Checked = Layout.Side == idx;
                    pm.Click += delegate { if (Layout.Side == idx) return; changeSide(idx); };
                    menu.Items.Add(pm);
                }
                syncTheme();
                WF.ToolStripMenuItem auto = new WF.ToolStripMenuItem("Start with Windows");
                auto.Checked = AutoStartOn();
                auto.Click += delegate { SetAutoStart(!auto.Checked); auto.Checked = AutoStartOn(); };
                menu.Items.Add(auto);
                menu.Items.Add("Open settings folder", null, delegate { Process.Start("explorer.exe", Store.Dir); });
                Action exitApp = delegate
                {
                    tray.Visible = false;
                    tray.Dispose();
                    WinKeyHook.Uninstall();
                    TaskbarCtl.Restore();
                    foreach (DockWindow d in docks) d.Close();
                    app.Shutdown();
                };
                menu.Items.Add("Exit (restore Windows taskbar)", null, delegate { exitApp(); });

                // Uninstall: taskbar Windows kembali seperti semula (tampil, auto-hide sesuai aslinya),
                // tidak lagi mulai bersama Windows, lalu file app & pengaturannya dihapus
                Action uninstallApp = delegate
                {
                    hideAll();
                    string msg = AppInfo.Packaged
                        ? "Uninstall SlideDock?\n\nThe Windows taskbar comes back with its original settings, SlideDock stops starting with Windows and closes.\n\nInstalled apps in Windows Settings then opens so you can finish removing SlideDock."
                        : "Uninstall SlideDock?\n\nThe Windows taskbar comes back with its original settings, SlideDock stops starting with Windows and closes, then SlideDock.exe and its settings (pinned apps, theme, calendar link) are deleted.";
                    if (MessageBox.Show(msg, "Uninstall SlideDock", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    Log("uninstall: restore taskbar defaults, remove autostart");
                    TaskbarCtl.RestoreDefaults();
                    SetAutoStart(false);
                    if (AppInfo.Packaged) Launcher.Open("ms-settings:appsfeatures");
                    else
                    {
                        // file .exe yang sedang berjalan tidak bisa dihapus: tunggu proses ini selesai, baru hapus
                        Func<string, string> q = delegate(string p) { return "'" + p.Replace("'", "''") + "'"; };
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        string ps = "Wait-Process -Id " + Process.GetCurrentProcess().Id + " -Timeout 30 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; " +
                                    "Remove-Item -LiteralPath " + q(exe) + "," + q(exe + ".old") + "," + q(exe + ".new") + " -Force -ErrorAction SilentlyContinue; " +
                                    "Remove-Item -LiteralPath " + q(Store.Dir) + " -Recurse -Force -ErrorAction SilentlyContinue";
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps + "\"");
                            psi.CreateNoWindow = true;
                            psi.UseShellExecute = false;
                            Process.Start(psi);
                        }
                        catch { }
                    }
                    exitApp();
                };
                menu.Items.Add("Uninstall SlideDock...", null, delegate { uninstallApp(); });
                tray.ContextMenuStrip = menu;

                // menu SlideDock di dock (klik kanan tombol Start / area kosong dock)
                Func<List<KeyValuePair<string, Action>>> dockMenu = delegate
                {
                    Func<bool, string> ck = delegate(bool on) { return on ? "\u2713   " : "      "; };
                    List<KeyValuePair<string, Action>> l = new List<KeyValuePair<string, Action>>();
                    l.Add(new KeyValuePair<string, Action>("Dock settings", delegate { use(dockAtCursor()); foreach (Popover pp in pops) if (pp != start) pp.HidePanel(); start.ShowSettings(cur.SurfaceTop); }));
                    l.Add(new KeyValuePair<string, Action>("Task Manager", delegate { Launcher.Open("taskmgr.exe"); }));
                    l.Add(new KeyValuePair<string, Action>("Windows Settings", delegate { Launcher.Open("ms-settings:"); }));
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    l.Add(new KeyValuePair<string, Action>(ck(dock.Theme == "glass") + "Theme: Glass 1", delegate { setTheme("glass"); }));
                    l.Add(new KeyValuePair<string, Action>(ck(dock.Theme == "glass2") + "Theme: Glass 2", delegate { setTheme("glass2"); }));
                    l.Add(new KeyValuePair<string, Action>(ck(dock.Theme == "dark") + "Theme: Dark", delegate { setTheme("dark"); }));
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    string[] pn = new string[] { "Bottom", "Left", "Right" };
                    for (int i = 0; i < 3; i++)
                    {
                        int idx = i;
                        l.Add(new KeyValuePair<string, Action>(ck(Layout.Side == idx) + "Position: " + pn[i], delegate { if (Layout.Side == idx) return; changeSide(idx); }));
                    }
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    l.Add(new KeyValuePair<string, Action>(ck(AutoStartOn()) + "Start with Windows", delegate { SetAutoStart(!AutoStartOn()); auto.Checked = AutoStartOn(); }));
                    l.Add(new KeyValuePair<string, Action>("Open settings folder", delegate { Process.Start("explorer.exe", Store.Dir); }));
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    l.Add(new KeyValuePair<string, Action>("Exit SlideDock (restore taskbar)", delegate { exitApp(); }));
                    return l;
                };
                foreach (DockWindow d in docks) d.MenuProvider = dockMenu;

                // ----- update: cek otomatis (20 detik setelah mulai, lalu tiap 6 jam), notifikasi Windows sekali per versi -----
                Action installUpdate = null;
                installUpdate = delegate
                {
                    if (Updater.Available == null || Updater.Busy) return;
                    string ver = Updater.Available.Ver.ToString(3);
                    Updater.Install(app.Dispatcher,
                        delegate { Log("updated to " + ver + ", restarting"); restartApp(); },
                        delegate(string err)
                        {
                            Log("update failed: " + err);
                            tray.ShowBalloonTip(8000, "SlideDock update failed", err + " The download page will open instead.", WF.ToolTipIcon.Warning);
                            if (Updater.Available != null) Launcher.Open(Updater.Available.Page);
                        });
                };
                start.OnUpdate = delegate { installUpdate(); };
                tray.BalloonTipClicked += delegate { if (Updater.Available != null && !Updater.Busy) installUpdate(); };
                Updater.Changed = delegate
                {
                    UpdateInfo u = Updater.Available;
                    foreach (DockWindow d in docks) d.SetUpdateDot(u != null);
                    start.RefreshUpdate();
                    if (u == null || Updater.Busy) return;
                    int code = u.Ver.Major * 1000000 + u.Ver.Minor * 1000 + u.Ver.Build;
                    if (Prefs.Get("notified", 0) == code) return;
                    Prefs.Set("notified", code);
                    string tip = u.Notes.Count > 0
                        ? string.Join("\n", u.Notes.Take(3).Select(x => "\u2022 " + (x.Length > 70 ? x.Substring(0, 67) + "..." : x)).ToArray()) + "\nClick to update."
                        : "Click here to update now (you have " + AppInfo.Version + "). You can also update from Start.";
                    if (tip.Length > 250) tip = tip.Substring(0, 247) + "...";
                    tray.ShowBalloonTip(10000, "SlideDock " + u.Ver.ToString(3) + " is available", tip, WF.ToolTipIcon.Info);
                };
                if (Updater.Enabled)
                {
                    DispatcherTimer updTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                    updTimer.Tick += delegate
                    {
                        updTimer.Interval = TimeSpan.FromHours(6);
                        if (Updater.AutoCheck) Updater.Check(app.Dispatcher, null);
                    };
                    updTimer.Start();
                }

                // ----- kalender: hari libur & Google Calendar (unduh di latar, simpan cache untuk offline) -----
                string calStatus = null;
                CalData.Changed = delegate { cal.DataChanged(); start.RefreshSettings(); };
                cal.OnCalendarSettings = delegate { cal.HidePanel(); use(cur); start.ShowSettings(cur.SurfaceTop); };
                DispatcherTimer calTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                calTimer.Tick += delegate { calTimer.Interval = TimeSpan.FromMinutes(15); CalData.Refresh(app.Dispatcher, false); };
                calTimer.Start();

                // Dock settings di Start: tiap pilihan langsung berlaku
                string[] themeKeys = new string[] { "glass", "glass2", "dark" };
                start.SettingsProvider = delegate
                {
                    List<DockSetting> l = new List<DockSetting>();
                    l.Add(new DockSetting { Section = "Appearance", Label = "Theme", Choices = new string[] { "Glass 1", "Glass 2", "Dark" },
                        Get = delegate { return Math.Max(0, Array.IndexOf(themeKeys, dock.Theme)); },
                        Set = delegate(int i) { setTheme(themeKeys[i]); } });
                    l.Add(new DockSetting { Section = "Appearance", Label = "Position", Choices = new string[] { "Bottom", "Left", "Right" },
                        Get = delegate { return Layout.Side; },
                        Set = delegate(int i)
                        {
                            if (Layout.Side == i) return;
                            changeSide(i);
                            // dock dibuat ulang (semua popup tertutup): buka lagi Dock settings di dock yang baru
                            DispatcherTimer reopen = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                            reopen.Tick += delegate { reopen.Stop(); use(dock); start.ShowSettings(dock.SurfaceTop); };
                            reopen.Start();
                        } });
                    l.Add(new DockSetting { Section = "Appearance", Label = "Dock size", Choices = new string[] { "Auto", "Small", "Medium", "Large" },
                        Get = delegate { return Prefs.DockSize; },
                        Set = delegate(int i) { Prefs.DockSize = i; foreach (DockWindow d in docks) d.ApplySize(); } });
                    l.Add(new DockSetting { Section = "Appearance", Label = "Magnify on hover", Choices = new string[] { "Off", "Subtle", "Strong" },
                        Get = delegate { return Prefs.Magnify; },
                        Set = delegate(int i) { Prefs.Magnify = i; } });
                    l.Add(new DockSetting { Section = "Behavior", Label = "Shrink to notch when idle", Toggle = true,
                        Get = delegate { return Prefs.Notch ? 1 : 0; },
                        Set = delegate(int i) { Prefs.Notch = i != 0; } });
                    l.Add(new DockSetting { Section = "Behavior", Label = "Drag notch & dock along the edge", Toggle = true,
                        Get = delegate { return Prefs.DockMove ? 1 : 0; },
                        Set = delegate(int i) { Prefs.DockMove = i != 0; foreach (DockWindow d in docks) d.ApplySlide(true); } });
                    l.Add(new DockSetting { Section = "Behavior", Label = "Start with Windows", Toggle = true,
                        Get = delegate { return AutoStartOn() ? 1 : 0; },
                        Set = delegate(int i) { SetAutoStart(i != 0); auto.Checked = AutoStartOn(); } });
                    string country = CalData.CountryName;
                    l.Add(new DockSetting { Section = "Calendar", Label = "Public holidays in " + country + " (from your Windows region)", Toggle = true,
                        Get = delegate { return CalData.HolidaysOn ? 1 : 0; },
                        Set = delegate(int i) { CalData.HolidaysOn = i != 0; CalData.Refresh(app.Dispatcher, false); } });
                    if (CalData.HolidaysOn && CalData.LocalLanguage != null)
                        l.Add(new DockSetting { Section = "Calendar", Label = "Holiday names", Choices = new string[] { "Local language", "English" },
                            Get = delegate { return CalData.HolidayLocal ? 0 : 1; },
                            Set = delegate(int i) { CalData.HolidayLocal = i == 0; CalData.Refresh(app.Dispatcher, false); } });
                    l.Add(new DockSetting { Section = "Calendar", Label = "Sundays in red", Toggle = true,
                        Get = delegate { return CalData.RedSundays ? 1 : 0; },
                        Set = delegate(int i) { CalData.RedSundays = i != 0; } });
                    if (CalData.PersonalUrl == null)
                    {
                        l.Add(new DockSetting { Section = "Calendar", Label = "Show your Google Calendar events (read-only)", Input = true,
                            Placeholder = "Paste the \"Secret address in iCal format\"",
                            SetText = delegate(string url)
                            {
                                calStatus = "Checking the link...";
                                start.RefreshSettings();
                                CalData.Connect(url, app.Dispatcher, delegate(string err)
                                {
                                    calStatus = err == null ? null : "Couldn't use that link: " + err;
                                    start.RefreshSettings();
                                });
                            } });
                        if (calStatus != null) l.Add(new DockSetting { Section = "Calendar", Label = calStatus });
                        l.Add(new DockSetting { Section = "Calendar", Label = "Where do I find this link?",
                            Click = delegate { start.HidePanel(); Launcher.Open("https://support.google.com/calendar/answer/37648"); } });
                    }
                    else
                    {
                        string synced = CalData.PersonalSynced == DateTime.MinValue ? "" :
                            "  \u00b7  synced " + (DateTime.Now - CalData.PersonalSynced).TotalMinutes.ToString("0") + " min ago";
                        l.Add(new DockSetting { Section = "Calendar", Label = (CalData.PersonalError != null ? "Google Calendar: last sync failed (" + CalData.PersonalError + "). Try again" : "Google Calendar connected" + synced + ". Sync now"),
                            Click = delegate { CalData.Refresh(app.Dispatcher, true); } });
                        l.Add(new DockSetting { Section = "Calendar", Label = "Disconnect Google Calendar",
                            Click = delegate { calStatus = null; CalData.Disconnect(app.Dispatcher); } });
                    }
                    if (!Updater.Enabled)
                        l.Add(new DockSetting { Section = "Updates", Label = "Version " + AppInfo.Version + "  ·  updates come from Microsoft Store",
                            Click = delegate { Launcher.Open("ms-windows-store://downloadsandupdates"); } });
                    else
                    {
                        UpdateInfo av = Updater.Available;
                        if (av != null)
                        {
                            l.Add(new DockSetting { Section = "Updates", Label = Updater.Busy ? "Downloading " + av.Ver.ToString(3) + "..." : "Install update " + av.Ver.ToString(3) + " (you have " + AppInfo.Version + ")",
                                Click = delegate { installUpdate(); } });
                            if (av.Notes.Count > 0)
                                l.Add(new DockSetting { Section = "Updates", Label = "What's new in " + av.Ver.ToString(3) + ":\n" + string.Join("\n", av.Notes.Select(x => "\u2022  " + x).ToArray()) });
                        }
                        l.Add(new DockSetting { Section = "Updates",
                            Label = Updater.Checking ? "Checking for updates..." :
                                    av == null && Updater.LastCheck > DateTime.MinValue && Updater.LastError == null ? "Version " + AppInfo.Version + "  ·  up to date. Check again" :
                                    Updater.LastError != null ? "Version " + AppInfo.Version + "  ·  couldn't check. Try again" :
                                    "Version " + AppInfo.Version + "  ·  check for updates",
                            Click = delegate { Updater.Check(app.Dispatcher, null); start.RefreshUpdate(); } });
                        l.Add(new DockSetting { Section = "Updates", Label = "Check for updates automatically", Toggle = true,
                            Get = delegate { return Updater.AutoCheck ? 1 : 0; },
                            Set = delegate(int i) { Updater.AutoCheck = i != 0; } });
                        l.Add(new DockSetting { Section = "Updates", Label = "Release notes",
                            Click = delegate { start.HidePanel(); Launcher.Open(AppInfo.ReleasesPage); } });
                    }
                    l.Add(new DockSetting { Section = "More", Label = "Open settings folder",
                        Click = delegate { start.HidePanel(); Process.Start("explorer.exe", Store.Dir); } });
                    l.Add(new DockSetting { Section = "More", Label = "Exit SlideDock (restore taskbar)",
                        Click = delegate { exitApp(); } });
                    l.Add(new DockSetting { Section = "More", Label = "Uninstall SlideDock (restore all Windows taskbar settings)",
                        Click = delegate { start.HidePanel(); uninstallApp(); } });
                    return l;
                };

                // ganti posisi dock tanpa memulai ulang app: dock baru dibuat & tampil dulu, baru dock lama ditutup
                // bangun ulang dock untuk semua layar saat ini (dipakai saat ganti posisi & saat jumlah layar berubah)
                Action rebuildDocks = delegate
                {
                        hideAll();
                        foreach (DockWindow d in docks) d.HidePreview();
                        List<DockWindow> old = new List<DockWindow>(docks);
                        List<DockWindow> fresh = new List<DockWindow>();
                        foreach (WF.Screen sc in WF.Screen.AllScreens.OrderByDescending(x => x.Primary))
                        {
                            DockWindow nd = new DockWindow(dipOf(sc), sc.Primary) { Device = sc.DeviceName, ScreenScale = UI.ScaleOf(sc) };
                            nd.MenuProvider = dockMenu;
                            wire(nd);
                            fresh.Add(nd);
                        }
                        docks.Clear();
                        docks.AddRange(fresh);
                        dock = docks[0];
                        use(dock);
                        foreach (DockWindow d in fresh) d.Show();
                        foreach (DockWindow d in old) { try { d.Close(); } catch { } }
                        dock.RetryHotkey();
                };
                changeSide = delegate(int side)
                {
                    if (Layout.Side == side) return;
                    app.Dispatcher.BeginInvoke(new Action(delegate                 // tunggu menu tertutup dulu
                    {
                        Layout.Save(side);
                        rebuildDocks();
                    }), DispatcherPriority.Background);
                };
                tray.MouseClick += delegate(object s, WF.MouseEventArgs e)
                {
                    if (e.Button == WF.MouseButtons.Left) { use(dock); toggle(); }
                };

                bool sessionEnding = false;
                app.SessionEnding += delegate(object s, SessionEndingCancelEventArgs e)
                {
                    sessionEnding = true;
                    Log("Windows " + (e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "shutdown/restart" : "sign out") + ": keep taskbar auto-hide on");
                    TaskbarCtl.KeepHiddenForNextLogin();
                };
                // Windows menutup SlideDock untuk uninstall/update (Restart Manager, mis. hapus versi Store dari
                // Settings): pesan end-session dengan ENDSESSION_CLOSEAPP. Ini bukan shutdown, jadi taskbar
                // dikembalikan penuh; tanpa ini auto-hide tertinggal aktif setelah app dihapus.
                bool closeAppPending = false;
                HwndSourceParameters sessParams = new HwndSourceParameters("SlideDock session");
                sessParams.WindowStyle = 0;               // jendela tersembunyi (bukan message-only: harus ikut menerima pesan ini)
                sessParams.Width = 0;
                sessParams.Height = 0;
                HwndSource sessWnd = new HwndSource(sessParams);
                sessWnd.AddHook(delegate(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
                {
                    bool closeApp = (lp.ToInt64() & 1) != 0;
                    if (msg == 0x11 && closeApp && !TaskbarCtl.Releasing)            // WM_QUERYENDSESSION
                    {
                        closeAppPending = true;
                        Log("Windows is closing SlideDock (app uninstall/update): restore taskbar defaults");
                        TaskbarCtl.RestoreDefaults();
                    }
                    else if (msg == 0x16 && closeAppPending && wp == IntPtr.Zero)    // WM_ENDSESSION dibatalkan: tetap jalan
                    {
                        closeAppPending = false;
                        sessionEnding = false;
                        TaskbarCtl.Releasing = false;
                        TaskbarCtl.HideEnabled = true;
                        TaskbarCtl.Apply();
                    }
                    return IntPtr.Zero;
                });
                app.Exit += delegate
                {
                    WinKeyHook.Uninstall();
                    MenuCloser.Uninstall();
                    if (sessionEnding) { Log("exit (Windows ending session)"); return; }
                    if (!restarting) TaskbarCtl.Restore();
                    Log(restarting ? "exit (restart)" : "exit (taskbar restored)");
                };
                // monitor dipasang/dilepas atau resolusi berubah. Saat Windows baru menyala, sinyal ini datang
                // beberapa kali berturut-turut (monitor kedua belum siap): tunggu 1,5 detik sampai tenang,
                // lalu sesuaikan dock DI DALAM app yang sama (dulu app dimulai ulang -> bisa saling menutup).
                DispatcherTimer displaySettle = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher);
                displaySettle.Interval = TimeSpan.FromMilliseconds(1500);
                displaySettle.Tick += delegate
                {
                    displaySettle.Stop();
                    WF.Screen[] screens = WF.Screen.AllScreens.OrderByDescending(x => x.Primary).ToArray();
                    bool same = screens.Length == docks.Count;
                    for (int i = 0; same && i < screens.Length; i++) same = screens[i].DeviceName == docks[i].Device;
                    if (same)
                    {
                        for (int i = 0; i < screens.Length; i++)
                        {
                            Rect r = dipOf(screens[i]);
                            docks[i].ScreenDip = r;
                            docks[i].ScreenScale = UI.ScaleOf(screens[i]);
                            if (Layout.V) docks[i].Height = r.Height; else docks[i].Width = r.Width;
                            docks[i].Reposition();
                        }
                        return;
                    }
                    Log("screens changed: " + docks.Count + " -> " + screens.Length + ", rebuilding docks");
                    rebuildDocks();
                };
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate { displaySettle.Stop(); displaySettle.Start(); }));
                };
                AppDomain.CurrentDomain.UnhandledException += delegate { WinKeyHook.Uninstall(); TaskbarCtl.Restore(); };
                Log("running: " + docks.Count + " dock(s), position " + Layout.Side);
                // error di tampilan: catat ke log, app tetap jalan (taskbar tidak "hilang")
                // error yang sama (mis. dari animasi per frame) dicatat paling sering sekali per menit, file maks ~500 KB
                Dictionary<string, DateTime> lastErr = new Dictionary<string, DateTime>();
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    try
                    {
                        string key = e.Exception.GetType().Name + "|" + e.Exception.Message + "|" + (e.Exception.StackTrace ?? "").Split('\n')[0];
                        DateTime prev;
                        if (lastErr.TryGetValue(key, out prev) && (DateTime.Now - prev).TotalSeconds < 60) return;
                        lastErr[key] = DateTime.Now;
                        string f = System.IO.Path.Combine(Store.Dir, "error.log");
                        if (File.Exists(f) && new FileInfo(f).Length > 500000) File.Delete(f);
                        File.AppendAllText(f, DateTime.Now + "  v" + AppInfo.Version + "  " + e.Exception + Environment.NewLine + Environment.NewLine);
                    }
                    catch { }
                };
                if (!dock.HotkeyOk)
                    tray.ShowBalloonTip(5000, "SlideDock", "Win+Alt+Space is already used by another app.", WF.ToolTipIcon.Warning);
                app.Run();
                GC.KeepAlive(sessWnd);
            }
        }
    }
}

