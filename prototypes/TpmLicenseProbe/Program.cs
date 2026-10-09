// Prototipo: ¿se puede atar la licencia de Baioss Record al TPM del equipo, sin conexión, y a qué coste?
//
// Prueba TRES estrategias en este equipo y mide cada una, sin elevación (como corre la aplicación):
//   A. IDENTIDAD DEL CHIP: leer la clave pública de endoso (EK) y la raíz de almacenamiento (SRK) del TPM a través del
//      proveedor de plataforma de Windows. La EK la graba el fabricante en el chip: no está en el disco, un clon no se
//      la lleva, sobrevive a reinstalar Windows y a borrar el TPM. Su hash serviría como componente de la huella.
//      También el UUID de sistema del SMBIOS (firmware, no disco) como respaldo para equipos sin TPM.
//   B. CLAVE DE USUARIO en el TPM: crear una clave ECDSA P-256 NO exportable, abrirla por nombre, firmar retos
//      (prueba de posesión), verificar con la pública, y que la privada no salga. Tiempos.
//   C. CLAVE DE MÁQUINA en el TPM: ¿hace falta elevación para crearla? ¿Puede un proceso SIN elevación abrirla y firmar?
//      (--machine-create se ejecuta elevado y deja su salida en machine-create.log; --machine-use se ejecuta normal.)
//   Además: el flujo de licencia completo (firma del proveedor + reto al TPM) con rechazo de licencias ajenas o
//   manipuladas, un proceso nuevo que abre y firma (la app al arrancar) y dónde queda el contenedor en disco.
//
// Uso: TpmLicenseProbe [--keep] · --sign-only <nombre> <machine|user> · --machine-create · --machine-use · --cleanup
#pragma warning disable CA1416 // el prototipo es solo Windows (TFM net8.0-windows)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

const string UserKeyName = "BaiossRecord.Prototipo.TPM.Usuario";
const string MachineKeyName = "BaiossRecord.Prototipo.TPM.Maquina";
var pcp = new CngProvider("Microsoft Platform Crypto Provider");
string here = AppContext.BaseDirectory;

if (args.Length >= 3 && args[0] == "--sign-only") return SignOnly(args[1], args[2] == "machine");
if (args.Length >= 1 && args[0] == "--machine-create") return MachineCreate();
if (args.Length >= 1 && args[0] == "--machine-use") return MachineUse();
if (args.Length >= 1 && args[0] == "--cleanup") { var report = Cleanup(); Console.Write(report); File.WriteAllText(Path.Combine(here, "cleanup.log"), report); return 0; }
if (args.Length >= 1 && args[0] == "--tbs") return TbsProbe();
bool keep = args.Contains("--keep");

Console.OutputEncoding = Encoding.UTF8;
var started = DateTime.Now;
Console.WriteLine($"Prototipo TPM · {started:yyyy-MM-dd HH:mm:ss} · usuario {Environment.UserName} · elevado: {IsElevated()} · 64 bits: {Environment.Is64BitProcess}");

// ---------------------------------------------------------------- A. Identidad del chip
Console.WriteLine();
Console.WriteLine("A) Identidad del chip a través del proveedor de plataforma (sin crear claves)");
var swProv = Stopwatch.StartNew();
int hr = Native.NCryptOpenStorageProvider(out var hProv, "Microsoft Platform Crypto Provider", 0);
Console.WriteLine($"   abrir el proveedor: {(hr == 0 ? "OK" : $"FALLA 0x{hr:X8}")} en {swProv.Elapsed.TotalMilliseconds:0.0} ms");
if (hr != 0) { Console.WriteLine("   sin proveedor de plataforma: este equipo no tiene TPM utilizable."); return 2; }
try
{
    foreach (var prop in new[] { "PCP_PLATFORM_TYPE", "PCP_PROVIDER_VERSION", "PCP_EKPUB", "PCP_SRKPUB", "PCP_EKCERT" })
    {
        var sw = Stopwatch.StartNew();
        var (code, bytes) = Native.GetProperty(hProv, prop);
        double ms = sw.Elapsed.TotalMilliseconds;
        if (code != 0) { Console.WriteLine($"   {prop}: FALLA 0x{code:X8} ({ms:0.0} ms)"); continue; }
        string shown = prop is "PCP_PLATFORM_TYPE" or "PCP_PROVIDER_VERSION"
            ? "«" + Encoding.Unicode.GetString(bytes).TrimEnd('\0') + "»"
            : $"{bytes.Length} bytes, SHA-256[..10] = {Hash10(bytes)}";
        Console.WriteLine($"   {prop}: {shown} ({ms:0.0} ms)");
    }
}
finally { Native.NCryptFreeObject(hProv); }

