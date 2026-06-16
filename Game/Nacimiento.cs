namespace ServidorCS.Game;

/// <summary>
/// Objetos iniciales por clase (LoadObjNacimiento, General.bas:2640). Lee Dat/NACIMIENTO.ini:
/// sección [clase] con CantidadObj + ObjIndexN/ObjAmountN/ObjEquippedN. Lo usa CharCreator para
/// armar el inventario de nacimiento (RellenarInventario, TCP.bas:2535) 1:1.
/// Convención del archivo: slot 3 = arma equipada, slot 4 = armadura (ObjIndex4=0 → se setea por raza).
/// </summary>
public static class Nacimiento
{
    public readonly record struct StartItem(short ObjIndex, int Amount, bool Equipped);

    private static readonly Dictionary<int, List<StartItem>> _porClase = new();
    private static readonly Dictionary<int, short> _armaduraRaza = new();
    private static bool _loaded;

    // Pocion de mana newbie (azules). Solo tiene sentido en clases magicas.
    private const short POCION_AZUL = 1602;
    // Clases magicas (deben coincidir con CharCreator.ClaseMagica): Clerigo,Mago,Asesino,Bardo,Druida,Paladin,Nigromante.
    private static readonly HashSet<int> _clasesMagicas = new() { 1, 2, 4, 6, 7, 9, 18 };

    public static List<StartItem> Get(int clase)
    {
        if (!_loaded) Load();
        return _porClase.TryGetValue(clase, out var l) ? l : new List<StartItem>();
    }

    /// <summary>
    /// Armadura de nacimiento por raza. Si Dat/NACIMIENTO.ini define la seccion [ARMADURA_RAZA]
    /// (RazaN=ObjIndex) se usa ese valor; si no, cae al <paramref name="fallback"/> hardcodeado.
    /// </summary>
    public static short ArmaduraRaza(byte raza, short fallback)
    {
        if (!_loaded) Load();
        return _armaduraRaza.TryGetValue(raza, out var oi) && oi > 0 ? oi : fallback;
    }

    private static void Load()
    {
        _loaded = true;
        string file = Path.Combine(DataPaths.Sub("Dat"), "NACIMIENTO.ini");
        var ini = new IniFile(file);
        if (!ini.Loaded)
        {
            // fallback alterno por si la carpeta Dat se resuelve distinto
            file = Path.Combine(AppContext.BaseDirectory, "Dat", "NACIMIENTO.ini");
            ini = new IniFile(file);
        }
        if (!ini.Loaded) { Console.WriteLine("[Nacimiento] NACIMIENTO.ini no encontrado; los PJ nuevos usarán kit mínimo."); return; }

        int clases = ValOr(ini.Get("INIT", "Clases"), 18);
        for (int c = 1; c <= clases; c++)
        {
            string sec = c.ToString();
            int cant = Val(ini.Get(sec, "CantidadObj"));
            if (cant <= 0) continue;
            var list = new List<StartItem>();
            for (int j = 1; j <= cant; j++)
            {
                short oi = (short)Val(ini.Get(sec, "ObjIndex" + j));
                int amt = Val(ini.Get(sec, "ObjAmount" + j));
                bool eq = Val(ini.Get(sec, "ObjEquipped" + j)) == 1;
                list.Add(new StartItem(oi, amt, eq));
            }
            _porClase[c] = list;
        }

        // Armadura por raza (opcional, data-driven). Seccion [ARMADURA_RAZA] con Raza1..Raza6=ObjIndex.
        for (int r = 1; r <= 6; r++)
        {
            short oi = (short)Val(ini.Get("ARMADURA_RAZA", "Raza" + r));
            if (oi > 0) _armaduraRaza[r] = oi;
        }

        Console.WriteLine($"[Nacimiento] {_porClase.Count} clases con kit inicial cargadas.");
        Validar();
    }

    /// <summary>
    /// Chequeo de coherencia del kit (solo logs, no altera datos). Avisa si el arma equipada (slot 3)
    /// esta prohibida para esa clase en obj.dat, o si una clase NO magica recibe pociones de mana.
    /// </summary>
    private static void Validar()
    {
        foreach (var (clase, list) in _porClase)
        {
            // Slot 3 = arma equipada (convencion del archivo).
            if (list.Count >= 3)
            {
                short arma = list[2].ObjIndex;
                if (arma > 0)
                {
                    var od = ObjData.Get(arma);
                    if (od.Name != null && od.ClasesProhibidas != null
                        && Array.IndexOf(od.ClasesProhibidas, clase) >= 0)
                        Console.WriteLine($"[Nacimiento][AVISO] Clase {clase}: el arma de nacimiento '{od.Name}' ({arma}) esta PROHIBIDA para esa clase en obj.dat; el PJ no podra re-equiparla.");
                }
            }
            // Pociones de mana en clase no magica.
            if (!_clasesMagicas.Contains(clase))
            {
                foreach (var it in list)
                    if (it.ObjIndex == POCION_AZUL)
                    { Console.WriteLine($"[Nacimiento][AVISO] Clase {clase} (no magica) recibe pociones de mana ({POCION_AZUL}); no las podra aprovechar."); break; }
            }
        }
    }

    /// <summary>val() de VB6: parsea el entero al inicio del string (ignora comentarios 'xxx al lado).</summary>
    private static int Val(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.TrimStart();
        int i = 0; bool neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
        int start = i;
        long v = 0;
        while (i < s.Length && char.IsDigit(s[i])) { v = v * 10 + (s[i] - '0'); i++; }
        if (i == start) return 0;
        return (int)(neg ? -v : v);
    }

    private static int ValOr(string s, int def)
    {
        int v = Val(s);
        return v == 0 ? def : v;
    }
}
