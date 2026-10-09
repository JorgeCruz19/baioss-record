using System.Security.Cryptography;
using Baioss.Record.Application.Licensing;

// Herramienta del PROVEEDOR para emitir licencias de Baioss Record.
//
//   baioss-license keygen
//       Genera un par de claves NUEVO. La PRIVADA se guarda a buen recaudo (con ella se emiten licencias);
//       la PÚBLICA se pega en la app (src/Baioss.Record.Application/Licensing/LicensePublicKey.cs).
//       Solo se hace UNA VEZ: si se pierde la privada, las licencias ya emitidas dejan de poder renovarse
//       y hay que publicar una build con una pública nueva.
//
//   baioss-license issue --machine <CÓDIGO-DE-EQUIPO> [--channels <1-4>] --private <CLAVE-PRIVADA-BASE64>
//       Emite una licencia PERPETUA para ese equipo con los canales PAGADOS (sin --channels: 4). El código
//       de equipo lo da la app del cliente (ventana de Licencia). La licencia solo funcionará en ESE PC y
//       los canales viajan FIRMADOS dentro de la clave: el cliente no puede subírselos.
//       El código dice además si el equipo está anclado a su chip TPM (una copia del disco no valida la
//       licencia) o no; sin TPM la herramienta avisa antes de entregar la licencia. Los códigos son los de
//       la huella v2: los que mostraban las builds anteriores a 2026-10 ya no sirven.
//
//   baioss-license integrity sign --dir <CARPETA-PUBLICADA> --private <CLAVE-PRIVADA-BASE64>
//       Genera el manifiesto de integridad (baioss-record.integrity): el SHA-256 de los ensamblados sensibles de
//       esa carpeta, FIRMADO con la privada. Es el ÚLTIMO paso de la publicación, sobre los bytes finales (después
//       de ofuscar y, si se firman digitalmente los binarios, después de firmarlos). Verifica el manifiesto al
//       instante con la clave pública de ESTA build y no deja nada si no cuadra. La app lo recalcula al arrancar:
//       una DLL parcheada se detecta y la licencia deja de aplicarse (ver docs/LICENCIAMIENTO.md).
//
//   baioss-license integrity verify --dir <CARPETA-PUBLICADA>
//       Comprueba la carpeta exactamente como lo hará la app. Código de salida: 0 intacta, 2 MODIFICADA,
//       3 sin verificar (no hay manifiesto, firma de otra clave, archivo ilegible…).

// La consola de Windows usa la codepage del sistema: sin esto, los acentos de los mensajes salen como signos
// raros (el mismo problema de codificación que ya se corrigió al leer la salida de FFmpeg).
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* consola redirigida: da igual */ }

if (args.Length == 0) { PrintUsage(); return 1; }

switch (args[0].ToLowerInvariant())
{
    case "keygen": return KeyGen();
    case "issue": return Issue(args);
    case "integrity": return Integrity(args);
    default: PrintUsage(); return 1;
}

static int KeyGen()
{
    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string priv = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
    string pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

    Console.WriteLine();
    Console.WriteLine("=== CLAVE PRIVADA (guárdala; NO la distribuyas ni la subas al repositorio) ===");
    Console.WriteLine(priv);
    Console.WriteLine();
    Console.WriteLine("=== CLAVE PÚBLICA (pégala en src/Baioss.Record.Application/Licensing/LicensePublicKey.cs) ===");
    Console.WriteLine(pub);
    Console.WriteLine();
    Console.WriteLine("Con la privada emites licencias:  baioss-license issue --machine <CÓDIGO> --private <PRIVADA>");
    return 0;
}

