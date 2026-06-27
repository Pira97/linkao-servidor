using System.Globalization;

namespace ServidorCS.Game;

/// <summary>
/// Modificadores de combate por clase (Balance.dat → ModClase, FileIO.bas:724). Multiplicadores que
/// escalan el poder de ataque/evasión/escudo según la clase. Indexado por eClass (1..18).
/// Si Balance.dat falta o no trae un valor, cae a 1.0 (no anula el combate).
/// </summary>
public static class BalanceData
{
    public struct ModClase
    {
        public double Evasion, AtaqueArmas, AtaqueProyectiles, AtaqueWrestling, Escudo, AtaqueArpon;
    }

    /// <summary>
    /// Reglas GLOBALES de combate (sección [COMBATE] de Balance.dat). Antes estaban hardcodeadas en
    /// Combat.cs; ahora se editan en texto y se recargan en caliente con /reloadbalance. Los defaults
    /// son EXACTAMENTE los valores que tenía el código, así nada cambia hasta que se toque un número.
    /// </summary>
    public struct CombateCfg
    {
        public double ArmaduraDefiendePvP; // % (0..1) que absorbe la armadura en PvP. Default 0.25
        public int DanoMinimoPvP;          // piso de daño PvP. Default 5
        public double TopeBurstPvP;        // techo de daño PvP = danoBase * esto. Default 1.5
        public int ImpactoBase;            // base de la curva de acierto. Default 80
        public int ImpactoMin;             // piso de prob. de impacto. Default 40
        public int ImpactoMax;             // techo de prob. de impacto. Default 98
        public double PesoNivel;           // cuánto suma el nivel al poder atq/eva. Default 2.5
        public int NivelBase;              // nivel a partir del cual el nivel empieza a sumar. Default 12
        public int EscalaMagiaPvP;         // daño mágico a usuario escala con esto * nivel. Default 2
        public int EscalaMagiaPvE;         // daño mágico a NPC escala con esto * nivel. Default 3
        public double BonusStatsMax;       // % extra de daño si Fuerza y Agilidad están al máximo. Default 0.07
        public double EscalaMagiaINT;      // daño mágico escala con esto * Inteligencia (%). Default 1.0
        public int DanoMagicoMinPvP;       // piso de daño mágico PvP (0 = sin piso). Default 0
        public int DanoMagicoMaxPvP;       // techo de daño mágico PvP (0 = sin techo). Default 0
        public int DanoMagicoMinPvE;       // piso de daño mágico contra NPCs (0 = sin piso). Default 0
        public int DanoMagicoMaxPvE;       // techo de daño mágico contra NPCs (0 = sin techo). Default 0
    }

    private static CombateCfg _combate;
    public static CombateCfg Combate { get { EnsureLoaded(); return _combate; } }

    /// <summary>
    /// Tiempo de respawn de NPCs (sección [RESPAWN] de Balance.dat). Antes era una constante de 20s
    /// igual para la hormiga y para el Rey Dragón; ahora escala con la exp que da el NPC. Igual que
    /// [COMBATE], se edita en texto y se recarga en caliente con /reloadbalance.
    /// </summary>
    public struct RespawnCfg
    {
        public int Segundos;      // piso: lo que tarda el bicho más débil (exp <= ExpReferencia). Default 45
        public int SegundosMax;   // techo: lo máximo que puede tardar un jefe. Default 600
        public int ExpReferencia; // exp a partir de la cual el tiempo empieza a crecer. Default 300
        public double Escala;     // exponente sobre (exp/ExpReferencia). Default 0.35
        public int Jitter;        // % de variación aleatoria ± aplicada al resultado. Default 20
    }

    private static RespawnCfg _respawn;
    public static RespawnCfg Respawn { get { EnsureLoaded(); return _respawn; } }

    /// <summary>
    /// Cooldowns de golpe y hechizo (sección [INTERVALOS] de Balance.dat). Antes eran const long
    /// en Intervals.cs; ahora se editan en vivo desde el panel GM (BalanceEditor.cs) o a mano en
    /// el .dat + /reloadbalance. Valores en milisegundos, igual que siempre.
    /// </summary>
    public struct IntervalosCfg
    {
        public long Atacar;      // cooldown entre golpes de arma. Default 1200
        public long LanzarSpell; // cooldown entre lanzamientos de hechizo. Default 500
    }

    private static IntervalosCfg _intervalos;
    public static IntervalosCfg Intervalos { get { EnsureLoaded(); return _intervalos; } }

