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

// La consola de Windows usa la codepage del sistema: sin esto, los acentos de los mensajes salen como signos
// raros (el mismo problema de codificación que ya se corrigió al leer la salida de FFmpeg).
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* consola redirigida: da igual */ }

if (args.Length == 0) { PrintUsage(); return 1; }

switch (args[0].ToLowerInvariant())
{
    case "keygen": return KeyGen();
    case "issue": return Issue(args);
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
    if (fingerprint.AsSpan().SequenceEqual(MachineFingerprintComposer.Compose(null, null, null, null)))
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

    Console.WriteLine();
    Console.WriteLine($"Equipo   : {Base32Crockford.Group(Base32Crockford.Encode(fingerprint), 4)}");
    Console.WriteLine($"Emitida  : {issuedOn:yyyy-MM-dd}  ·  Tipo: perpetua  ·  Canales: {channels}");
    Console.WriteLine();
    Console.WriteLine("=== LICENCIA (envíala al cliente; solo funcionará en ese equipo) ===");
    Console.WriteLine(key);
    Console.WriteLine();
    return 0;
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
}
