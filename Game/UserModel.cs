namespace ServidorCS.Game;

// Modelo de usuario portado 1:1 desde Declares.bas (VB6).
// Se usan clases (referencia mutable) para reproducir el UserList() global del VB6.
// Arrays base-1: se dimensionan con tamaño+1 y se ignora el índice 0, igual que VB6.

public static class Constants
{
    public const int MAX_INVENTORY_SLOTS = 25;
    public const int MAX_BANCOINVENTORY_SLOTS = 40;
    public const int NUMSKILLS = 27;
    public const int NUMATRIBUTOS = 5;
    public const int MAXUSERHECHIZOS = 120;
    public const int MAXMASCOTAS = 3;
    public const int MAXAMIGOS = 5;
    public const int MAX_CORREOS = 10;
    public const int MAX_CORREOS_SLOTS = 20;
}

// Position (Declares.bas)
public struct Position
{
    public short X;
    public short Y;
}

// WorldPos (Declares.bas)
public struct WorldPos
{
    public short Map;
    public short X;
    public short Y;
    public short Dead_Map;
    public short Dead_X;
    public short Dead_Y;
}

// UserObj (slot de inventario)
public struct UserObj
{
    public short ObjIndex;
    public int Amount;
    public bool Equipped;
}

// Inventario (Declares.bas)
public sealed class Inventario
{
    // Object(1 To MAX_INVENTORY_SLOTS): índice 0 sin usar.
    public UserObj[] Object = new UserObj[Constants.MAX_INVENTORY_SLOTS + 1];
    public short NudiEqpObjIndex;
    public byte NudiEqpSlot;
    public short WeaponEqpObjIndex;
    public byte WeaponEqpSlot;
    public short ArmourEqpObjIndex;
    public byte ArmourEqpSlot;
    public short EscudoEqpObjIndex;
    public byte EscudoEqpSlot;
    public short CascoEqpObjIndex;
    public byte CascoEqpSlot;
    public short MunicionEqpObjIndex;
    public byte MunicionEqpSlot;
    public short AnilloEqpObjIndex;
    public byte AnilloEqpSlot;
    public short BarcoObjIndex;
    public byte BarcoSlot;
    public short NroItems;
    public short MonturaObjIndex;
    public byte MonturaSlot;
    public short MagicIndex;
    public short MagicSlot;
}

// Correo (tCorreos de Declares.bas): mensaje + emisor + item adjunto.
public struct Correo
{
    public string Mensaje;
    public string Emisor;
    public bool Leida;
    public short ObjIndex;
    public int Cantidad;
}

// BancoInventario (Declares.bas): 40 slots, formato objindex-amount (sin equipped).
public sealed class BancoInventario
{
    public UserObj[] Object = new UserObj[Constants.MAX_BANCOINVENTORY_SLOTS + 1]; // 1..40
    public short NroItems;
}

// Char (apariencia) — Declares.bas
public sealed class CharData
{
    public short CharIndex;
    public short Head;
    public short body;
    public byte Donador;
    public short WeaponAnim;
    public short ShieldAnim;
    public short CascoAnim;
    public short[] Particles = new short[16]; // 1..15
    public short FX;
    public short Loops;
    public byte heading;      // eHeading: N=1,E=2,S=3,O=4 [[ao_heading_order]]
    public short ParticulaFx;
    public byte WeaponAnimSkin;
    public byte ShieldAnumSkin;
    public byte CascoAnimSkin;
    public byte BodySkin;
    public byte ObjetoSkin;
    public byte Arma_Aura;
    public byte Body_Aura;
    public byte Escudo_Aura;
    public byte Head_Aura;
    public byte Otra_Aura;
    public byte Anillo_Aura;
}

// UserStats — Declares.bas
public sealed class UserStats
{
    public int GLD;
    public int Banco;
    public short MaxHP, MinHP;
    public short MaxSta, MinSta;
    public short MaxMAN, MinMAN;
    public short MaxHIT, MinHIT;
    public short MaxHam, MinHam;
    public short MaxAGU, MinAGU;
    public short def;
    public short ExtraHIT, ExtraDEF;
    public double Exp;
    public byte ELV;
    public int ELU;
    public byte[] UserSkills = new byte[Constants.NUMSKILLS + 1];
    public byte[] UserAtributos = new byte[Constants.NUMATRIBUTOS + 1];
    public byte[] UserAtributosBackUP = new byte[Constants.NUMATRIBUTOS + 1];
    public short[] UserHechizos = new short[Constants.MAXUSERHECHIZOS + 1];
    public short UsuariosMatados;
    public short NPCsMuertos;
    public short SkillPts;
    public int[] ExpSkills = new int[Constants.NUMSKILLS + 1];
    public int[] EluSkills = new int[Constants.NUMSKILLS + 1];
    public int ArenaPoints;
}

