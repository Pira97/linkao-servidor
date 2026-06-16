namespace ServidorCS.Game;

/// <summary>
/// Base de datos de NPCs (NPCs.dat, INI [NPCN]). Versión mínima: Name, Body, Head,
/// Heading (lo que el cliente necesita para dibujar el NPC). El resto (stats, IA,
/// drops, comercio) se carga al portar MODULO_NPCs.bas.
/// </summary>
public static class NpcData
{
    public struct NpcInfo
    {
        public string Name; public short Body, Head; public byte Heading;
        public int MaxHP; public bool Attackable; public int GiveEXP, GiveGLD;
        public bool Hostil; public int MinHIT, MaxHIT;
        public int PoderAtaque, PoderEvasion; // para el cálculo de impacto/evasión (NPCs.dat)
        public byte Movement;  // TipoAI: 0=persigue usuarios, 1=estático (solo ataca adyacente)
        public int Domable;    // puntos requeridos para domar (0 = no domable)
        public short[] Spells; // hechizos que lanza (Sp1..LanzaSpells); null = no lanza
        public bool Comercia;
        public byte NpcType;   // eNPCType (Declares.bas:424): 1=Revividor,4=Banquero,3=Entrenador,16=Subastador,18=Convertidor,19=Shop,5=facciones,7=transportador, 2=GuardiasCity
        public byte Status;    // facción del NPC (0/3=neutral, 1=imperial, 2=republicano, 4=caos, 5=renegado)
        public byte Ciudad;    // CIUDAD_* (1=Imperial,2=Republicana,3=Caotica,5=Rinkel); derivada de Status si 0. Para guardias.
        public (short objIndex, int amount)[] Inventario; // items que vende (Obj1..NroItems)
        // Drops al morir (Drops.dat: DropN=ObjIndex,Amount,Prob). Prob en % (puede ser decimal).
        public (short objIndex, int amount, double prob)[] Drops;
        // Entrenador (NpcType=3): criaturas que puede invocar (NroCriaturas + CI1..CIN = npcindex).
        public int[] Criaturas;
        // Sonidos del NPC (NPCs.dat Snd1/Snd2/Snd3): Snd1=al atacar, Snd2=al ser golpeado, Snd3=al morir.
        public short Snd1, Snd2, Snd3;
        // Equipamiento visible (NPCs.dat): anims de arma/escudo/casco (guardias, etc.). Se mandan en CharacterCreate.
        public short WeaponAnim, ShieldAnim, CascoAnim;
        // Auras por slot (bots): del campo Aura de cada ítem sacro. 0 = sin aura.
        public short Aura, AuraArma, AuraEscudo, AuraCasco;
        // AguaValida (NPCs.dat): 1 = puede pisar agua (criaturas marinas). 0 = bloqueado por agua.
        public bool AguaValida;
        // TierraInvalida (NPCs.dat "TierraInValida"): 1 = NO puede pisar tierra (criatura solo-agua).
        public bool TierraInvalida;
    }

    private static Dictionary<int, NpcInfo> _cache;

    public static NpcInfo Get(int npcIndex)
    {
        EnsureLoaded();
        return _cache.TryGetValue(npcIndex, out var n) ? n : default;
    }

    /// <summary>Registra/define un NPC en memoria (sin tocar NPCs.dat). Lo usa el sistema de
    /// bots para crear definiciones sintéticas en índices altos. Sobrescribe si ya existe.</summary>
    public static void Register(int npcIndex, NpcInfo info)
    {
        EnsureLoaded();
        _cache[npcIndex] = info;
    }

