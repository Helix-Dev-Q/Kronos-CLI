using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace KronosCLI;

internal static class Injector
{
    public static void Inject(IntPtr process, string dllPath)
    {
        byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
        IntPtr remote = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length,
            Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
        if (remote == IntPtr.Zero) throw new Win32Exception();

        try
        {
            if (!Native.WriteProcessMemory(process, remote, path, (UIntPtr)path.Length, out _))
                throw new Win32Exception();

            IntPtr loadLibrary = Native.GetProcAddress(Native.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            IntPtr thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero) throw new Win32Exception();

            Native.WaitForSingleObject(thread, 15000);
            Native.GetExitCodeThread(thread, out uint moduleLow);
            Native.CloseHandle(thread);
            if (moduleLow == 0)
                throw new Exception("LoadLibraryW returned NULL in the target (bad path or missing dependency)");
        }
        finally
        {
            Native.VirtualFreeEx(process, remote, UIntPtr.Zero, Native.MEM_RELEASE);
        }
    }
}

internal static class Native
{
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;
    public const int INTERNET_OPTION_REFRESH = 37, INTERNET_OPTION_SETTINGS_CHANGED = 39, INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
    public const int INTERNET_PER_CONN_FLAGS = 1, INTERNET_PER_CONN_PROXY_SERVER = 2, INTERNET_PER_CONN_PROXY_BYPASS = 3,
        INTERNET_PER_CONN_AUTOCONFIG_URL = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount, dwOptionError;
        public IntPtr pOptions;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INTERNET_PER_CONN_OPTION
    {
        [FieldOffset(0)] public int dwOption;
        [FieldOffset(8)] public int dwValue;
        [FieldOffset(8)] public IntPtr pszValue;
    }

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    public static extern bool InternetSetOption(IntPtr h, int option, ref INTERNET_PER_CONN_OPTION_LIST list, int size);

    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    public static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int size);

    public const uint CREATE_SUSPENDED = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param,
        uint flags, out uint tid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeThread(IntPtr h, out uint code);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
}

internal static class Console
{
    const int STD_INPUT_HANDLE = -10;
    const int STD_OUTPUT_HANDLE = -11;
    const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    const uint ENABLE_WINDOW_INPUT = 0x0008;
    const uint ENABLE_MOUSE_INPUT = 0x0010;
    const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    public const ushort KeyEvent = 0x0001;
    public const ushort MouseEvent = 0x0002;

    static uint? originalInputMode;

    [StructLayout(LayoutKind.Explicit)]
    public struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyRecord Key;
        [FieldOffset(4)] public MouseRecord Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKey;
        public ushort ScanCode;
        public ushort Char;
        public uint ControlKeys;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseRecord
    {
        public short X;
        public short Y;
        public uint Buttons;
        public uint ControlKeys;
        public uint Flags;
    }

    public static bool TryEnableAnsi()
    {
        try
        {
            IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out uint mode)) return false;
            return SetConsoleMode(handle, mode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { return false; }
    }

    public static bool TryEnableMouse()
    {
        try
        {
            IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out uint mode)) return false;

            uint wanted = (mode | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_EXTENDED_FLAGS)
                & ~(ENABLE_QUICK_EDIT_MODE | ENABLE_VIRTUAL_TERMINAL_INPUT);
            if (!SetConsoleMode(handle, wanted)) return false;

            originalInputMode ??= mode;
            return true;
        }
        catch { return false; }
    }

    public static void RestoreInput()
    {
        if (originalInputMode is not { } mode) return;
        try { SetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), mode | ENABLE_EXTENDED_FLAGS); } catch { }
        originalInputMode = null;
    }

    public static int ReadInput(InputRecord[] buffer)
    {
        try
        {
            return ReadConsoleInputW(GetStdHandle(STD_INPUT_HANDLE), buffer, (uint)buffer.Length, out uint read)
                ? (int)read
                : -1;
        }
        catch { return -1; }
    }

    public static int PendingInput()
    {
        try { return GetNumberOfConsoleInputEvents(GetStdHandle(STD_INPUT_HANDLE), out uint count) ? (int)count : 0; }
        catch { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadConsoleInputW(IntPtr handle, [Out] InputRecord[] buffer, uint length, out uint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);
}

internal static class Dialogs
{
    const uint FOS_PICKFOLDERS = 0x20;
    const uint FOS_FORCEFILESYSTEM = 0x40;
    const uint FOS_PATHMUSTEXIST = 0x800;
    const uint FOS_FILEMUSTEXIST = 0x1000;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    public static string? PickFolder(string title, string? start) => Show(title, start, true, null, null);

    public static string? PickFile(string title, string filterName, string filterSpec, string? start) =>
        Show(title, start, false, filterName, filterSpec);

    static string? Show(string title, string? start, bool folders, string? filterName, string? filterSpec)
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try { result = ShowDialog(title, start, folders, filterName, filterSpec); }
            catch (Exception e) { Log.Detail($"Browse dialog failed: {e.Message}"); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    static string? ShowDialog(string title, string? start, bool folders, string? filterName, string? filterSpec)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | (folders ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST));
            dialog.SetTitle(title);
            if (!folders && filterName != null && filterSpec != null)
                dialog.SetFileTypes(1, [new FilterSpec { Name = filterName, Spec = filterSpec }]);
            if (StartFolder(start) is { } folder && SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out var item) == 0)
                dialog.SetFolder(item);

            if (dialog.Show(GetConsoleWindow()) != 0) return null;
            dialog.GetResult(out var chosen);
            chosen.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    static string? StartFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) return full;
            string? parent = Path.GetDirectoryName(full);
            return parent != null && Directory.Exists(parent) ? parent : null;
        }
        catch { return null; }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialog { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
}

internal static class ProcessScope
{
    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const uint ERROR_INSUFFICIENT_BUFFER = 122;
    const uint MIB_TCP_STATE_TIME_WAIT = 11;

    public static string? OwnerName(IPEndPoint client, IPEndPoint proxy) =>
        OwnerOf(client, proxy) is { } pid ? NameOf(pid) : null;

    public static bool IsFortnite(string? processName) =>
        processName != null &&
        (processName.StartsWith("FortniteClient-", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("FortniteLauncher", StringComparison.OrdinalIgnoreCase));

    public static int? ParentOf(int pid)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                if (entry.th32ProcessID == pid) return (int)entry.th32ParentProcessID;
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    static string? NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    static int? OwnerOf(IPEndPoint client, IPEndPoint proxy)
    {
        if (client.AddressFamily != AddressFamily.InterNetwork || proxy.AddressFamily != AddressFamily.InterNetwork)
            return null;

        foreach (var row in Read())
        {
            if (row.OwningPid == 0 || row.State == MIB_TCP_STATE_TIME_WAIT) continue;
            if (Port(row.LocalPort) != client.Port || Port(row.RemotePort) != proxy.Port) continue;
            if (!new IPAddress(row.LocalAddr).Equals(client.Address) || !new IPAddress(row.RemoteAddr).Equals(proxy.Address)) continue;
            return row.OwningPid;
        }
        return null;
    }

    static MIB_TCPROW_OWNER_PID[] Read()
    {
        int size = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue;
                if (result != 0) return [];

                int count = Marshal.ReadInt32(buffer);
                var rows = new MIB_TCPROW_OWNER_PID[count];
                int stride = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                    rows[i] = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buffer + 4 + i * stride);
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    static int Port(uint port) => (int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