static int Issue(string[] args)
{
    string? machine = ValueOf(args, "--machine");
    string? priv = ValueOf(args, "--private");
    if (machine is null || priv is null)
    {
        Console.Error.WriteLine("Faltan argumentos: --machine <CÓDIGO-DE-EQUIPO> [--channels <1-4>] --private <CLAVE-PRIVADA-BASE64>");
        return 1;
    }

    // El código de equipo que ve el cliente es la huella en Base32 Crockford (tolerante a guiones/minúsculas).
    // Decodificación ESTRICTA con la longitud exacta de la huella: con la variante leniente, un código con un
    // carácter de menos (dictado por teléfono, copiado a medias) producía una «huella» de otra longitud y la
    // herramienta firmaba SIN ERROR una licencia que en el equipo del cliente jamás validaría («de otro equipo»).
    if (!Base32Crockford.TryDecodeExact(machine, MachineFingerprintComposer.Length, out var fingerprint))
    {
        Console.Error.WriteLine($"El código de equipo no es válido: deben ser {MachineFingerprintComposer.Length * 8 / 5} caracteres " +
                                "(4 grupos de 4). Revisa que esté copiado completo, sin caracteres de más ni de menos.");
        return 1;
    }

    // La huella de «un equipo sin ningún identificador» es una CONSTANTE conocida (la misma en todos los equipos
    // en ese estado). Emitir para ella sería vender una licencia universal: se rechaza y se pide revisar el equipo.
    if (MachineFingerprintComposer.IsGeneric(fingerprint))
    {
        Console.Error.WriteLine("Ese código corresponde a un equipo SIN identificadores utilizables (huella genérica): una licencia");
        Console.Error.WriteLine("emitida para él funcionaría en cualquier equipo en ese estado. Revisa el equipo del cliente antes de emitir.");
        return 1;
    }

    using var ecdsa = ECDsa.Create();
    try { ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(priv), out _); }
    catch (Exception ex) when (ex is FormatException or CryptographicException)
    {
        Console.Error.WriteLine("La clave privada no es válida.");
        return 1;
    }

    // Canales PAGADOS (viajan firmados dentro de la clave; el precio depende de ellos). Sin --channels: 4.
    int channels = 4;
    if (ValueOf(args, "--channels") is { } channelsText &&
        (!int.TryParse(channelsText, out channels) || channels is < 1 or > 4))
    {
        Console.Error.WriteLine("--channels debe ser un número de 1 a 4 (los canales que el cliente pagó).");
        return 1;
    }

    var issuedOn = DateOnly.FromDateTime(DateTime.UtcNow);
    var message = LicenseKey.BuildSignedMessage(fingerprint, LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, (byte)channels);
    var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    string key = LicenseKey.Encode(LicenseKey.CurrentVersion, LicenseType.Lifetime, issuedOn, (byte)channels, signature);

    // El código dice a qué está atado el equipo (bit de nivel de la huella): al chip TPM, que una copia del disco no se
    // lleva, o a identificadores clásicos, que en un PC sin números de serie reales se clonan con el disco.
    bool tpmAnchored = MachineFingerprintComposer.IsTpmAnchored(fingerprint);
    Console.WriteLine();
    Console.WriteLine($"Equipo   : {Base32Crockford.Group(Base32Crockford.Encode(fingerprint), 4)}");
    Console.WriteLine($"Ancla    : {(tpmAnchored ? "chip TPM del equipo (una copia del disco NO valida esta licencia)" : "identificadores clásicos, SIN TPM")}");
    Console.WriteLine($"Emitida  : {issuedOn:yyyy-MM-dd}  ·  Tipo: perpetua  ·  Canales: {channels}");
    if (!tpmAnchored)
    {
        Console.WriteLine();
        Console.WriteLine("AVISO: este código es de un equipo SIN chip TPM 2.0 activo. Si además el equipo no tiene números de serie");
        Console.WriteLine("reales (PC de integrador), esta licencia valdría también en una copia de su disco. Antes de entregarla puedes");
        Console.WriteLine("pedir al cliente que active el TPM en la BIOS (Intel PTT / AMD fTPM) y te envíe el código nuevo que le");
        Console.WriteLine("mostrará la aplicación: una licencia emitida para ESTE código dejará de valer si el TPM se activa después.");
    }
    Console.WriteLine();
    Console.WriteLine("=== LICENCIA (envíala al cliente; solo funcionará en ese equipo) ===");
    Console.WriteLine(key);
    Console.WriteLine();
    return 0;
}

static int Integrity(string[] args)
{
    string? sub = args.Length > 1 ? args[1].ToLowerInvariant() : null;
    string? dir = ValueOf(args, "--dir");
    if (sub is not ("sign" or "verify") || dir is null)
    {
        Console.Error.WriteLine("Uso: integrity sign --dir <CARPETA-PUBLICADA> --private <CLAVE-PRIVADA-BASE64>");
        Console.Error.WriteLine("     integrity verify --dir <CARPETA-PUBLICADA>");
        return 1;
    }
    dir = Path.GetFullPath(dir);
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"La carpeta «{dir}» no existe.");
        return 1;
    }
    string manifestPath = Path.Combine(dir, IntegrityManifest.FileName);
    return sub == "sign" ? IntegritySign(args, dir, manifestPath) : IntegrityVerify(dir, manifestPath);
}

/// <summary>SHA-256 hex de un archivo de la carpeta publicada; null si no existe; lanza si no se puede leer.</summary>
static string? HashFileIn(string dir, string name)
{
    string path = Path.Combine(dir, name);
    if (!File.Exists(path)) return null;
    using var stream = File.OpenRead(path);
    return IntegrityManifest.HashStream(stream);
}

