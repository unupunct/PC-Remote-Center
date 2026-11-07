using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteControlServer.Terminal;

/// <summary>
/// Thin wrapper around the Windows Pseudo Console (ConPTY) API: one pseudo console plus one attached
/// child process. Output bytes are delivered via <see cref="OutputReceived"/> on a reader thread.
/// </summary>
public sealed class ConPty : IDisposable
{
    private IntPtr _hPc;
    private SafeFileHandle? _inWrite, _outRead, _inRead, _outWrite;
    private IntPtr _hProcess, _hThread;
    private Thread? _readThread;
    private int _disposed;
    private readonly object _writeLock = new();

    public int ProcessId { get; private set; }
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public event Action<byte[], int>? OutputReceived;
    public event Action<int>? Exited;

    public static ConPty Start(string commandLine, string? workingDirectory, int cols, int rows,
        Action<byte[], int>? onOutput = null, Action<int>? onExit = null)
    {
        var pty = new ConPty();
        // handlers must be attached before the reader thread starts or early output (and the DSR query) would be lost
        if (onOutput != null) pty.OutputReceived += onOutput;
        if (onExit != null) pty.Exited += onExit;
        try { pty.StartCore(commandLine, workingDirectory, (short)cols, (short)rows); }
        catch { pty.Dispose(); throw; }
        return pty;
    }

