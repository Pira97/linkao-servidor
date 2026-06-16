using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Cripto de cuentas 1:1 con el VB6:
///  - ShiftDecrypt = SDesencriptar (ModEncrypt.bas): revierte el cifrado por desplazamiento que
///    aplica el cliente (_encriptar_ao_shift en protocol_outgoing.gd). El bloque trae los bytes
///    (char + N) & 0xFF y un sufijo de 2 bytes (dígitos de N + 10) para recuperar N.
///  - Sha256Hex = CSHA256.SHA256: SHA-256 sobre los bytes CP1252 del texto, hex en MINÚSCULAS.
///  - PasswordValida (Cuentas.bas:510): hash == SHA256(password & salt).
/// </summary>
public static class Crypto
{
    /// <summary>Revierte el shift-cipher del cliente sobre los bytes crudos del bloque.</summary>
    public static string ShiftDecrypt(byte[] block)
    {
        if (block == null || block.Length < 2) return "";
        int n = block.Length;
        int d0 = block[n - 2] - 10; // char del primer dígito de N
        int d1 = block[n - 1] - 10; // char del segundo dígito de N
        int randomNum = (d0 - '0') * 10 + (d1 - '0');

        var plain = new byte[n - 2];
        for (int i = 0; i < n - 2; i++)
            plain[i] = (byte)((block[i] - randomNum) & 0xFF);
        return Cp1252.GetString(plain);
    }

    /// <summary>SHA-256 de los bytes CP1252 del texto, en hex minúsculas (64 chars). 1:1 CSHA256.</summary>
    public static string Sha256Hex(string text)
    {
        var bytes = Cp1252.GetBytes(text ?? "");
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>PasswordValida (Cuentas.bas:510): hash almacenado == SHA256(password & salt).</summary>
    public static bool PasswordValida(string passwordPlano, string passwordHash, string salt)
    {
        if (string.IsNullOrEmpty(passwordHash) || salt == null) return false;
        return string.Equals(passwordHash, Sha256Hex(passwordPlano + salt), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RandomString alfanumérico (para Salt de cuentas nuevas). 1:1 intención VB6.</summary>
    public static string RandomString(int len)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.ToString();
    }
}
