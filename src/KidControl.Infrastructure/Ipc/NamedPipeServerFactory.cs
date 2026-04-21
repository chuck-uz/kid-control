using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace KidControl.Infrastructure.Ipc;

/// <summary>
/// Creates named pipe server handles with a DACL that allows authenticated users (UI in user session)
/// to connect to pipes owned by LocalSystem. The portable <see cref="NamedPipeServerStream"/> overloads
/// that take <see cref="PipeSecurity"/> are not available in this project's reference assemblies.
/// </summary>
[SupportedOSPlatform("windows10.0")]
internal static class NamedPipeServerFactory
{
    private const uint SddlRevision1 = 1;
    private const uint PipeAccessOutbound = 0x0000_0002;
    private const uint PipeAccessDuplex = 0x0000_0003;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint PipeTypeByte = 0x0000_0004;
    private const uint PipeWait = 0x0000_0000;
    private const uint PipeUnlimitedInstances = 255;

    /// <summary>SY full control, AU read/write (connect + transfer).</summary>
    private const string DefaultSddl = "D:(A;;GA;;;SY)(A;;GRGW;;;AU)";

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    public static NamedPipeServerStream CreateOutbound(string pipeName, PipeOptions options)
    {
        var handle = CreatePipeHandle(pipeName, PipeAccessOutbound | FileFlagOverlapped, options);
        return CreateStream(PipeDirection.Out, handle);
    }

    public static NamedPipeServerStream CreateDuplex(string pipeName, PipeOptions options)
    {
        var handle = CreatePipeHandle(pipeName, PipeAccessDuplex | FileFlagOverlapped, options);
        return CreateStream(PipeDirection.InOut, handle);
    }

    private static SafePipeHandle CreatePipeHandle(string pipeName, uint openMode, PipeOptions options)
    {
        _ = options;

        var fullName = pipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? pipeName
            : $@"\\.\pipe\{pipeName}";

        var securityDescriptor = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = 0
            };

            if (ConvertStringSecurityDescriptorToSecurityDescriptor(DefaultSddl, SddlRevision1, out securityDescriptor, out _))
            {
                sa.lpSecurityDescriptor = securityDescriptor;
            }

            var h = CreateNamedPipeW(
                fullName,
                openMode,
                PipeTypeByte | PipeWait,
                PipeUnlimitedInstances,
                64 * 1024,
                64 * 1024,
                0,
                ref sa);

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                throw new IOException("CreateNamedPipeW failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return new SafePipeHandle(h, ownsHandle: true);
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                LocalFree(securityDescriptor);
            }
        }
    }

    private static NamedPipeServerStream CreateStream(PipeDirection direction, SafePipeHandle handle)
    {
        // (direction, isAsync, isConnected, safePipeHandle)
        return new NamedPipeServerStream(direction, isAsync: true, isConnected: false, handle);
    }
}
