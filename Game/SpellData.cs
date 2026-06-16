namespace ServidorCS.Game;

/// <summary>
/// Base de datos de hechizos (Hechizos.dat, formato INI [HECHIZON]).
/// Carga los campos que el núcleo de magia necesita: nombre, palabras mágicas,
/// tipo, efecto sobre HP (SubeHP 1=cura/2=daña + MinHP/MaxHP), maná/stamina,
/// target y FX. Equivale a la lectura de Hechizos de FileIO.bas.
/// </summary>
public static class SpellData
{
    public struct Spell
    {
        public string Nombre;
        public string Desc;            // descripción (Hechizos.dat "Desc")
        public string PalabrasMagicas;
        public int Tipo;
        public int SubeHP;      // 1 = cura HP, 2 = quita HP
        public int MinHP, MaxHP; // cantidad curada/quitada
        public int ManaRequerido;
        public int StaRequerido;
        public int Target;       // 1=usuarios, 2=npc, etc.
        public int FXgrh;        // FX a mostrar sobre el objetivo
        public int Loops;
        public int WAV;          // sonido del hechizo (Hechizos.dat "WAV"; 0 = sin sonido)
        public int Particle;     // sistema de partículas sobre el objetivo (0 = ninguno)
        public int TimeParticula; // duración de la partícula (ms)
        // Efectos de estado
        public bool Paraliza, Inmoviliza, Ceguera, RemoverParalisis;
        public int Envenena;        // 0=no, >0 = nivel de veneno (normal/crítico)
        public bool CuraVeneno;     // quita el envenenamiento
        public bool Invisibilidad;  // vuelve invisible al objetivo
        public bool Incinera;       // incinera al objetivo
        public bool AutoLanzar;     // se lanza sobre uno mismo sin apuntar (invisibilidad propia, etc.)
        // --- Efectos adicionales (tHechizo, Declares.bas:743) ---
        public int SubeSta; public int MinSta, MaxSta;            // cura/quita stamina
        public int SubeFuerza; public int MinFuerza, MaxFuerza;   // buff/debuff de fuerza
        public int SubeAgilidad; public int MinAgilidad, MaxAgilidad; // buff/debuff de agilidad
        public bool RemoverEstupidez;  // quita la estupidez (ceguera mental)
        public bool Estupidez;         // causa estupidez al objetivo
        public bool Revivir;           // resucita al objetivo muerto
        public bool RemueveInvis;      // revela ocultos/invisibles del área
        public int Invoca; public int NumNpc, Cant;  // invoca NumNpc x Cant criaturas
        public bool HechizoDeArea;     // afecta a todos los del área (no solo al target)
        public int AreaRadio;          // radio del área (tiles)
        public int MinSkill;           // skill mínimo de Magia para lanzarlo
        public int MinLevel;           // nivel mínimo del lanzador (0 = sin requisito). Hechizos de leveo.
        public string HechizeroMsg, TargetMsg, PropioMsg; // mensajes (lanzador / objetivo / propio)
        public bool Warp;              // teletransporta al objetivo al tile apuntado
        public int MaterializaObj, MaterializaCant; // crea un objeto en el piso
        public int SubeCarisma; public int MinCarisma, MaxCarisma;
        // --- Validaciones de PuedeLanzar (modHechizos.bas:323) ---
        public int[] ClasesProhibidas; // IDs de clase que NO pueden lanzarlo ("1-2-3-..."). null/[] = ninguna
        public int ItemRequerido, CantidadRequerida; // objeto+cantidad que exige (y consume) el hechizo
        public int Anillo;             // 1=requiere Anillo Espectral(1329) o de Penumbras(1330); 2=requiere Penumbras
        // --- Metamorfosis / Sanacion / Desencantar ---
        public bool Metamorfosis;      // transforma al lanzador en otro body (solo sobre uno mismo)
        public bool Sanacion;          // cura total: quita incinerado y veneno
        public bool Desencantar;       // remueve la metamorfosis del objetivo
        public bool ResucitaFamiliar;  // uFamiliar: en VB6 solo da la animación
        public int Body;               // body al que se transforma (Metamorfosis)
        public int ExtraHIT, ExtraDEF; // bonus de daño/defensa mientras dura la metamorfosis
    }