    /// <summary>Catálogo resumido (índice + nombre + tipo) de todos los NPCs cargados,
    /// ordenado por índice. Lo usa el panel GM para el buscador de "Crear NPC".</summary>
    public static List<(int Index, string Name, byte NpcType)> All()
    {
        EnsureLoaded();
        var list = new List<(int, string, byte)>(_cache.Count);
        foreach (var kv in _cache)
        {
            if (string.IsNullOrEmpty(kv.Value.Name)) continue;
            list.Add((kv.Key, kv.Value.Name, kv.Value.NpcType));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    private static void EnsureLoaded()
    {
        if (_cache != null) return;
        _cache = new Dictionary<int, NpcInfo>();
        string file = FindFile();
        if (file == null) return;

        // Multiplicadores globales de exp/oro (Server.ini [INIT] Exp/Oro → Expc/Oroc, FileIO.bas:1611-1612).
        // VB6 los aplica al cargar cada NPC: .GiveEXP = GiveEXP * Expc, .GiveGLD = GiveGLD * Oroc
        // (MODULO_NPCs.bas:1215/1223).
        int expc = Network.ServerConfig.ReadInt("Exp", 1);
        if (expc <= 0) expc = 1;
        int oroc = Network.ServerConfig.ReadInt("Oro", 1);
        if (oroc <= 0) oroc = 1;

        var ini = new IniFile(file);
        for (int i = 1; i <= 2000; i++)
        {
            string name = ini.Get("NPC" + i, "Name");
            if (string.IsNullOrEmpty(name)) name = ini.Get("NPC" + i, "Nombre");
            if (string.IsNullOrEmpty(name)) continue;
            var info = new NpcInfo
            {
                Name = name,
                Body = (short)ini.GetInt("NPC" + i, "Body"),
                Head = (short)ini.GetInt("NPC" + i, "Head"),
                Heading = (byte)ini.GetInt("NPC" + i, "Heading"),
                MaxHP = ini.GetInt("NPC" + i, "MaxHP"),
                Attackable = ini.GetInt("NPC" + i, "Attackable") == 1,
                GiveEXP = ini.GetInt("NPC" + i, "GiveEXP") * expc,
                GiveGLD = ini.GetInt("NPC" + i, "GiveGLD") * oroc,
                Hostil = ini.GetInt("NPC" + i, "Hostile") == 1,
                MinHIT = ini.GetInt("NPC" + i, "MinHIT"),
                MaxHIT = ini.GetInt("NPC" + i, "MaxHIT"),
                PoderAtaque = ini.GetInt("NPC" + i, "PoderAtaque"),
                PoderEvasion = ini.GetInt("NPC" + i, "PoderEvasion"),
                Movement = (byte)ini.GetInt("NPC" + i, "Movement"),
                Domable = ini.GetInt("NPC" + i, "Domable"),
                Spells = LoadSpells(ini, i),
                Comercia = ini.GetInt("NPC" + i, "Comercia") == 1,
                NpcType = (byte)ini.GetInt("NPC" + i, "NpcType"),
                Status = (byte)ini.GetInt("NPC" + i, "Status"),
                Snd1 = (short)ini.GetInt("NPC" + i, "Snd1"),
                Snd2 = (short)ini.GetInt("NPC" + i, "Snd2"),
                Snd3 = (short)ini.GetInt("NPC" + i, "Snd3"),
                WeaponAnim = (short)ini.GetInt("NPC" + i, "WeaponAnim"),
                ShieldAnim = (short)ini.GetInt("NPC" + i, "ShieldAnim"),
                CascoAnim = (short)ini.GetInt("NPC" + i, "CascoAnim"),
                AguaValida = ini.GetInt("NPC" + i, "AguaValida") == 1,
                TierraInvalida = ini.GetInt("NPC" + i, "TierraInValida") == 1,
            };
            // Ciudad del guardia (MODULO_NPCs.bas:1187): si no está en el DAT, derivar del Status.
            info.Ciudad = (byte)ini.GetInt("NPC" + i, "Ciudad");
            if (info.Ciudad == 0)
            {
                info.Ciudad = info.Status switch
                {
                    1 => (byte)1,  // CIUDAD_IMPERIAL
                    2 => (byte)2,  // CIUDAD_REPUBLICANA
                    4 => (byte)3,  // CIUDAD_CAOTICA
                    _ => (byte)0,
                };
            }
            // Inventario del mercader: Obj1..NroItems = "objindex-cantidad".
            if (info.Comercia)
            {
                int nro = ini.GetInt("NPC" + i, "NroItems");
                var inv = new List<(short, int)>();
                for (int k = 1; k <= nro; k++)
                {
                    var parts = ini.Get("NPC" + i, "Obj" + k).Split('-');
                    if (parts.Length >= 2 && short.TryParse(parts[0], out var oi) && int.TryParse(parts[1], out var am) && oi > 0)
                        inv.Add((oi, am));
                }
                info.Inventario = inv.ToArray();
            }
            // Entrenador (NpcType=3): lista de criaturas que puede invocar (NroCriaturas + CI1..CIN).
            if (info.NpcType == 3)
            {
                int nro = ini.GetInt("NPC" + i, "NroCriaturas");
                if (nro > 0)
                {
                    var cri = new int[nro + 1]; // 1..nro (índice 0 sin usar, 1:1 VB6)
                    for (int k = 1; k <= nro; k++) cri[k] = ini.GetInt("NPC" + i, "CI" + k);
                    info.Criaturas = cri;
                }
            }
            _cache[i] = info;
        }

        // Cargar drops desde Drops.dat (archivo separado, sección [NPCxxx]).
        LoadDrops();
    }

    /// <summary>Carga los drops de Drops.dat y los inyecta en cada NpcInfo (VB6: CargarNpcDrops).</summary>
    private static void LoadDrops()
    {
        string dropsFile = FindDropsFile();
        if (dropsFile == null) return;
        var ini = new IniFile(dropsFile);

        // Copia de claves porque vamos a reemplazar structs en el dict.
        foreach (var npcId in new List<int>(_cache.Keys))
        {
            int numDrops = ini.GetInt("NPC" + npcId, "NumDrops");
            if (numDrops <= 0) continue;
            if (numDrops > 20) numDrops = 20; // MAX_NPC_DROPS

            var drops = new List<(short, int, double)>();
            for (int k = 1; k <= numDrops; k++)
            {
                string ln = ini.Get("NPC" + npcId, "Drop" + k);
                if (string.IsNullOrEmpty(ln)) continue;
                var p = ln.Split(',');
                if (p.Length < 3) continue;
                if (!short.TryParse(p[0].Trim(), out var oi) || oi <= 0) continue;
                int.TryParse(p[1].Trim(), out var am);
                double.TryParse(p[2].Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var prob);
                if (am <= 0) am = 1;
                drops.Add((oi, am, prob));
            }
            if (drops.Count > 0)
            {
                var info = _cache[npcId];
                info.Drops = drops.ToArray();
                _cache[npcId] = info;
            }
        }
    }

    private static string FindDropsFile()
    {
        foreach (var c in new[]
        {
            Path.Combine(DataPaths.Sub("Dat"), "Drops.dat"),
            DataPaths.Root + "Drops.dat",
            Path.Combine(AppContext.BaseDirectory, "Dat", "Drops.dat"),
            Path.Combine(AppContext.BaseDirectory, "Drops.dat"),
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>Carga los hechizos del NPC: LanzaSpells=N + Sp1..SpN (NPCs.dat).</summary>
    private static short[] LoadSpells(IniFile ini, int npcId)
    {
        int n = ini.GetInt("NPC" + npcId, "LanzaSpells");
        if (n <= 0) return null;
        var list = new List<short>();
        for (int k = 1; k <= n; k++)
        {
            short sp = (short)ini.GetInt("NPC" + npcId, "Sp" + k);
            if (sp > 0) list.Add(sp);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string FindFile()
    {
        foreach (var c in new[]
        {
            Path.Combine(DataPaths.Sub("Dat"), "NPCs.dat"),
            DataPaths.Root + "NPCs.dat",
            Path.Combine(AppContext.BaseDirectory, "Dat", "NPCs.dat"),
            Path.Combine(AppContext.BaseDirectory, "NPCs.dat"),
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
