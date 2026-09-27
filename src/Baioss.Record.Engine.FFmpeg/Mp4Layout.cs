using System.Buffers.Binary;
using System.Text;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Cómo está dispuesto un MP4/MOV (ISO-BMFF) mirando SOLO las cabeceras de sus cajas de primer nivel (saltos de caja en
/// caja: no lee el contenido, así que cuesta lo mismo en un archivo de 8 GB que en uno de 8 MB):
/// <list type="bullet">
///   <item><b>Fragmentado</b> (fMP4, lo que escribe el grabador en modo robusto): hay cajas <c>moof</c>. Reproducible
///   aunque se corte; sin índice de búsqueda al inicio y sin duración en el Explorador.</item>
///   <item><b>Estándar con el índice al inicio</b> (faststart, lo que deja la finalización): <c>moov</c> antes de
///   <c>mdat</c> y sin <c>moof</c>. Archivo «normal»: duración visible, búsqueda exacta.</item>
///   <item><b>Estándar con el índice al final</b> (moov tras mdat): lo que escribe FFmpeg sin flags; ilegible hasta
///   que se cierra bien.</item>
/// </list>
/// </summary>
public static class Mp4Layout
{
    public readonly record struct Layout(bool Fragmented, bool HasMoov, bool MoovBeforeMdat)
    {
        /// <summary>Archivo «normal» ya finalizado: sin fragmentos y con el índice al inicio.</summary>
        public bool IsFinalized => !Fragmented && HasMoov && MoovBeforeMdat;
    }

    public static Layout Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Inspect(stream);
    }

    public static Layout Inspect(Stream stream)
    {
        long pos = 0, length = stream.Length;
        bool moof = false;
        long moov = -1, mdat = -1;
        Span<byte> header = stackalloc byte[16];
        while (pos + 8 <= length)
        {
            stream.Position = pos;
            if (stream.Read(header[..8]) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header[4..8]);
            if (size == 1)
            {
                if (stream.Read(header[8..16]) < 8) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]); // caja grande (largesize)
            }
            else if (size == 0) size = length - pos; // «hasta el final del archivo»
            if (size < 8) break; // cabecera corrupta: se deja de mirar
            switch (type)
            {
                case "moof": moof = true; break;
                case "moov": if (moov < 0) moov = pos; break;
                case "mdat": if (mdat < 0) mdat = pos; break;
            }
            pos += size;
        }
        return new Layout(moof, moov >= 0, moov >= 0 && (mdat < 0 || moov < mdat));
    }
}
