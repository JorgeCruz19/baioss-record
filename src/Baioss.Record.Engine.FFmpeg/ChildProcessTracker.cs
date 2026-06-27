using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Asocia cada proceso FFmpeg hijo a un Windows <b>Job Object</b> con
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: cuando el proceso de la app termina por CUALQUIER vía
/// (cierre ordenado, crash, kill del operador en el Administrador de tareas, fin de sesión de Windows),
/// el SO mata automáticamente todos los FFmpeg asociados.
///
/// <para>Sin esto, en Windows no hay relación de vida padre-hijo: un FFmpeg huérfano seguiría grabando,
/// reteniendo el dispositivo de captura exclusivo (DeckLink/DirectShow) o el receptor NDI y dejando el
/// archivo a medias, de modo que la app reiniciada no podría reabrir esa entrada. (Auditoría 24/7, C2.)</para>
///
/// <para>Es una red de seguridad <b>best-effort</b>: si el SO no es Windows o el job no se pudo crear, no
/// hace nada y el comportamiento es el de antes. El job es anónimo: su único handle vive en este proceso,
/// así que al morir el proceso el handle se cierra y se dispara el kill del árbol.</para>
/// </summary>
public static class ChildProcessTracker
{
    private static readonly IntPtr s_job = CreateKillOnCloseJob();

    /// <summary>Asocia un proceso ya arrancado al job. No lanza: ante fallo, deja el proceso como estaba.</summary>
    public static void Track(Process process)
    {
        if (s_job == IntPtr.Zero) return;
        try
        {
            if (!process.HasExited)
                AssignProcessToJobObject(s_job, process.Handle);
        }
        catch { /* best-effort: si falla, el proceso vive como hasta ahora */ }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectInfoClass.ExtendedLimitInformation, ptr, (uint)len))
                    return IntPtr.Zero; // no utilizable → desactiva el tracker
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoClass { ExtendedLimitInformation = 9 }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoClass infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