Console.WriteLine();
Console.WriteLine("   SMBIOS (firmware, no disco) leído con GetSystemFirmwareTable, sin WMI:");
try
{
    var sw = Stopwatch.StartNew();
    var smbios = Smbios.Read();
    Console.WriteLine($"   fabricante «{smbios.Manufacturer}», producto «{smbios.Product}», serial de equipo «{smbios.SystemSerial}», serial de placa «{smbios.BoardSerial}»");
    Console.WriteLine($"   UUID de sistema: {smbios.Uuid} ({(smbios.UuidIsPlaceholder ? "RELLENO: no sirve" : "real")}) · leído en {sw.Elapsed.TotalMilliseconds:0.0} ms");
}
catch (Exception ex) { Console.WriteLine($"   SMBIOS no legible: {ex.Message}"); }

// ---------------------------------------------------------------- B. Clave de usuario
Console.WriteLine();
Console.WriteLine("B) Clave ECDSA P-256 NO exportable en el TPM, ámbito de USUARIO");
try
{
    var (exists, exMs) = Time(() => CngKey.Exists(UserKeyName, pcp, CngKeyOpenOptions.UserKey));
    Console.WriteLine($"   consulta de existencia: {exMs:0.0} ms ({(exists ? "ya existía" : "no existía")})");
}
catch (CryptographicException ex) { Console.WriteLine($"   consulta de existencia FALLA: {Describe(ex)}"); return 2; }

var (created, createMs) = Time(() => Create(UserKeyName, machine: false));
Console.WriteLine($"   creada en {createMs:0.0} ms · {created.Algorithm.Algorithm}, {created.KeySize} bits, exportable: {created.ExportPolicy}, uniqueName: {created.UniqueName}");
created.Dispose();

