using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Creación de personajes nuevos (HandleLoginNewChar). Versión núcleo: valida nombre libre,
/// crea el .chr con stats base, lo asocia a la cuenta (.cnt) y entra al mundo.
///
/// Falta del VB6 original: stats exactos por raza/clase (tablas), validación de nombre por
/// dados/atributos previos (ThrowDices), restricciones de clase. Se usan valores base sensatos.
/// </summary>
public static class CharCreator
{
    public static void LoginNewChar(Connection conn, string cuenta, string nombre,
        byte raza, byte genero, byte clase, byte hogar, short head)
    {
        var u = UserListManager.UserList[conn.UserIndex];
        nombre = nombre.Trim();

        if (string.IsNullOrEmpty(nombre) || !NombreValido(nombre))
        {
            ServerPackets.ShowMessageBox(conn, "Nombre de personaje inválido.");
            return;
        }

        string file = System.IO.Path.Combine(CharLoader.CharPath, nombre.ToUpperInvariant() + ".chr");
        if (System.IO.File.Exists(file))
        {
            ServerPackets.ShowMessageBox(conn, "Ya existe un personaje con ese nombre.");
            return;
        }

        // Cuerpo según raza+género (DarCuerpo de TCP.bas, valores clásicos AO).
        short body = CuerpoPorRaza(raza, genero);
        if (head <= 0) head = (short)(raza == 1 ? 1 : raza); // cabeza base si el cliente no manda

        // Status de facción (color del nick) según la CIUDAD ELEGIDA (Nix=imperial / Illiandor=republicano).
        // VB6 ConnectNewUser (TCP.bas:496): Hogar = cIlliandor(2) → Status 3 (Republicano);
        // si no (Nix, etc.) → Status 2 (Ciudadano). El cliente colorea el nick con este Status.
        const byte cIlliandor = 2;
        byte faccionStatus = hogar == cIlliandor ? (byte)3 : (byte)2;

        // VB6 ConnectNewUser (TCP.bas:502,564): TODO personaje nuevo nace en el Dungeon Newbie
        // (Hogar = cDungeonNewbie = 6, Pos = Ciudades.DungeonNewbie), sin importar el hogar elegido.
        const byte cDungeonNewbie = 6;
        hogar = cDungeonNewbie;
        var dn = CityData.Get(cDungeonNewbie);
        (short map, short x, short y) pos = (dn.Map, dn.X, dn.Y);

        // Personaje NUEVO: si quedó un .mac huérfano de un PJ borrado con el mismo nombre,
        // el nuevo heredaría los macros de otro jugador. Arranca siempre limpio.
        Macros.Delete(nombre);

        // Escribir el .chr nuevo con secciones mínimas.
        CrearCharfile(file, nombre, cuenta, raza, genero, clase, hogar, body, head, pos, faccionStatus);

        // Asociar el personaje a la cuenta (.cnt [PJS]).
        AsociarACuenta(cuenta, nombre);

        // Cargar y entrar al mundo.
        if (CharLoader.LoadCharacter(u, nombre))
        {
            u.Name = nombre;
            u.Account = cuenta;
            u.flags.UserLogged = true;
            LoginFlow.EnterWorld(conn, u);
            Console.WriteLine($"[ServidorCS] Personaje nuevo creado: {nombre} (cuenta {cuenta})");
        }
    }

    private static bool NombreValido(string n)
    {
        if (n.Length < 1 || n.Length > 30) return false;
        foreach (char c in n)
            if (!char.IsLetter(c) && c != ' ') return false;
        return true;
    }

    // ===== Stats iniciales por raza+clase (AsignarAtributos/ConnectNewUser/GetVida/GetMana) =====
    // eClass: Clerigo=1, Mago=2, Guerrero=3, Asesino=4, ladron=5, Bardo=6, Druida=7, Gladiador=8,
    // Paladin=9, Cazador=10, Mercenario=17, Nigromante=18. eRaza: Humano=1..Orco=6.

    /// <summary>Atributos base por clase (AsignarAtributos, TCP.bas:320-341), antes de ModRaza.</summary>
    private static (int F, int A, int I, int C, int Co) AtributosBase(byte clase) => clase switch
    {
        1 or 4 or 6 or 7 or 9 or 18 => (14, 14, 18, 10, 18), // Clerigo/Asesino/Bardo/Druida/Paladin/Nigromante
        2 => (6, 18, 18, 10, 18),                            // Mago
        _ => (18, 18, 6, 10, 18),                            // clases sin magia
    };

