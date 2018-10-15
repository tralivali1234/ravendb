using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Raven.Server.Utils
{
    /// <summary>
    /// A utility class to determine a process parent.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ParentProcessUtilities
    {
        // These members must match PROCESS_BASIC_INFORMATION
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ParentProcessUtilities processInformation, int processInformationLength, out int returnLength);

        /// <summary>
        /// Gets the parent process of the current process.
        /// </summary>
        /// <returns>An instance of the Process class.</returns>
        public static Process GetParentProcess()
        {
            using (var process = Process.GetCurrentProcess())
            using (var processHandle = process.SafeHandle)
            {
                return GetParentProcess(processHandle.DangerousGetHandle());
            }
        }

        /// <summary>
        /// Gets the parent process of specified process.
        /// </summary>
        /// <param name="processId">The process id.</param>
        /// <returns>An instance of the Process class.</returns>
        public static Process GetParentProcess(int processId)
        {
            using (var process = Process.GetProcessById(processId))
            using (var processHandle = process.SafeHandle)
            {
                return GetParentProcess(processHandle.DangerousGetHandle());
            }
        }

        /// <summary>
        /// Gets the parent process of a specified process.
        /// </summary>
        /// <param name="handle">The process handle.</param>
        /// <returns>An instance of the Process class or null if an error occurred.</returns>
        private static Process GetParentProcess(IntPtr handle)
        {
            var pbi = new ParentProcessUtilities();
            var status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out int _);
            if (status != 0)
                return null;

            try
            {
                return Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32());
            }
            catch (ArgumentException)
            {
                // not found
                return null;
            }
        }
    }
}