var (opened, openMs) = Time(() => CngKey.Open(UserKeyName, pcp, CngKeyOpenOptions.UserKey));
Console.WriteLine($"   abierta por nombre en {openMs:0.0} ms (lo que haría cada arranque de la aplicación)");
byte[] publicBlob;
string tpmCode;
using (opened)
{
    (publicBlob, var expMs) = Time(() => opened.Export(CngKeyBlobFormat.EccPublicBlob));
    tpmCode = Hash10(publicBlob);
    Console.WriteLine($"   pública exportada en {expMs:0.0} ms ({publicBlob.Length} bytes) → código: {tpmCode}");
    try { opened.Export(CngKeyBlobFormat.EccPrivateBlob); Console.WriteLine("   ¡¡la PRIVADA SE EXPORTÓ!!: esto invalidaría el diseño."); }
    catch (CryptographicException ex) { Console.WriteLine($"   la privada NO se exporta (bien): {Describe(ex)}"); }

    using var signer = new ECDsaCng(opened);
    using var verifier = ECDsa.Create(signer.ExportParameters(includePrivateParameters: false));
    var signTimes = new List<double>(); var verifyTimes = new List<double>();
    for (int i = 0; i < 20; i++)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var (sig, sMs) = Time(() => signer.SignData(nonce, HashAlgorithmName.SHA256));
        var (ok, vMs) = Time(() => verifier.VerifyData(nonce, sig, HashAlgorithmName.SHA256));
        if (!ok) { Console.WriteLine("   ¡una firma del TPM NO verificó con su pública!"); return 3; }
        signTimes.Add(sMs); verifyTimes.Add(vMs);
    }
    Console.WriteLine($"   20 retos firmados por el TPM: p50 {P50(signTimes):0.0} ms, máx {signTimes.Max():0.0} ms, primera {signTimes[0]:0.0} ms · verificación p50 {P50(verifyTimes):0.00} ms");

    // Flujo de licencia completo.
    Console.WriteLine();
    Console.WriteLine("   Licencia firmada por el proveedor y atada al código del TPM:");
    using var vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256); // la clave PRIVADA del proveedor (en producción, la de LicenseTool)
    using var vendorPublic = ECDsa.Create(vendor.ExportParameters(false));
    string license = $"BAIOSS-RECORD-LICENSE-TPM-PROTO|channels=4|tpm={tpmCode}";
    var licenseSig = vendor.SignData(Encoding.UTF8.GetBytes(license), HashAlgorithmName.SHA256);
    var (verdict, totalMs) = Time(() => CheckLicense(license, licenseSig, vendorPublic, signer, publicBlob));
    Console.WriteLine($"   · de ESTE equipo: {verdict} ({totalMs:0.0} ms: firma del proveedor + reto al TPM)");
    string foreign = $"BAIOSS-RECORD-LICENSE-TPM-PROTO|channels=4|tpm={Hash10(RandomNumberGenerator.GetBytes(104))}";
    Console.WriteLine($"   · válida pero de OTRO equipo: {CheckLicense(foreign, vendor.SignData(Encoding.UTF8.GetBytes(foreign), HashAlgorithmName.SHA256), vendorPublic, signer, publicBlob)}");
    var tampered = (byte[])licenseSig.Clone(); tampered[5] ^= 0x55;
    Console.WriteLine($"   · firma del proveedor manipulada: {CheckLicense(license, tampered, vendorPublic, signer, publicBlob)}");
    Console.WriteLine($"   · más canales sin volver a firmar: {CheckLicense(license.Replace("channels=4", "channels=8"), licenseSig, vendorPublic, signer, publicBlob)}");
}

Console.WriteLine();
Console.WriteLine("   Un proceso NUEVO abre la clave de usuario por nombre y firma (como la app al arrancar):");
RunChild($"--sign-only {UserKeyName} user");

// ---------------------------------------------------------------- C. Clave de máquina
Console.WriteLine();
Console.WriteLine("C) Clave de MÁQUINA en el TPM (compartida por todos los usuarios de Windows del equipo)");
try
{
    var (exists, ms) = Time(() => CngKey.Exists(MachineKeyName, pcp, CngKeyOpenOptions.MachineKey));
    Console.WriteLine($"   consultar si existe, sin elevación: OK en {ms:0.0} ms ({(exists ? "existe" : "no existe")})");
}
catch (CryptographicException ex) { Console.WriteLine($"   consultar si existe, sin elevación: FALLA → {Describe(ex)}"); }
try
{
    var (k, ms) = Time(() => Create(MachineKeyName, machine: true));
    Console.WriteLine($"   crear sin elevación: OK en {ms:0.0} ms (no hace falta administrador)");
    k.Dispose();
}
catch (CryptographicException ex) { Console.WriteLine($"   crear sin elevación: FALLA → {Describe(ex)}"); }
string log = Path.Combine(here, "machine-create.log");
if (File.Exists(log))
{
    Console.WriteLine("   resultado de --machine-create ejecutado ELEVADO (machine-create.log):");
    foreach (var line in File.ReadAllLines(log)) Console.WriteLine("     " + line);
    Console.WriteLine("   y ahora, SIN elevación, abrir esa clave de máquina y firmar:");
    RunChild("--machine-use");
}
else Console.WriteLine("   (para probar la creación elevada: ejecutar «--machine-create» como administrador y volver a lanzar el prototipo)");

// ---------------------------------------------------------------- Contenedor en disco
Console.WriteLine();
Console.WriteLine("Contenedor de la clave de usuario en disco (lo que un clon del disco se llevaría):");
string pcpUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Crypto", "PCPKSP");
if (Directory.Exists(pcpUser))
{
    var recent = Directory.EnumerateFiles(pcpUser, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f))
        .Where(f => f.LastWriteTime >= started.AddSeconds(-2)).ToList();
    foreach (var f in recent) Console.WriteLine($"   {f.FullName} · {f.Length} bytes");
    if (recent.Count == 0) Console.WriteLine($"   (ningún archivo nuevo bajo {pcpUser})");
}
else Console.WriteLine($"   {pcpUser}: no existe");
Console.WriteLine("   Es la clave ENVUELTA por este TPM (no exportable): otro chip no puede abrirla. La prueba cruzada en otro equipo no se puede hacer desde aquí.");