    /// <summary>Modificadores de atributo por raza (Balance.dat [MODRAZA]).</summary>
    private static (int F, int A, int I, int C, int Co) ModRaza(byte raza) => raza switch
    {
        1 => (1, 1, 1, 0, 2),    // Humano
        2 => (0, 2, 3, 2, 0),    // Elfo
        3 => (2, 0, 2, -1, 1),   // Drow
        4 => (-5, 3, 4, 0, -1),  // Gnomo
        5 => (3, -1, -5, -1, 4), // Enano
        6 => (5, -2, -5, -4, 4), // Orco
        _ => (0, 0, 0, 0, 0),
    };

    /// <summary>GetVidaInicialN1 (GameLogic.bas:1596) — portada en Leveling.VidaInicial.</summary>
    private static int VidaInicial(byte raza, byte clase) => Leveling.VidaInicial(raza, clase);

    /// <summary>GetManaInicialN1 (GameLogic.bas:1763) — portada en Leveling.ManaInicial.</summary>
    private static int ManaInicial(byte raza, byte clase) => Leveling.ManaInicial(raza, clase);

    /// <summary>Clase con maná (recibe el hechizo Dardo Mágico H1=2 al nacer). ConnectNewUser:538.</summary>
    private static bool ClaseMagica(byte clase) => clase is 1 or 2 or 4 or 6 or 7 or 9 or 18;

    private static readonly Random _rngStats = new();

    /// <summary>Armadura de nacimiento por raza (RellenarInventario Case 4, TCP.bas:2541-2560).</summary>
    private static short ArmaduraPorRaza(byte raza) => raza switch
    {
        2 => 464,   // Elfo
        3 => 465,   // Drow
        4 => 466,   // gnomo
        5 => 466,   // enano
        6 => 1087,  // Orco
        _ => 463,   // Humano
    };

    private static short CuerpoPorRaza(byte raza, byte genero)
    {
        // genero: 1=Hombre, 2=Mujer. raza: 1=Humano..6=Orco.
        bool hombre = genero == 1;
        return raza switch
        {
            1 => 1,                       // Humano
            2 => 2,                       // Elfo
            3 => 3,                       // Drow
            4 => (short)(hombre ? 52 : 138), // gnomo
            5 => (short)(hombre ? 52 : 138), // enano
            6 => (short)(hombre ? 252 : 253), // Orco
            _ => 1,
        };
    }