// UserFlags — sólo los flags usados por el núcleo jugable (se ampliará al portar más lógica).
public sealed class UserFlags
{
    public byte Muerto;
    public byte Paralizado;
    public byte Inmovilizado;
    public bool TorneoCongelado;   // congelado durante la cuenta regresiva previa al combate de torneo
    public byte Ciego;
    public byte Hambre;
    public byte Sed;
    public byte Envenenado;
    public bool Meditando;
    public byte Montando;
    public byte Descansar;
    public bool UserLogged;
    // Trabajo (minería, pesca, leña, etc.) — FASE 2
    public bool Trabajando;
    public int Lingoteando;
    public byte RecibioCorreo;
    public bool Navegando;
    public byte Vuela;         // 1 = vuela (atraviesa agua sin barco). VB6 flags.Vuela
    public byte Oculto;        // 1 = oculto (skill Ocultarse). VB6 flags.Oculto
    public byte Invisible;     // 1 = invisible por hechizo (NO se revela al atacar; expira por tiempo)
    public double InvisibleExpira; // segundos hasta que termina la invisibilidad mágica
    public byte Incinerado;    // 1 = incinerado (daño periódico por fuego)
    public int NivelVeneno;    // nivel de envenenamiento (0 = sano); daño periódico por tick
    // Trabajo activo: skill y posición del recurso (usado por GameTimer.DoTrabajar)
    public byte WorkSkill;
    public byte WorkX, WorkY;
    public byte Estupido;      // 1 = estúpido (hechizo Estupidez), pantalla alterada
    // Momento (segundos) en que expira cada efecto de estado; 0 = sin efecto.
    public double ParalisisExpira;
    public double CegueraExpira;
    public double EstupidezExpira;
    public double IncineradoExpira;  // incineración: dura unos segundos y se apaga sola
    public double VenenoExpira;      // veneno crítico: dura unos segundos (0 = veneno normal, persiste)
    public double MaldecidoExpira;   // maldición: impide atacar por unos segundos
    public byte Maldecido;           // 1 = maldecido (no puede atacar)
    public double ArmaMagicaExpira;  // arma mágica: el golpe cuerpo a cuerpo hace 120-170 por 2 min
    // Metamorfosis (hechizo): transforma el body del usuario por 60s. OrigBody/OrigHead guardan
    // la apariencia previa; MetamorfosisExtraHIT/DEF el bonus a revertir; MetamorfosisExpira el fin.
    public byte Metamorfoseado;
    public byte Desnudo;   // 1 = sin armadura (DarCuerpoDesnudo). VB6 UserFlags.Desnudo.
    public short MetamorfosisBody, MetamorfosisHead;
    public short MetamorfosisExtraHIT, MetamorfosisExtraDEF;
    public short OrigBody, OrigHead;
    public double MetamorfosisExpira;
    // Buff/debuff de atributos (SubeFU/SubeAG): TomoPocion marca que hay un efecto activo;
    // al expirar AtributoEfectoExpira se restauran los UserAtributos desde UserAtributosBackUP.
    public bool TomoPocion;
    public double AtributoEfectoExpira;
    // Hechizos especiales de guerrero (116-120): Sacrificio Impío (próximo golpe certero) y
    // Furor Ígneo (velocidad de ataque, dura unos segundos). Timers/cooldowns en segundos (TickCount64/1000).
    public bool SacrificioImpio;
    public bool FurorIgneo;
    public double FurorIgneoExpira;
    public double FurorIgneoCooldownExpira;
    public double TempleCooldownExpira;
    // Lista de amigos (Declares.bas flags): CantidadAmigos = cuántos tiene; CheckAmigos = 1 si tiene ≥1.
    public byte CantidadAmigos;
    public byte CheckAmigos;
    // Veces que el usuario murió (a manos de otro jugador). .chr [FLAGS] Murio. Usado por MiniStats.
    public int MuertesUsuario;
    // Racha de kills PvP seguidas (killstreak). Se reinicia al morir y al desconectarse.
    // Dispara sonidos: 1=FIRST_BLOOD(262), 2=DOUBLE_KILL(261), 3=TRIPLE_KILL(270), >=7=KILL_SPREE(175).
    public int KillStreak;
    // AFK: momento (TickCount64 ms) de la última actividad/movimiento del usuario, y si tiene la
    // partícula de AFK activa. Si pasa AFK_TIMEOUT sin moverse, se le difunde la partícula; al moverse se quita.
    public long LastActivityAt;
    public bool AfkParticula;
    // Aggro: CharIndex del NPC que ataca al usuario / que el usuario ataca (0 = ninguno). VB6
    // UserFlags.AtacadoPorNpc/NPCAtacado (allí es npcindex; acá CharIndex). Se resetean al morir.
    public int AtacadoPorNpc;
    public int NPCAtacado;
    // Centinela: el usuario respondió correctamente la clave anti-macro de esta ronda.
    public bool CentinelaOK;
    // Seguro de resurrección (ResuscitationSafeToggle): si está activo no te resucitan enemigos.
    public bool SeguroResu;
    // Contador de cárcel (Counters.Pena en VB6): minutos restantes de condena. Se persiste para no
    // perderlo al reloguear. La restricción de movimiento en sí es un sistema aparte (pendiente).
    public int Pena;
}