Console.WriteLine();
if (keep) Console.WriteLine("--keep: las claves del prototipo se conservan (bórralas con --cleanup).");
else Console.Write(Cleanup());
return 0;

// ------------------------------------------------------------------------------------------------ funciones

CngKey Create(string name, bool machine) => CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
{
    Provider = pcp,
    KeyCreationOptions = (machine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None) | CngKeyCreationOptions.OverwriteExistingKey,
    ExportPolicy = CngExportPolicies.None, // no exportable: la privada no sale del chip
    KeyUsage = CngKeyUsages.Signing,
});

// Verificación en el cliente: (a) la licencia la firmó el proveedor; (b) habla de ESTE TPM; (c) este TPM demuestra que tiene
// la privada firmando un reto fresco. Sin (c), un clon con la misma licencia y el mismo hash pasaría (a) y (b).
static string CheckLicense(string license, byte[] licenseSig, ECDsa vendorPublic, ECDsaCng tpm, byte[] publicBlob)
{
    if (!vendorPublic.VerifyData(Encoding.UTF8.GetBytes(license), licenseSig, HashAlgorithmName.SHA256)) return "RECHAZADA (firma del proveedor inválida)";
    if (!string.Equals(license.Split("tpm=")[1], Hash10(publicBlob), StringComparison.Ordinal)) return "RECHAZADA (es de otro equipo)";
    var nonce = RandomNumberGenerator.GetBytes(32);
    byte[] proof;
    try { proof = tpm.SignData(nonce, HashAlgorithmName.SHA256); }
    catch (CryptographicException ex) { return $"RECHAZADA (el TPM no pudo firmar: {Describe(ex)})"; }
    using var pub = ECDsa.Create(tpm.ExportParameters(false));
    return pub.VerifyData(nonce, proof, HashAlgorithmName.SHA256) ? "VÁLIDA" : "RECHAZADA (la prueba de posesión no verificó)";
}

int SignOnly(string name, bool machine)
{
    try
    {
        var sw = Stopwatch.StartNew();
        using var k = CngKey.Open(name, pcp, machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.UserKey);
        using var s = new ECDsaCng(k);
        var sig = s.SignData(RandomNumberGenerator.GetBytes(32), HashAlgorithmName.SHA256);
        Console.WriteLine($"OK: abrir + firmar en {sw.Elapsed.TotalMilliseconds:0.0} ms, firma de {sig.Length} bytes");
        return 0;
    }
    catch (CryptographicException ex) { Console.WriteLine("FALLO: " + Describe(ex)); return 1; }
}

// Ejecutado ELEVADO (lo que haría el instalador, que corre como administrador): crea DOS claves de máquina, una con los
// permisos por defecto y otra concediendo lectura/uso a «Usuarios» (SDDL: GR a BU), para ver cuál puede usar después la
// aplicación sin elevación.
int MachineCreate()
{
    var lines = new List<string> { $"{DateTime.Now:HH:mm:ss} elevado: {IsElevated()}" };
    foreach (var (name, grantUsers) in new[] { (MachineKeyName, false), (MachineKeyName + ".Usuarios", true) })
    {
        try
        {
            var (k, ms) = Time(() => Create(name, machine: true));
            lines.Add($"«{name}»: creada en {ms:0.0} ms (exportable: {k.ExportPolicy})");
            if (grantUsers)
            {
                var sd = new System.Security.AccessControl.RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;BU)");
                var bytes = new byte[sd.BinaryLength];
                sd.GetBinaryForm(bytes, 0);
                const int DaclSecurityInformation = 0x4;
                var (_, aclMs) = Time(() => { k.SetProperty(new CngProperty("Security Descr", bytes, CngPropertyOptions.Persist | (CngPropertyOptions)DaclSecurityInformation)); return 0; });
                lines.Add($"   permisos: SYSTEM y Administradores todo, Usuarios lectura/uso · aplicados en {aclMs:0.0} ms");
            }
            using var s = new ECDsaCng(k);
            var sig = s.SignData(RandomNumberGenerator.GetBytes(32), HashAlgorithmName.SHA256);
            lines.Add($"   firma de prueba elevada OK ({sig.Length} bytes)");
            k.Dispose();
        }
        catch (CryptographicException ex) { lines.Add($"«{name}»: FALLO: {Describe(ex)}"); }
    }
    File.WriteAllLines(Path.Combine(here, "machine-create.log"), lines);
    return 0;
}

