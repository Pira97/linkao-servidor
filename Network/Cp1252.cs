namespace ServidorCS.Network;

/// <summary>
/// Codec CP1252 (Windows-1252) implementado a mano, SIN dependencias de NuGet.
/// Es la página de código que usaba el VB6 original (ver memoria [[vb6_encoding]]).
///
/// CP1252:
///   - 0x00–0x7F: idéntico a ASCII.
///   - 0xA0–0xFF: idéntico a Latin-1 (mismo code point Unicode).
///   - 0x80–0x9F: tabla especial (€, comillas tipográficas, etc.).
///
/// Crítico para que ñ/á/é/í/ó/ú viajen byte-idénticos al cliente Godot.
/// </summary>
public static class Cp1252
{
    // Mapeo de los 32 bytes 0x80–0x9F a Unicode. 0xFFFD = indefinido en CP1252.
    private static readonly char[] HighMap =
    {
        '€', '�', '‚', 'ƒ', '„', '…', '†', '‡', // 80-87
        'ˆ', '‰', 'Š', '‹', 'Œ', '�', 'Ž', '�', // 88-8F
        '�', '‘', '’', '“', '”', '•', '–', '—', // 90-97
        '˜', '™', 'š', '›', 'œ', '�', 'ž', 'Ÿ', // 98-9F
    };

    public static byte[] GetBytes(string value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();
        var result = new byte[value.Length];
        for (int i = 0; i < value.Length; i++)
            result[i] = CharToByte(value[i]);
        return result;
    }

    public static string GetString(byte[] bytes) => GetString(bytes, 0, bytes.Length);

    public static string GetString(byte[] bytes, int offset, int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            byte b = bytes[offset + i];
            chars[i] = b < 0x80 || b >= 0xA0 ? (char)b : HighMap[b - 0x80];
        }
        return new string(chars);
    }

    private static byte CharToByte(char c)
    {
        // Rango directo: ASCII y Latin-1 alto (incluye ñ á é í ó ú ü ...).
        if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) return (byte)c;

        // Buscar en la tabla especial 0x80–0x9F.
        for (int i = 0; i < HighMap.Length; i++)
            if (HighMap[i] == c) return (byte)(0x80 + i);

        // Sin representación en CP1252: '?' (igual que el lpDefaultChar del VB6).
        return (byte)'?';
    }
}
