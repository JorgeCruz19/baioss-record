using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baioss.Record.Application.Licensing;

/// <summary>Veredicto de la autocomprobación de integridad de los binarios del producto.</summary>
public enum IntegrityVerdict
{
    /// <summary>
    /// No se pudo verificar: no hay manifiesto (desarrollo, build portable), su firma no es válida, no se pudo
    /// leer, un archivo no se pudo leer o la comprobación aún está en curso. Es un <b>fallo nuestro o una
    /// ausencia</b>, nunca una prueba de nada: <b>NO cambia el comportamiento</b> (mismo trato que
    /// <see cref="LicenseState.Unknown"/>: en un grabador 24/7, estorbar por una duda propia sería peor que una copia).
    /// </summary>
    Unverified,
    /// <summary>El manifiesto es auténtico y TODOS los archivos que cubre coinciden con él.</summary>
    Intact,
    /// <summary>
    /// El manifiesto es auténtico y al menos un archivo, <b>leído y resumido con éxito</b>, NO coincide con lo que el
    /// proveedor firmó. Es evidencia POSITIVA de que la instalación no es la original (parcheada, mezclada con otra
    /// versión o una actualización a medias). Solo este veredicto tiene consecuencias.
    /// </summary>
    Tampered,
}

/// <summary>Qué pasó con UN archivo del manifiesto al comprobarlo.</summary>
public enum IntegrityFileStatus
{
    /// <summary>Resumido con éxito y coincide con el manifiesto.</summary>
    Match,
    /// <summary>Resumido con éxito y NO coincide: evidencia positiva.</summary>
    Mismatch,
    /// <summary>No existe en el disco: no se puede afirmar nada.</summary>
    Missing,
    /// <summary>Existe pero no se pudo leer (bloqueado por el antivirus, permisos…): no se puede afirmar nada.</summary>
    Unreadable,
}

/// <summary>Resultado de comprobar un archivo del manifiesto.</summary>
public sealed record IntegrityFileReport(string File, IntegrityFileStatus Status);

/// <summary>
/// Resultado de la autocomprobación. <see cref="Detail"/> es una frase para el registro de la aplicación;
/// <see cref="Files"/>, el detalle por archivo (vacío si ni siquiera se llegó a comprobar ninguno).
/// </summary>
public sealed record IntegrityReport(IntegrityVerdict Verdict, string Detail, IReadOnlyList<IntegrityFileReport> Files)
{
    /// <summary>Un veredicto «no verificado» sin detalle por archivo, con el motivo.</summary>
    public static IntegrityReport Unverified(string detail) => new(IntegrityVerdict.Unverified, detail, Array.Empty<IntegrityFileReport>());

    /// <summary>Nombres de los archivos que NO coinciden, para decírselo al operador.</summary>
    public IEnumerable<string> MismatchedFiles => Files.Where(f => f.Status == IntegrityFileStatus.Mismatch).Select(f => f.File);
}

/// <summary>
/// Manifiesto de integridad de los binarios: los resúmenes SHA-256 de los ensamblados con lógica sensible,
/// FIRMADOS por el proveedor. La app los recalcula al arrancar y los compara.
///
/// <para><b>Por qué un archivo firmado junto a los binarios y no constantes compiladas.</b> Los tres ensamblados
/// con la lógica de licencia (Application, Infrastructure y Engine.FFmpeg) se OFUSCAN después de compilar, así
/// que sus bytes definitivos solo existen al final del pipeline de publicación; y ningún ensamblado puede llevar
/// dentro su propio resumen. El manifiesto se genera como ÚLTIMO paso sobre los bytes finales. Va firmado con la
/// misma clave privada del proveedor que las licencias: sin ella no se puede regenerar, así que quien parchea
/// una DLL no puede «arreglar» el manifiesto recomputando los resúmenes.</para>
///
/// <para><b>Separador de dominio.</b> La firma cubre <c>BAIOSS-RECORD-INTEGRITY-v1 ‖ longitud ‖ contenido</c>:
/// una firma de licencia nunca puede reinterpretarse como manifiesto ni al revés, aunque compartan clave.</para>
///
/// <para><b>Formato en disco.</b> Una línea: <c>base64(JSON) ‖ "." ‖ base64(firma r‖s de 64 bytes)</c>, igual que
/// las copias del estado de licencia. Se firman los BYTES EXACTOS del JSON tal como viajan (no se canonicaliza):
/// al verificar se usa lo que hay en el archivo, byte a byte.</para>
///
/// <para><b>Qué NO decide esta clase.</b> Solo calcula el veredicto a partir de un lector de resúmenes que le
/// inyectan (la infraestructura lee archivos; la herramienta del proveedor, también; las pruebas, lo que quieran).
/// Sin E/S propia y SIN excepciones hacia fuera: cualquier entrada rara es <see cref="IntegrityVerdict.Unverified"/>.</para>
/// </summary>
public static class IntegrityManifest
{
    /// <summary>Nombre del manifiesto, junto al ejecutable.</summary>
    public const string FileName = "baioss-record.integrity";