// Estado anti-autoclicker por usuario (Declares.bas ClickHistorial + AntiClick).
public sealed class AntiClickState
{
    public long UltimoUso;             // tick del último uso manual
    public long[] Tiempos = new long[11]; // 1..10 ticks de los últimos usos (array circular)
    public byte Indice;                // índice circular actual
    public byte Muestras;              // cantidad de muestras acumuladas (máx 10)
    public byte Detecciones;           // detecciones consecutivas de patrón
    public long VentanaInicio;         // inicio de la ventana de rate-limit (1s)
    public int ContadorPaquetes;       // paquetes en la ventana actual
}

// Amigos (Declares.bas:1532): slot de la lista de amigos. Nombre="Vacio" = libre;
// index = userIndex del amigo si está online (0 = offline), cacheado por ObtenerIndexAmigos.
public struct AmigoSlot
{
    public string Nombre;
    public int index;
}

// tFacciones (Declares.bas:1510). Status = facción (1=Renegado, 2=Ciudadano, 3=Republicano,
// 4=Caos, 5=Armada, 6=Milicia, 0=sin facción). Los *Matados son frags por facción de la víctima,
// usados como requisito para enlistarse y para las recompensas/rangos. [[facciones_jugador]]
public sealed class Faccion
{
    public byte Status;
    public int CiudadanosMatados;
    public int RenegadosMatados;
    public int RepublicanosMatados;
    public int MilicianosMatados;
    public int ArmadaMatados;
    public int CaosMatados;
    public int Rango;
}

// User — la estructura central. Sólo los campos que el núcleo jugable necesita hoy;
// el resto (Donador, Correos, Guild, Party, Faccion...) se agrega al portar esos módulos.
public sealed class User
{
    public string Name = "";
    public int id;
    public string Account = "";
    public bool showName;

    public CharData Char = new();
    public CharData OrigChar = new();
    public string desc = "";

    public byte Clase;   // eClass
    // Privilegios de GM (de Server.ini vía AdminLoader): 7=Consejero/RM, 8=SemiDios, 9=Dios, 10=Soporte.
    public byte FaccionStatus;
    // Facción del JUGADOR (VB6 tFacciones, Declares.bas:1510). Cargada de [FACCIONES] del .chr.
    public Faccion Faccion = new();
    public byte raza;    // eRaza
    public byte Genero;  // eGenero
    public byte Hogar;   // eCiudad
    public long RunaDonadorNextAt; // cooldown de la Runa de Transporte (Donador): TickCount64 mínimo del próximo uso

    public Inventario Invent = new();
    public BancoInventario BancoInvent = new();
    public List<Correo> Correos = new();   // bandeja de correos del jugador
    public AmigoSlot[] Amigos = NuevaListaAmigos(); // 1..MAXAMIGOS; Nombre="Vacio" = libre
    public string QuienAmigo = ""; // nombre de quien envió la última solicitud de amistad pendiente
    public AntiClickState AntiClick = new(); // estado anti-autoclicker
    public int CreditoDonador; // saldo de créditos de donación (cuenta .cnt [cuenta] Creditos)

