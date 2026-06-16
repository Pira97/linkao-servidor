namespace ServidorCS.Game;

/// <summary>
/// Sistema de BOTS de prueba (NUEVO, no VB6). Invoca "jugadores" controlados por el server
/// (en realidad NPCs con cuerpo de jugador) de cualquier clase/raza, equipados con un set
/// "sacro" configurable, que pelean contra el invocador. Sirve para probar combate/balance.
///
/// - Cada bot es un NpcInstance hostil (Movement=0 → persigue, melee adyacente; los casters
///   lanzan hechizos a distancia vía la IA de NpcManager).
/// - La apariencia sale de la raza (cuerpo/cabeza de jugador) + el set sacro (anims de
///   arma/escudo/casco y cuerpo de la armadura).
/// - Se registran en índices altos (BOT_INDEX_BASE+) que no chocan con NPCs.dat.
///
/// PARA AJUSTAR EL SACRO: completá los ObjIndex en la tabla _clases (ArmorObj/WeaponObj/
/// ShieldObj/CascoObj). Los ves en /editobj. 0 = nada (queda desnudo / sin esa pieza).
/// </summary>
public static class Bots
{
    public const int BOT_INDEX_BASE = 30000;
    private static int _nextOffset = 0;

    // Tope de bots vivos simultáneos (anti-spam: evita saturar el server).
    public const int MAX_BOTS = 60;

    // (OBSOLETO) Antes multiplicaba el daño melee de los bots. Ahora el daño sale del arma del
    // obj.dat (ver DanoArma); se deja por compatibilidad pero ya no se usa.
    public const int BOT_DMG_MULT = 5;

    // Cache de definiciones registradas por (clase,raza,faccion): así spamear NO crea miles de
    // entradas en NpcData (antes cada Spawn registraba una nueva → leak). Reusa el índice.
    private static readonly Dictionary<(byte clase, byte raza, byte faccion), int> _regIndex = new();

    // Color de nick (privileges del cliente get_nick_color): 5=Armada(azul acero), 6=Milicia(dorado), 4=Caos(rojo).
    private static byte StatusDeFaccion(byte faccion) => faccion switch { 1 => 5, 2 => 6, 3 => 4, _ => 4 };

    // Anim de arma del estandarte por facción (entradas nuevas en armas.dat: 155 Armada, 156 Caos, 157 República).
    private static short EstandarteAnim(byte faccion) => faccion switch { 1 => 155, 3 => 156, 2 => 157, _ => (short)0 };

    /// <summary>Algunos bots de facción llevan el estandarte en mano (~1 de cada 3).</summary>
    private static void TalVezDarEstandarte(NpcManager.NpcInstance b, byte faccion)
    {
        if (b == null || faccion == 0 || _nickRng.Next(3) != 0) return;
        short banner = EstandarteAnim(faccion);
        if (banner > 0) NpcManager.SetBotWeaponAnim(b, banner);
    }

    // eClass: Clerigo=1, Mago=2, Guerrero=3, Asesino=4, Ladron=5, Bardo=6, Druida=7,
    //         Gladiador=8, Paladin=9, Cazador=10, Mercenario=17, Nigromante=18.
    // eRaza: Humano=1, Elfo=2, Drow=3, Gnomo=4, Enano=5, Orco=6.

    public struct BotClase
    {
        public byte Clase;
        public string Nombre;
        public byte RazaDefault;
        // Set sacro (ObjIndex de obj.dat; 0 = nada). Completar con /editobj.
        public int ArmorObj, WeaponObj, ShieldObj, CascoObj;
        public short[] Spells;     // hechizos que castea (null = melee puro)
        public short HealSpell;    // hechizo de cura a aliados (clérigos); 0 = no cura
        public short AtaqueParticula; // partícula al golpear (cazador = flecha explosiva 173); 0 = ninguna
        public short Aura;         // aura visible (índice de aura; 0 = sin aura)
        public int Hp;
        public int MinHit, MaxHit;
        public int PoderAtaque, PoderEvasion;
    }