// Ejecutado SIN elevación: ¿puede la aplicación abrir y firmar con cada clave de máquina?
int MachineUse()
{
    int worst = 0;
    foreach (var name in new[] { MachineKeyName, MachineKeyName + ".Usuarios" })
    {
        Console.Write($"«{name}» → ");
        worst = Math.Max(worst, SignOnly(name, machine: true));
    }
    return worst;
}

void RunChild(string arguments)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    var sw = Stopwatch.StartNew();
    using var child = Process.Start(psi)!;
    string output = child.StandardOutput.ReadToEnd().Trim();
    child.WaitForExit();
    Console.WriteLine($"   proceso hijo: «{output}» (código {child.ExitCode}; proceso entero {sw.Elapsed.TotalMilliseconds:0} ms incluido el arranque del runtime)");
}

// Borra las claves del prototipo y cuenta qué pasó con cada una (las de máquina solo se pueden borrar con elevación).
string Cleanup()
{
    var sb = new StringBuilder().AppendLine($"limpieza {DateTime.Now:HH:mm:ss} · elevado: {IsElevated()}");
    foreach (var (name, opt) in new[] { (UserKeyName, CngKeyOpenOptions.UserKey), (MachineKeyName, CngKeyOpenOptions.MachineKey), (MachineKeyName + ".Usuarios", CngKeyOpenOptions.MachineKey) })
    {
        try
        {
            if (!CngKey.Exists(name, pcp, opt)) { sb.AppendLine($"   «{name}»: no existe"); continue; }
            using var k = CngKey.Open(name, pcp, opt);
            k.Delete();
            sb.AppendLine($"   «{name}»: borrada");
        }
        catch (CryptographicException ex) { sb.AppendLine($"   «{name}»: {Describe(ex)}"); }
    }
    return sb.ToString();
}