    /// <summary>Versión del formato que emite y entiende esta build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Archivos que cubre el manifiesto: el ejecutable y el ensamblado de la app (composición, no ofuscados) y
    /// los tres ensamblados ofuscados con la lógica de licencia y el motor. FFmpeg NO: lo aporta el cliente.
    /// </summary>
    public static readonly IReadOnlyList<string> SensitiveFiles = new[]
    {
        "Baioss.Record.App.exe",
        "Baioss.Record.App.dll",
        "Baioss.Record.Application.dll",
        "Baioss.Record.Infrastructure.dll",
        "Baioss.Record.Engine.FFmpeg.dll",
    };

    /// <summary>Contenido firmado del manifiesto (JSON). <see cref="Files"/>: nombre de archivo → SHA-256 en hexadecimal.</summary>
    public sealed record Payload(int Version, string CreatedAt, Dictionary<string, string> Files);

    private static readonly byte[] DomainTag = Encoding.ASCII.GetBytes("BAIOSS-RECORD-INTEGRITY-v1");
    private const int SignatureLength = 64; // ECDSA P-256, formato fijo r‖s

    /// <summary>SHA-256 de un flujo, en hexadecimal en minúsculas (el formato que guarda el manifiesto).</summary>
    public static string HashStream(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    /// <summary>Mensaje EXACTO que se firma: separador de dominio ‖ longitud del contenido (4 bytes LE) ‖ contenido.</summary>
    public static byte[] BuildSignedMessage(ReadOnlySpan<byte> payload)
    {
        var msg = new byte[DomainTag.Length + 4 + payload.Length];
        DomainTag.CopyTo(msg, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(DomainTag.Length), (uint)payload.Length);
        payload.CopyTo(msg.AsSpan(DomainTag.Length + 4));
        return msg;
    }

    /// <summary>
    /// Compone y FIRMA un manifiesto (lado del proveedor). <paramref name="hashes"/>: nombre de archivo → SHA-256 hex.
    /// Se niega a firmar un manifiesto vacío o con nombres que no sean nombres de archivo simples: un manifiesto
    /// así no podría verificarse nunca y dejaría la build «sin verificar» en silencio.
    /// </summary>
    public static string Sign(IReadOnlyDictionary<string, string> hashes, ECDsa privateKey, DateTimeOffset createdAt)
    {
        if (hashes.Count == 0) throw new ArgumentException("El manifiesto debe cubrir al menos un archivo.", nameof(hashes));
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, hex) in hashes.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            if (!IsPlainFileName(name)) throw new ArgumentException($"«{name}» no es un nombre de archivo simple.", nameof(hashes));
            if (hex.Length != SHA256.HashSizeInBytes * 2 || !hex.All(Uri.IsHexDigit))
                throw new ArgumentException($"El resumen de «{name}» no es un SHA-256 en hexadecimal.", nameof(hashes));
            files[name] = hex.ToLowerInvariant();
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Payload(CurrentVersion, createdAt.ToString("O"), files));
        byte[] signature = privateKey.SignData(BuildSignedMessage(json), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(json) + "." + Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifica un manifiesto y comprueba los archivos que cubre. <paramref name="hashFile"/> devuelve el SHA-256 hex
    /// del archivo (por nombre, relativo a la carpeta del producto), <c>null</c> si no existe, y LANZA si no se puede
    /// leer; aquí se convierte en <see cref="IntegrityFileStatus.Unreadable"/>.
    /// <para>Reglas del veredicto: <b>Tampered</b> solo con firma válida y al menos un archivo leído con éxito que no
    /// coincide; <b>Intact</b> solo si TODOS coinciden; cualquier otra cosa —formato raro, firma inválida, clave
    /// pública mal configurada, archivo ausente o ilegible— es <b>Unverified</b>, con el motivo en
    /// <see cref="IntegrityReport.Detail"/>. Jamás lanza.</para>
    /// </summary>
    public static IntegrityReport Verify(string? manifestText, string publicKeyBase64, Func<string, string?> hashFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestText)) return IntegrityReport.Unverified("el manifiesto está vacío");
            var text = manifestText.Trim();
            int dot = text.IndexOf('.');
            if (dot <= 0 || dot == text.Length - 1) return IntegrityReport.Unverified("el manifiesto no tiene el formato esperado");

