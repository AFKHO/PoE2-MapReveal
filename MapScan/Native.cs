using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Poe2Map;

/// <summary>
///     Windows süreç belleği okuma. Sadece okuma - hiçbir yazma yolu yok.
/// </summary>
internal static class Native
{
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmRead = 0x0010;

    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemImage = 0x1000000;
    internal const uint MemMapped = 0x40000;

    internal const uint PageReadWrite = 0x04;
    internal const uint PageReadOnly = 0x02;
    internal const uint PageGuard = 0x100;
    internal const uint PageNoAccess = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        internal IntPtr BaseAddress;
        internal IntPtr AllocationBase;
        internal uint AllocationProtect;
        internal uint PartitionId;
        internal IntPtr RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualQueryEx(
        IntPtr process, IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    /// <summary>
    ///     Oyun sürecini bulur. İsim eşleşmesi bizim kendi tercihimiz: GGG istemcisi
    ///     dağıtıma göre farklı adlar kullanıyor, hepsi "PathOfExile" ile başlıyor.
    /// </summary>
    internal static Process? FindGame()
    {
        Process? best = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Birden fazla aday varsa en çok bellek kullanan gerçek istemcidir;
                // başlatıcı sarmalayıcılar küçük kalır.
                if (best == null || p.WorkingSet64 > best.WorkingSet64)
                {
                    best = p;
                }
            }
            catch
            {
                // erişilemeyen süreçler atlanır
            }
        }

        return best;
    }
}