// Lee la clave de endoso DIRECTAMENTE del chip (TBS + TPM2_ReadPublic sobre el identificador persistente estándar de la EK,
// 0x81010001), sin pasar por el proveedor de plataforma, y la compara con lo que dice el proveedor. También mira si Windows
// guarda una copia de la EK en el registro (una copia en disco viajaría con un clon).
int TbsProbe()
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine($"TBS directo · elevado: {IsElevated()}");
    var p = new TbsContextParams2 { Version = 2, Flags = 0x4 /* includeTpm20 */ };
    var sw = Stopwatch.StartNew();
    uint r = Native.Tbsi_Context_Create(ref p, out var ctx);
    Console.WriteLine($"   Tbsi_Context_Create: 0x{r:X8} en {sw.Elapsed.TotalMilliseconds:0.0} ms");
    if (r != 0) return 2;
    try
    {
        // Identificadores persistentes que hay en el chip (TPM2_GetCapability, TPM_CAP_HANDLES desde 0x81000000).
        byte[] getCap = { 0x80, 0x01, 0, 0, 0, 0x16, 0, 0, 0x01, 0x7A, 0, 0, 0, 0x01, 0x81, 0, 0, 0, 0, 0, 0, 0x20 };
        var (rc1, resp1, ms1) = Submit(ctx, getCap);
        if (rc1 == 0 && resp1.Length >= 19)
        {
            int count = (int)BEU32(resp1, 15);
            var handles = Enumerable.Range(0, count).Select(i => $"0x{BEU32(resp1, 19 + 4 * i):X8}");
            Console.WriteLine($"   identificadores persistentes ({ms1:0.0} ms): {string.Join(", ", handles)}");
        }
        else Console.WriteLine($"   GetCapability: TBS 0x{rc1:X8}, {resp1.Length} bytes");

        foreach (uint handle in new uint[] { 0x81010001, 0x81000001 })
        {
            byte[] readPublic = { 0x80, 0x01, 0, 0, 0, 0x0E, 0, 0, 0x01, 0x73, (byte)(handle >> 24), (byte)(handle >> 16), (byte)(handle >> 8), (byte)handle };
            var times = new List<double>();
            byte[] resp = Array.Empty<byte>(); uint rc = 0;
            for (int i = 0; i < 10; i++) { (rc, resp, var ms) = Submit(ctx, readPublic); times.Add(ms); }
            if (rc != 0 || resp.Length < 12) { Console.WriteLine($"   ReadPublic(0x{handle:X8}): TBS 0x{rc:X8}, {resp.Length} bytes"); continue; }
            uint tpmRc = BEU32(resp, 6);
            if (tpmRc != 0) { Console.WriteLine($"   ReadPublic(0x{handle:X8}): el TPM respondió 0x{tpmRc:X8} (no existe ese identificador)"); continue; }
            int size = (resp[10] << 8) | resp[11];
            var area = resp.AsSpan(12, size).ToArray();
            int type = (area[0] << 8) | area[1];
            Console.WriteLine($"   ReadPublic(0x{handle:X8}): área pública de {size} bytes, tipo 0x{type:X4} ({(type == 1 ? "RSA" : type == 0x23 ? "ECC" : "?")}), SHA-256[..10] = {Hash10(area)} · p50 {P50(times):0.0} ms, máx {times.Max():0.0} ms");
            if (handle == 0x81010001 && type == 1 && size >= 256)
            {
                var modulusFromChip = area[^256..];
                Native.NCryptOpenStorageProvider(out var hProv2, "Microsoft Platform Crypto Provider", 0);
                var (c, blob) = Native.GetProperty(hProv2, "PCP_EKPUB");
                Native.NCryptFreeObject(hProv2);
                bool same = c == 0 && blob.Length >= 256 && blob.AsSpan(blob.Length - 256).SequenceEqual(modulusFromChip);
                Console.WriteLine($"   ¿el módulo RSA leído del chip coincide con el que da el proveedor (PCP_EKPUB)? {(same ? "SÍ" : "NO")}");
            }
        }
    }
    finally { Native.Tbsip_Context_Close(ctx); }

    try
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TPM\WMI\Endorsement");
        if (key is null) Console.WriteLine("   registro …/TPM/WMI/Endorsement: no existe");
        else
        {
            Console.WriteLine($"   registro …/TPM/WMI/Endorsement: valores [{string.Join(", ", key.GetValueNames().Select(n => $"{n}: {(key.GetValue(n) as byte[])?.Length ?? 0} bytes"))}], subclaves [{string.Join(", ", key.GetSubKeyNames())}]");
            Console.WriteLine("   (una copia de la EK en el registro vive en el disco: un clon se la llevaría; por eso el producto debe leer del chip)");
        }
    }
    catch (Exception ex) { Console.WriteLine($"   registro …/TPM/WMI/Endorsement: {ex.Message}"); }
    return 0;

    static (uint Rc, byte[] Response, double Ms) Submit(IntPtr ctx, byte[] command)
    {
        var buffer = new byte[4096];
        uint len = (uint)buffer.Length;
        var sw = Stopwatch.StartNew();
        uint rc = Native.Tbsip_Submit_Command(ctx, 0, 200, command, (uint)command.Length, buffer, ref len);
        return (rc, rc == 0 ? buffer[..(int)len] : Array.Empty<byte>(), sw.Elapsed.TotalMilliseconds);
    }
    static uint BEU32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
}