            byte[] payload, signature;
            try
            {
                payload = Convert.FromBase64String(text[..dot]);
                signature = Convert.FromBase64String(text[(dot + 1)..]);
            }
            catch (FormatException) { return IntegrityReport.Unverified("el manifiesto no tiene el formato esperado"); }
            if (signature.Length != SignatureLength) return IntegrityReport.Unverified("la firma del manifiesto no tiene el tamaño esperado");

            // La firma se comprueba ANTES de interpretar el contenido: un JSON que no viene del proveedor no se mira.
            bool authentic;
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
                authentic = ecdsa.VerifyData(BuildSignedMessage(payload), signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return IntegrityReport.Unverified("no se pudo comprobar la firma del manifiesto (clave pública no válida)");
            }
            if (!authentic) return IntegrityReport.Unverified("la firma del manifiesto no es válida (no lo emitió el proveedor de esta build)");

            Payload? content;
            try { content = JsonSerializer.Deserialize<Payload>(payload); }
            catch (JsonException) { return IntegrityReport.Unverified("el contenido del manifiesto no se pudo interpretar"); }
            if (content is null) return IntegrityReport.Unverified("el contenido del manifiesto está vacío");
            if (content.Version != CurrentVersion) return IntegrityReport.Unverified($"versión de manifiesto {content.Version} no soportada (esta build entiende la {CurrentVersion})");
            if (content.Files is null || content.Files.Count == 0) return IntegrityReport.Unverified("el manifiesto no cubre ningún archivo");

            var files = new List<IntegrityFileReport>(content.Files.Count);
            foreach (var (name, expected) in content.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                // Un manifiesto auténtico con un nombre que no es un archivo simple solo puede ser un error del
                // proveedor: no se afirma nada (y nunca se compone una ruta con él).
                if (!IsPlainFileName(name)) return IntegrityReport.Unverified($"el manifiesto contiene un nombre de archivo no válido («{name}»)");

                string? actual;
                try { actual = hashFile(name); }
                catch (Exception) { files.Add(new(name, IntegrityFileStatus.Unreadable)); continue; }
                if (actual is null) { files.Add(new(name, IntegrityFileStatus.Missing)); continue; }
                files.Add(new(name, string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                    ? IntegrityFileStatus.Match
                    : IntegrityFileStatus.Mismatch));
            }

            var mismatched = files.Where(f => f.Status == IntegrityFileStatus.Mismatch).Select(f => f.File).ToList();
            if (mismatched.Count > 0)
                return new IntegrityReport(IntegrityVerdict.Tampered,
                    "no coinciden con el manifiesto firmado por el proveedor: " + string.Join(", ", mismatched), files);
            if (files.All(f => f.Status == IntegrityFileStatus.Match))
                return new IntegrityReport(IntegrityVerdict.Intact,
                    $"los {files.Count} archivos coinciden con el manifiesto firmado por el proveedor", files);
            var pending = files.Where(f => f.Status != IntegrityFileStatus.Match)
                .Select(f => $"{f.File} ({(f.Status == IntegrityFileStatus.Missing ? "no existe" : "no se pudo leer")})");
            return new IntegrityReport(IntegrityVerdict.Unverified, "no se pudo comprobar: " + string.Join(", ", pending), files);
        }
        catch (Exception ex)
        {
            // Cinturón: esta clase alimenta el arranque del subsistema de licencia y NUNCA puede propagar nada.
            return IntegrityReport.Unverified("fallo interno al comprobar la integridad: " + ex.Message);
        }
    }

    /// <summary>Un nombre de archivo sin carpetas ni caracteres inválidos (el manifiesto nunca compone rutas).</summary>
    public static bool IsPlainFileName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name is not ("." or "..")
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal);
}
