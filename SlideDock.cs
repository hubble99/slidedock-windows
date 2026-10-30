// SlideDock v4 - dock rounded yang sepenuhnya menggantikan taskbar Windows.
// Compiler: C# 5 bawaan .NET Framework 4.8 (tanpa SDK tambahan).
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
            if (all == null || (DateTime.Now - loadedAt).TotalSeconds > 120) Load();
            return all;
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
        static DateTime peekUntil = DateTime.MinValue;

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
            if (prev < 0) prev = (int)Native.SHAppBarMessage(4, ref d);
            d.lParam = (IntPtr)(on ? (prev | 1) : prev);
            Native.SHAppBarMessage(0xA, ref d);
        }

        public static void Apply()
        {
            SetAutoHide(true);
            if (HideEnabled) foreach (IntPtr t in Trays()) Native.ShowWindow(t, 0);
        }

        public static void ShowAll()
        {
            foreach (IntPtr t in Trays()) Native.ShowWindow(t, 5);
        }

        // Tampilkan taskbar Windows sebentar (untuk ikon tray)
        public static void Peek(int seconds)
        {
            peekUntil = DateTime.Now.AddSeconds(seconds);
            ShowAll();
        }

        static int lastAutoHideCheck;

        public static void Enforce()
        {
            if (!HideEnabled || DateTime.Now < peekUntil) return;
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
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
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
            t.Foreground = Brushes.White;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }

        // gaya menu klik kanan untuk seluruh app: gelap, sudut membulat
        public static void ApplyMenuStyle(Application app)
        {
            string ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
            string menuXaml =
                "<Style " + ns + " TargetType='ContextMenu'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='HasDropShadow' Value='False'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>" +
                "<Border Background='#FA1E1E24' BorderBrush='#40FFFFFF' BorderThickness='1' CornerRadius='12' Padding='5' Margin='10'>" +
                "<Border.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='3' Opacity='0.5'/></Border.Effect>" +
                "<StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>" +
                "</Border></ControlTemplate></Setter.Value></Setter></Style>";
            string itemXaml =
                "<Style " + ns + " TargetType='MenuItem'>" +
                "<Setter Property='OverridesDefaultStyle' Value='True'/>" +
                "<Setter Property='Foreground' Value='White'/>" +
                "<Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI Variable, Segoe UI'/>" +
                "<Setter Property='FontSize' Value='13'/>" +
                "<Setter Property='Cursor' Value='Hand'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>" +
                "<Border x:Name='bd' Background='Transparent' CornerRadius='8' Padding='12,8,22,8' MinWidth='170'>" +
                "<ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'>" +
                "<Setter TargetName='bd' Property='Background' Value='#30FFFFFF'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></Setter.Value></Setter></Style>";
            try
            {
                app.Resources[typeof(ContextMenu)] = System.Windows.Markup.XamlReader.Parse(menuXaml);
                app.Resources[typeof(MenuItem)] = System.Windows.Markup.XamlReader.Parse(itemXaml);
            }
            catch { }
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
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
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
        Point SidePoint(Size s)
        {
            double y = Scr.Top + Math.Max(8, (Scr.Height - s.Height) / 2);
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
                BeginAnimation(Window.TopProperty, null);
                if (Layout.V)
                {
                    Point sp2 = SidePoint(new Size(ActualWidth, ActualHeight));
                    Left = sp2.X;
                    Top = sp2.Y;
                    UpdateLayout();
                    ApplySurface(false);
                    return;
                }
                Top = anchorBottom - ActualHeight;
                if (anchorRight > 0) Left = Math.Max(0, anchorRight - ActualWidth);
                if (anchorCenter > 0) Left = Math.Max(0, anchorCenter - ActualWidth / 2);
                UpdateLayout();
                ApplySurface(false);
            };
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
            if (UI.Theme != "glass")
            {
                foreach (Border c in cards)
                {
                    c.Background = new SolidColorBrush(Color.FromArgb(0xF8, 0x1B, 0x1B, 0x21));
                    c.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
                }
                return;
            }
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
                    dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x6E, 0x14, 0x14, 0x1C)), null, new RectangleGeometry(unit)));
                    LinearGradientBrush gl = new LinearGradientBrush();
                    gl.StartPoint = new Point(0, 0);
                    gl.EndPoint = new Point(0, 1);
                    gl.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 255, 255, 255), 0));
                    gl.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.5));
                    dg.Children.Add(new GeometryDrawing(gl, null, new RectangleGeometry(unit)));
                    DrawingBrush db = new DrawingBrush(dg);
                    db.Stretch = Stretch.Fill;
                    card.Background = db;
                    card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
                }
            }
            catch { }
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
            // hanya geser naik, tanpa fade: memudarkan jendela transparan memaksa seluruh panel digambar ulang
            // tiap frame (~20 fps saat GPU sibuk), sedangkan menggeser jendela murah (~60 fps)
            DoubleAnimation slide = new DoubleAnimation(target + SlideDistance, target, TimeSpan.FromMilliseconds(230));
            slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.Completed += delegate { BeginAnimation(Window.TopProperty, null); Top = target; };
            Top = target + SlideDistance;    // frame pertama sudah di titik awal geser (bukan di posisi akhir lalu melompat turun)
            Opacity = 1;
            Activate();
            // frame pertama setelah tampil butuh ~100 ms untuk digambar. Mulai geser SETELAH frame itu jadi,
            // supaya animasi tidak "terlewat" separuh lalu tersentak di akhir.
            int ticks = 0, gen = ++openGen;
            EventHandler onFrame = null;
            onFrame = delegate
            {
                if (++ticks < 2) return;
                CompositionTarget.Rendering -= onFrame;
                if (closing || !IsVisible || gen != openGen) return;
                BeginAnimation(Window.TopProperty, slide);
            };
            CompositionTarget.Rendering += onFrame;
        }

        public void HidePanel()
        {
            if (!IsOpen) return;
            HiddenAt = DateTime.Now;
            closing = true;
            BeginAnimation(Window.TopProperty, null);
            BeginAnimation(Window.OpacityProperty, null);
            // jendela transparan menyimpan gambar terakhirnya & menampilkannya lagi sesaat ketika dibuka ulang
            // (terlihat seperti panel "melompat" ke bawah). Jadi kosongkan dulu (opacity 0), tunggu frame itu
            // benar-benar tergambar, baru sembunyikan: saat dibuka lagi tidak ada sisa gambar lama.
            Opacity = 0;
            int ticks = 0;
            EventHandler onFrame = null;
            onFrame = delegate
            {
                if (++ticks < 3) return;
                CompositionTarget.Rendering -= onFrame;
                if (!closing) return;            // sudah dibuka lagi sebelum sempat disembunyikan
                Hide();
                closing = false;
            };
            CompositionTarget.Rendering += onFrame;
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

        public FlatSlider()
        {
            Height = 26;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            Brush accent = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
            track.Height = 4;
            track.CornerRadius = new CornerRadius(2);
            track.Background = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255));
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
            double w = ActualWidth - 16;
            if (w <= 0) return;
            Value = (e.GetPosition(this).X - 8) / w;
            if (Changed != null) Changed(val);
        }

        void Layout()
        {
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
            t.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
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
            t.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
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
            tb.Foreground = Brushes.White;
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
            t.Foreground = Brushes.White;
            t.FontSize = 13.5;
            t.FontFamily = UI.Text;
            txt.Children.Add(t);
            if (!string.IsNullOrEmpty(sub))
            {
                TextBlock s = new TextBlock();
                s.Text = sub;
                s.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
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
            sBox.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
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
            search.Foreground = Brushes.White;
            search.CaretBrush = Brushes.White;
            search.FontSize = 15;
            search.FontFamily = UI.Text;
            hint.Text = "Search apps, files, or the web";
            hint.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
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

        public void Prewarm() { Catalog.All(); }

        public void ShowPanel(double dockTop)
        {
            if (IsOpen) return;
            search.Text = "";
            Rebuild();
            SlideIn(delegate(Size s) { return new Point(Popover.Scr.Left + (Popover.Scr.Width - s.Width) / 2, dockTop - s.Height + 6); });
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
        readonly TextBlock diskInfo = new TextBlock();
        readonly DispatcherTimer clockTimer = new DispatcherTimer();
        bool addMode;
        readonly WrapPanel trayWrap = new WrapPanel();
        public Action OnTrayFull;

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
            head.Margin = new Thickness(6, 0, 0, 8);
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            leftTitle.Foreground = Brushes.White;
            leftTitle.FontSize = 16;
            leftTitle.FontWeight = FontWeights.SemiBold;
            leftTitle.FontFamily = UI.Text;
            leftTitle.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(leftTitle);
            Border addBtn = new Border();
            addBtn.CornerRadius = new CornerRadius(10);
            addBtn.Padding = new Thickness(12, 6, 12, 6);
            addBtn.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            addBtn.Cursor = Cursors.Hand;
            addText.Foreground = Brushes.White;
            addText.FontSize = 12.5;
            addText.FontFamily = UI.Text;
            addBtn.Child = addText;
            addBtn.MouseEnter += delegate { addBtn.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)); };
            addBtn.MouseLeave += delegate { addBtn.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)); };
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
            filterBox.Background = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255));
            filterBox.Padding = new Thickness(12, 7, 12, 7);
            filterBox.Margin = new Thickness(4, 0, 4, 8);
            filter.Background = Brushes.Transparent;
            filter.BorderThickness = new Thickness(0);
            filter.Foreground = Brushes.White;
            filter.CaretBrush = Brushes.White;
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
            root.Children.Add(left);

            // ---- kanan: info & daya ----
            Border right = new Border();
            right.CornerRadius = new CornerRadius(0, 23, 23, 0);
            right.Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
            right.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
            right.BorderThickness = new Thickness(1, 0, 0, 0);
            right.Padding = new Thickness(18, 20, 18, 16);
            Grid.SetColumn(right, 1);
            DockPanel rp = new DockPanel();

            StackPanel power = new StackPanel();
            DockPanel.SetDock(power, Dock.Bottom);
            TextBlock pt = new TextBlock();
            pt.Visibility = Visibility.Collapsed;
            pt.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
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
            rp.Children.Add(power);

            StackPanel info = new StackPanel();
            greet.Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255));
            greet.FontSize = 13;
            greet.FontFamily = UI.Text;
            clock.Foreground = Brushes.White;
            clock.FontSize = 40;
            clock.FontWeight = FontWeights.SemiBold;
            clock.FontFamily = UI.Text;
            clock.Margin = new Thickness(0, 2, 0, 0);
            date.Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255));
            date.FontSize = 12.5;
            date.FontFamily = UI.Text;
            date.TextWrapping = TextWrapping.Wrap;
            info.Children.Add(greet);
            info.Children.Add(clock);
            info.Children.Add(date);
            Border sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(0, 16, 0, 12);
            sep.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            info.Children.Add(sep);
            info.Children.Add(InfoRow("", wifiInfo));
            info.Children.Add(InfoRow("", batInfo));
            info.Children.Add(InfoRow("", diskInfo));
            TextBlock trayTitle = new TextBlock();
            trayTitle.Text = "Running in background";
            trayTitle.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
            trayTitle.FontSize = 12;
            trayTitle.FontWeight = FontWeights.SemiBold;
            trayTitle.FontFamily = UI.Text;
            trayTitle.Margin = new Thickness(0, 14, 0, 4);
            info.Children.Add(trayTitle);
            trayWrap.MaxWidth = 220;
            trayWrap.Margin = new Thickness(-6, 0, 0, 0);
            info.Children.Add(trayWrap);
            Border settings = new Border();
            settings.CornerRadius = new CornerRadius(10);
            settings.Padding = new Thickness(8, 7, 8, 7);
            settings.Margin = new Thickness(-8, 10, 0, 0);
            settings.Background = Brushes.Transparent;
            settings.Cursor = Cursors.Hand;
            UI.Hover(settings, 0x30);
            StackPanel sr = new StackPanel();
            sr.Orientation = Orientation.Horizontal;
            sr.Children.Add(UI.Glyph("", 14));
            TextBlock st = new TextBlock();
            st.Text = "Settings";
            st.Foreground = Brushes.White;
            st.FontSize = 13;
            st.FontFamily = UI.Text;
            st.Margin = new Thickness(10, 0, 0, 0);
            sr.Children.Add(st);
            settings.Child = sr;
            settings.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open("ms-settings:"); };
            info.Children.Add(settings);

            Border taskMgr = new Border();
            taskMgr.CornerRadius = new CornerRadius(10);
            taskMgr.Padding = new Thickness(8, 7, 8, 7);
            taskMgr.Margin = new Thickness(-8, 2, 0, 0);
            taskMgr.Background = Brushes.Transparent;
            taskMgr.Cursor = Cursors.Hand;
            taskMgr.ToolTip = "Ctrl+Shift+Esc";
            UI.Hover(taskMgr, 0x30);
            StackPanel tr = new StackPanel();
            tr.Orientation = Orientation.Horizontal;
            tr.Children.Add(UI.Glyph("\uE9D9", 14));
            TextBlock tt = new TextBlock();
            tt.Text = "Task Manager";
            tt.Foreground = Brushes.White;
            tt.FontSize = 13;
            tt.FontFamily = UI.Text;
            tt.Margin = new Thickness(10, 0, 0, 0);
            tr.Children.Add(tt);
            taskMgr.Child = tr;
            taskMgr.MouseLeftButtonUp += delegate { HidePanel(); Launcher.Open("taskmgr.exe"); };
            info.Children.Add(taskMgr);
            rp.Children.Add(info);

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

        FrameworkElement InfoRow(string glyph, TextBlock text)
        {
            StackPanel r = new StackPanel();
            r.Orientation = Orientation.Horizontal;
            r.Margin = new Thickness(0, 4, 0, 4);
            TextBlock g = UI.Glyph(glyph, 14);
            g.Width = 20;
            g.Opacity = 0.85;
            text.Foreground = Brushes.White;
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
                t.Foreground = up ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x66)) : new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255));
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
            b.Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
            b.Child = UI.Glyph(glyph, 15);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            sp.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x45, 255, 255, 255)); };
            sp.MouseLeave += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)); };
            TextBlock t = new TextBlock();
            t.Text = label;
            t.Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255));
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
                n.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
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

        public void ShowPanel(double dockTop)
        {
            if (IsOpen) return;
            addMode = false;
            filter.Text = "";
            Rebuild();
            FillInfo();
            sv.ScrollToTop();
            SlideIn(delegate(Size s) { return new Point(Popover.Scr.Left + (Popover.Scr.Width - s.Width) / 2, dockTop - s.Height + 6); });
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
            HashSet<string> inStart = new HashSet<string>(items.Select(p => p.Target), StringComparer.OrdinalIgnoreCase);
            string q = filter.Text.Trim();
            HashSet<string> seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (AppEntry a in Catalog.Desktop().Concat(Catalog.All()).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (inStart.Contains(a.Target) || !seen.Add(a.Name)) continue;
                if (q.Length > 0 && a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                AppEntry e = a;
                Border t = Tiles.App(e.Name, Icons.For(e.Target), delegate { StartStore.Add(e.Target, e.Name); Rebuild(); });
                t.ToolTip = "Click to add " + e.Name + " to Start";
                grid.Children.Add(t);
            }
        }
    }

    // ----- Wi-Fi versi dock -----
    class WifiNet { public string Ssid; public string Auth; public int Signal; public bool Known; public bool Connected; }

    static class Wifi
    {
        static string Run(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    return o;
                }
            }
            catch { return ""; }
        }

        public static List<WifiNet> Scan()
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
            string o = Run("wlan connect name=\"" + n.Ssid.Replace("\"", "") + "\"");
            return o.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0 ? null : o.Trim();
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

        public WifiPanel()
        {
            StackPanel sp = this;
            sp.Width = 300;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = new TextBlock();
            title.Text = "Wi-Fi";
            title.Foreground = Brushes.White;
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

            status.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
            status.FontSize = 12;
            status.FontFamily = UI.Text;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(4, 2, 4, 8);
            sp.Children.Add(status);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.MaxHeight = 360;
            sv.Content = list;
            sp.Children.Add(sv);

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
            st.Foreground = Brushes.White;
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

        static Border PillButton(string text, bool primary)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(9);
            b.Padding = new Thickness(14, 6, 14, 6);
            b.Cursor = Cursors.Hand;
            b.HorizontalAlignment = HorizontalAlignment.Right;
            Color normal = primary ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromArgb(0x30, 255, 255, 255);
            Color hover = primary ? Color.FromRgb(0x7D, 0xD8, 0xFF) : Color.FromArgb(0x48, 255, 255, 255);
            b.Background = new SolidColorBrush(normal);
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(hover); };
            b.MouseLeave += delegate { b.Background = new SolidColorBrush(normal); };
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12.5;
            t.FontFamily = UI.Text;
            t.Foreground = primary ? Brushes.Black : Brushes.White;
            b.Child = t;
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

        FrameworkElement Row(WifiNet n)
        {
            StackPanel outer = new StackPanel();
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(10, 7, 10, 7);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Cursor = Cursors.Hand;
            bool open = expanded == n.Ssid;
            b.Background = open ? new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)) : Brushes.Transparent;
            if (!open) UI.Hover(b, 0x22);
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string bars = n.Signal < 30 ? "" : n.Signal < 55 ? "" : n.Signal < 80 ? "" : "";
            TextBlock sig = UI.Glyph(bars, 16);
            sig.Width = 22;
            g.Children.Add(sig);
            StackPanel txt = new StackPanel();
            txt.Margin = new Thickness(10, 0, 0, 0);
            TextBlock name = new TextBlock();
            name.Text = n.Ssid;
            name.Foreground = Brushes.White;
            name.FontSize = 13.5;
            name.FontFamily = UI.Text;
            name.FontWeight = n.Connected ? FontWeights.SemiBold : FontWeights.Normal;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.Children.Add(name);
            TextBlock sub = new TextBlock();
            sub.Text = (n.Connected ? "Connected" : Wifi.IsOpen(n) ? "Open" : "Secured") + (n.Known && !n.Connected ? " · saved" : "") + " · " + n.Signal + "%";
            sub.Foreground = new SolidColorBrush(n.Connected ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromArgb(0x90, 255, 255, 255));
            sub.FontSize = 11;
            sub.FontFamily = UI.Text;
            txt.Children.Add(sub);
            Grid.SetColumn(txt, 1);
            g.Children.Add(txt);
            if (!Wifi.IsOpen(n))
            {
                TextBlock lk = UI.Glyph("", 12);
                lk.Opacity = 0.6;
                Grid.SetColumn(lk, 2);
                g.Children.Add(lk);
            }
            b.Child = g;
            b.MouseLeftButtonUp += delegate { expanded = open ? null : n.Ssid; Render(last); };
            outer.Children.Add(b);

            if (open)
            {
                StackPanel act = new StackPanel();
                act.Margin = new Thickness(42, 4, 6, 8);
                if (n.Connected)
                {
                    Border dis = PillButton("Disconnect", false);
                    dis.MouseLeftButtonUp += delegate
                    {
                        message = "Disconnected from " + n.Ssid;
                        ThreadPool.QueueUserWorkItem(delegate { Wifi.Disconnect(); Thread.Sleep(1200); Dispatcher.BeginInvoke(new Action(Refresh)); });
                    };
                    act.Children.Add(dis);
                }
                else
                {
                    PasswordBox pw = null;
                    if (!n.Known && !Wifi.IsOpen(n))
                    {
                        Border pwBox = new Border();
                        pwBox.CornerRadius = new CornerRadius(9);
                        pwBox.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
                        pwBox.Padding = new Thickness(10, 6, 10, 6);
                        pwBox.Margin = new Thickness(0, 0, 0, 8);
                        pw = new PasswordBox();
                        pw.Background = Brushes.Transparent;
                        pw.BorderThickness = new Thickness(0);
                        pw.Foreground = Brushes.White;
                        pw.CaretBrush = Brushes.White;
                        pw.FontSize = 13;
                        pw.ToolTip = "Network password";
                        pwBox.Child = pw;
                        act.Children.Add(pwBox);
                    }
                    Border con = PillButton("Connect", true);
                    PasswordBox pwRef = pw;
                    Action go = delegate { DoConnect(n, pwRef != null ? pwRef.Password : null); };
                    con.MouseLeftButtonUp += delegate { go(); };
                    if (pw != null)
                    {
                        pw.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) go(); };
                        Dispatcher.BeginInvoke(new Action(delegate { pwRef.Focus(); }), DispatcherPriority.Input);
                    }
                    act.Children.Add(con);
                }
                outer.Children.Add(act);
            }
            return outer;
        }

        void DoConnect(WifiNet n, string password)
        {
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

        void ToggleWifi()
        {
            if (wifiArea.Visibility == Visibility.Visible) { wifiArea.Visibility = Visibility.Collapsed; return; }
            wifiArea.Visibility = Visibility.Visible;
            wifiPanel.Start();
        }

        public QuickPop()
        {
            Border card = UI.Card(20, 14);
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
            volIcon.Cursor = Cursors.Hand;
            volIcon.ToolTip = "Mute / unmute";
            volIcon.MouseLeftButtonUp += delegate
            {
                SysInfo.SetMute(!SysInfo.Muted());
                UpdateVolIcon();
            };
            FrameworkElement volRow = SliderRow(volIcon, volume);
            volRow.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(volRow);

            Border sep = new Border();
            sep.Height = 1;
            sep.Margin = new Thickness(0, 12, 0, 8);
            sep.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            sp.Children.Add(sep);

            Grid foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            batText.Foreground = Brushes.White;
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
            wifiArea.BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            wifiArea.BorderThickness = new Thickness(1, 0, 0, 0);
            wifiArea.Margin = new Thickness(0, 14, 0, 12);
            wifiArea.Padding = new Thickness(14, 0, 14, 0);
            wifiArea.Child = wifiPanel;
            wifiArea.Visibility = Visibility.Collapsed;
            Grid.SetColumn(wifiArea, 1);
            two.Children.Add(wifiArea);
            wifiPanel.OnClose = delegate { HidePanel(); };
            AnchorCenter = true;
            card.Child = two;
            Content = card;

            volume.Changed = delegate(double v) { SysInfo.SetVolume(v); UpdateVolIcon(); };
            brightTimer.Interval = TimeSpan.FromMilliseconds(120);
            brightTimer.Tick += delegate
            {
                brightTimer.Stop();
                int v = pendingBright;
                if (v >= 0) ThreadPool.QueueUserWorkItem(delegate { SysInfo.SetBrightness(v); });
            };
            bright.Changed = delegate(double v) { pendingBright = (int)Math.Round(v * 100); if (!brightTimer.IsEnabled) brightTimer.Start(); };
            bright.Committed = delegate(double v) { pendingBright = (int)Math.Round(v * 100); brightTimer.Stop(); int x = pendingBright; ThreadPool.QueueUserWorkItem(delegate { SysInfo.SetBrightness(x); }); };
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
            b.Background = new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255));
            b.Cursor = Cursors.Hand;
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)); };
            b.MouseLeave += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255)); };
            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(UI.Glyph(glyph, 17));
            TextBlock t = new TextBlock();
            t.Text = label;
            t.Foreground = Brushes.White;
            t.FontSize = 11.5;
            t.FontFamily = UI.Text;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(t);
            if (sub != null)
            {
                sub.Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 255, 255, 255));
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
            volIcon.Text = m ? "" : "";
            volume.Opacity = m ? 0.5 : 1;
        }

        public void ShowAt(double right, double dockTop)
        {
            if (IsOpen) return;
            wifiArea.Visibility = Visibility.Collapsed;
            SysInfo.ResetAudio();
            double v = SysInfo.Volume();
            volume.Value = v < 0 ? 0 : v;
            UpdateVolIcon();
            batText.Text = "Battery " + SysInfo.BatteryText();
            wifiName.Text = "...";
            brightRow.Visibility = Visibility.Visible;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string ssid = SysInfo.Ssid();
                int b = SysInfo.Brightness();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    wifiName.Text = ssid ?? "Not connected";
                    if (b < 0) brightRow.Visibility = Visibility.Collapsed;
                    else bright.Value = b / 100.0;
                }));
            });
            SlideIn(delegate(Size s)
            {
                double x = Popover.Scr.Left + (Popover.Scr.Width - s.Width) / 2;
                return new Point(x, dockTop - s.Height + 6);
            });
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
        static T Wait<T>(global::Windows.Foundation.IAsyncOperation<T> op)
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
                c.Background = new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255));
                c.Padding = new Thickness(12, 9, 8, 10);
                c.Margin = new Thickness(0, 0, 0, 6);
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel tx = new StackPanel();
                TextBlock app = new TextBlock();
                app.Text = it.App + "  \u00B7  " + When(it.Time);
                app.Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 255, 255, 255));
                app.FontSize = 11;
                app.FontFamily = UI.Text;
                tx.Children.Add(app);
                if (!string.IsNullOrEmpty(it.Title))
                {
                    TextBlock ti = new TextBlock();
                    ti.Text = it.Title;
                    ti.Foreground = Brushes.White;
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
                    bo.Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255));
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
            nTitle.Foreground = Brushes.White;
            nTitle.FontSize = 15;
            nTitle.FontWeight = FontWeights.SemiBold;
            nTitle.FontFamily = UI.Text;
            nTitle.VerticalAlignment = VerticalAlignment.Center;
            nh.Children.Add(nTitle);
            clearAll.Text = "Clear all";
            clearAll.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
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
            nsep.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));

            title.Foreground = Brushes.White;
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.FontFamily = UI.Text;
            title.Margin = new Thickness(2, 0, 0, 10);
            sp.Children.Add(title);

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            monthText.Foreground = Brushes.White;
            monthText.FontSize = 13.5;
            monthText.FontFamily = UI.Text;
            monthText.VerticalAlignment = VerticalAlignment.Center;
            monthText.Margin = new Thickness(2, 0, 0, 0);
            head.Children.Add(monthText);
            FrameworkElement prev = NavButton("\uE76B", -1);
            FrameworkElement next = NavButton("\uE76C", 1);
            Grid.SetColumn(prev, 1);
            Grid.SetColumn(next, 2);
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
                t.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
                t.FontSize = 11;
                t.FontFamily = UI.Text;
                t.TextAlignment = TextAlignment.Center;
                wd.Children.Add(t);
            }
            sp.Children.Add(wd);
            days.Columns = 7;
            days.Rows = 6;
            sp.Children.Add(days);

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
            nt.Foreground = Brushes.White;
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
            Grid two = new Grid();
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ncard.VerticalAlignment = VerticalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
            two.Children.Add(ncard);
            Grid.SetColumn(card, 1);
            two.Children.Add(card);
            Cards = new List<Border> { ncard, card };
            Content = two;
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
            for (int i = 0; i < 42; i++)
            {
                DateTime d = start.AddDays(i);
                Border c = new Border();
                c.Width = 32;
                c.Height = 32;
                c.Margin = new Thickness(3, 2, 3, 2);
                c.CornerRadius = new CornerRadius(16);
                TextBlock t = new TextBlock();
                t.Text = d.Day.ToString();
                t.FontSize = 12.5;
                t.FontFamily = UI.Text;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                if (d == today)
                {
                    c.Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
                    t.Foreground = Brushes.Black;
                    t.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    t.Foreground = Brushes.White;
                    t.Opacity = d.Month == month.Month ? 1 : 0.35;
                }
                c.Child = t;
                days.Children.Add(c);
            }
        }

        static string Cap(string s)
        {
            return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);
        }

        public void ShowAt(double right, double dockTop)
        {
            if (IsOpen) return;
            month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            title.Text = Cap(DateTime.Today.ToString("dddd, d MMMM yyyy", Id));
            LoadNotifs();
            Render();
            AnchorCenter = true;
            SlideIn(delegate(Size s)
            {
                double x = Popover.Scr.Left + (Popover.Scr.Width - s.Width) / 2;
                return new Point(x, dockTop - s.Height + 6);
            });
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
        public Action OnShowOriginal;
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
            empty.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
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
                Tiles.Menu(b,
                    "Open " + e.Tip, (Action)delegate { HidePanel(); Launcher.Open(e.Exe); },
                    "Full tray menu...", (Action)delegate { HidePanel(); if (OnShowOriginal != null) OnShowOriginal(); });
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
                int border = UI.Theme == "glass" ? 0x00A8A4A0 : 0x00504A48;   // garis tepi (BGR): terang untuk Glass
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            };
            SizeChanged += delegate { Place(); ApplyGlass(); UpdateThumbs(); };
            watch.Interval = TimeSpan.FromMilliseconds(120);
            watch.Tick += delegate { CheckCursor(); };
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
            if (UI.Theme != "glass") return;
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
            if (cap == null || UI.Theme != "glass")
            {
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26));
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
            dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x6E, 0x14, 0x14, 0x1C)), null, new RectangleGeometry(unit)));
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
                card.MouseEnter += delegate { card.Background = new SolidColorBrush(Color.FromArgb(0x2D, 255, 255, 255)); };
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
                title.Foreground = Brushes.White;
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
                slot.Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
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

    // ================= dock =================
    class DockWindow : Window
    {
        public Action OnToggle, OnDoubleClick, OnFullscreen, OnSearch, OnPower, OnQuick, OnClock, OnTray;
        public bool HotkeyOk;
        public double SurfaceTop { get { return Top + ActualHeight - (fullCard != null && fullCard.ActualHeight > 0 ? fullCard.ActualHeight : 70); } }
        public Func<bool> AnyPopupOpen;
        public Action OnPopup;
        public void HidePreview() { previewTimer.Stop(); preview.HidePreview(); }
        const bool ReserveSpace = false;   // false = app terbuka penuh layar, dock melayang di atasnya
        Border fullCard;
        Border notch;
        readonly Image notchIcon = new Image();
        readonly StackPanel notchApps = new StackPanel();
        readonly TextBlock notchBat = new TextBlock();
        readonly TextBlock notchBatGlyph = UI.Glyph("\uE83F", 13);
        readonly TextBlock notchCharge = UI.Glyph("\uE945", 9);
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
        readonly TextBlock batGlyph = UI.Glyph("\uE83F", 15);
        readonly TextBlock chargeGlyph = UI.Glyph("\uE945", 11);
        readonly TextBlock batText = new TextBlock();
        Border statusTile;
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
        public double SurfaceExtent { get { return (fullCard != null && fullCard.ActualWidth > 0 ? fullCard.ActualWidth : 70) + 10; } }
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
            Grid rootGrid = new Grid();
            rootGrid.Children.Add(card);
            rootGrid.Children.Add(notch);
            Content = rootGrid;
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
            Drop += delegate(object s, DragEventArgs e)
            {
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
                    if (s == "dark" || s == "glass") Theme = s;
                }
            }
            catch { }
        }

        public void ApplyTheme(string th)
        {
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
                    b.RenderTransform = new ScaleTransform(1, 1);
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
                WrapWithShape(fullCard, 20, out fullShape, out fullGloss);
                WrapWithShape(notch, 14, out notchShape, out notchGloss);
            }
            UI.Theme = Theme;
            bool glass = Theme == "glass";
            foreach (System.Windows.Shapes.Path p in new System.Windows.Shapes.Path[] { fullShape, notchShape })
            {
                p.Fill = new SolidColorBrush(glass ? Color.FromArgb(0x5C, 0x14, 0x14, 0x1C) : Color.FromArgb(0xF4, 0x1B, 0x1B, 0x21));
                p.Stroke = new SolidColorBrush(Color.FromArgb((byte)(glass ? 0x80 : 0x40), 255, 255, 255));
                p.StrokeThickness = 1;
            }
            foreach (System.Windows.Shapes.Path gl in new System.Windows.Shapes.Path[] { fullGloss, notchGloss })
            {
                LinearGradientBrush g = new LinearGradientBrush();
                g.StartPoint = Layout.Side == 1 ? new Point(1, 0) : new Point(0, 0);                       // kilau dari tepi luar
                g.EndPoint = Layout.Side == 1 ? new Point(0, 0) : Layout.Side == 2 ? new Point(1, 0) : new Point(0, 1);
                g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(glass ? 0x30 : 0x00), 255, 255, 255), 0));
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
            notch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
            notch.BorderThickness = new Thickness(1);
            notch.Padding = new Thickness(10, 5, 12, 5);
            notch.HorizontalAlignment = HorizontalAlignment.Center;
            notch.VerticalAlignment = VerticalAlignment.Bottom;
            notch.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black };
            notch.Opacity = 0;
            notch.IsHitTestVisible = false;
            notch.ToolTip = "Hover to open the dock";
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
            dot.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
            sp.Children.Add(dot);
            sp.Children.Add(notchBatGlyph);
            notchCharge.Margin = new Thickness(2, 0, 0, 0);
            sp.Children.Add(notchCharge);
            notchBat.Foreground = Brushes.White;
            notchBat.FontSize = 11.5;
            notchBat.FontFamily = UI.Text;
            notchBat.VerticalAlignment = VerticalAlignment.Center;
            notchBat.Margin = Layout.V ? new Thickness(0, 2, 0, 0) : new Thickness(4, 0, 0, 0);
            if (Layout.V)
            {
                notchBat.FontSize = 10;
                notchBat.HorizontalAlignment = HorizontalAlignment.Center;
                notchCharge.Margin = new Thickness(0, 2, 0, 0);
            }
            sp.Children.Add(notchBat);
            notch.Child = sp;
        }

        // morph: satu bentuk menyusut/melebar ke ukuran bentuk lain sambil memudar
        void SetCompact(bool c)
        {
            if (c == compact) return;
            compact = c;
            FrameworkElement show = c ? (FrameworkElement)notch : fullCard;
            FrameworkElement hide = c ? (FrameworkElement)fullCard : notch;
            double sw = Math.Max(1, show.ActualWidth), shh = Math.Max(1, show.ActualHeight);
            double hw = Math.Max(1, hide.ActualWidth), hh = Math.Max(1, hide.ActualHeight);
            show.IsHitTestVisible = true;
            hide.IsHitTestVisible = false;
            Morph(show, hw / sw, hh / shh, 1, 1, 0, 1, 300);
            Morph(hide, 1, 1, sw / hw, shh / hh, 1, 0, 220);
        }

        static void Morph(FrameworkElement e, double fx, double fy, double tx, double ty, double fo, double to, int ms)
        {
            ScaleTransform st = new ScaleTransform(fx, fy);
            e.RenderTransformOrigin = Layout.Origin;
            e.RenderTransform = st;
            Duration d = TimeSpan.FromMilliseconds(ms);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            DoubleAnimation ax = new DoubleAnimation(fx, tx, d);
            ax.EasingFunction = ease;
            DoubleAnimation ay = new DoubleAnimation(fy, ty, d);
            ay.EasingFunction = ease;
            DoubleAnimation ao = new DoubleAnimation(fo, to, d);
            ao.EasingFunction = ease;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
            e.BeginAnimation(UIElement.OpacityProperty, ao);
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

        void HoverPoll()
        {
            if (hiddenForFullscreen) return;
            bool menu = MenuOpen();
            bool popup = AnyPopupOpen != null && AnyPopupOpen();
            if (CursorNear() || popup || menu || preview.Showing)   // dock penuh hanya saat ada interaksi; selain itu notch
            {
                lastNear = DateTime.Now;
                SetCompact(false);
                return;
            }
            if ((DateTime.Now - lastNear).TotalMilliseconds > 700) SetCompact(true);
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

        // efek membesar seperti Dock Mac: dihitung per frame (60 fps) dengan kurva kosinus
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
            foreach (UIElement c in itemsPanel.Children)
            {
                FrameworkElement fe = c as FrameworkElement;
                if (fe == null) continue;
                ScaleTransform st = fe.Tag as ScaleTransform;
                if (st == null) continue;
                double target = 1;
                if (!double.IsNaN(magX))
                {
                    double cx = Layout.V ? fe.TranslatePoint(new Point(0, fe.ActualHeight / 2), itemsPanel).Y : fe.TranslatePoint(new Point(fe.ActualWidth / 2, 0), itemsPanel).X;
                    double d = Math.Min(Math.Abs(magX - cx) / 130.0, 1);
                    target = 1 + 0.42 * Math.Cos(d * Math.PI / 2);
                }
                double cur = st.ScaleX;
                double nv = cur + (target - cur) * 0.22;
                if (Math.Abs(target - nv) < 0.002) nv = target; else moving = true;
                st.ScaleX = nv;
                st.ScaleY = nv;
                Panel.SetZIndex(fe, (int)(nv * 100));
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
            b.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
            return b;
        }

        Border NewTile()
        {
            Border t = new Border();
            t.MinWidth = 46;
            t.Height = 50;
            t.Margin = Layout.V ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0);
            t.CornerRadius = new CornerRadius(12);
            t.Background = Brushes.Transparent;
            t.Cursor = Cursors.Hand;
            UI.Hover(t, 0x30);
            return t;
        }
        Border GlyphTile(string glyph, double size, string tip, Action click)
        {
            Border t = NewTile();
            t.Child = UI.Glyph(glyph, size);
            t.ToolTip = tip;
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return t;
        }

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
            t.Child = g;
            t.ToolTip = "Start (Windows key)  -  right-click for SlideDock options";
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
            t.ToolTip = "Recycle Bin";
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

        FrameworkElement MakeStatus()
        {
            statusTile = NewTile();
            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            batText.Foreground = Brushes.White;
            batText.FontFamily = UI.Text;
            batText.VerticalAlignment = VerticalAlignment.Center;
            if (Layout.V)
            {
                // dock tegak: Wi-Fi, volume, baterai ditumpuk ke bawah
                statusTile.Height = double.NaN;
                statusTile.Padding = new Thickness(0, 8, 0, 8);
                sp.Orientation = Orientation.Vertical;
                netGlyph.Margin = new Thickness(0, 0, 0, 7);
                volGlyph.Margin = new Thickness(0, 0, 0, 7);
                StackPanel bat = new StackPanel();
                bat.Orientation = Orientation.Horizontal;
                bat.HorizontalAlignment = HorizontalAlignment.Center;
                chargeGlyph.Margin = new Thickness(2, 0, 0, 0);
                bat.Children.Add(batGlyph);
                bat.Children.Add(chargeGlyph);
                batText.FontSize = 10.5;
                batText.HorizontalAlignment = HorizontalAlignment.Center;
                batText.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(netGlyph);
                sp.Children.Add(volGlyph);
                sp.Children.Add(bat);
                sp.Children.Add(batText);
            }
            else
            {
                statusTile.Padding = new Thickness(8, 0, 8, 0);
                sp.Orientation = Orientation.Horizontal;
                netGlyph.Margin = new Thickness(0, 0, 9, 0);
                volGlyph.Margin = new Thickness(0, 0, 9, 0);
                batText.FontSize = 12;
                batText.Margin = new Thickness(4, 0, 0, 0);
                chargeGlyph.Margin = new Thickness(2, 0, 0, 0);
                sp.Children.Add(netGlyph);
                sp.Children.Add(volGlyph);
                sp.Children.Add(batGlyph);
                sp.Children.Add(chargeGlyph);
                sp.Children.Add(batText);
            }
            statusTile.Child = sp;
            statusTile.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (OnQuick != null) OnQuick(); };
            return statusTile;
        }
        Border trayTile;
        readonly PreviewWindow preview = new PreviewWindow();
        readonly DispatcherTimer previewTimer = new DispatcherTimer();
        DockItem pendingItem;
        Border pendingTile;

        static string ItemKey(DockItem it) { return (it.Lnk ?? "") + "|" + (it.Key ?? ""); }

        void ShowPreview()
        {
            previewTimer.Stop();
            DockItem it = pendingItem;
            Border t = pendingTile;
            if (it == null || t == null || it.Wins.Count == 0 || !t.IsVisible || compact) return;
            Point tl = t.PointToScreen(new Point(0, 0));
            Point br = t.PointToScreen(new Point(t.ActualWidth, t.ActualHeight));
            Point mid = t.TranslatePoint(new Point(t.ActualWidth / 2, 0), this);
            preview.Scr = ScreenDip;
            preview.ScrScale = Scale();
            Point ttl = t.TranslatePoint(new Point(0, 0), this);
            preview.CenterY = Top + ttl.Y + t.ActualHeight / 2;
            Point cardL = fullCard.TranslatePoint(new Point(0, 0), this);
            preview.SideX = Layout.Side == 1 ? Left + cardL.X + fullCard.ActualWidth : Left + cardL.X;
            preview.ShowFor(ItemKey(it), new List<IntPtr>(it.Wins), IconOf(it), new Rect(tl, br), Left + mid.X, SurfaceTop);
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
                        line.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
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
            clockTime.Foreground = Brushes.White;
            clockTime.FontSize = 13.5;
            clockTime.FontWeight = FontWeights.SemiBold;
            clockTime.FontFamily = UI.Text;
            clockTime.TextAlignment = TextAlignment.Center;
            clockDate.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
            clockDate.FontSize = 10.5;
            clockDate.FontFamily = UI.Text;
            clockDate.TextAlignment = TextAlignment.Center;
            clock.Children.Add(clockTime);
            clock.Children.Add(clockDate);
            t.Child = clock;
            t.ToolTip = "Calendar";
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
            netGlyph.Text = kind == "eth" ? "\uE839" : "\uE701";
            netGlyph.Opacity = kind == "none" ? 0.35 : 1;
            string netText = kind == "wifi" ? "Wi-Fi connected" : kind == "eth" ? "Ethernet connected" : "No internet";

            WF.PowerStatus ps = WF.SystemInformation.PowerStatus;
            bool hasBat = (ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            string batTip = "";
            if (hasBat)
            {
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;
                batGlyph.Text = pct >= 100 ? "\uE83F" : ((char)(0xE850 + Math.Max(0, Math.Min(9, pct / 10)))).ToString();
                batText.Text = pct + "%";
                notchBat.Text = pct + "%";
                notchBatGlyph.Text = batGlyph.Text;
                chargeGlyph.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
                notchCharge.Visibility = chargeGlyph.Visibility;
                batGlyph.Visibility = Visibility.Visible;
                batText.Visibility = Visibility.Visible;
                batTip = "  |  Battery " + pct + "%" + (charging ? " (charging)" : "");
            }
            else
            {
                batGlyph.Visibility = Visibility.Collapsed;
                batText.Visibility = Visibility.Collapsed;
                chargeGlyph.Visibility = Visibility.Collapsed;
            }
            statusTile.ToolTip = netText + batTip + "  |  Click: Quick Settings (Wi-Fi, volume, brightness)";
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
                dot.Background = new SolidColorBrush(it.Active ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromArgb(0xB0, 255, 255, 255));
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
            t.ToolTip = it.Name;
            t.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Activate(it); };

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
            StringBuilder sb = new StringBuilder();
            foreach (DockItem it in list)
                sb.Append(it.Lnk).Append('|').Append(it.Key).Append('|').Append(it.Wins.Count).Append('|').Append(it.Active ? 1 : 0).Append(';');
            string sig = sb.ToString();
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
                ng.ToolTip = it.Name;
                Image im = new Image();
                im.Source = IconOf(it);
                im.Width = 20;
                im.Height = 20;
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                ng.Children.Add(im);
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

        static bool AutoStartOn()
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetAutoStart(bool on)
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue(RunName, false);
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex m = new Mutex(true, "SlideDock.SingleInstance", out created))
            {
                if (!created)
                {
                    // dimulai ulang karena jumlah monitor berubah: tunggu instance lama selesai
                    if (Array.IndexOf(args, "--restart") < 0) return;
                    try { if (!m.WaitOne(15000)) return; } catch (AbandonedMutexException) { }
                }
                bool restarting = false;

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                UI.ApplyMenuStyle(app);
                Layout.Load();

                StartWindow start = new StartWindow();
                SearchWindow search = new SearchWindow();
                CalendarPop cal = new CalendarPop();
                TrayPop trayPop = new TrayPop();
                QuickPop quick = new QuickPop();
                cal.OnNotifications = delegate { WinKeyHook.SendWinCombo(0x4E); };
                trayPop.OnShowOriginal = delegate { TaskbarCtl.Peek(10); WinKeyHook.SendWinCombo(0x42); };  // Win+B
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
                Action<DockWindow> use = delegate(DockWindow d) { cur = d; Popover.Scr = d.ScreenDip; Popover.SideGap = d.SurfaceExtent; };
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
                start.OnTrayFull = delegate { hideAll(); TaskbarCtl.Peek(10); WinKeyHook.SendWinCombo(0x42); };  // Win+B
                wire = delegate(DockWindow d)
                {
                    d.OnToggle = delegate { use(d); toggle(); };
                    d.OnDoubleClick = hideAll;
                    d.OnFullscreen = hideAll;
                    d.OnPopup = delegate { };
                    d.OnSearch = delegate { use(d); if (switchTo(search)) search.ShowPanel(d.SurfaceTop); };
                    d.OnQuick = delegate { use(d); if (switchTo(quick)) quick.ShowAt(d.ClockRight(), d.SurfaceTop); };
                    d.OnClock = delegate { use(d); if (switchTo(cal)) cal.ShowAt(d.ClockRight(), d.SurfaceTop); };
                    d.OnTray = delegate { use(d); if (switchTo(trayPop)) trayPop.ShowAt(d.TrayLeft(), d.SurfaceTop); };
                    d.AnyPopupOpen = delegate { if (cur != d) return false; foreach (Popover p in pops) if (p.IsOpen) return true; return false; };
                };
                foreach (DockWindow d in docks) { wire(d); d.Show(); }
                WinKeyHook.OnWinTap = delegate { use(dockAtCursor()); toggle(); };
                // tombol Windows tetap membuka Start dock, kecuali saat game fullscreen (Direct3D) / mode presentasi
                WinKeyHook.Enabled = delegate { int st; return Native.SHQueryUserNotificationState(out st) != 0 || (st != 3 && st != 4); };
                WinKeyHook.Install();
                MenuCloser.Install();

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
                tray.Icon = DR.SystemIcons.Application;
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
                WF.ToolStripMenuItem hideTb = new WF.ToolStripMenuItem("Hide Windows taskbar");
                hideTb.Checked = true;
                hideTb.Click += delegate
                {
                    hideTb.Checked = !hideTb.Checked;
                    TaskbarCtl.HideEnabled = hideTb.Checked;
                    if (hideTb.Checked) TaskbarCtl.Apply(); else TaskbarCtl.ShowAll();
                };
                menu.Items.Add(hideTb);
                WF.ToolStripMenuItem winKey = new WF.ToolStripMenuItem("Windows key opens dock");
                winKey.Checked = true;
                winKey.Click += delegate { winKey.Checked = !winKey.Checked; WinKeyHook.Active = winKey.Checked; };
                menu.Items.Add(winKey);
                WF.ToolStripMenuItem themeGlass = new WF.ToolStripMenuItem("Theme: Glass");
                WF.ToolStripMenuItem themeDark = new WF.ToolStripMenuItem("Theme: Dark");
                Action syncTheme = delegate { themeGlass.Checked = dock.Theme == "glass"; themeDark.Checked = dock.Theme == "dark"; };
                themeGlass.Click += delegate { foreach (DockWindow d in docks) d.ApplyTheme("glass"); syncTheme(); };
                themeDark.Click += delegate { foreach (DockWindow d in docks) d.ApplyTheme("dark"); syncTheme(); };
                menu.Items.Add(themeGlass);
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
                tray.ContextMenuStrip = menu;

                // menu SlideDock di dock (klik kanan tombol Start / area kosong dock)
                Func<List<KeyValuePair<string, Action>>> dockMenu = delegate
                {
                    Func<bool, string> ck = delegate(bool on) { return on ? "\u2713   " : "      "; };
                    List<KeyValuePair<string, Action>> l = new List<KeyValuePair<string, Action>>();
                    l.Add(new KeyValuePair<string, Action>("Task Manager", delegate { Launcher.Open("taskmgr.exe"); }));
                    l.Add(new KeyValuePair<string, Action>("Windows Settings", delegate { Launcher.Open("ms-settings:"); }));
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    l.Add(new KeyValuePair<string, Action>(ck(dock.Theme == "glass") + "Theme: Glass", delegate { foreach (DockWindow d in docks) d.ApplyTheme("glass"); syncTheme(); }));
                    l.Add(new KeyValuePair<string, Action>(ck(dock.Theme == "dark") + "Theme: Dark", delegate { foreach (DockWindow d in docks) d.ApplyTheme("dark"); syncTheme(); }));
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    string[] pn = new string[] { "Bottom", "Left", "Right" };
                    for (int i = 0; i < 3; i++)
                    {
                        int idx = i;
                        l.Add(new KeyValuePair<string, Action>(ck(Layout.Side == idx) + "Position: " + pn[i], delegate { if (Layout.Side == idx) return; changeSide(idx); }));
                    }
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    l.Add(new KeyValuePair<string, Action>(ck(WinKeyHook.Active) + "Windows key opens dock", delegate { WinKeyHook.Active = !WinKeyHook.Active; winKey.Checked = WinKeyHook.Active; }));
                    l.Add(new KeyValuePair<string, Action>(ck(AutoStartOn()) + "Start with Windows", delegate { SetAutoStart(!AutoStartOn()); auto.Checked = AutoStartOn(); }));
                    l.Add(new KeyValuePair<string, Action>("Open settings folder", delegate { Process.Start("explorer.exe", Store.Dir); }));
                    l.Add(new KeyValuePair<string, Action>(null, null));
                    l.Add(new KeyValuePair<string, Action>("Exit SlideDock (restore taskbar)", delegate { exitApp(); }));
                    return l;
                };
                foreach (DockWindow d in docks) d.MenuProvider = dockMenu;

                // ganti posisi dock tanpa memulai ulang app: dock baru dibuat & tampil dulu, baru dock lama ditutup
                changeSide = delegate(int side)
                {
                    if (Layout.Side == side) return;
                    app.Dispatcher.BeginInvoke(new Action(delegate                 // tunggu menu tertutup dulu
                    {
                        hideAll();
                        foreach (DockWindow d in docks) d.HidePreview();
                        Layout.Save(side);
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
                    }), DispatcherPriority.Background);
                };
                tray.MouseClick += delegate(object s, WF.MouseEventArgs e)
                {
                    if (e.Button == WF.MouseButtons.Left) { use(dock); toggle(); }
                };

                app.Exit += delegate { WinKeyHook.Uninstall(); MenuCloser.Uninstall(); if (!restarting) TaskbarCtl.Restore(); };
                // monitor dipasang/dilepas atau resolusi berubah
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        WF.Screen[] screens = WF.Screen.AllScreens.OrderByDescending(x => x.Primary).ToArray();
                        if (screens.Length == docks.Count)
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
                        // jumlah layar berubah: mulai ulang SlideDock agar setiap layar punya dock
                        restarting = true;
                        try { Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "--restart"); } catch { restarting = false; return; }
                        tray.Visible = false;
                        tray.Dispose();
                        foreach (DockWindow d in docks) d.Close();
                        app.Shutdown();
                    }), DispatcherPriority.Background);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate { WinKeyHook.Uninstall(); TaskbarCtl.Restore(); };
                // error di tampilan: catat ke log, app tetap jalan (taskbar tidak "hilang")
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    try { File.AppendAllText(System.IO.Path.Combine(Store.Dir, "error.log"), DateTime.Now + "  " + e.Exception + Environment.NewLine + Environment.NewLine); } catch { }
                    e.Handled = true;
                };
                if (!dock.HotkeyOk)
                    tray.ShowBalloonTip(5000, "SlideDock", "Win+Alt+Space is already used by another app.", WF.ToolTipIcon.Warning);
                app.Run();
            }
        }
    }
}

