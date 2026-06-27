namespace ServidorCS.Game;

/// <summary>
/// Sistema de BOTS de prueba (NUEVO, no VB6). Invoca "jugadores" controlados por el server
/// (en realidad NPCs con cuerpo de jugador) de cualquier clase/raza, que pelean contra el
/// invocador. Sirve para probar combate/balance.
///
/// - Cada bot es un NpcInstance hostil (Movement=0 → persigue, melee adyacente; los casters
///   lanzan hechizos a distancia vía la IA de NpcManager).
/// - La apariencia sale de la raza (cuerpo/cabeza de jugador) + el MEJOR equipo craftable real
///   para su clase/raza/nivel (MejorEquipoParaNivel) — nunca un set fijo, así respeta
///   ClasesProhibidas/RazasProhibidas de cada ítem (un Elfo nunca termina con algo que en el
///   juego real no podría llevar puesto).
/// - Se registran en índices altos (BOT_INDEX_BASE+) que no chocan con NPCs.dat.
///
/// PARA AJUSTAR: `BotClase.WeaponObj` en _clases sólo define el ARQUETIPO de arma (melee/arco/
/// arpón, vía su Proyectil) que usa MejorEquipoParaNivel para elegir — no se equipa directo.
/// </summary>
public static class Bots
{
    public const int BOT_INDEX_BASE = 30000;
    private static int _nextOffset = 0;

    // Tope de bots vivos simultáneos (anti-spam: evita saturar el server).
    // 0 = sin límite (chequeo desactivado en Spawn).
    public const int MAX_BOTS = 0;

    // (OBSOLETO) Antes multiplicaba el daño melee de los bots. Ahora el daño sale del arma del
    // obj.dat (ver DanoArma); se deja por compatibilidad pero ya no se usa.
    public const int BOT_DMG_MULT = 5;

    // Cache de definiciones registradas por (clase,raza,faccion,nivel): así spamear NO crea miles
    // de entradas en NpcData (antes cada Spawn registraba una nueva → leak). Reusa el índice.
    // OJO: nivel es parte de la clave a propósito — sin esto, pedir un "Mago nivel 10" después de
    // un "Mago nivel 50" reusaría el NpcData del nivel 50 (HP/Poder pegados al primer nivel pedido).
    private static readonly Dictionary<(byte clase, byte raza, byte faccion, byte nivel), int> _regIndex = new();

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
        // Arquetipo de arma de la clase (ObjIndex de obj.dat, sólo para leer su Proyectil —
        // arco/arpón/melee). NUNCA se equipa directo: el equipo real (armadura/arma/escudo/casco)
        // siempre sale de MejorEquipoParaNivel según clase+raza+nivel, para no ponerle a un bot
        // un ítem con RazasProhibidas que le quede ilegal a su raza. Completar con /editobj.
        public int WeaponObj;
        public short[] Spells;     // hechizos que castea (null = melee puro)
        // Nivel mínimo del BOT (no del hechizo — Hechizos.dat casi no tiene MinLevel poblado)
        // para conocer cada entrada de Spells, mismo índice/longitud que Spells. Sólo lo mira un
        // bot "progresivo" (SpellsHastaNivel); un bot sacro normal conoce TODO Spells desde que
        // se invoca, igual que siempre. null = todos disponibles desde nivel 1.
        public byte[] SpellNiveles;
        public short HealSpell;    // hechizo de cura a aliados (clérigos); 0 = no cura
        public short AtaqueParticula; // partícula al golpear (cazador = flecha explosiva 173); 0 = ninguna
        public int MinHit, MaxHit; // fallback a mano limpia (sin WeaponObj); ver DanoArma
        // Agilidad "base" del bot (VB6 ModBotStats.CrearBot [STATS] Agilidad, ahí con skills fijos
        // en 100). Alimenta PoderAtaque/PoderEvasion vía PoderDeBot, MISMA fórmula/tablas que un
        // jugador real (BalanceData) — así "nivel" escala el poder del bot de verdad, no con un
        // multiplicador inventado. HP/Maná NO usan esto: salen de Leveling.VidaFijaPorNivel/
        // ManaFijaPorNivel (raza+clase+nivel), igual que un personaje real.
        public byte Agilidad;
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
    //
    // Las clases se cargan de Dat/BotClases.dat (formato INI [BOTCLASEn], mismo patrón que
    // NpcData/SpellData) para poder editarlas sin recompilar. Si el archivo no está o no trae
    // ninguna clase válida, se cae a esta misma tabla como fallback embebido (ver SeedDefaults).
    private static Dictionary<byte, BotClase> _clases;

    /// <summary>Recarga BotClases.dat en caliente. Limpia también _regIndex: si no, los bots ya
    /// registrados en NpcData seguirían con la definición vieja aunque el .dat haya cambiado.</summary>
    public static void Reload()
    {
        _clases = null;
        _regIndex.Clear();
        EnsureLoaded();
        Console.WriteLine($"[Bots] Recargado: {_clases?.Count ?? 0} clases.");
    }

