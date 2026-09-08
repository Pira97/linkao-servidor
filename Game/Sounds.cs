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
    public const short PARALIZAR    = 203;  // BUG-012: mismo WAV que el hechizo Paralizar (Hechizos.dat HECHIZO9)
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
    // Artes marciales del Bardo (12-sep-2026, pedido del usuario): reemplazan al IMPACTO(86) y
    // al PARALIZAR(203) genéricos, SOLO para la clase Bardo(6) peleando con nudillos.
    public const short BARDO_GOLPE      = 600;   // golpe cuerpo a cuerpo del Bardo con nudillos
    public const short BARDO_PARALIZA   = 601;   // el golpe del Bardo con nudillos paraliza

    // --- Golpes propios por clase (16-sep-2026) -------------------------------------------
    // Los trajo el usuario con nombre ("Clerigo golpe.mp3", "pala.mp3", ...) y los importa
    // web-poc-pixi/importar_sonidos_golpes.py a assets/sfx/<n>.ogg. Arrancan en 700 para que
    // se vea de un vistazo qué vino del AO original (<= 601) y qué se agregó a mano.
    public const short GOLPE_GENERICO   = 700;   // impacto cuerpo a cuerpo sin clase propia
    public const short GOLPE_GUERRERO   = 701;
    public const short GOLPE_PALADIN    = 702;
    public const short GOLPE_CLERIGO    = 703;
    public const short GOLPE_NIGROMANTE = 704;
    public const short GOLPE_GLADIADOR  = 705;
    public const short GOLPE_ASESINO    = 706;   // el golpe normal, sin apuñalar
    public const short APUNALA_ASESINO  = 707;   // la apuñalada (reemplaza al golpe de arriba)
    public const short ARPON_MERCENARIO = 708;   // arpón al salir (proyectil 2)
    public const short FLECHA_ARCO      = 709;   // flecha al salir, antes de llegar
    public const short GOLPE_MAGO       = 710;
    public const short ESCUDO_BLOQUEO   = 711;   // bloqueo con escudo, todas las clases
    public const short GOLPE_PARALIZA   = 712;   // el golpe de artes marciales que paraliza

    // --- Voces de racha de kills (24-sep-2026) --------------------------------------------
    // Estilo DotA, de `Escritorio\Sons`; los importa web-poc-pixi/importar_sonidos_kills.py.
    public const short RACHA_4          = 720;   // Killing Spree
    public const short RACHA_5          = 721;   // Dominating
    public const short RACHA_6          = 722;   // Mega Kill
    public const short RACHA_7          = 723;   // Unstoppable
    public const short RACHA_8          = 724;   // Wicked Sick
    public const short RACHA_9          = 725;   // Monster Kill
    public const short RACHA_10         = 726;   // Godlike
    public const short RACHA_11         = 727;   // Rampage
    public const short RACHA_12         = 728;   // Ownage
    public const short RACHA_13_O_MAS   = 729;   // Monster Kill (Ludacris), suena en cada kill desde la 13

    // --- Poder de los Dioses (25-sep-2026) ------------------------------------------------
    // Lo trajo el usuario (Downloads\sonido.mp3); lo importa web-poc-pixi/importar_sonido_poder_dioses.py.
    public const short PODER_DIOSES     = 730;   // alguien recibe el poder: suena para todo el server

    /// <summary>
    /// Sonido de la N-ésima kill seguida (racha de jugador o de bot; se corta al morir).
    /// 1-3 son los del AO; de la 4 en adelante, las voces nuevas.
    /// </summary>
    public static short DeRacha(int n) => n switch
    {
        <= 0 => 0,
        1  => FIRST_BLOOD,
        2  => DOUBLE_KILL,
        3  => TRIPLE_KILL,
        4  => RACHA_4,
        5  => RACHA_5,
        6  => RACHA_6,
        7  => RACHA_7,
        8  => RACHA_8,
        9  => RACHA_9,
        10 => RACHA_10,
        11 => RACHA_11,
        12 => RACHA_12,
        _  => RACHA_13_O_MAS,
    };

    /// <summary>
    /// Sonido de impacto cuerpo a cuerpo según la clase del que pega (eClass, el mismo
    /// numerito que documenta Dat/BotClases.dat). Las clases sin sonido propio —Ladrón(5),
    /// Druida(7) y las de trabajo— caen en GOLPE_GENERICO. El Bardo(6) NO está acá: tiene su
    /// propia rama en Combat.SonidoImpactoMelee porque su sonido depende del arma (nudillos),
    /// no sólo de la clase.
    /// </summary>
    public static short GolpeDeClase(int clase) => clase switch
    {
        1  => GOLPE_CLERIGO,
        2  => GOLPE_MAGO,
        3  => GOLPE_GUERRERO,
        4  => GOLPE_ASESINO,
        8  => GOLPE_GLADIADOR,
        9  => GOLPE_PALADIN,
        17 => ARPON_MERCENARIO,
        18 => GOLPE_NIGROMANTE,
        _  => GOLPE_GENERICO,
    };
}
