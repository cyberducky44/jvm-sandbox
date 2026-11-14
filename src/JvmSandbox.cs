// jvm-sandbox: runs a JVM (or any program) inside a Windows AppContainer.
// The kernel enforces the sandbox, so the JVM itself runs unmodified at full speed.
// Targets C# 5 so it builds with the csc.exe that ships with every Windows install.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class JvmSandbox
{
    const uint RO = 0x1200A9;            // FILE_GENERIC_READ | FILE_GENERIC_EXECUTE
    const uint RW = 0x1301BF | 0x40;     // modify + FILE_DELETE_CHILD
    const uint LIST = 0x1200A1;          // list names + traverse + read attributes, this folder only

    const uint WRITE = 0x10156;          // write/append/delete bits, used for deny entries
    const uint ALL = 0x1F01FF;

    static bool verbose;
    static string logPath, javaHome;
    static readonly List<string> gameDirs = new List<string>();
    static IntPtr containerSid;
    static IntPtr allPackagesSid;
    static readonly Dictionary<string, uint> grants = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Log("error: " + e.Message); return 126; }
    }

    static int Run(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--grant-list")   // elevated helper, see below
        {
            ConvertStringSidToSidW(args[1], out containerSid);
            ConvertStringSidToSidW("S-1-15-2-1", out allPackagesSid);
            for (int k = 2; k < args.Length; k++) Grant(args[k], LIST, false);
            return 0;
        }
        string name = "jvm-sandbox";
        bool net = true, lan = false, elevate = true;
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a == "--rw" && i + 1 < args.Length) Want(args[++i], RW);
            else if (a == "--ro" && i + 1 < args.Length) Want(args[++i], RO);
            else if (a == "--name" && i + 1 < args.Length) name = "jvm-sandbox." + args[++i];
            else if (a == "--no-net") net = false;
            else if (a == "--lan") lan = true;
            else if (a == "--verbose") verbose = true;
            else if (a == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (a == "--no-elevate") elevate = false;
            else if (a == "--help" || a == "-h" || a == "/?") { Usage(); return 0; }
            else if (a.StartsWith("--")) { Log("unknown option " + a); Usage(); return 2; }
            else break;
        }
        if (i >= args.Length) { Usage(); return 2; }

        string exe = ResolveExe(args[i]);
        string[] cmd = new string[args.Length - i];
        Array.Copy(args, i, cmd, 0, cmd.Length);
        cmd[0] = exe;

        // --- work out what the game legitimately needs ---
        DirectoryInfo bin = Directory.GetParent(exe);
        if (bin != null && bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && bin.Parent != null)
            Want(javaHome = bin.Parent.FullName, RO);
        else
            Want(exe, RO);
        ScanCommand(cmd);
        // Lunar Client keeps its jars, natives, profiles, mods, logs and settings in ~/.lunarclient
        string lunar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lunarclient");
        if (Directory.Exists(lunar) && (string.Join("|", cmd) + "|" + Environment.CurrentDirectory).IndexOf(lunar, StringComparison.OrdinalIgnoreCase) >= 0)
            Want(lunar, RW);
        string inst = Environment.GetEnvironmentVariable("INST_DIR");   // Prism Launcher / MultiMC
        if (!string.IsNullOrEmpty(inst) && Directory.Exists(inst))
        {
            Want(inst, RW);
            try
            {
                string root = Directory.GetParent(Path.GetFullPath(inst).TrimEnd('\\', '/')).Parent.FullName;
                Want(Path.Combine(root, "libraries"), RO);
                Want(Path.Combine(root, "assets"), RO);
            }
            catch (Exception) { }
        }

        // --- AppContainer identity ---
        int hr = CreateAppContainerProfile(name, name, "Sandbox for untrusted JVM code", IntPtr.Zero, 0, out containerSid);
        if (hr != 0)
        {
            hr = DeriveAppContainerSidFromAppContainerName(name, out containerSid);
            if (hr != 0) throw new Exception("cannot create AppContainer profile (0x" + hr.ToString("X8") + ")");
        }
        ConvertStringSidToSidW("S-1-15-2-1", out allPackagesSid);
        IntPtr sidStrPtr;
        ConvertSidToStringSidW(containerSid, out sidStrPtr);
        string sidStr = Marshal.PtrToStringUni(sidStrPtr);

        // Private, writable temp dir (JVM, LWJGL and friends all extract things to %TEMP%)
        IntPtr folderPtr;
        if (GetAppContainerFolderPath(sidStr, out folderPtr) == 0)
        {
            string temp = Path.Combine(Marshal.PtrToStringUni(folderPtr), "Temp");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
        }

        // Java's Path.toRealPath() looks every path component up in its parent directory, so the
        // parents of granted paths must be listable (names only - not inherited, no file contents).
        Dictionary<string, bool> parents = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, uint> g in grants)
        {
            if (!Grant(g.Key, g.Value, true) && !File.Exists(g.Key) && !Directory.Exists(g.Key)) continue;
            for (DirectoryInfo d = Directory.GetParent(g.Key); d != null; d = d.Parent) parents[d.FullName] = true;
        }
        // The launcher's own files must not be writable (or, for stored logins, readable) by mods,
        // even when they sit inside a folder the game may write to.
        if (javaHome != null)
            foreach (KeyValuePair<string, uint> g in grants)
                if ((g.Value & RW) == RW && javaHome.StartsWith(g.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { Grant(javaHome, WRITE, true, true); break; }
        foreach (string gd in gameDirs)
        {
            if (!Directory.Exists(gd)) continue;
            foreach (string pat in new string[] { "launcher_accounts*", "launcher_msa_credentials*" })
                foreach (string f in Directory.GetFiles(gd, pat)) Grant(f, ALL, true, true);
        }
        List<string> needAdmin = new List<string>();
        foreach (string p in parents.Keys)
            if (!grants.ContainsKey(p) && !Grant(p, LIST, false)) needAdmin.Add(p);
        if (needAdmin.Count > 0 && !elevate)
            Log("warning: sandbox cannot see folder names in: " + string.Join(", ", needAdmin.ToArray()) + " - Java may fail to resolve paths");
        else if (needAdmin.Count > 0)
        {
            // System-owned folders (drive roots, C:\Users): needs one elevation, once.
            Log("one-time setup: admin rights needed to let the sandbox see folder names in: " + string.Join(", ", needAdmin.ToArray()));
            StringBuilder ea = new StringBuilder("--grant-list " + sidStr);
            foreach (string p in needAdmin) { ea.Append(' '); Quote(ea, p); }
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    System.Reflection.Assembly.GetExecutingAssembly().Location, ea.ToString());
                psi.Verb = "runas";
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                System.Diagnostics.Process.Start(psi).WaitForExit();
            }
            catch (Exception) { Log("warning: setup was declined; the game will probably fail to start"); }
        }

        // --- capabilities (network) ---
        List<string> capSids = new List<string>();
        if (net) capSids.Add("S-1-15-3-1");                       // internetClient
        if (net && lan) { capSids.Add("S-1-15-3-2"); capSids.Add("S-1-15-3-3"); } // internetClientServer, privateNetworkClientServer
        int saSize = Marshal.SizeOf(typeof(SID_AND_ATTRIBUTES));
        IntPtr caps = Marshal.AllocHGlobal(saSize * Math.Max(1, capSids.Count));
        for (int c = 0; c < capSids.Count; c++)
        {
            SID_AND_ATTRIBUTES sa = new SID_AND_ATTRIBUTES();
            ConvertStringSidToSidW(capSids[c], out sa.Sid);
            sa.Attributes = 4; // SE_GROUP_ENABLED
            Marshal.StructureToPtr(sa, new IntPtr(caps.ToInt64() + c * saSize), false);
        }
        SECURITY_CAPABILITIES sc = new SECURITY_CAPABILITIES();
        sc.AppContainerSid = containerSid;
        sc.Capabilities = capSids.Count > 0 ? caps : IntPtr.Zero;
        sc.CapabilityCount = (uint)capSids.Count;
        IntPtr scPtr = Marshal.AllocHGlobal(Marshal.SizeOf(sc));
        Marshal.StructureToPtr(sc, scPtr, false);

        IntPtr attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        IntPtr attrs = Marshal.AllocHGlobal(attrSize);
        if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref attrSize)) throw Win32("InitializeProcThreadAttributeList");
        if (!UpdateProcThreadAttribute(attrs, 0, new IntPtr(0x00020009) /* SECURITY_CAPABILITIES */, scPtr,
                new IntPtr(Marshal.SizeOf(sc)), IntPtr.Zero, IntPtr.Zero)) throw Win32("UpdateProcThreadAttribute");

        // --- job: the whole process tree dies with this wrapper ---
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION jl = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        jl.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
        int jlSize = Marshal.SizeOf(jl);
        IntPtr jlPtr = Marshal.AllocHGlobal(jlSize);
        Marshal.StructureToPtr(jl, jlPtr, false);
        SetInformationJobObject(job, 9, jlPtr, (uint)jlSize);

        // --- launch ---
        STARTUPINFOEX si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf(si);
        si.lpAttributeList = attrs;
        // Pass our stdio on explicitly: Windows does not do it by itself for GUI programs like javaw.exe,
        // and launchers read the game's output through these pipes.
        IntPtr hIn = GetStdHandle(-10), hOut = GetStdHandle(-11), hErr = GetStdHandle(-12);
        IntPtr bad = new IntPtr(-1);
        if ((hIn != IntPtr.Zero && hIn != bad) || (hOut != IntPtr.Zero && hOut != bad) || (hErr != IntPtr.Zero && hErr != bad))
        {
            si.StartupInfo.dwFlags |= 0x100; // STARTF_USESTDHANDLES
            si.StartupInfo.hStdInput = hIn; si.StartupInfo.hStdOutput = hOut; si.StartupInfo.hStdError = hErr;
        }
        PROCESS_INFORMATION pi;
        StringBuilder cl = new StringBuilder();
        foreach (string a in cmd) { if (cl.Length > 0) cl.Append(' '); Quote(cl, a); }
        const uint flags = 0x00080000 /* EXTENDED_STARTUPINFO_PRESENT */ | 0x4 /* CREATE_SUSPENDED */ | 0x400 /* UNICODE_ENVIRONMENT */;
        if (!CreateProcessW(exe, cl, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, null, ref si, out pi))
            throw Win32("CreateProcess " + exe);
        AssignProcessToJobObject(job, pi.hProcess);
        ResumeThread(pi.hThread);
        if (verbose) Log("started " + exe + " (pid " + pi.dwProcessId + ", " + (cmd.Length - 1) + " args) in sandbox " + name);
        Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; }; // child gets it too
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        uint code;
        GetExitCodeProcess(pi.hProcess, out code);
        if (verbose) Log(Path.GetFileName(exe) + " (pid " + pi.dwProcessId + ") exited with code " + (int)code);
        return (int)code;
    }

    static void Usage()
    {
        Console.Error.WriteLine(
            "usage: jvm-sandbox [options] [--] <java.exe> [java args...]\n" +
            "  --rw <path>    extra path the sandbox may read and write\n" +
            "  --ro <path>    extra path the sandbox may read\n" +
            "  --name <id>    use a separate sandbox identity (isolates instances from each other)\n" +
            "  --no-net       no network access at all\n" +
            "  --lan          also allow LAN access and listening (hosting / LAN worlds)\n" +
            "  --log <file>   also append this wrapper's messages to a file\n" +
            "  --verbose      print what is being granted\n" +
            "  --no-elevate   never ask for admin rights (skips first-run setup of system folders)\n" +
            "Auto-granted: the Java home (ro), classpath / -jar / -javaagent / java.library.path /\n" +
            "--assetsDir (ro), --gameDir and %INST_DIR% (rw). Everything else is denied.");
    }

    static void Log(string s)
    {
        Console.Error.WriteLine("[jvm-sandbox] " + s);
        if (logPath != null) try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch (Exception) { }
    }

    static string ResolveExe(string exe)
    {
        if (exe.IndexOfAny(new char[] { '\\', '/' }) >= 0 || File.Exists(exe)) return Path.GetFullPath(exe);
        string[] exts = Path.HasExtension(exe) ? new string[] { "" } : new string[] { ".exe", ".cmd", ".bat" };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            foreach (string ext in exts)
            {
                if (dir.Trim().Length == 0) continue;
                string p = Path.Combine(dir.Trim(), exe + ext);
                if (File.Exists(p)) return p;
            }
        throw new Exception("cannot find " + exe);
    }

    static void ScanCommand(string[] cmd)
    {
        for (int i = 1; i < cmd.Length; i++)
        {
            string a = cmd[i];
            bool next = i + 1 < cmd.Length;
            if ((a == "-cp" || a == "-classpath" || a == "--class-path" || a == "-p" || a == "--module-path") && next) WantList(cmd[++i]);
            else if (a.StartsWith("--class-path=")) WantList(a.Substring(13));
            else if (a.StartsWith("-Djava.library.path=")) WantList(a.Substring(20));
            else if (a.StartsWith("-javaagent:")) Want(a.Substring(11).Split('=')[0], RO);
            else if (a == "-jar" && next) Want(cmd[++i], RO);
            else if (a == "--gameDir" && next) { gameDirs.Add(Path.GetFullPath(cmd[i + 1])); Want(cmd[++i], RW); }
            else if (a == "--assetsDir" && next) Want(cmd[++i], RO);
        }
    }

    static void WantList(string list)
    {
        foreach (string e in list.Split(';'))
        {
            string p = e.Trim();
            if (p.EndsWith("*")) p = p.Substring(0, p.Length - 1);
            if (p.Length > 0) Want(p, RO);
        }
    }

    static void Want(string path, uint access)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (path.Length > 3) path = path.TrimEnd('\\', '/');
        }
        catch (Exception) { return; }
        uint cur;
        grants.TryGetValue(path, out cur);
        grants[path] = cur | access;
    }

    // Refuse to open up locations that would defeat the point of the sandbox.
    static bool TooBroad(string path)
    {
        if (Path.GetPathRoot(path).TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        string p = path.TrimEnd('\\') + "\\";
        Environment.SpecialFolder[] sens = {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory };
        foreach (Environment.SpecialFolder f in sens)
        {
            string s = Environment.GetFolderPath(f);
            if (s.Length == 0) continue;
            // the folder itself or any ancestor of it
            if ((s.TrimEnd('\\') + "\\").StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Returns true if the sandbox has (or now has) the access.
    // With deny=true, adds a deny entry instead (overrides anything inherited from a parent).
    static bool Grant(string path, uint access, bool inherit, bool deny = false)
    {
        bool dir = Directory.Exists(path);
        if (!dir && !File.Exists(path)) return false;
        IntPtr dacl, sd;
        if (GetNamedSecurityInfoW(path, 1, 4, IntPtr.Zero, IntPtr.Zero, out dacl, IntPtr.Zero, out sd) != 0) return false;
        try
        {
            if (HasAccess(dacl, access, deny)) return true;   // already set: nothing to do (keeps relaunches instant)
            if (inherit && !deny && TooBroad(path)) { Log("refusing to grant access to " + path + " (too broad)"); return true; }
            EXPLICIT_ACCESS ea = new EXPLICIT_ACCESS();
            ea.grfAccessPermissions = access;
            ea.grfAccessMode = deny ? 3 : 1;        // DENY_ACCESS : GRANT_ACCESS
            ea.grfInheritance = dir && inherit ? 3u : 0u;      // children inherit
            ea.TrusteeForm = 0;                     // TRUSTEE_IS_SID
            ea.TrusteeType = 5;                     // well-known group
            ea.ptstrName = containerSid;
            IntPtr newDacl;
            uint rc = SetEntriesInAclW(1, ref ea, dacl, out newDacl);
            if (rc == 0 && inherit)
                rc = SetNamedSecurityInfoW(path, 1, 4, IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero); // propagates to children
            else if (rc == 0)
            {
                // Set this one folder only. SetNamedSecurityInfo would re-walk the entire tree below it
                // (minutes on something like AppData) even though this ACE is not inherited.
                ushort ctl; uint rev;
                GetSecurityDescriptorControl(sd, out ctl, out rev);
                IntPtr nsd = Marshal.AllocHGlobal(64);
                InitializeSecurityDescriptor(nsd, 1);
                SetSecurityDescriptorDacl(nsd, true, newDacl, false);
                ushort keep = (ushort)((ctl & 0x1400) | ((ctl & 0x0400) != 0 ? 0x0100 : 0)); // keep auto-inherited / protected state
                SetSecurityDescriptorControl(nsd, 0x1500, keep);
                rc = SetFileSecurityW(path, 4, nsd) ? 0u : (uint)Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(nsd);
            }
            if (rc != 0 && inherit) Log("warning: could not grant access to " + path + " (error " + rc + ")");
            else if (rc == 0 && verbose) Log((deny ? "denied " : "granted ") + (access == WRITE ? "write " : access == ALL ? "all " : access == LIST ? "list " : access == RO ? "ro " : "rw ") + path);
            if (newDacl != IntPtr.Zero) LocalFree(newDacl);
            return rc == 0;
        }
        finally { LocalFree(sd); }
    }

    static bool HasAccess(IntPtr dacl, uint want, bool deny)
    {
        if (dacl == IntPtr.Zero) return false;
        int count = (ushort)Marshal.ReadInt16(dacl, 4);
        uint have = 0;
        for (int i = 0; i < count; i++)
        {
            IntPtr ace;
            if (!GetAce(dacl, (uint)i, out ace)) continue;
            byte type = Marshal.ReadByte(ace, 0), aceFlags = Marshal.ReadByte(ace, 1);
            if (type != (deny ? 1 : 0) || (aceFlags & 0x8) != 0) continue;   // entries that apply to this object
            IntPtr sid = new IntPtr(ace.ToInt64() + 8);
            if (EqualSid(sid, containerSid) || (!deny && EqualSid(sid, allPackagesSid)))
                have |= (uint)Marshal.ReadInt32(ace, 4);
        }
        if ((have & 0x10000000) != 0) return true;              // GENERIC_ALL
        return (have & want) == want;
    }

    static void Quote(StringBuilder sb, string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) { sb.Append(a); return; }
        sb.Append('"');
        int bs = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', bs); sb.Append(ch); }
            bs = 0;
        }
        sb.Append('\\', bs * 2);
        sb.Append('"');
    }

    static Exception Win32(string what)
    {
        return new Exception(what + ": " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    // ---------------- interop ----------------
    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct EXPLICIT_ACCESS
    {
        public uint grfAccessPermissions; public int grfAccessMode; public uint grfInheritance;
        public IntPtr pMultipleTrustee; public int MultipleTrusteeOperation; public int TrusteeForm; public int TrusteeType; public IntPtr ptstrName;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public ulong io0, io1, io2, io3, io4, io5;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int CreateAppContainerProfile(string name, string display, string desc, IntPtr caps, uint capCount, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr str);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSidW(string str, out IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint GetNamedSecurityInfoW(string name, int type, uint info, IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr sd);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint SetNamedSecurityInfoW(string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint SetEntriesInAclW(uint count, ref EXPLICIT_ACCESS ea, IntPtr oldAcl, out IntPtr newAcl);
    [DllImport("advapi32.dll")] static extern bool GetSecurityDescriptorControl(IntPtr sd, out ushort control, out uint revision);
    [DllImport("advapi32.dll")] static extern bool InitializeSecurityDescriptor(IntPtr sd, uint revision);
    [DllImport("advapi32.dll")] static extern bool SetSecurityDescriptorDacl(IntPtr sd, bool present, IntPtr dacl, bool defaulted);
    [DllImport("advapi32.dll")] static extern bool SetSecurityDescriptorControl(IntPtr sd, ushort mask, ushort bits);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetFileSecurityW(string path, uint info, IntPtr sd);
    [DllImport("advapi32.dll")] static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("advapi32.dll")] static extern bool EqualSid(IntPtr a, IntPtr b);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
}