    // --- Battle Pass (NUEVO, no VB6) — boosts personales temporales otorgados por el pase. ---
    // Multiplicador y vencimiento (TickCount64 en segundos) de exp/oro EXTRA personal, encima del
    // multiplicador global de evento. 1.0 / 0 = sin boost activo. [[battle_pass]]
    public double ExpBoostMult = 1.0;
    public long ExpBoostUntil;   // segundos (Environment.TickCount64/1000) hasta cuando dura
    public double OroBoostMult = 1.0;
    public long OroBoostUntil;

    public BattlePass.Progress BattlePass; // progreso del pase de temporada (cargado al login)

    /// <summary>Lista de amigos inicializada con todos los slots en "Vacio" (1:1 VB6 .chr nuevo).</summary>
    public static AmigoSlot[] NuevaListaAmigos()
    {
        var a = new AmigoSlot[Constants.MAXAMIGOS + 1];
        for (int i = 1; i <= Constants.MAXAMIGOS; i++) a[i].Nombre = "Vacio";
        return a;
    }
    public WorldPos Pos;

    public bool ConnIDValida;
    public int ConnID = -1;

    public UserStats Stats = new();
    public UserFlags flags = new();

    public string ip = "";
    public DateTime LogOnTime;
    public int UpTime;

    public byte Redundance;

    // Hechizo seleccionado con CastSpell, pendiente de objetivo (WorkLeftClick). 0 = ninguno.
    public byte SpellPendiente;

    // Timers de cooldown (ms del último uso, Environment.TickCount64). Equiv. UserCounters.Timer*.
    public long TimerAtacar, TimerLanzarSpell, TimerTrabajar, TimerUsar, TimerClicsMouse,
        TimerMagiaGolpe, TimerGolpeMagia, TimerGolpeUsar, TimerUsarArco;

    // Timers internos del GameTimer (regen/hambre/sed).
    public long _timerSanar, _timerSta, _timerHambre, _timerSed, _tInicioMeditar, _timerMeditar;
    public long _timerManaAnillo; // regen de maná del Anillo de la Quinta Esencia (EfectoMagico AceleraMana)
    public long _timerVeneno, _timerIncinera;

    // NPC seleccionado con LeftClick (CharIndex), para comerciar. 0 = ninguno.
    public short TargetNpcCharIndex;
    public bool Comerciando;
    public bool ComercioNpcNoCompra;   // true si el NPC con el que comercia solo vende (no compra)

    // Mascotas (domar): CharIndex de cada mascota viva. 0 = slot vacío. Máx 3.
    public short[] MascotasCharIndex = new short[4]; // 1..3
    public int NroMascotas;

    // Party (grupo): id del grupo, 0 = ninguno. GuildIndex del clan, 0 = sin clan.
    public int PartyId;
    public int GuildIndex;
    // Alineación elegida en GuildFundation, pendiente hasta que llegue CreateNewGuild (1-4).
    public byte FundandoGuildAlineacion;
    // Casamiento (TCasamiento, Declares.bas): Candidato = userIndex al que le propuso casamiento.
    public int CasamientoCandidato;
    public byte CasamientoCasado;
    public string CasamientoPareja = "";
    // Runa de teletransporte: casteo de 6s antes de viajar al hogar. 0 = no casteando.
    public byte CasteandoRuna;
    public byte RunaSlot;
    // Casteo de resucitar/resurrección: durante el casteo se muestra la partícula 18 sobre el muerto.
    // ResucitandoHasta = segundos hasta completar (0 = inactivo); ResucitandoTarget = userIndex muerto;
    // ResucitandoFull = true para Resurrección (vida completa), false para Resucitar (sólo 20 HP).
    public double ResucitandoHasta;
    public int ResucitandoTarget;
    public bool ResucitandoFull;
    public byte ResucitandoX, ResucitandoY; // posición al iniciar el casteo (si se mueve, se cancela)
    // Portal de teletransporte (hechizo 53, uCreateTelep): casteo por segundos. PortalTime 0 = inactivo;
    // a los 5s aparece el objeto 672 (TileExit→Intermundia) en (PortalX,PortalY); a los 15s desaparece.
    public byte PortalTime;
    public short PortalMap, PortalX, PortalY;
    public bool PortalCreado;

