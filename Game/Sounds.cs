namespace ServidorCS.Game;

/// <summary>
/// Índices de sonido (.wav) 1:1 con las constantes SND_* de Declares.bas del servidor VB6.
/// El cliente reproduce el wav por número (PlayWave). NO inventar valores: deben coincidir
/// con los .wav del cliente, igual que el VB6.
/// </summary>
public static class Sounds
{
    public const short SWING        = 2;    // golpe al aire / fallo cuerpo a cuerpo
    public const short WARP         = 3;
    public const short PUERTA       = 5;
    public const short NIVEL        = 6;
    public const short IMPACTO3     = 10;   // impacto de proyectil (flecha)
    public const short USERMUERTE   = 11;   // muerte de usuario (MUERTE_HOMBRE)
    public const short TALAR        = 13;
    public const short PESCAR       = 14;
    public const short MINERO       = 15;
    public const short SACARARMA    = 25;   // desenvainar arma
    public const short ESCUDO       = 37;   // bloqueo con escudo
    public const short MARTILLOHERRERO = 41;
    public const short LABUROCARPINTERO = 42;
    public const short SANAR        = 55;
    public const short RESUCITAR    = 84;
    public const short IMPACTO      = 86;   // impacto cuerpo a cuerpo (golpe que conecta)
    public const short DROP         = 132;  // caída de ítem al piso
    public const short BEBER        = 135;
    public const short FALLASFLECHA = 145;  // fallo de flecha
    public const short CASAMIENTO   = 161;
    public const short ORO2         = 172;
    public const short RESUCITADO   = 204;
    public const short VENENO       = 239;
    public const short ARROJADIZA   = 68;   // proyectil arrojadizo (daga/shuriken)
}