    private void StartCore(string commandLine, string? cwd, short cols, short rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) failed");
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) { inRead.Dispose(); inWrite.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) failed"); }
        _inWrite = inWrite; _outRead = outRead;

        int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, PSEUDOCONSOLE_INHERIT_CURSOR, out _hPc);
        _inRead = inRead; _outWrite = outWrite; // kept open until Dispose (closing them early made cmd see EOF on some builds)
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed (ConPTY needs Windows 10 1809 or newer)");

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            // A parent that disabled Ctrl+C (SetConsoleCtrlHandler(NULL,TRUE), e.g. a launcher/test host) passes that
            // flag to every child; shells hosted here must always honour Ctrl+C.
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            // STARTUPINFOEXW built by hand (112 bytes on x64): cb at 0, lpAttributeList at 104
            const int siSize = 112;
            var si = Marshal.AllocHGlobal(siSize);
            try
            {
                for (int i = 0; i < siSize; i += 8) Marshal.WriteInt64(si, i, 0);
                Marshal.WriteInt32(si, 0, siSize);
                // Same as Windows Terminal / node-pty: explicit NULL std handles so the child can never inherit
                // the parent's (possibly redirected) stdio instead of the pseudo console.
                Marshal.WriteInt32(si, 60, STARTF_USESTDHANDLES);
                Marshal.WriteIntPtr(si, 104, attrList);
                var cmd = new StringBuilder(commandLine);
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        IntPtr.Zero, cwd, si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{commandLine}'");
                _hProcess = pi.hProcess; _hThread = pi.hThread; ProcessId = pi.dwProcessId;
            }
            finally { Marshal.FreeHGlobal(si); }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY reader" };
        _readThread.Start();
        var waiter = new Thread(WaitForExit) { IsBackground = true, Name = "ConPTY exit waiter" };
        waiter.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                if (!ReadFile(_outRead!, buf, buf.Length, out int n, IntPtr.Zero) || n <= 0)
                {
                    RemoteControlServer.Services.AppLog.Info("ConPTY reader loop ended", new { n, err = Marshal.GetLastWin32Error() });
                    break;
                }
                OutputReceived?.Invoke(buf, n);
            }
        }
        catch (Exception ex) { RemoteControlServer.Services.AppLog.Warn("ConPTY reader ended: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private void WaitForExit()
    {
        var wr = WaitForSingleObject(_hProcess, 0xFFFFFFFF);
        if (Volatile.Read(ref _disposed) != 0) return;
        bool ok = GetExitCodeProcess(_hProcess, out var code);
        RemoteControlServer.Services.AppLog.Info("ConPTY process wait finished", new { waitResult = wr, gotCode = ok, code, pid = ProcessId, hProc = _hProcess.ToInt64() });
        ExitCode = (int)code;
        HasExited = true;
        // give the reader a moment to drain the final output before announcing the exit
        Thread.Sleep(150);
        Exited?.Invoke((int)code);
    }

    public void Write(string text)
    {
        if (HasExited || _inWrite == null || _inWrite.IsClosed) throw new InvalidOperationException("The shell has exited.");
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            int off = 0;
            while (off < bytes.Length)
            {
                var chunk = new byte[bytes.Length - off];
                Array.Copy(bytes, off, chunk, 0, chunk.Length);
                if (!WriteFile(_inWrite, chunk, chunk.Length, out int written, IntPtr.Zero))
                    throw new IOException("Writing to the pseudo console failed.", new Win32Exception(Marshal.GetLastWin32Error()));
                off += written;
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_hPc == IntPtr.Zero || HasExited) return;
        int hr = ResizePseudoConsole(_hPc, new COORD { X = (short)cols, Y = (short)rows });
        if (hr != 0) throw new Win32Exception(hr, "ResizePseudoConsole failed");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (_hProcess != IntPtr.Zero && !HasExited) TerminateProcess(_hProcess, 1); } catch { }
        var pc = _hPc; _hPc = IntPtr.Zero;
        // ClosePseudoConsole can block until output is drained on older builds - run it off-thread while the reader keeps draining
        if (pc != IntPtr.Zero) Task.Run(() => { try { ClosePseudoConsole(pc); } catch { } });
        _inWrite?.Dispose();
        Task.Run(async () =>
        {
            await Task.Delay(500);
            _outRead?.Dispose(); _inRead?.Dispose(); _outWrite?.Dispose();
            if (_hProcess != IntPtr.Zero) CloseHandle(_hProcess);
            if (_hThread != IntPtr.Zero) CloseHandle(_hThread);
        });
    }

    // ------------------------------------------------------------------ native

    /// <summary>ConPTY asks the host for the cursor position (ESC[6n) and waits for the reply; <see cref="VtScreen"/> answers it.</summary>
    private const uint PSEUDOCONSOLE_INHERIT_CURSOR = 1;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    // conpty.dll + OpenConsole.exe (Microsoft.Windows.Console.ConPTY) sit next to the application. If they are
    // missing the inbox kernel32 implementation is used instead.
    private static readonly bool UseBundled = File.Exists(Path.Combine(AppContext.BaseDirectory, "conpty.dll"))
                                              && File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenConsole.exe"));

    private static int CreatePseudoConsole(COORD size, SafeFileHandle i, SafeFileHandle o, uint f, out IntPtr h) =>
        UseBundled ? B_Create(size, i, o, f, out h) : K_Create(size, i, o, f, out h);
    private static int ResizePseudoConsole(IntPtr h, COORD size) => UseBundled ? B_Resize(h, size) : K_Resize(h, size);
    private static void ClosePseudoConsole(IntPtr h) { if (UseBundled) B_Close(h); else K_Close(h); }

    [DllImport("conpty.dll", EntryPoint = "CreatePseudoConsole")]
    private static extern int B_Create(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("conpty.dll", EntryPoint = "ResizePseudoConsole")] private static extern int B_Resize(IntPtr hPC, COORD size);
    [DllImport("conpty.dll", EntryPoint = "ClosePseudoConsole")] private static extern void B_Close(IntPtr hPC);

    [DllImport("kernel32.dll", EntryPoint = "CreatePseudoConsole")]
    private static extern int K_Create(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll", EntryPoint = "ResizePseudoConsole")] private static extern int K_Resize(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll", EntryPoint = "ClosePseudoConsole")] private static extern void K_Close(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, IntPtr lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
}