    // Parsea "1-2-3-..." → int[]{1,2,3,...}. Vacío/null → null.
    private static int[] ParseClases(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<int>();
        foreach (var p in parts) if (int.TryParse(p, out var v) && v > 0) list.Add(v);
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static Dictionary<int, Spell> _spells;
    public static void Reload() { _spells = null; EnsureLoaded(); Console.WriteLine($"[SpellData] Recargado: {_spells?.Count ?? 0} hechizos."); }

    public static string GetName(int spellIndex) => Get(spellIndex).Nombre ?? "";

    public static Spell Get(int spellIndex)
    {
        EnsureLoaded();
        return _spells.TryGetValue(spellIndex, out var s) ? s : default;
    }

    private static void EnsureLoaded()
    {
        if (_spells != null) return;
        _spells = new Dictionary<int, Spell>();

        string file = FindFile();
        if (file == null) return;

        var ini = new IniFile(file);
        for (int i = 1; i <= 2000; i++)
        {
            string name = ini.Get("HECHIZO" + i, "Nombre");
            if (string.IsNullOrEmpty(name)) continue;
            _spells[i] = new Spell
            {
                Nombre = name,
                Desc = ini.Get("HECHIZO" + i, "Desc"),
                PalabrasMagicas = ini.Get("HECHIZO" + i, "PalabrasMagicas"),
                Tipo = ini.GetInt("HECHIZO" + i, "Tipo"),
                SubeHP = ini.GetInt("HECHIZO" + i, "SubeHP"),
                MinHP = ini.GetInt("HECHIZO" + i, "MinHP"),
                MaxHP = ini.GetInt("HECHIZO" + i, "MaxHP"),
                ManaRequerido = ini.GetInt("HECHIZO" + i, "ManaRequerido"),
                StaRequerido = ini.GetInt("HECHIZO" + i, "StaRequerido"),
                Target = ini.GetInt("HECHIZO" + i, "Target"),
                FXgrh = ini.GetInt("HECHIZO" + i, "FXgrh"),
                Loops = ini.GetInt("HECHIZO" + i, "Loops"),
                WAV = ini.GetInt("HECHIZO" + i, "WAV"),
                Particle = ini.GetInt("HECHIZO" + i, "Particle"),
                TimeParticula = ini.GetInt("HECHIZO" + i, "TimeParticula"),
                Paraliza = ini.GetInt("HECHIZO" + i, "Paraliza") == 1,
                Inmoviliza = ini.GetInt("HECHIZO" + i, "Inmoviliza") == 1,
                Ceguera = ini.GetInt("HECHIZO" + i, "Ceguera") == 1,
                RemoverParalisis = ini.GetInt("HECHIZO" + i, "RemoverParalisis") == 1,
                Envenena = ini.GetInt("HECHIZO" + i, "Envenena"),
                CuraVeneno = ini.GetInt("HECHIZO" + i, "CuraVeneno") == 1,
                Invisibilidad = ini.GetInt("HECHIZO" + i, "Invisibilidad") == 1,
                Incinera = ini.GetInt("HECHIZO" + i, "Incinera") == 1,
                AutoLanzar = ini.GetInt("HECHIZO" + i, "AutoLanzar") > 0,
                SubeSta = ini.GetInt("HECHIZO" + i, "SubeSta"),
                MinSta = ini.GetInt("HECHIZO" + i, "MinSta"),
                MaxSta = ini.GetInt("HECHIZO" + i, "MaxSta"),
                // OJO: en Hechizos.dat las keys son SubeFU/MinFU/MaxFU y SubeAG/MinAG/MaxAG (no los nombres largos).
                SubeFuerza = ini.GetInt("HECHIZO" + i, "SubeFU"),
                MinFuerza = ini.GetInt("HECHIZO" + i, "MinFU"),
                MaxFuerza = ini.GetInt("HECHIZO" + i, "MaxFU"),
                SubeAgilidad = ini.GetInt("HECHIZO" + i, "SubeAG"),
                MinAgilidad = ini.GetInt("HECHIZO" + i, "MinAG"),
                MaxAgilidad = ini.GetInt("HECHIZO" + i, "MaxAG"),
                RemoverEstupidez = ini.GetInt("HECHIZO" + i, "RemoverEstupidez") == 1,
                Estupidez = ini.GetInt("HECHIZO" + i, "Estupidez") == 1,
                Revivir = ini.GetInt("HECHIZO" + i, "Revivir") == 1,
                RemueveInvis = ini.GetInt("HECHIZO" + i, "RemueveInvisibilidadParcial") == 1,
                Invoca = ini.GetInt("HECHIZO" + i, "Invoca"),
                NumNpc = ini.GetInt("HECHIZO" + i, "NumNpc"),
                Cant = ini.GetInt("HECHIZO" + i, "Cant"),
                HechizoDeArea = ini.GetInt("HECHIZO" + i, "HechizoDeArea") == 1,
                AreaRadio = ini.GetInt("HECHIZO" + i, "AreaEfecto"),
                MinSkill = ini.GetInt("HECHIZO" + i, "MinSkill"),
                MinLevel = ini.GetInt("HECHIZO" + i, "MinLevel"),
                HechizeroMsg = ini.Get("HECHIZO" + i, "HechizeroMsg"),
                TargetMsg = ini.Get("HECHIZO" + i, "TargetMsg"),
                PropioMsg = ini.Get("HECHIZO" + i, "PropioMsg"),
                Warp = ini.GetInt("HECHIZO" + i, "Warp") == 1,
                MaterializaObj = ini.GetInt("HECHIZO" + i, "MaterializaObj"),
                MaterializaCant = ini.GetInt("HECHIZO" + i, "MaterializaCant"),
                SubeCarisma = ini.GetInt("HECHIZO" + i, "SubeCA"),
                MinCarisma = ini.GetInt("HECHIZO" + i, "MinCA"),
                MaxCarisma = ini.GetInt("HECHIZO" + i, "MaxCA"),
                ClasesProhibidas = ParseClases(ini.Get("HECHIZO" + i, "ClasesProhibidas")),
                ItemRequerido = ini.GetInt("HECHIZO" + i, "ItemRequerido"),
                CantidadRequerida = Math.Max(ini.GetInt("HECHIZO" + i, "CantidadItemRequerido"),
                                             ini.GetInt("HECHIZO" + i, "CantidadRequerida")),
                Anillo = ini.GetInt("HECHIZO" + i, "Anillo"),
                Metamorfosis = ini.GetInt("HECHIZO" + i, "Metamorfosis") == 1,
                Sanacion = ini.GetInt("HECHIZO" + i, "Sanacion") == 1,
                Desencantar = ini.GetInt("HECHIZO" + i, "Desencantar") == 1,
                ResucitaFamiliar = ini.GetInt("HECHIZO" + i, "ResucitaFamiliar") == 1,
                Body = ini.GetInt("HECHIZO" + i, "Body"),
                ExtraHIT = ini.GetInt("HECHIZO" + i, "ExtraHIT"),
                ExtraDEF = ini.GetInt("HECHIZO" + i, "ExtraDEF"),
            };
        }
    }

    private static string FindFile()
    {
        foreach (var c in new[]
        {
            Path.Combine(DataPaths.Sub("Dat"), "Hechizos.dat"),
            DataPaths.Root + "Hechizos.dat",
            Path.Combine(AppContext.BaseDirectory, "Dat", "Hechizos.dat"),
            Path.Combine(AppContext.BaseDirectory, "Hechizos.dat"),
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