    /// <summary>
    /// Tasas de EXP y ORO globales del servidor (sección [EXP] de Balance.dat). Multiplicadores
    /// PERSISTENTES (sobreviven reinicios) y silenciosos (sin banner/anuncio), independientes del
    /// sistema de "eventos" temporales de Events.cs (ExpMultiplicador/OroMultiplicador) — todos se
    /// multiplican entre sí en Combat.cs. Se editan en vivo desde el panel GM (BalanceEditor.cs) o
    /// a mano + /reloadbalance.
    /// </summary>
    public struct ExpCfg
    {
        public double TasaGlobal;    // EXP. 1.0 = normal. Guardado en el .dat como porcentaje entero (100 = 1.0).
        public double TasaGlobalOro; // ORO. Idem.
    }

    private static ExpCfg _exp;
    public static ExpCfg Exp { get { EnsureLoaded(); return _exp; } }

    /// <summary>Ruta de Balance.dat en disco (para BalanceEditor.Save), o null si no se encontró.</summary>
    public static string FilePath => FindFile();

    // eClass (Declares.bas:149) → nombre de la sección/clave en Balance.dat.
    private static readonly string[] _nombre =
    {
        "", "Clerigo", "Mago", "Guerrero", "Asesino", "Ladron", "Bardo", "Druida", "Gladiador",
        "Paladin", "Cazador", "Pescador", "Herrero", "Leñador", "Minero", "Carpintero", "Sastre",
        "Mercenario", "Nigromante",
    };

    private static ModClase[] _mod;

    public static void Reload() { _mod = null; EnsureLoaded(); }

    public static ModClase Get(int clase)
    {
        EnsureLoaded();
        return (clase >= 1 && clase < _mod.Length) ? _mod[clase] : _mod[3]; // default ≈ Guerrero (1.0)
    }

    // eRaza (Declares.bas): 1=Humano,2=Elfo,3=Drow,4=gnomo,5=enano,6=Orco. Key Balance.dat: "<Raza>DañoPVP".
    private static readonly string[] _raza = { "", "Humano", "Elfo", "Drow", "gnomo", "enano", "Orco" };
    private static double[] _razaDanoPvp;

    /// <summary>Multiplicador de daño PvP por raza (MODRAZA, FileIO.bas:751): DañoPVP/100, clamp [0.5, 1.5].</summary>
    public static double RazaDanoPvp(int raza)
    {
        EnsureLoaded();
        return (raza >= 1 && raza < _razaDanoPvp.Length) ? _razaDanoPvp[raza] : 1.0;
    }

    private static double[] _razaDanoMagicoPvp, _razaDanoMagicoPve;
    private static int[] _razaResistenciaMagica;

    /// <summary>Multiplicador de daño MÁGICO PvP por raza del lanzador ([MODRAZA] "&lt;Raza&gt;DañoMagicoPVP"/100, clamp [0.5, 1.5]).</summary>
    public static double RazaDanoMagicoPvp(int raza)
    {
        EnsureLoaded();
        return (raza >= 1 && raza < _razaDanoMagicoPvp.Length) ? _razaDanoMagicoPvp[raza] : 1.0;
    }

    /// <summary>Multiplicador de daño MÁGICO PvE por raza del lanzador ([MODRAZA] "&lt;Raza&gt;DañoMagicoPVE"/100, clamp [0.5, 1.5]).</summary>
    public static double RazaDanoMagicoPve(int raza)
    {
        EnsureLoaded();
        return (raza >= 1 && raza < _razaDanoMagicoPve.Length) ? _razaDanoMagicoPve[raza] : 1.0;
    }

    /// <summary>Resistencia mágica plana por raza del objetivo ([MODRAZA] "&lt;Raza&gt;ResistenciaMagica", clamp [0, 75]). Se suma a la del equipo.</summary>
    public static int RazaResistenciaMagica(int raza)
    {
        EnsureLoaded();
        return (raza >= 1 && raza < _razaResistenciaMagica.Length) ? _razaResistenciaMagica[raza] : 0;
    }

