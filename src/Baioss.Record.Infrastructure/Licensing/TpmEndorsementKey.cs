using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>
/// Lee la clave pública de endoso (EK) del chip TPM 2.0 del equipo, DIRECTAMENTE del chip.
///
/// <para><b>Por qué del chip y no de Windows.</b> Windows guarda una copia de la EK en el registro
/// (<c>HKLM\SYSTEM\CurrentControlSet\Services\TPM\WMI\Endorsement\EkPub</c>), es decir, en el disco: una copia que
/// un clon del disco se lleva puesta. Para que la huella no se pueda clonar, la EK se pide al propio chip con el
/// comando <c>TPM2_ReadPublic</c> sobre su identificador persistente estándar (0x81010001, donde Windows la deja al
/// aprovisionar el TPM), enviado por TBS (el servicio base de TPM de Windows). Funciona sin administrador y tarda
/// alrededor de un milisegundo (medido en el prototipo: mismo valor que reporta el proveedor de plataforma).</para>
///
/// <para><b>Fallos.</b> Nunca lanza. «No hay TPM» y «el chip no tiene la EK en su sitio» son definitivos: la huella
/// cae al nivel clásico. Un fallo que puede ser pasajero (el servicio TBS aún arrancando justo tras encender el
/// equipo) se reintenta unos segundos: decidir mal el nivel dejaría la licencia sin validar toda la sesión. No hay
/// vía de escape «si falla, se da por bueno»: quien clonara un disco podría provocar el fallo a propósito.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class TpmEndorsementKey
{
    /// <summary>Identificador persistente estándar de la EK RSA (perfil de credenciales de EK del TCG).</summary>
    public const uint PersistentHandle = 0x81010001;

    /// <summary>Resultado de la lectura: el área pública de la EK (o null) y por qué, en palabras, para el registro.</summary>
    public readonly record struct ReadResult(byte[]? PublicArea, string Detail);

    private const uint TbsSuccess = 0;
    private const uint TbsTpmNotFound = 0x8028400F;
    private const uint TbsServiceDisabled = 0x80284010;
    private const uint TbsAccessDenied = 0x80284012;

    /// <summary>Esperas entre intentos ante un fallo que puede ser pasajero (≈ 3,75 s en total).</summary>
    internal static readonly int[] RetryDelaysMs = { 250, 500, 1000, 2000 };

    /// <summary>Lee la EK del chip, con reintentos ante fallos pasajeros. <paramref name="sleep"/> existe para las pruebas.</summary>
    public static ReadResult TryRead(Action<int>? sleep = null) => TryRead(ReadOnce, sleep ?? Thread.Sleep);

    /// <summary>El bucle de reintentos sobre UN intento de lectura cualquiera: el real (<see cref="ReadOnce"/>) o, en las
    /// pruebas, uno simulado, para demostrar qué fallos se reintentan y cuáles no sin depender del chip de la máquina.</summary>
    internal static ReadResult TryRead(Func<(ReadResult Result, bool Definitive)> readOnce, Action<int> sleep)
    {
        ReadResult last = default;
        for (int attempt = 0; ; attempt++)
        {
            bool definitive;
            try { (last, definitive) = readOnce(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return new ReadResult(null, "este Windows no tiene el servicio base de TPM (tbs.dll)");
            }
            catch (Exception ex)
            {
                (last, definitive) = (new ReadResult(null, $"fallo inesperado al hablar con el TPM ({ex.GetType().Name})"), false);
            }
            if (last.PublicArea is not null || definitive || attempt >= RetryDelaysMs.Length) return last;
            sleep(RetryDelaysMs[attempt]);
        }
    }

    private static (ReadResult Result, bool Definitive) ReadOnce()
    {
        var parameters = new TbsContextParams2 { Version = 2, Flags = 0x4 /* includeTpm20 */ };
        uint rc = Tbsi_Context_Create(ref parameters, out var context);
        if (rc != TbsSuccess)
        {
            return rc switch
            {
                TbsTpmNotFound => (new ReadResult(null, "el equipo no tiene un TPM 2.0 activo"), true),
                TbsServiceDisabled => (new ReadResult(null, "el servicio base de TPM de Windows está deshabilitado"), true),
                TbsAccessDenied => (new ReadResult(null, "Windows denegó el acceso al TPM"), true),
                _ => (new ReadResult(null, $"el servicio base de TPM no respondió (0x{rc:X8})"), false),
            };
        }
        try
        {
            // TPM2_ReadPublic(objectHandle): etiqueta sin sesiones, tamaño 14, código de comando 0x173, identificador.
            Span<byte> command = stackalloc byte[14];
            BinaryPrimitives.WriteUInt16BigEndian(command, 0x8001);
            BinaryPrimitives.WriteUInt32BigEndian(command[2..], 14);
            BinaryPrimitives.WriteUInt32BigEndian(command[6..], 0x0000_0173);
            BinaryPrimitives.WriteUInt32BigEndian(command[10..], PersistentHandle);
            var response = new byte[2048];
            uint length = (uint)response.Length;
            rc = Tbsip_Submit_Command(context, 0 /* localidad 0 */, 200 /* prioridad normal */, command.ToArray(), (uint)command.Length, response, ref length);
            if (rc != TbsSuccess) return (new ReadResult(null, $"el TPM no aceptó la consulta (0x{rc:X8})"), false);

            var area = ParseReadPublicResponse(response.AsSpan(0, (int)Math.Min(length, (uint)response.Length)), out string detail);
            // Una respuesta del propio chip (aunque sea «ese identificador no existe») es definitiva: no va a cambiar reintentando.
            return (new ReadResult(area, detail), true);
        }
        finally { Tbsip_Context_Close(context); }
    }

    /// <summary>
    /// Extrae el área pública (TPMT_PUBLIC) de la respuesta a <c>TPM2_ReadPublic</c>; null si la respuesta es un error
    /// del chip o no tiene la forma esperada. Pura, para probarla sin TPM.
    /// </summary>
    internal static byte[]? ParseReadPublicResponse(ReadOnlySpan<byte> response, out string detail)
    {
        // Cabecera: etiqueta (2) ‖ tamaño (4) ‖ código de respuesta (4). Luego TPM2B_PUBLIC: tamaño (2) ‖ área pública.
        if (response.Length < 10) { detail = "respuesta del TPM demasiado corta"; return null; }
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(response[2..]);
        uint code = BinaryPrimitives.ReadUInt32BigEndian(response[6..]);
        if (code != 0)
        {
            detail = $"el TPM no tiene la clave de endoso en su identificador estándar (respuesta 0x{code:X})";
            return null;
        }
        if (declared != response.Length || response.Length < 12) { detail = "respuesta del TPM con un tamaño incoherente"; return null; }
        int size = BinaryPrimitives.ReadUInt16BigEndian(response[10..]);
        // Un área pública real mide cientos de bytes (RSA-2048: 314; ECC P-256: ~122); menos de 32 no es una clave.
        if (size < 32 || 12 + size > response.Length) { detail = "respuesta del TPM sin un área pública válida"; return null; }
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(response[12..]);
        if (type is not (0x0001 /* RSA */ or 0x0023 /* ECC */)) { detail = $"clave de endoso de un tipo desconocido (0x{type:X4})"; return null; }
        detail = type == 0x0001 ? "clave de endoso RSA leída del chip" : "clave de endoso ECC leída del chip";
        return response.Slice(12, size).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TbsContextParams2
    {
        public uint Version;
        public uint Flags;
    }

    // El nombre exportado va EXPLÍCITO: la build de venta se ofusca (se renombran los métodos) y la función nativa debe
    // seguir encontrándose. Si no se encontrara, la huella caería al nivel clásico en silencio.
    [DllImport("tbs.dll", EntryPoint = "Tbsi_Context_Create", ExactSpelling = true)]
    private static extern uint Tbsi_Context_Create(ref TbsContextParams2 contextParams, out IntPtr context);
    [DllImport("tbs.dll", EntryPoint = "Tbsip_Context_Close", ExactSpelling = true)]
    private static extern uint Tbsip_Context_Close(IntPtr context);
    [DllImport("tbs.dll", EntryPoint = "Tbsip_Submit_Command", ExactSpelling = true)]
    private static extern uint Tbsip_Submit_Command(IntPtr context, uint locality, uint priority,
        byte[] command, uint commandLength, byte[] result, ref uint resultLength);
}