    private static void EnsureLoaded()
    {
        if (_clases != null) return;

        string file = FindFile();
        if (file == null) { SeedDefaults(); return; }

        var ini = new IniFile(file);
        if (!ini.Loaded) { SeedDefaults(); return; }

        var loaded = new Dictionary<byte, BotClase>();
        for (int i = 1; i <= 200; i++)
        {
            string sec = "BOTCLASE" + i;
            string nombre = ini.Get(sec, "Nombre");
            if (string.IsNullOrEmpty(nombre)) continue;

            byte clase = (byte)ini.GetInt(sec, "Clase");
            var (spells, spellNiveles) = LoadSpells(ini, sec);
            loaded[clase] = new BotClase
            {
                Clase = clase,
                Nombre = nombre,
                RazaDefault = (byte)ini.GetInt(sec, "RazaDefault"),
                WeaponObj = ini.GetInt(sec, "WeaponObj"),
                MinHit = ini.GetInt(sec, "MinHit"),
                MaxHit = ini.GetInt(sec, "MaxHit"),
                Agilidad = (byte)ini.GetInt(sec, "Agilidad"),
                HealSpell = (short)ini.GetInt(sec, "HealSpell"),
                AtaqueParticula = (short)ini.GetInt(sec, "AtaqueParticula"),
                Spells = spells,
                SpellNiveles = spellNiveles,
            };
        }

        if (loaded.Count == 0)
        {
            Console.WriteLine("[Bots] BotClases.dat vacío o con formato inesperado, usando clases por defecto.");
            SeedDefaults();
            return;
        }
        _clases = loaded;
    }

    /// <summary>Hechizos del bot: LanzaSpells=N + Sp1..SpN (mismo formato que NpcData.LoadSpells) +
    /// Sp{k}Nivel opcional (nivel mínimo del BOT para conocer ese hechizo — sólo lo usan los
    /// progresivos; sin la clave = nivel 1, compatible con un .dat viejo sin esas claves).</summary>
    private static (short[] spells, byte[] niveles) LoadSpells(IniFile ini, string sec)
    {
        int n = ini.GetInt(sec, "LanzaSpells");
        if (n <= 0) return (null, null);
        var list = new List<short>();
        var niveles = new List<byte>();
        for (int k = 1; k <= n; k++)
        {
            short sp = (short)ini.GetInt(sec, "Sp" + k);
            if (sp <= 0) continue;
            byte nv = (byte)ini.GetInt(sec, "Sp" + k + "Nivel");
            list.Add(sp);
            niveles.Add(nv < 1 ? (byte)1 : nv);
        }
        return list.Count > 0 ? (list.ToArray(), niveles.ToArray()) : (null, null);
    }