    // Spells (índices de Hechizos.dat): Apocalipsis=25, Juicio final=52, Implosion=34,
    // Inmovilizar=24, Descarga electrica=93, Tormenta de fuego=15, Paralizar=9.
    // Set sacro por clase (ObjIndex de obj.dat). Mapa: Tunica Dorada=519, Tunica RM+15=1090,
    // Gorro RM+20=993, Gorro Arcano=1206, Baculo DM+20=1147, Baculo DM+10=1181, Baculo Larzull=1252,
    // Armadura Nigromante=903, Armadura Placas+2=391, Armadura Pieles=872, Armadura Legendaria=1211,
    // Armadura Dragon Azul=873, Dragon Blanco=876, Placas Dorada RM+10=1093, Espada Lazurt+1=747,
    // Espada Saramiana=1257, Hacha Saramiana=1244, Espada MataDragones=402, Daga Infernal=740,
    // Nudillos Oro=1333, Arco Elfico=899, Arpon Incendiario=1596, Casco Dorado=661, Bifurcado=1078,
    // Vikingo=1079, Legendario=1276, Harbinger Kin=668, Escudo Reflexion RM+15=1088, RM+8=1025,
    // RM+30=1180, Leon+1=1100, Dual=1267, Torre+1=1002, Arcano=1358.
    private static readonly Dictionary<byte, BotClase> _clases = new()
    {
        [2]  = new BotClase { Clase = 2,  Nombre = "Mago",       RazaDefault = 4, ArmorObj = 519,  WeaponObj = 1147, ShieldObj = 0,    CascoObj = 993,  Hp = 4500, MinHit = 1,  MaxHit = 5,   PoderAtaque = 250, PoderEvasion = 200, Spells = new short[]{ 25, 52, 34 } },
        [18] = new BotClase { Clase = 18, Nombre = "Nigromante", RazaDefault = 3, ArmorObj = 903,  WeaponObj = 1181, ShieldObj = 1088, CascoObj = 661,  Hp = 6000, MinHit = 60, MaxHit = 90,  PoderAtaque = 320, PoderEvasion = 260, Spells = new short[]{ 34, 24 } },
        [1]  = new BotClase { Clase = 1,  Nombre = "Clerigo",    RazaDefault = 1, ArmorObj = 391,  WeaponObj = 747,  ShieldObj = 1180, CascoObj = 1078, Hp = 6500, MinHit = 60, MaxHit = 95,  PoderAtaque = 330, PoderEvasion = 270, Spells = new short[]{ 69, 24, 9 }, HealSpell = 71 },
        [7]  = new BotClase { Clase = 7,  Nombre = "Druida",     RazaDefault = 1, ArmorObj = 872,  WeaponObj = 1252, ShieldObj = 1358, CascoObj = 1206, Hp = 6000, MinHit = 55, MaxHit = 85,  PoderAtaque = 300, PoderEvasion = 280, Spells = new short[]{ 24 } },
        [3]  = new BotClase { Clase = 3,  Nombre = "Guerrero",   RazaDefault = 6, ArmorObj = 1211, WeaponObj = 0,    ShieldObj = 1002, CascoObj = 1079, Hp = 8000, MinHit = 80, MaxHit = 120, PoderAtaque = 380, PoderEvasion = 280, Spells = null },
        [4]  = new BotClase { Clase = 4,  Nombre = "Asesino",    RazaDefault = 3, ArmorObj = 872,  WeaponObj = 740,  ShieldObj = 1088, CascoObj = 1276, Hp = 6500, MinHit = 70, MaxHit = 110, PoderAtaque = 360, PoderEvasion = 340, Spells = null },
        [6]  = new BotClase { Clase = 6,  Nombre = "Bardo",      RazaDefault = 1, ArmorObj = 1090, WeaponObj = 1333, ShieldObj = 1358, CascoObj = 993,  Hp = 6500, MinHit = 65, MaxHit = 100, PoderAtaque = 340, PoderEvasion = 330, Spells = new short[]{ 24 } },
        [9]  = new BotClase { Clase = 9,  Nombre = "Paladin",    RazaDefault = 1, ArmorObj = 873,  WeaponObj = 1257, ShieldObj = 1180, CascoObj = 1078, Hp = 7500, MinHit = 75, MaxHit = 115, PoderAtaque = 360, PoderEvasion = 290, Spells = new short[]{ 87, 24, 93 } },
        [10] = new BotClase { Clase = 10, Nombre = "Cazador",    RazaDefault = 2, ArmorObj = 873,  WeaponObj = 899,  ShieldObj = 1358, CascoObj = 1078, Hp = 7000, MinHit = 80, MaxHit = 120, PoderAtaque = 370, PoderEvasion = 300, Spells = null, AtaqueParticula = 173 },
        [17] = new BotClase { Clase = 17, Nombre = "Mercenario", RazaDefault = 5, ArmorObj = 876,  WeaponObj = 1257, ShieldObj = 1002, CascoObj = 1079, Hp = 7500, MinHit = 75, MaxHit = 115, PoderAtaque = 360, PoderEvasion = 290, Spells = null },
    };

    // Pool de nicks inventados (estilo AO) para los bots.
    private static readonly string[] _nicks = {
        "Thoranis", "Kael", "Morgath", "Eldric", "Drogan", "Valka", "Nyx", "Sael",
        "Brunor", "Aldric", "Zephyr", "Korvax", "Lyra", "Faelan", "Garruk", "Mireia",
        "Voss", "Ragnar", "Selene", "Tharos", "Ulfric", "Kira", "Bane", "Orin",
        "Sombra", "Belial", "Astra", "Dorian", "Grim", "Hela", "Varko", "Nerion",
    };
    private static readonly Random _nickRng = new();
    private static int _nickSeq = 0;
    private static string RandomNick()
    {
        // nombre + sufijo numérico corto para que no se repitan visualmente.
        return _nicks[_nickRng.Next(_nicks.Length)] + (++_nickSeq);
    }