    // Tras un teleport (WarpUser), el PRIMER paso del usuario no debe disparar otro TileExit:
    // evita que entrar a un dungeon caminando caiga sobre el teleport de retorno y rebote afuera.
    // El cruce normal de mapas sigue fluido (el primer paso casi nunca cae sobre otro teleport).
    public bool RecienTeleportado;

    // Último personaje (usuario) apuntado con LeftClick (CharIndex). Para iniciar trade.
    public short TargetUserCharIndex;
    // Sesión de comercio usuario-a-usuario en curso (null = ninguna).
    public UserTradeSession Trade;

    // Último objeto apuntado con DoubleClick (ObjIndex). Equivalente a flags.TargetObj en VB6.
    public short TargetObj;

    // Target de tile/objeto (LookatTile, GameLogic.bas:779). Equivalen a flags.Target* del VB6.
    // TargetX/Y = última posición clickeada (la usa otBotellaVacia para HayAgua).
    // TargetObjMap/X/Y = posición del objeto apuntado (la usa otLlaves para abrir/cerrar la puerta).
    public short TargetMap;
    public byte TargetX, TargetY;
    public short TargetObjMap;
    public byte TargetObjX, TargetObjY;

    // --- Visibilidad por área (AOI server-driven, equivalente a ModAreas/ConnGroups del VB6) ---
    // Sets de lo que ESTE observador tiene actualmente renderizado en su cliente. El servidor manda
    // CharacterCreate al entrar al área y CharacterRemove(desvanecido=false) al salir → sin fantasmas.
    public HashSet<int> VisibleUsers = new();   // userIndex de otros jugadores visibles para mí
    public HashSet<int> VisibleNpcs = new();    // CharIndex de NPCs visibles para mí
    public HashSet<int> VisibleObjs = new();    // objetos del piso visibles: tile codificado (x*101+y)
    // Bloque de área actual (Pos\9). -1 = sin inicializar. Si no cambió, no se rehace el escaneo completo.
    public int AreaBlockX = -1, AreaBlockY = -1;

    // Enlace al socket real (reemplaza outgoingData/incomingData de clsByteQueue,
    // que ahora viven en Network.Connection).
    public Network.Connection Conn;

    /// <summary>
    /// Resetea TODO el estado por-personaje a valores frescos antes de cargar un .chr, preservando
    /// los campos de conexión (Conn/ConnID/ConnIDValida/Account/id/ip). Imprescindible porque el slot
    /// de UserList se reutiliza entre logins de la misma conexión (volver al panel de personajes y
    /// elegir otro): sin esto, los slots de inventario/punteros de equipo/flags que el .chr nuevo NO
    /// sobrescribe (slots vacíos, claves ausentes) conservaban los del PJ anterior → ítems duplicados
    /// y cuerpo/equipo del personaje previo.
    /// </summary>
    public void ResetForLogin()
    {
        Char = new CharData();
        OrigChar = new CharData();
        Invent = new Inventario();
        BancoInvent = new BancoInventario();
        Stats = new UserStats();
        flags = new UserFlags();
        Faccion = new Faccion();
        Correos = new List<Correo>();
        Amigos = NuevaListaAmigos();
        QuienAmigo = "";
        AntiClick = new AntiClickState();
        desc = "";
        showName = false;
        Clase = 0; raza = 0; Genero = 0; Hogar = 0; FaccionStatus = 0;
        Pos = default;
        GuildIndex = 0; PartyId = 0; FundandoGuildAlineacion = 0;
        CreditoDonador = 0;
        ExpBoostMult = 1.0; ExpBoostUntil = 0; OroBoostMult = 1.0; OroBoostUntil = 0;
        BattlePass = null;
        SpellPendiente = 0;
        TargetNpcCharIndex = 0; TargetUserCharIndex = 0; Comerciando = false; Trade = null;
        TargetObj = 0; TargetMap = 0; TargetX = 0; TargetY = 0;
        TargetObjMap = 0; TargetObjX = 0; TargetObjY = 0;
        for (int i = 1; i < MascotasCharIndex.Length; i++) MascotasCharIndex[i] = 0;
        NroMascotas = 0;
        CasamientoCandidato = 0; CasamientoCasado = 0; CasamientoPareja = "";
        CasteandoRuna = 0; RunaSlot = 0;
        PortalTime = 0; PortalMap = 0; PortalX = 0; PortalY = 0; PortalCreado = false;
        Redundance = 0;
    }
}