static int IntegritySign(string[] args, string dir, string manifestPath)
{
    string? priv = ValueOf(args, "--private");
    if (priv is null)
    {
        Console.Error.WriteLine("Falta --private <CLAVE-PRIVADA-BASE64> (la misma con la que se emiten las licencias).");
        return 1;
    }
    using var ecdsa = ECDsa.Create();
    try { ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(priv), out _); }
    catch (Exception ex) when (ex is FormatException or CryptographicException)
    {
        Console.Error.WriteLine("La clave privada no es válida.");
        return 1;
    }

    // TODOS los archivos sensibles deben estar: un manifiesto parcial dejaría sin cubrir justo lo que importa, y
    // que falte alguno significa que la carpeta no es una publicación completa.
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    Console.WriteLine($"Carpeta: {dir}");
    foreach (string name in IntegrityManifest.SensitiveFiles)
    {
        string? hex;
        try { hex = HashFileIn(dir, name); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo leer «{name}»: {ex.Message}");
            return 1;
        }
        if (hex is null)
        {
            Console.Error.WriteLine($"Falta «{name}» en la carpeta: ¿es la carpeta publicada (scripts\\publish.ps1)?");
            return 1;
        }
        hashes[name] = hex;
        Console.WriteLine($"  {hex}  {name}");
    }

    string blob = IntegrityManifest.Sign(hashes, ecdsa, DateTimeOffset.UtcNow);
    File.WriteAllText(manifestPath, blob); // UTF-8 sin BOM, una línea

    // Verificación INMEDIATA con la clave pública compilada en esta build: si la privada no es su pareja, el
    // manifiesto quedaría «sin verificar» EN SILENCIO en casa del cliente (la app no bloquea nada, pero tampoco
    // protege). Mejor fallar aquí, ruidosamente, y no dejar nada.
    var report = IntegrityManifest.Verify(File.ReadAllText(manifestPath), LicensePublicKey.ProviderPublicKeyBase64, n => HashFileIn(dir, n));
    if (report.Verdict != IntegrityVerdict.Intact)
    {
        try { File.Delete(manifestPath); } catch { /* best-effort */ }
        Console.Error.WriteLine($"El manifiesto recién firmado NO verifica con la clave pública de esta build ({report.Detail}).");
        Console.Error.WriteLine("¿Es la clave privada correcta (la pareja de LicensePublicKey.cs)? No se deja ningún manifiesto.");
        return 1;
    }
    Console.WriteLine();
    Console.WriteLine($"Manifiesto firmado y verificado: {manifestPath}");
    return 0;
}

static int IntegrityVerify(string dir, string manifestPath)
{
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"No hay manifiesto en «{manifestPath}»: la app arrancaría con la integridad SIN VERIFICAR");
        Console.Error.WriteLine("(no bloquea nada, pero tampoco detectaría una DLL parcheada). Genéralo con «integrity sign».");
        return 3;
    }
    var report = IntegrityManifest.Verify(File.ReadAllText(manifestPath), LicensePublicKey.ProviderPublicKeyBase64, n => HashFileIn(dir, n));
    Console.WriteLine($"Carpeta: {dir}");
    foreach (var f in report.Files)
    {
        string label = f.Status switch
        {
            IntegrityFileStatus.Match => "coincide     ",
            IntegrityFileStatus.Mismatch => "NO COINCIDE  ",
            IntegrityFileStatus.Missing => "no existe    ",
            _ => "no se pudo leer",
        };
        Console.WriteLine($"  [{label}] {f.File}");
    }
    Console.WriteLine();
    switch (report.Verdict)
    {
        case IntegrityVerdict.Intact:
            Console.WriteLine($"INTACTA: {report.Detail}.");
            return 0;
        case IntegrityVerdict.Tampered:
            Console.Error.WriteLine($"MODIFICADA: {report.Detail}. La app NO aplicará la licencia en esta carpeta.");
            Console.Error.WriteLine("Si acabas de reemplazar DLL a mano (o de ofuscar), vuelve a firmar el manifiesto con «integrity sign».");
            return 2;
        default:
            Console.Error.WriteLine($"SIN VERIFICAR: {report.Detail}.");
            Console.Error.WriteLine("La app no bloquearía nada, pero el manifiesto no protege así: revisa la firma/clave y vuelve a firmarlo.");
            return 3;
    }
}

static string? ValueOf(string[] args, string name)
{
    int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void PrintUsage()
{
    Console.WriteLine("Emisor de licencias de Baioss Record");
    Console.WriteLine();
    Console.WriteLine("  baioss-license keygen");
    Console.WriteLine("      Genera el par de claves del proveedor (una sola vez).");
    Console.WriteLine();
    Console.WriteLine("  baioss-license issue --machine <CÓDIGO-DE-EQUIPO> [--channels <1-4>] --private <CLAVE-PRIVADA-BASE64>");
    Console.WriteLine("      Emite una licencia perpetua para ese equipo con los canales PAGADOS (sin --channels: 4).");
    Console.WriteLine();
    Console.WriteLine("  baioss-license integrity sign --dir <CARPETA-PUBLICADA> --private <CLAVE-PRIVADA-BASE64>");
    Console.WriteLine("      Genera el manifiesto de integridad firmado (último paso de la publicación, sobre los bytes finales).");
    Console.WriteLine();
    Console.WriteLine("  baioss-license integrity verify --dir <CARPETA-PUBLICADA>");
    Console.WriteLine("      Comprueba la carpeta como lo hará la app (0 intacta, 2 modificada, 3 sin verificar).");
}