    public static IEnumerable<BotClase> Clases => _clases.Values;
    public static bool ClaseValida(byte clase) => _clases.ContainsKey(clase);

    /// <summary>Mapa nombre→clase (para el comando /bot mago, /bot guerrero, etc.).</summary>
    public static byte ClasePorNombre(string nombre)
    {
        nombre = nombre.Trim().ToLowerInvariant();
        foreach (var c in _clases.Values)
            if (c.Nombre.ToLowerInvariant() == nombre) return c.Clase;
        return 0;
    }

    /// <summary>Cuerpo de jugador desnudo por raza+género (DarCuerpo, igual que CharCreator).</summary>
    private static short CuerpoPorRaza(byte raza, byte genero)
    {
        bool hombre = genero == 1;
        return raza switch
        {
            1 => 1, 2 => 2, 3 => 3,
            4 => (short)(hombre ? 52 : 138),
            5 => (short)(hombre ? 52 : 138),
            6 => (short)(hombre ? 252 : 253),
            _ => 1,
        };
    }

    private static short CabezaPorRaza(byte raza) => (short)(raza <= 1 ? 1 : raza);

    /// <summary>
    /// Daño melee del bot = daño NORMAL del arma equipada leído de obj.dat. Usa el daño PvP del arma
    /// (MinHITPVP/MaxHITPVP) si está definido —los bots pegan a usuarios—, sino el daño base (MinHIT/MaxHIT).
    /// Sin arma (Guerrero a mano limpia) usa el rango base de la clase como fallback.
    /// </summary>
    private static (int min, int max) DanoArma(BotClase cfg)
    {
        if (cfg.WeaponObj > 0)
        {
            var w = ObjData.Get(cfg.WeaponObj);
            if (w.MaxHITPVP > 0) return (w.MinHITPVP, w.MaxHITPVP);
            if (w.MaxHIT > 0)    return (w.MinHIT, w.MaxHIT);
        }
        return (cfg.MinHit, cfg.MaxHit);   // a mano limpia: rango base de la clase
    }