static bool IsElevated()
{
    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

static string Hash10(byte[] data) => Convert.ToHexString(SHA256.HashData(data)[..10]);

static (T Result, double Ms) Time<T>(Func<T> f)
{
    var sw = Stopwatch.StartNew();
    var r = f();
    return (r, sw.Elapsed.TotalMilliseconds);
}

static double P50(List<double> xs) { var s = xs.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

static string Describe(CryptographicException ex) => $"{ex.Message.Trim()} (HRESULT 0x{ex.HResult:X8})";

static class Native
{
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] public static extern int NCryptOpenStorageProvider(out IntPtr phProvider, string pszProviderName, uint dwFlags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] static extern int NCryptGetProperty(IntPtr hObject, string pszProperty, byte[]? pbOutput, uint cbOutput, out uint pcbResult, uint dwFlags);
    [DllImport("ncrypt.dll")] public static extern int NCryptFreeObject(IntPtr hObject);
    [DllImport("kernel32.dll")] public static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
    [DllImport("tbs.dll")] public static extern uint Tbsi_Context_Create(ref TbsContextParams2 contextParams, out IntPtr context);
    [DllImport("tbs.dll")] public static extern uint Tbsip_Context_Close(IntPtr context);
    [DllImport("tbs.dll")] public static extern uint Tbsip_Submit_Command(IntPtr context, uint locality, uint priority, byte[] command, uint commandLength, byte[] result, ref uint resultLength);

    public static (int Code, byte[] Bytes) GetProperty(IntPtr handle, string name)
    {
        int hr = NCryptGetProperty(handle, name, null, 0, out uint size, 0);
        if (hr != 0) return (hr, Array.Empty<byte>());
        var buffer = new byte[size];
        hr = NCryptGetProperty(handle, name, buffer, size, out size, 0);
        return (hr, hr == 0 ? buffer[..(int)size] : Array.Empty<byte>());
    }
}

/// <summary>Tabla SMBIOS cruda (GetSystemFirmwareTable 'RSMB'): tipo 1 (sistema: fabricante, producto, serial, UUID) y tipo 2 (placa: serial).</summary>
sealed record Smbios(string Manufacturer, string Product, string SystemSerial, string BoardSerial, string Uuid, bool UuidIsPlaceholder)
{
    public static Smbios Read()
    {
        const uint RSMB = 0x52534D42;
        uint size = Native.GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) throw new InvalidOperationException("GetSystemFirmwareTable devolvió 0 bytes");
        var raw = new byte[size];
        Native.GetSystemFirmwareTable(RSMB, 0, raw, size);
        var table = raw.AsSpan(8); // cabecera: método, versión mayor/menor, revisión DMI, longitud
        string manufacturer = "", product = "", systemSerial = "", boardSerial = "", uuid = ""; bool placeholder = true;
        int pos = 0;
        while (pos + 4 <= table.Length)
        {
            byte type = table[pos]; byte length = table[pos + 1];
            if (length < 4) break;
            var formatted = table.Slice(pos, length);
            int strings = pos + length;
            var list = new List<string>();
            int p = strings;
            while (p < table.Length)
            {
                int end = p; while (end < table.Length && table[end] != 0) end++;
                if (end == p) { p++; break; } // doble nulo: fin de la estructura
                list.Add(Encoding.ASCII.GetString(table.Slice(p, end - p))); p = end + 1;
                if (p < table.Length && table[p] == 0) { p++; break; }
            }
            string S(int index) => index >= 1 && index <= list.Count ? list[index - 1].Trim() : "";
            if (type == 1 && length >= 0x18)
            {
                manufacturer = S(formatted[4]); product = S(formatted[5]); systemSerial = S(formatted[7]);
                var u = formatted.Slice(8, 16);
                placeholder = u.ToArray().All(b => b == 0) || u.ToArray().All(b => b == 0xFF);
                uuid = new Guid(u).ToString().ToUpperInvariant(); // mismo orden de bytes que lo muestra Windows
            }
            else if (type == 2 && length >= 8) boardSerial = S(formatted[7]);
            else if (type == 127) break;
            pos = p;
        }
        return new Smbios(manufacturer, product, systemSerial, boardSerial, uuid, placeholder);
    }
}

[StructLayout(LayoutKind.Sequential)]
struct TbsContextParams2 { public uint Version; public uint Flags; }