    private static void EnsureLoaded()
    {
        if (_mod != null) return;
        _mod = new ModClase[_nombre.Length];
        string file = FindFile();
        var ini = file != null ? new IniFile(file) : null;

        for (int i = 1; i < _nombre.Length; i++)
        {
            string n = _nombre[i];
            var m = new ModClase
            {
                Evasion = D(ini, "MODEVASION", n),
                AtaqueArmas = D(ini, "MODATAQUEARMAS", n),
                AtaqueProyectiles = D(ini, "MODATAQUEPROYECTILES", n),
                AtaqueWrestling = D(ini, "MODATAQUEWRESTLING", n),
                Escudo = D(ini, "MODESCUDO", n),
                AtaqueArpon = D(ini, "MODAtaqueArpon", n),
            };
            // Fallback: sin dato → 1.0 (no romper el cálculo de combate).
            if (m.Evasion <= 0) m.Evasion = 1;
            if (m.AtaqueArmas <= 0) m.AtaqueArmas = 1;
            if (m.AtaqueProyectiles <= 0) m.AtaqueProyectiles = 1;
            if (m.AtaqueWrestling <= 0) m.AtaqueWrestling = 1;
            if (m.Escudo <= 0) m.Escudo = 1;
            if (m.AtaqueArpon <= 0) m.AtaqueArpon = 1;
            _mod[i] = m;
        }
        // Multiplicador de daño PvP por raza ([MODRAZA] "<Raza>DañoPVP", default 100 → 1.0; clamp 0.5-1.5).
        _razaDanoPvp = new double[_raza.Length];
        for (int i = 1; i < _raza.Length; i++)
        {
            double v = ini != null ? D(ini, "MODRAZA", _raza[i] + "DañoPVP") : 0;
            if (v <= 0) v = 100;
            double m = v / 100.0;
            _razaDanoPvp[i] = m < 0.5 ? 0.5 : m > 1.5 ? 1.5 : m;
        }
        // Multiplicadores de daño MÁGICO por raza del lanzador ([MODRAZA] "<Raza>DañoMagicoPVP"/"PVE",
        // default 100 → 1.0, clamp 0.5-1.5) y resistencia mágica plana por raza del objetivo
        // ([MODRAZA] "<Raza>ResistenciaMagica", default 0, clamp 0-75). Nuevos, no rompen dats viejos.
        _razaDanoMagicoPvp = new double[_raza.Length];
        _razaDanoMagicoPve = new double[_raza.Length];
        _razaResistenciaMagica = new int[_raza.Length];
        for (int i = 1; i < _raza.Length; i++)
        {
            double vp = ini != null ? D(ini, "MODRAZA", _raza[i] + "DañoMagicoPVP") : 0;
            if (vp <= 0) vp = 100;
            double mp = vp / 100.0;
            _razaDanoMagicoPvp[i] = mp < 0.5 ? 0.5 : mp > 1.5 ? 1.5 : mp;

            double ve = ini != null ? D(ini, "MODRAZA", _raza[i] + "DañoMagicoPVE") : 0;
            if (ve <= 0) ve = 100;
            double me = ve / 100.0;
            _razaDanoMagicoPve[i] = me < 0.5 ? 0.5 : me > 1.5 ? 1.5 : me;

            int rm = (int)(ini != null ? D(ini, "MODRAZA", _raza[i] + "ResistenciaMagica") : 0);
            _razaResistenciaMagica[i] = rm < 0 ? 0 : rm > 75 ? 75 : rm;
        }
        // Reglas globales de combate ([COMBATE]). Cada valor cae a su default histórico si falta.
        // Los porcentajes (ArmaduraDefiendePvP/TopeBurstPvP/BonusStatsMax) se escriben como número
        // entero en el .dat (25, 150, 7) y se convierten a fracción acá.
        _combate = new CombateCfg
        {
            ArmaduraDefiendePvP = Dp(ini, "ArmaduraDefiendePvP", 25) / 100.0,
            DanoMinimoPvP       = (int)Dp(ini, "DanoMinimoPvP", 5),
            TopeBurstPvP        = Dp(ini, "TopeBurstPvP", 150) / 100.0,
            ImpactoBase         = (int)Dp(ini, "ImpactoBase", 80),
            ImpactoMin          = (int)Dp(ini, "ImpactoMin", 40),
            ImpactoMax          = (int)Dp(ini, "ImpactoMax", 98),
            PesoNivel           = Dp(ini, "PesoNivel", 25) / 10.0,   // 25 → 2.5 (el .ini no maneja decimales cómodos)
            NivelBase           = (int)Dp(ini, "NivelBase", 12),
            EscalaMagiaPvP      = (int)Dp(ini, "EscalaMagiaPvP", 2),
            EscalaMagiaPvE      = (int)Dp(ini, "EscalaMagiaPvE", 3),
            BonusStatsMax       = Dp(ini, "BonusStatsMax", 7) / 100.0,
            EscalaMagiaINT      = Dp(ini, "EscalaMagiaINT", 100) / 100.0,   // 100 → 1.0 (mismo peso que 1*INT%)
            DanoMagicoMinPvP    = (int)Dp(ini, "DanoMagicoMinPvP", 0),
            DanoMagicoMaxPvP    = (int)Dp(ini, "DanoMagicoMaxPvP", 0),
            DanoMagicoMinPvE    = (int)Dp(ini, "DanoMagicoMinPvE", 0),
            DanoMagicoMaxPvE    = (int)Dp(ini, "DanoMagicoMaxPvE", 0),
        };

        // Respawn de NPCs ([RESPAWN]). Como en [COMBATE], los decimales se escriben como entero
        // (Escala=35 → 0.35) para que un "0,35" tipeado con coma no se lea como 35.
        _respawn = new RespawnCfg
        {
            Segundos      = (int)Dp(ini, "RESPAWN", "Segundos", 45),
            SegundosMax   = (int)Dp(ini, "RESPAWN", "SegundosMax", 600),
            ExpReferencia = (int)Dp(ini, "RESPAWN", "ExpReferencia", 300),
            Escala        = Dp(ini, "RESPAWN", "Escala", 35) / 100.0,
            Jitter        = (int)Dp(ini, "RESPAWN", "Jitter", 20),
        };
        if (_respawn.Segundos < 1) _respawn.Segundos = 1;
        if (_respawn.SegundosMax < _respawn.Segundos) _respawn.SegundosMax = _respawn.Segundos;
        if (_respawn.ExpReferencia < 1) _respawn.ExpReferencia = 1;
        if (_respawn.Escala < 0) _respawn.Escala = 0;
        if (_respawn.Jitter < 0) _respawn.Jitter = 0;
        if (_respawn.Jitter > 90) _respawn.Jitter = 90;

        // Intervalos ([INTERVALOS]). Piso de 50ms para no permitir un cooldown que rompa el combate.
        _intervalos = new IntervalosCfg
        {
            Atacar      = (long)Dp(ini, "INTERVALOS", "Atacar", 1200),
            LanzarSpell = (long)Dp(ini, "INTERVALOS", "LanzarSpell", 500),
        };
        if (_intervalos.Atacar < 50) _intervalos.Atacar = 50;
        if (_intervalos.LanzarSpell < 50) _intervalos.LanzarSpell = 50;

        // Tasas de EXP/ORO globales ([EXP]). Porcentaje entero igual que ArmaduraDefiendePvP/TopeBurstPvP:
        // 100 → 1.0. Clamp generoso (0.1x a 10x) para no permitir un valor que rompa el balance.
        double tasaExp = Dp(ini, "EXP", "TasaGlobal", 100) / 100.0;
        if (tasaExp < 0.1) tasaExp = 0.1;
        if (tasaExp > 10) tasaExp = 10;
        double tasaOro = Dp(ini, "EXP", "TasaGlobalOro", 100) / 100.0;
        if (tasaOro < 0.1) tasaOro = 0.1;
        if (tasaOro > 10) tasaOro = 10;
        _exp = new ExpCfg { TasaGlobal = tasaExp, TasaGlobalOro = tasaOro };

        Console.WriteLine($"[BalanceData] ModClase + ModRaza + Combate + Respawn + Intervalos + Exp cargado ({(ini != null ? "Balance.dat" : "defaults")}).");
    }

    private static double D(IniFile ini, string sec, string key)
    {
        if (ini == null) return 0;
        string v = ini.Get(sec, key);
        return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>Lee [COMBATE]/key como número; si falta o el .dat no existe, devuelve el default.</summary>
    private static double Dp(IniFile ini, string key, double def) => Dp(ini, "COMBATE", key, def);

    /// <summary>Lee sec/key como número; si falta o el .dat no existe, devuelve el default.</summary>
    private static double Dp(IniFile ini, string sec, string key, double def)
    {
        if (ini == null) return def;
        string v = ini.Get(sec, key);
        return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : def;
    }

    private static string FindFile()
    {
        foreach (var c in new[]
        {
            Path.Combine(DataPaths.Sub("Dat"), "Balance.dat"),
            DataPaths.Root + "Balance.dat",
            Path.Combine(AppContext.BaseDirectory, "Dat", "Balance.dat"),
        })
            if (File.Exists(c)) return c;
        return null;
    }
}
