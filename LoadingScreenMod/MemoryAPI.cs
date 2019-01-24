using System;
using System.Runtime.InteropServices;

namespace LoadingScreenMod
{
    /// <summary>
    /// I want to display the amount of memory used by the Cities process. Unfortunately, there is no working cross-platform way
    /// to get that information in Unity/Mono. I have tried:
    /// - The properties in Process (PrivateMemorySize64 etc.): These work fine in MS .NET but return 0 in Mono.
    /// - PerformanceCounters: Like above.
    /// - GC.GetTotalMemory: This works but is useless (managed memory only), the results are 10-30% of the true values.
    /// The solution in this class works but is not portable as it accesses WINAPI directly.
    /// </summary>
    public static class MemoryAPI
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);

        [StructLayout(LayoutKind.Sequential, Size = 72)]
        struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;                          // The size of the structure, in bytes (DWORD).
            public uint PageFaultCount;              // The number of page faults (DWORD).
            public ulong PeakWorkingSetSize;         // The peak working set size, in bytes (SIZE_T).
            public ulong WorkingSetSize;             // The current working set size, in bytes (SIZE_T).
            public ulong QuotaPeakPagedPoolUsage;    // The peak paged pool usage, in bytes (SIZE_T).
            public ulong QuotaPagedPoolUsage;        // The current paged pool usage, in bytes (SIZE_T).
            public ulong QuotaPeakNonPagedPoolUsage; // The peak nonpaged pool usage, in bytes (SIZE_T).
            public ulong QuotaNonPagedPoolUsage;     // The current nonpaged pool usage, in bytes (SIZE_T).
            public ulong PagefileUsage;              // The Commit Charge value in bytes for this process (SIZE_T). Commit Charge is the total amount of memory that the memory manager has committed for a running process.
            public ulong PeakPagefileUsage;          // The peak value in bytes of the Commit Charge during the lifetime of this process (SIZE_T).
        }

        static IntPtr handle;
        internal static int pfMax, wsMax;

        /// <summary>
        /// Returns the number of megabytes used by the current process.
        /// </summary>
        public static void GetUsage(out int pfMegas, out int wsMegas)
        {
            if (handle == IntPtr.Zero)
                handle = GetCurrentProcess();

            PROCESS_MEMORY_COUNTERS mem;
            mem.cb = (uint) Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
            GetProcessMemoryInfo(handle, out mem, mem.cb);

            pfMegas = (int) (mem.PagefileUsage >> 20);
            wsMegas = (int) (mem.WorkingSetSize >> 20);
            pfMax = Math.Max(pfMax, pfMegas);
            wsMax = Math.Max(wsMax, wsMegas);
        }
    }
}
