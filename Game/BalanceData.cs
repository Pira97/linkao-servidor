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
    }

    private static CombateCfg _combate;
    public static CombateCfg Combate { get { EnsureLoaded(); return _combate; } }

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
        };

        Console.WriteLine($"[BalanceData] ModClase + ModRaza + Combate cargado ({(ini != null ? "Balance.dat" : "defaults")}).");
    }

    private static double D(IniFile ini, string sec, string key)
    {
        if (ini == null) return 0;
        string v = ini.Get(sec, key);
        return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>Lee [COMBATE]/key como número; si falta o el .dat no existe, devuelve el default.</summary>
    private static double Dp(IniFile ini, string key, double def)
    {
        if (ini == null) return def;
        string v = ini.Get("COMBATE", key);
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
