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

    // --- Sonidos custom de este server (no 1:1 VB6; pedidos por el cliente) ---
    public const short ENTRADA          = 15;   // un usuario entra al mundo (login)
    public const short FLECHA_EXPLOSIVA = 27;    // flecha explosiva al impactar
    public const short FUNDAR_CLAN      = 3;     // fanfarria al fundar un clan
    public const short NIVEL_NUEVO      = 72;    // subir de nivel (reemplaza al 6)
    public const short INCINERADO       = 78;    // el usuario está incinerado (quemándose)
    public const short SANAR_HERIDAS    = 101;   // el sacerdote te cura las heridas
    public const short ALARMA_CIUDAD    = 139;   // ciudad atacada (enemigo agrede a un guardia)
    public const short CASAMIENTO_USERS = 140;   // casamiento entre usuarios
    public const short DRAGON_ESPADA    = 149;   // golpe de Espada Mata Dragones a un dragón
    public const short SERRUCHO1        = 169;   // carpintería (serrucho) — fabricar item
    public const short SERRUCHO2        = 170;   // carpintería (serrucho) — fabricar item
    public const short ORO_POCO         = 171;   // tirar pocas monedas de oro
    public const short GOLEM_PASO1      = 220;   // pasos de golem
    public const short GOLEM_PASO2      = 221;
    public const short GOLEM_PASO3      = 222;
    public const short EVENTO_INICIO    = 252;   // inicio/curso de un evento del juego
    public const short KILL_SPREE       = 175;   // racha de más de 7 kills seguidas
    public const short DOUBLE_KILL      = 261;   // 2 kills seguidas
    public const short FIRST_BLOOD      = 262;   // 1ª sangre (primer kill de la racha)
    public const short TRIPLE_KILL      = 270;   // 3 kills seguidas
    public const short MUERTE_USUARIO   = 389;   // el usuario muere (reemplaza al 11)
    public const short FLAUTA           = 393;   // tocar la flauta (instrumento)
    public const short DESCONEXION      = 434;   // el usuario se desconecta del juego
    public const short COLLAR_PENDIENTE = 458;   // equipar collar/pendiente
}