    /// <summary>
    /// Invoca un bot de la clase/raza dada en (map,x,y). raza=0 usa la recomendada de la clase.
    /// Devuelve el NpcInstance o null.
    /// </summary>
    public static NpcManager.NpcInstance Spawn(int map, byte x, byte y, byte clase, byte raza = 0, int owner = 0, byte faccion = 0, byte heading = 0, byte genero = 1)
    {
        if (!_clases.TryGetValue(clase, out var cfg)) return null;
        if (raza < 1 || raza > 6) raza = cfg.RazaDefault;
        if (faccion > 3) faccion = 0;

        // Tope anti-spam: no permitir más de MAX_BOTS vivos (evita saturar el server).
        if (NpcManager.CountBots() >= MAX_BOTS) return null;

        // Vida y maná REALES del juego al nivel 50, según raza+clase (GameLogic.bas, vía Leveling).
        int realHp   = Leveling.VidaObjetivoN50(raza, cfg.Clase);
        int realMana = Leveling.ManaObjetivoN50(raza, cfg.Clase);

        // Reusar la definición si esta clase+raza+facción ya se registró (no acumular entradas en NpcData).
        if (_regIndex.TryGetValue((clase, raza, faccion), out int cached))
        {
            var (fx0, fy0) = NpcManager.FreeTileNear(map, x, y);
            var b0 = NpcManager.SpawnAt(map, cached, fx0, fy0);
            if (b0 != null) { NpcManager.InitBot(b0, owner, RandomNick(), heading); b0.BotFaccion = faccion; b0.BotHealSpell = cfg.HealSpell; b0.BotAtaqueParticula = cfg.AtaqueParticula; b0.MaxMana = b0.MinMana = realMana; TalVezDarEstandarte(b0, faccion); }
            return b0;
        }

        // Apariencia: cuerpo de la armadura sacra si está, sino cuerpo desnudo de la raza.
        short body = CuerpoPorRaza(raza, genero);
        if (cfg.ArmorObj > 0) { int rop = ObjData.Get(cfg.ArmorObj).Ropaje; if (rop > 0) body = (short)rop; }

        // Daño del bot = daño NORMAL del arma equipada según obj.dat (no un valor inventado).
        var (botMin, botMax) = DanoArma(cfg);

        short weaponAnim = cfg.WeaponObj > 0 ? (short)ObjData.Get(cfg.WeaponObj).WeaponAnim : (short)0;
        short shieldAnim = cfg.ShieldObj > 0 ? (short)ObjData.Get(cfg.ShieldObj).ShieldAnim : (short)0;
        short cascoAnim  = cfg.CascoObj  > 0 ? (short)ObjData.Get(cfg.CascoObj).CascoAnim  : (short)0;

        var info = new NpcData.NpcInfo
        {
            Name = "Bot " + cfg.Nombre,
            Body = body, Head = CabezaPorRaza(raza), Heading = 3,
            MaxHP = realHp,
            Attackable = true, Hostil = true, Movement = 0,
            MinHIT = botMin, MaxHIT = botMax,
            PoderAtaque = cfg.PoderAtaque, PoderEvasion = cfg.PoderEvasion,
            WeaponAnim = weaponAnim, ShieldAnim = shieldAnim, CascoAnim = cascoAnim,
            // Auras REALES de los ítems sacros equipados (ObjData.Aura de cada pieza).
            Aura      = cfg.ArmorObj  > 0 ? (short)ObjData.Get(cfg.ArmorObj).Aura  : (short)0,
            AuraArma  = cfg.WeaponObj > 0 ? (short)ObjData.Get(cfg.WeaponObj).Aura : (short)0,
            AuraEscudo= cfg.ShieldObj > 0 ? (short)ObjData.Get(cfg.ShieldObj).Aura : (short)0,
            AuraCasco = cfg.CascoObj  > 0 ? (short)ObjData.Get(cfg.CascoObj).Aura  : (short)0,
            Spells = cfg.Spells,
            Status = StatusDeFaccion(faccion),   // color de nick según facción (caos por defecto)
            GiveEXP = 0, GiveGLD = 0,
            NpcType = 0,
        };

        int idx = BOT_INDEX_BASE + (_nextOffset++);
        NpcData.Register(idx, info);
        _regIndex[(clase, raza, faccion)] = idx;       // cachear para reusar en próximos spawns
        var (fx, fy) = NpcManager.FreeTileNear(map, x, y);
        var bot = NpcManager.SpawnAt(map, idx, fx, fy);
        if (bot != null) { NpcManager.InitBot(bot, owner, RandomNick(), heading); bot.BotFaccion = faccion; bot.BotHealSpell = cfg.HealSpell; bot.BotAtaqueParticula = cfg.AtaqueParticula; bot.MaxMana = bot.MinMana = realMana; TalVezDarEstandarte(bot, faccion); } // dueño + nick + facción + cura + maná + dirección
        return bot;
    }

    // Hechizo Inmovilizar (índice de Hechizos.dat) que SIEMPRE lleva el bot de sparring.
    public const short SPELL_INMOVILIZAR = 24;

    /// <summary>
    /// Invoca un bot de SPARRING PvP: en vez de seguir/proteger al dueño, lo ATACA (se acerca, golpea
    /// cuerpo a cuerpo y lo inmoviliza/le lanza hechizos a distancia con los intervalos reales). Si el
    /// jugador lo paraliza, el bot se remueve solo (fin del test). raza=0 usa la recomendada de la clase.
    /// </summary>
    public static NpcManager.NpcInstance SpawnSpar(int map, byte x, byte y, byte clase, byte raza = 0, int owner = 0, byte heading = 0, bool soloMelee = false)
    {
        var bot = Spawn(map, x, y, clase, raza, owner, faccion: 0, heading: heading);
        if (bot == null) return null;
        bot.BotSpar = true;
        bot.BotAtacar = false;   // su objetivo es el dueño, no el modo "atacar a todos"
        bot.BotSparSoloMelee = soloMelee;   // "no pegar desde cualquier lugar": sólo cuerpo a cuerpo

        // Garantizar que pueda inmovilizar: agrega Inmovilizar (24) a sus hechizos (sin pisar el set base).
        var spells = new List<short>();
        if (bot.Spells != null) spells.AddRange(bot.Spells);
        if (!spells.Contains(SPELL_INMOVILIZAR)) spells.Insert(0, SPELL_INMOVILIZAR);
        bot.Spells = spells.ToArray();
        return bot;
    }

    /// <summary>Activa el modo "atacar" de los bots del jugador (atacan a todos menos a él).</summary>
    public static void Atacar(int ownerUserIndex) => NpcManager.SetBotsAtacar(ownerUserIndex, true);

    /// <summary>Elimina TODOS los bots invocados (de cualquiera). Devuelve cuántos.</summary>
    public static int MatarTodos() => NpcManager.KillAllBots(0);

    /// <summary>Forma en fila a los bots del jugador (se acomodan en una línea detrás suyo).</summary>
    public static void Formar(int ownerUserIndex) => NpcManager.FormarBots(ownerUserIndex);
}