    private static void CrearCharfile(string file, string nombre, string cuenta,
        byte raza, byte genero, byte clase, byte hogar, short body, short head, (short map, short x, short y) pos,
        byte faccionStatus)
    {
        var sb = new System.Text.StringBuilder();
        void Sec(string s) => sb.Append('[').Append(s).Append("]\r\n");
        void Kv(string k, object v) => sb.Append(k).Append('=').Append(v).Append("\r\n");

        // Inventario de nacimiento por clase (RellenarInventario, TCP.bas:2535). Slot 3 = arma equipada,
        // slot 4 = armadura por raza (Elfo464/Drow465/gnomo-enano466/Orco1087/Humano463). El body se
        // toma de la armadura equipada; el WeaponAnim del arma. Se calcula ANTES de [INIT] para escribir
        // el body/arma correctos.
        var kit = Nacimiento.Get(clase);
        var slots = new (short obj, int amt, bool eq)[Constants.MAX_INVENTORY_SLOTS + 1];
        short armaObj = 0, armaduraObj = 0;
        for (int j = 1; j <= kit.Count && j <= Constants.MAX_INVENTORY_SLOTS; j++)
        {
            var it = kit[j - 1];
            short obj = it.ObjIndex;
            bool eq = it.Equipped;
            if (j == 4) { obj = Nacimiento.ArmaduraRaza(raza, ArmaduraPorRaza(raza)); eq = true; armaduraObj = obj; }   // armadura al nacer ([ARMADURA_RAZA] o fallback por raza)
            else if (j == 3) { eq = true; armaObj = obj; }                                 // arma equipada
            slots[j] = (obj, it.Amount, eq);
        }
        if (armaduraObj > 0) { int rop = ObjData.Get(armaduraObj).Ropaje; if (rop > 0) body = (short)rop; }
        short armaAnim = armaObj > 0 ? (short)ObjData.Get(armaObj).WeaponAnim : (short)0;
        int nroItems = Math.Min(kit.Count, Constants.MAX_INVENTORY_SLOTS);

        // Stats iniciales por raza+clase (AsignarAtributos/ConnectNewUser, TCP.bas:320-559).
        var ab = AtributosBase(clase);
        var mr = ModRaza(raza);
        int atF = ab.F + mr.F, atA = ab.A + mr.A, atI = Math.Max(0, ab.I + mr.I), atC = ab.C + mr.C, atCo = ab.Co + mr.Co;
        int maxHP = VidaInicial(raza, clase);
        int miInt = _rngStats.Next(1, Math.Max(2, atA / 6) + 1); // RandomNumber(1, Agilidad\6)
        if (miInt == 1) miInt = 2;
        int maxSta = 20 * miInt;
        int maxMAN = ClaseMagica(clase) ? ManaInicial(raza, clase) : 0;
        short hechizo1 = ClaseMagica(clase) ? (short)2 : (short)0; // Dardo Mágico

        Sec("FLAGS"); Kv("Muerto", 0); Kv("Navegando", 0);
        Sec("INIT");
        Kv("Genero", genero); Kv("Raza", raza); Kv("Hogar", hogar); Kv("Clase", clase);
        Kv("Desc", ""); Kv("Heading", 3); Kv("Head", head); Kv("Body", body);
        Kv("Arma", armaAnim); Kv("Escudo", 0); Kv("Casco", 0);
        Kv("Position", $"{pos.map}-{pos.x}-{pos.y}");
        Sec("STATS");
        Kv("MaxHP", maxHP); Kv("MinHP", maxHP); Kv("MaxMAN", maxMAN); Kv("MinMAN", maxMAN);
        Kv("MaxSTA", maxSta); Kv("MinSTA", maxSta); Kv("MaxHIT", 2); Kv("MinHIT", 1);
        Kv("MaxAGU", 100); Kv("MinAGU", 100); Kv("MaxHAM", 100); Kv("MinHAM", 100);
        Kv("ELV", 1); Kv("ELU", 300); Kv("EXP", 0); Kv("GLD", 2000000); Kv("BANCO", 0);
        Kv("SkillPtsLibres", 10);
        Sec("ATRIBUTOS");
        // AT1=Fuerza, AT2=Agilidad, AT3=Inteligencia, AT4=Carisma, AT5=Constitución (eAtributos).
        Kv("AT1", atF); Kv("AT2", atA); Kv("AT3", atI); Kv("AT4", atC); Kv("AT5", atCo);
        Sec("HECHIZOS");
        for (int h = 1; h <= Constants.MAXUSERHECHIZOS; h++) Kv("H" + h, h == 1 ? hechizo1 : 0);

        Sec("Inventory");
        Kv("CantidadItems", nroItems);
        for (int s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
        {
            var sl = slots[s];
            Kv("Obj" + s, sl.obj > 0 ? $"{sl.obj}-{sl.amt}-{(sl.eq ? 1 : 0)}" : "0-0-0");
        }
        // Punteros de equipo (los lee CharLoader): arma en slot 3, armadura en slot 4.
        if (armaObj > 0) Kv("WeaponEqpSlot", 3);
        if (armaduraObj > 0) Kv("ArmourEqpSlot", 4);
        Sec("BancoInventory");
        Kv("CantidadItems", 0);
        for (int s = 1; s <= Constants.MAX_BANCOINVENTORY_SLOTS; s++) Kv("Obj" + s, "0-0");
        Sec("CORREO");
        for (int c = 1; c <= Constants.MAX_CORREOS_SLOTS; c++)
        { Kv("Carta" + c, 0); Kv("Emisor" + c, 0); Kv("Leida" + c, 0); Kv("Objeto" + c, "0-0"); }
        Sec("GUILD"); Kv("GUILDINDEX", 0);
        // Facción inicial (color del nick): Status 2=Ciudadano (Nix) / 3=Republicano (Illiandor).
        Sec("FACCIONES");
        Kv("Status", faccionStatus);
        Kv("CiudMatados", 0); Kv("CriMatados", 0); Kv("crimatadosrango", 0);
        Kv("Reenlistadas", 0); Kv("FechaIngreso", "");
        Kv("Recibio", 0); Kv("RecompensasReal", 0); Kv("Recompensascaos", 0);

        System.IO.File.WriteAllBytes(file, Cp1252.GetBytes(sb.ToString()));
    }

    private static void AsociarACuenta(string cuenta, string nombre)
    {
        string cnt = System.IO.Path.Combine(AccountManager.AccountPath, cuenta.ToUpperInvariant() + ".cnt");
        if (!System.IO.File.Exists(cnt)) return;
        var doc = new IniDocument(cnt);
        if (!doc.Loaded) return;
        var ini = new IniFile(cnt);
        int n = ini.GetInt("PJS", "NumPjs");
        n++;
        doc.Set("PJS", "NumPjs", n.ToString());
        doc.Set("PJS", "PJ" + n, nombre);
        doc.Save(cnt);
    }
}