    private static string FindFile()
    {
        foreach (var c in new[]
        {
            Path.Combine(DataPaths.Sub("Dat"), "BotClases.dat"),
            DataPaths.Root + "BotClases.dat",
            Path.Combine(AppContext.BaseDirectory, "Dat", "BotClases.dat"),
            Path.Combine(AppContext.BaseDirectory, "BotClases.dat"),
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>Fallback embebido (mismos 10 valores que había hardcodeados antes de migrar a
    /// Dat/BotClases.dat, ahora con Agilidad en vez de PoderAtaque/PoderEvasion flat — ver
    /// PoderDeBot — y con SpellNiveles: progresión de hechizos por nivel, ver SpellsHastaNivel):
    /// así un despliegue sin el .dat sigue funcionando igual que hoy, INCLUYENDO la progresión.
    /// WeaponObj es sólo un arquetipo de arma (arco/arpón/melee vía su Proyectil) para que
    /// MejorEquipoParaNivel busque el tipo correcto — el equipo real (armadura/arma/escudo/casco)
    /// SIEMPRE sale de ahí según clase+raza+nivel, nunca de un set fijo (así respeta
    /// RazasProhibidas/ClasesProhibidas de cada ítem para TODOS los bots, no sólo progresivos).
    /// Hechizos usados (índice de Hechizos.dat): 2=Proyectil Mágico, 9=Paralizar, 24=Inmovilizar,
    /// 25=Apocalipsis, 34=Implosión, 52=Juicio Final, 69=Descarga Flamígera, 87=Castigo Divino,
    /// 93=Descarga Eléctrica, 121=Dardo Arcano, 122=Centella Menor.</summary>
    private static void SeedDefaults()
    {
        Console.WriteLine("[Bots] Dat/BotClases.dat no encontrado, usando clases hardcodeadas por defecto.");
        _clases = new Dictionary<byte, BotClase>
        {
            [2]  = new BotClase { Clase = 2,  Nombre = "Mago",       RazaDefault = 4, WeaponObj = 1147, MinHit = 1,  MaxHit = 5,   Agilidad = 24, Spells = new short[]{ 2, 121, 122, 25, 34, 52 }, SpellNiveles = new byte[]{ 1, 15, 22, 36, 42, 48 } },
            [18] = new BotClase { Clase = 18, Nombre = "Nigromante", RazaDefault = 3, WeaponObj = 1181, MinHit = 60, MaxHit = 90,  Agilidad = 22, Spells = new short[]{ 2, 121, 24, 34 }, SpellNiveles = new byte[]{ 1, 15, 20, 36 } },
            [1]  = new BotClase { Clase = 1,  Nombre = "Clerigo",    RazaDefault = 1, WeaponObj = 747,  MinHit = 60, MaxHit = 95,  Agilidad = 22, Spells = new short[]{ 2, 9, 24, 69 }, SpellNiveles = new byte[]{ 1, 18, 22, 32 }, HealSpell = 71 },
            [7]  = new BotClase { Clase = 7,  Nombre = "Druida",     RazaDefault = 1, WeaponObj = 1252, MinHit = 55, MaxHit = 85,  Agilidad = 22, Spells = new short[]{ 2, 121, 24 }, SpellNiveles = new byte[]{ 1, 15, 22 } },
            [3]  = new BotClase { Clase = 3,  Nombre = "Guerrero",   RazaDefault = 6, WeaponObj = 0,    MinHit = 80, MaxHit = 120, Agilidad = 24, Spells = null },
            [4]  = new BotClase { Clase = 4,  Nombre = "Asesino",    RazaDefault = 3, WeaponObj = 740,  MinHit = 70, MaxHit = 110, Agilidad = 32, Spells = null },
            [6]  = new BotClase { Clase = 6,  Nombre = "Bardo",      RazaDefault = 1, WeaponObj = 1333, MinHit = 65, MaxHit = 100, Agilidad = 26, Spells = new short[]{ 2, 121, 24 }, SpellNiveles = new byte[]{ 1, 15, 22 } },
            [9]  = new BotClase { Clase = 9,  Nombre = "Paladin",    RazaDefault = 1, WeaponObj = 1257, MinHit = 75, MaxHit = 115, Agilidad = 24, Spells = new short[]{ 2, 24, 93, 87 }, SpellNiveles = new byte[]{ 1, 18, 26, 36 } },
            [10] = new BotClase { Clase = 10, Nombre = "Cazador",    RazaDefault = 2, WeaponObj = 899,  MinHit = 80, MaxHit = 120, Agilidad = 30, Spells = null, AtaqueParticula = 173 },
            [17] = new BotClase { Clase = 17, Nombre = "Mercenario", RazaDefault = 5, WeaponObj = 1257, MinHit = 75, MaxHit = 115, Agilidad = 26, Spells = null },
        };
    }

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

    public static IEnumerable<BotClase> Clases { get { EnsureLoaded(); return _clases.Values; } }
    public static bool ClaseValida(byte clase) { EnsureLoaded(); return _clases.ContainsKey(clase); }

    /// <summary>Mapa nombre→clase (para el comando /bot mago, /bot guerrero, etc.).</summary>
    public static byte ClasePorNombre(string nombre)
    {
        EnsureLoaded();
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
    /// PoderAtaque/PoderEvasion REALES para un nivel dado: misma fórmula y mismas tablas de
    /// balance que un jugador (Combat.cs PoderAtaqueBase/PoderEvasion), NO un multiplicador
    /// inventado. Skill fijo en 100 (el bot está "maxeado", igual que el VB6 original que
    /// hardcodeaba .skills(i)=100 para los bots); el tipo de arma decide qué multiplicador de
    /// clase (Armas/Proyectiles/Arpón/Wrestling) aplica, igual que PoderAtaqueUsuario.
    /// </summary>
    private static (int poderAtaque, int poderEvasion) PoderDeBot(BotClase cfg, byte nivel)
    {
        const int skill = 100; // bot "maxeado": mismo tramo (>=91) que un jugador con la skill al tope
        int ag = cfg.Agilidad;
        var mc = BalanceData.Get(cfg.Clase);
        var cc = BalanceData.Combate;
        double nivelBonus = cc.PesoNivel * Math.Max(nivel - cc.NivelBase, 0);

        double modClaseAtaque;
        if (cfg.WeaponObj > 0)
        {
            int proy = ObjData.Get(cfg.WeaponObj).Proyectil;
            modClaseAtaque = proy == 1 ? mc.AtaqueProyectiles : proy == 2 ? mc.AtaqueArpon : mc.AtaqueArmas;
        }
        // VB6 BotPoderAtaqueWrestling (SistemaCombate.bas:226) usa el multiplicador AtaqueArmas,
        // NO AtaqueWrestling — misma rareza ya preservada para jugadores (Combat.cs PoderAtaqueWrestling).
        else modClaseAtaque = mc.AtaqueArmas;

        double t = skill + 3 * ag; // sk>=91 siempre (skill fijo en 100): t = sk + 3*ag
        int poderAtaque = (int)(t * modClaseAtaque + nivelBonus);

        // Evasión: fórmula ESPECÍFICA de bots de VB6 (BotPoderEvasion, ModBotSistCombate.bas), SIN
        // el ×0.5 final que sí tiene la fórmula de jugadores (Combat.cs PoderEvasion) — a pedido
        // explícito: los bots evaden más fácil que un jugador equivalente, fiel al código VB6 tal
        // cual estaba escrito (aunque nunca se ejecutó/balanceó en una partida real).
        double lTemp = (skill + skill / 33.0 * ag) * mc.Evasion;
        int poderEvasion = (int)(lTemp + nivelBonus);

        return (poderAtaque, poderEvasion);
    }

    /// <summary>Hechizos que un bot "progresivo" YA conoce a "nivel" (subconjunto de cfg.Spells
    /// filtrado por cfg.SpellNiveles). Los bots sacro (no progresivos) ignoran esto y usan
    /// cfg.Spells completo desde que se invocan, como siempre. null-safe: sin SpellNiveles
    /// cargado, devuelve todo cfg.Spells (mismo comportamiento que antes de esta feature).</summary>
    private static short[] SpellsHastaNivel(BotClase cfg, byte nivel)
    {
        if (cfg.Spells == null) return null;
        if (cfg.SpellNiveles == null) return cfg.Spells;
        var list = new List<short>(cfg.Spells.Length);
        for (int i = 0; i < cfg.Spells.Length; i++)
            if (i >= cfg.SpellNiveles.Length || cfg.SpellNiveles[i] <= nivel) list.Add(cfg.Spells[i]);
        return list.Count > 0 ? list.ToArray() : null;
    }

    /// <summary>Arma el array de Drops (100% de caída) con las piezas REALMENTE equipadas (sacro o,
    /// si es progresivo, lo elegido por MejorEquipoParaNivel — por eso recibe ObjIndex sueltos y
    /// no un BotClase entero).</summary>
    private static (short objIndex, int amount, double prob)[] DropsDelSet(int armorObj, int weaponObj, int shieldObj, int cascoObj)
    {
        var drops = new List<(short, int, double)>(4);
        if (armorObj  > 0) drops.Add(((short)armorObj,  1, 100));
        if (weaponObj > 0) drops.Add(((short)weaponObj, 1, 100));
        if (shieldObj > 0) drops.Add(((short)shieldObj, 1, 100));
        if (cascoObj  > 0) drops.Add(((short)cascoObj,  1, 100));
        return drops.ToArray();
    }

    /// <summary>
    /// Mejor equipo CRAFTEABLE (herrero/sastre) que un personaje de "nivel" podría llevar puesto —
    /// lo usa TODO bot al invocarse (sacro y progresivo), en vez de un set fijo por clase que
    /// ignoraba la raza. No hay tabla de nivel en Dat/ArmasHerrero.dat y afines (son listas planas
    /// de ObjIndex sin nivel) así que se recorre obj.dat entero vía ObjData.
    /// Candidato: craftable (SkHerreria o SkSastreria > 0), MinELV &lt;= nivel, no prohibido para
    /// la clase/raza del bot (ClasesProhibidas/RazasProhibidas de ObjData — así un Elfo nunca
    /// termina con un ítem que en el juego real no podría ni ponerse), y con el rol correcto
    /// (mismos campos que ya usa Spawn/DanoArma: Ropaje=armadura, WeaponAnim=arma con el MISMO
    /// tipo de proyectil que el arquetipo de la clase (BotClase.WeaponObj) —para no convertir un
    /// Cazador arquero en espadachín—, ShieldAnim=escudo, CascoAnim=casco). En cada rol se queda
    /// con el de mayor MinELV (mejor gear alcanzable),
    /// desempate por MaxDef/MaxHIT. 0 = no hay nada craftable de ese rol para ese nivel/clase/raza.
    /// </summary>
    private static (int armor, int weapon, int shield, int casco) MejorEquipoParaNivel(byte clase, byte raza, byte nivel, int proyectilArmaSacra)
    {
        int bestArmorObj = 0, bestArmorMinElv = -1, bestArmorDef = -1;
        int bestWeaponObj = 0, bestWeaponMinElv = -1, bestWeaponHit = -1;
        int bestShieldObj = 0, bestShieldMinElv = -1, bestShieldDef = -1;
        int bestCascoObj = 0, bestCascoMinElv = -1, bestCascoDef = -1;

        for (int i = 1; i <= ObjData.Count; i++)
        {
            var o = ObjData.Get(i);
            if (o.Name == null) continue;
            if (o.SkHerreria <= 0 && o.SkSastreria <= 0) continue;   // no craftable
            if (o.MinELV <= 0 || o.MinELV > nivel) continue;
            if (o.ClasesProhibidas != null && Array.IndexOf(o.ClasesProhibidas, (int)clase) >= 0) continue;
            if (o.RazasProhibidas != null && Array.IndexOf(o.RazasProhibidas, (int)raza) >= 0) continue;

            if (o.Ropaje > 0)
            {
                if (o.MinELV > bestArmorMinElv) { bestArmorObj = i; bestArmorMinElv = o.MinELV; bestArmorDef = o.MaxDef; }
                else if (o.MinELV == bestArmorMinElv && o.MaxDef > bestArmorDef) { bestArmorObj = i; bestArmorDef = o.MaxDef; }
            }
            if (o.WeaponAnim > 0 && o.Proyectil == proyectilArmaSacra)
            {
                if (o.MinELV > bestWeaponMinElv) { bestWeaponObj = i; bestWeaponMinElv = o.MinELV; bestWeaponHit = o.MaxHIT; }
                else if (o.MinELV == bestWeaponMinElv && o.MaxHIT > bestWeaponHit) { bestWeaponObj = i; bestWeaponHit = o.MaxHIT; }
            }
            if (o.ShieldAnim > 0)
            {
                if (o.MinELV > bestShieldMinElv) { bestShieldObj = i; bestShieldMinElv = o.MinELV; bestShieldDef = o.MaxDef; }
                else if (o.MinELV == bestShieldMinElv && o.MaxDef > bestShieldDef) { bestShieldObj = i; bestShieldDef = o.MaxDef; }
            }
            if (o.CascoAnim > 0)
            {
                if (o.MinELV > bestCascoMinElv) { bestCascoObj = i; bestCascoMinElv = o.MinELV; bestCascoDef = o.MaxDef; }
                else if (o.MinELV == bestCascoMinElv && o.MaxDef > bestCascoDef) { bestCascoObj = i; bestCascoDef = o.MaxDef; }
            }
        }

        return (bestArmorObj, bestWeaponObj, bestShieldObj, bestCascoObj);
    }

    /// <summary>Da EXP a un bot "progresivo" por matar un NPC (golpe final se lleva todo el
    /// GiveEXP, sin repartir — los bots no arman party). No hace nada si el bot no es progresivo.
    /// Sube de nivel (recalcula stats Y se re-equipa) tantas veces como la EXP alcance.</summary>
    public static void DarExpABot(NpcManager.NpcInstance bot, int exp)
    {
        if (bot == null || !bot.BotLeveling || exp <= 0) return;
        bot.BotExp += exp;
        while (bot.BotNivelActual < Leveling.STAT_MAXELV && bot.BotExp >= Leveling.ELU(bot.BotNivelActual))
        {
            bot.BotExp -= Leveling.ELU(bot.BotNivelActual);
            bot.BotNivelActual++;
            SubirNivelBot(bot);
        }
    }

    /// <summary>Recalcula HP/Maná/Poder (mismas fórmulas que un Spawn nuevo, ver PoderDeBot) y
    /// re-equipa a un bot progresivo para su BotNivelActual actual, difundiendo el cambio de
    /// apariencia en vivo (mismo mecanismo que SetBotWeaponAnim — no hace falta recrear el NPC).</summary>
    private static void SubirNivelBot(NpcManager.NpcInstance bot)
    {
        EnsureLoaded();
        if (!_clases.TryGetValue(bot.BotClaseId, out var cfg)) return;
        byte nivel = bot.BotNivelActual;

        bot.MaxHP = Leveling.VidaFijaPorNivel(bot.BotRaza, cfg.Clase, nivel);
        if (bot.MinHP > bot.MaxHP) bot.MinHP = bot.MaxHP;
        bot.MaxMana = Leveling.ManaFijaPorNivel(bot.BotRaza, cfg.Clase, nivel);
        if (bot.MinMana > bot.MaxMana) bot.MinMana = bot.MaxMana;

        var (poderAtaque, poderEvasion) = PoderDeBot(cfg, nivel);
        bot.PoderAtaque = poderAtaque; bot.PoderEvasion = poderEvasion;

        // Desbloquea los hechizos que correspondan a este nivel (ver SpellsHastaNivel/BotClases.dat).
        bot.Spells = SpellsHastaNivel(cfg, nivel);

        int proySacra = cfg.WeaponObj > 0 ? ObjData.Get(cfg.WeaponObj).Proyectil : 0;
        var (armorObj, weaponObj, shieldObj, cascoObj) = MejorEquipoParaNivel(cfg.Clase, bot.BotRaza, nivel, proySacra);

        short body = CuerpoPorRaza(bot.BotRaza, 1);
        if (armorObj > 0) { int rop = ObjData.Get(armorObj).Ropaje; if (rop > 0) body = (short)rop; }
        bot.Body = body;
        bot.WeaponAnim = weaponObj > 0 ? (short)ObjData.Get(weaponObj).WeaponAnim : (short)0;
        bot.ShieldAnim = shieldObj > 0 ? (short)ObjData.Get(shieldObj).ShieldAnim : (short)0;
        bot.CascoAnim  = cascoObj  > 0 ? (short)ObjData.Get(cascoObj).CascoAnim  : (short)0;
        bot.EquipArmorObj = armorObj; bot.EquipShieldObj = shieldObj; bot.EquipCascoObj = cascoObj;

        var cfgConArma = cfg; cfgConArma.WeaponObj = weaponObj;
        var (hitMin, hitMax) = DanoArma(cfgConArma);
        bot.MinHIT = hitMin; bot.MaxHIT = hitMax;

        NpcManager.BroadcastNpcAppearance(bot.Map, bot);

        // Mismo efecto (sonido + partícula) que ve un jugador al subir de nivel.
        Combat.LevelUpEffect(bot.Map, bot.X, bot.Y, bot.CharIndex, nivel);
    }

    /// <summary>
    /// Invoca un bot de la clase/raza dada en (map,x,y). raza=0 usa la recomendada de la clase.
    /// nivel (1-50, default 50) escala HP/Maná/PoderAtaque/PoderEvasion con las MISMAS fórmulas
    /// que un jugador real de ese nivel (Leveling + PoderDeBot) — no es un multiplicador inventado.
    /// El equipo (armadura/arma/escudo/casco) SIEMPRE es el mejor craftable real para clase/raza/
    /// nivel (MejorEquipoParaNivel) — no hay un set fijo por clase. leveling=true: bot
    /// "progresivo" — además sube de nivel matando NPCs de verdad (ver Bots.DarExpABot) y se
    /// re-equipa solo al subir. Un bot progresivo nunca comparte el caché de definiciones (su
    /// equipo y nivel van a cambiar con el tiempo, no es una definición estática reusable).
    /// Devuelve el NpcInstance o null.
    /// </summary>
    // smart: pasa a NpcManager.SpawnAt para que NpcInstance.BotSmart quede en true DESDE ANTES del
    // primer CharacterCreate (ver el comentario grande en SpawnAt) — evita el "teletransporte" del
    // primer spawn. Poner bot.BotSmart=true DESPUÉS de que Spawn() retorna (como hacía el caller
    // antes) llega tarde: ese primer broadcast ya salió sin el marcador de protocolo.
    public static NpcManager.NpcInstance Spawn(int map, byte x, byte y, byte clase, byte raza = 0, int owner = 0, byte faccion = 0, byte heading = 0, byte genero = 1, byte nivel = 50, bool leveling = false, bool smart = false)
    {
        EnsureLoaded();
        if (!_clases.TryGetValue(clase, out var cfg)) return null;
        if (raza < 1 || raza > 6) raza = cfg.RazaDefault;
        if (faccion > 3) faccion = 0;
        if (nivel < 1 || nivel > 50) nivel = 50;

        // Tope anti-spam: no permitir más de MAX_BOTS vivos (evita saturar el server). MAX_BOTS=0 = sin límite.
        if (MAX_BOTS > 0 && NpcManager.CountBots() >= MAX_BOTS) return null;

        // Vida y maná REALES del juego para ESE nivel, según raza+clase (GameLogic.bas, vía Leveling).
        int realHp   = Leveling.VidaFijaPorNivel(raza, cfg.Clase, nivel);
        int realMana = Leveling.ManaFijaPorNivel(raza, cfg.Clase, nivel);

        // Equipo: SIEMPRE lo mejor craftable para clase+raza+nivel (MejorEquipoParaNivel) — nada
        // de un set "sacro" fijo que ignore la raza (ítems con RazasProhibidas le quedarían mal
        // puestos a la mitad de las razas). WeaponObj de la clase se usa sólo como pista de
        // arquetipo de arma (Proyectil: arco/arpón/melee), nunca se equipa directo.
        int proySacra = cfg.WeaponObj > 0 ? ObjData.Get(cfg.WeaponObj).Proyectil : 0;
        var (armorObj, weaponObj, shieldObj, cascoObj) = MejorEquipoParaNivel(cfg.Clase, raza, nivel, proySacra);

        // Reusar la definición si esta clase+raza+facción+nivel ya se registró (no acumular entradas
        // en NpcData). Los progresivos NUNCA pasan por acá: su equipo/nivel cambia con el tiempo.
        if (!leveling && _regIndex.TryGetValue((clase, raza, faccion, nivel), out int cached))
        {
            var (fx0, fy0) = NpcManager.FreeTileNear(map, x, y);
            var b0 = NpcManager.SpawnAt(map, cached, fx0, fy0, botSmart: smart);
            if (b0 != null) { NpcManager.InitBot(b0, owner, RandomNick(), heading); b0.BotFaccion = faccion; b0.BotHealSpell = cfg.HealSpell; b0.BotAtaqueParticula = cfg.AtaqueParticula; b0.MaxMana = b0.MinMana = realMana; b0.EquipArmorObj = armorObj; b0.EquipShieldObj = shieldObj; b0.EquipCascoObj = cascoObj; TalVezDarEstandarte(b0, faccion); }
            return b0;
        }

        // Apariencia: cuerpo de la armadura elegida si está, sino cuerpo desnudo de la raza.
        short body = CuerpoPorRaza(raza, genero);
        if (armorObj > 0) { int rop = ObjData.Get(armorObj).Ropaje; if (rop > 0) body = (short)rop; }

        // Daño del bot = daño NORMAL del arma equipada según obj.dat (no un valor inventado).
        var cfgConArma = cfg; cfgConArma.WeaponObj = weaponObj;
        var (botMin, botMax) = DanoArma(cfgConArma);
        // Poder de ataque/evasión REALES para este nivel (ver PoderDeBot).
        var (poderAtaque, poderEvasion) = PoderDeBot(cfg, nivel);

        short weaponAnim = weaponObj > 0 ? (short)ObjData.Get(weaponObj).WeaponAnim : (short)0;
        short shieldAnim = shieldObj > 0 ? (short)ObjData.Get(shieldObj).ShieldAnim : (short)0;
        short cascoAnim  = cascoObj  > 0 ? (short)ObjData.Get(cascoObj).CascoAnim  : (short)0;

        var info = new NpcData.NpcInfo
        {
            Name = "Bot " + cfg.Nombre,
            Body = body, Head = CabezaPorRaza(raza), Heading = 3,
            MaxHP = realHp,
            Attackable = true, Hostil = true, Movement = 0,
            MinHIT = botMin, MaxHIT = botMax,
            PoderAtaque = poderAtaque, PoderEvasion = poderEvasion,
            WeaponAnim = weaponAnim, ShieldAnim = shieldAnim, CascoAnim = cascoAnim,
            // Auras REALES de las piezas equipadas (ObjData.Aura de cada pieza).
            Aura      = armorObj  > 0 ? (short)ObjData.Get(armorObj).Aura  : (short)0,
            AuraArma  = weaponObj > 0 ? (short)ObjData.Get(weaponObj).Aura : (short)0,
            AuraEscudo= shieldObj > 0 ? (short)ObjData.Get(shieldObj).Aura : (short)0,
            AuraCasco = cascoObj  > 0 ? (short)ObjData.Get(cascoObj).Aura  : (short)0,
            // Progresivo: sólo los hechizos ya desbloqueados a "nivel" (SpellsHastaNivel). Sacro:
            // el set completo de siempre, sin importar nivel.
            Spells = leveling ? SpellsHastaNivel(cfg, nivel) : cfg.Spells,
            Status = StatusDeFaccion(faccion),   // color de nick según facción (caos por defecto)
            GiveEXP = 0, GiveGLD = 0,
            NpcType = 0,
            // Al morir, el bot suelta lo que tiene puesto (reusa Combat.TirarDrops, igual que
            // cualquier NPC con Drops.dat): armadura/arma/escudo/casco, cada uno 100% de caída.
            Drops = DropsDelSet(armorObj, weaponObj, shieldObj, cascoObj),
        };

        int idx = BOT_INDEX_BASE + (_nextOffset++);
        NpcData.Register(idx, info);
        if (!leveling) _regIndex[(clase, raza, faccion, nivel)] = idx;   // progresivos no se cachean
        var (fx, fy) = NpcManager.FreeTileNear(map, x, y);
        var bot = NpcManager.SpawnAt(map, idx, fx, fy, botSmart: smart);
        if (bot != null)
        {
            NpcManager.InitBot(bot, owner, RandomNick(), heading);
            bot.BotFaccion = faccion; bot.BotHealSpell = cfg.HealSpell; bot.BotAtaqueParticula = cfg.AtaqueParticula;
            bot.MaxMana = bot.MinMana = realMana;
            bot.EquipArmorObj = armorObj; bot.EquipShieldObj = shieldObj; bot.EquipCascoObj = cascoObj;
            if (leveling) { bot.BotLeveling = true; bot.BotNivelActual = nivel; bot.BotExp = 0; bot.BotClaseId = clase; bot.BotRaza = raza; }
            TalVezDarEstandarte(bot, faccion);
        }
        return bot;
    }

    // Hechizo Inmovilizar (índice de Hechizos.dat) que SIEMPRE lleva el bot de sparring.
    public const short SPELL_INMOVILIZAR = 24;

    /// <summary>
    /// Invoca un bot de SPARRING PvP: en vez de seguir/proteger al dueño, lo ATACA (se acerca, golpea
    /// cuerpo a cuerpo y lo inmoviliza/le lanza hechizos a distancia con los intervalos reales). Si el
    /// jugador lo paraliza, el bot se remueve solo (fin del test). raza=0 usa la recomendada de la clase.
    /// </summary>
    public static NpcManager.NpcInstance SpawnSpar(int map, byte x, byte y, byte clase, byte raza = 0, int owner = 0, byte heading = 0, bool soloMelee = false, byte nivel = 50)
    {
        var bot = Spawn(map, x, y, clase, raza, owner, faccion: 0, heading: heading, nivel: nivel);
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

    // ========================================================================================
    //  POBLACIÓN DEL MUNDO (PoblarMundo, NUEVO): 50 bots progresivos por facción (Armada/Milicia/
    //  Caos, 150 en total), repartidos entre su ciudad y los dungeons donde esa facción ya
    //  "pertenece" (misma rotación 2-de-3 que DungeonBots.Rotacion), cada uno con el nivel real
    //  de la zona donde nace. Todos BotLeveling=true: cazan NPCs, suben de nivel y se re-equipan
    //  solos, y se atacan entre sí si se cruzan con uno de facción rival (ver
    //  NpcManager.TickBotLeveling/NearestRivalFaccionBot). Visibles en el panel espía vía
    //  NpcManager.BotsEspectables().
    // ========================================================================================

    // (map, nivelMin, nivelMax) de los 25 mapas de dungeon (mismo orden/agrupación que
    // DungeonBots.Mapas: Newbie N1/N2, Dragón, Gaugin, Marabel, Veriil x6, Zero x3, Cristal x3,
    // Fárzhë x5, Krëwh x2). CALIBRACIÓN PROPIA: sólo Gaugin(207) y Fárzhë(755/756/760) tienen
    // rango real registrado en MapasPorNivel.cs; el resto se interpola por posición (el array ya
    // progresa de más fácil a más difícil, mismo orden que el comentario de DungeonBots).
    // Ajustable sin recompilar no es posible acá (es código, no un .dat) — si algún tramo se
    // siente mal, es un solo array para tocar.
    private static readonly (int map, byte min, byte max)[] MapasPoblacion =
    {
        (37,  1, 10), (208, 1, 15),                          // Newbie N1/N2
        (48, 10, 20),                                        // Dragón
        (207, 20, 37),                                       // Gaugin (real: MapasPorNivel.cs)
        (115, 20, 28), (116, 22, 30),                        // Marabel 1-2
        (140, 25, 30), (141, 27, 32), (142, 29, 34),         // Veriil 1-3
        (143, 31, 36), (144, 33, 38), (145, 35, 40),         // Veriil 4-6
        (209, 30, 38), (210, 32, 40), (211, 34, 40),         // Zero 1-3
        (230, 35, 42), (231, 37, 44), (232, 39, 45),         // Cristal 1-3
        (754, 35, 45),                                       // Fárzhë piso1
        (755, 35, 50),                                       // Fárzhë especial (real: MapasPorNivel.cs)
        (756, 40, 50),                                       // Fárzhë piso3 (real: MapasPorNivel.cs)
        (757, 40, 48),                                       // Fárzhë piso4
        (760, 45, 50),                                       // Fárzhë piso5 (real: MapasPorNivel.cs)
        (758, 45, 50), (759, 47, 50),                        // Krëwh 1-2
    };

    // Misma rotación 2-de-3 facciones por dungeon que ya usa DungeonBots.Rotacion (por índice en
    // MapasPoblacion): una facción sólo puebla los dungeons donde ya "pertenece" en el mundo.
    private static readonly (byte a, byte b)[] RotacionPoblacion = { (1, 2), (2, 3), (3, 1) };

    private static readonly Random _poblacionRng = new();

    /// <summary>
    /// Invoca 50 bots progresivos por facción (Armada=1/Milicia=2/Caos=3, 150 en total): 10 en
    /// su ciudad (GuerraFacciones.CiudadDeFaccion, nivel 1-15) y 40 repartidos al azar entre los
    /// dungeons de MapasPoblacion donde esa facción "pertenece" por RotacionPoblacion, con el
    /// nivel real de esa zona. Todos BotLeveling=true + BotFaccion=f: cazan NPCs, suben de nivel,
    /// y pelean con cualquier bot de facción rival que se crucen (NpcManager.TickBotLeveling).
    /// NO es idempotente: llamarlo de nuevo suma otros 150 (usar Bots.MatarTodos antes si se
    /// quiere repoblar limpio). Devuelve cuántos se lograron invocar.
    /// </summary>
    public static int PoblarMundo()
    {
        EnsureLoaded();
        var clases = _clases.Values.ToList();
        if (clases.Count == 0) return 0;

        int total = 0;
        for (byte faccion = 1; faccion <= 3; faccion++)
        {
            var ciudad = CityData.Get(GuerraFacciones.CiudadDeFaccion(faccion));
            if (ciudad.Map > 0)
            {
                for (int i = 0; i < 10; i++)
                {
                    byte nivel = (byte)_poblacionRng.Next(1, 16);
                    if (SpawnPoblador(ciudad.Map, ciudad.X, ciudad.Y, 10, faccion, clases, nivel) != null) total++;
                }
            }

            var dungeonsDeFaccion = new List<int>();
            for (int i = 0; i < MapasPoblacion.Length; i++)
            {
                var (a, b) = RotacionPoblacion[i % RotacionPoblacion.Length];
                if (a == faccion || b == faccion) dungeonsDeFaccion.Add(i);
            }
            if (dungeonsDeFaccion.Count == 0) continue;

            for (int i = 0; i < 40; i++)
            {
                int idx = dungeonsDeFaccion[_poblacionRng.Next(dungeonsDeFaccion.Count)];
                var (map, min, max) = MapasPoblacion[idx];
                byte nivel = (byte)_poblacionRng.Next(min, max + 1);
                if (SpawnPoblador(map, 50, 50, 45, faccion, clases, nivel) != null) total++;
            }
        }
        Console.WriteLine($"[Bots] PoblarMundo: {total} bots invocados.");
        return total;
    }

    /// <summary>Invoca un bot progresivo de clase al azar en un tile libre cerca de (cx,cy).</summary>
    private static NpcManager.NpcInstance SpawnPoblador(int map, int cx, int cy, int radio, byte faccion, List<BotClase> clases, byte nivel)
    {
        var (sx, sy) = GuerraFacciones.TileLibreCerca(map, (byte)cx, (byte)cy, radio);
        if (sx == 0) return null;
        byte clase = clases[_poblacionRng.Next(clases.Count)].Clase;
        byte heading = (byte)(1 + _poblacionRng.Next(4));
        var bot = Spawn(map, sx, sy, clase, raza: 0, owner: 0, faccion: faccion, heading: heading, nivel: nivel, leveling: true);
        // Igual que GuerraFacciones.SpawnGuerrero: algunos nacen con montura o alas (cosmético +
        // recuperan esa apariencia solos al bajarse de una barca, ver DarMonturaOAlas).
        if (bot != null) GuerraFacciones.DarMonturaOAlas(bot);
        return bot;
    }
}
