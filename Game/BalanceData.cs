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
        Console.WriteLine($"[BalanceData] ModClase + ModRaza cargado ({(ini != null ? "Balance.dat" : "defaults 1.0")}).");
    }

    private static double D(IniFile ini, string sec, string key)
    {
        if (ini == null) return 0;
        string v = ini.Get(sec, key);
        return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
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
