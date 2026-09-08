using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Buff "Poder de los Dioses" (NUEVO, los textos vienen del locale de ImperiumAO). UN solo jugador
/// a la vez lo carga: +25% de exp, +50% de oro y daño doble (físico y mágico) CONTRA NPCs — contra
/// otros jugadores pega normal (pedido del usuario) —, marcado con una aureola visible para todos. Cada vez que alguien lo recibe suena Sounds.PODER_DIOSES en todo el
/// server y una voz lo anuncia.
///
/// Reglas (25-sep-2026, pedido del usuario): está SIEMPRE activo desde que arranca el server. Si
/// nadie lo tiene, lo gana el primero que entre a un dungeon de nivel alto (o alguien que ya esté
/// adentro). Quien mata al portador se lo saca. Si el portador muere por otra cosa, sale del
/// dungeon o se desconecta, el poder queda libre y pasa a otro que esté adentro.
///
/// Sólo dungeons de nivel alto: el Newbie (37/208) y los de nivel bajo/medio quedan afuera a
/// propósito. La lista y los bonus están en Dat/PoderDioses.ini — versionado, sube con el deploy
/// (Server.ini no se sube: tiene credenciales). Se lee al arrancar y cada vez que un GM lo prende:
///   Mapas=143,144,145,...   ExpPct=25   OroPct=50   DanoPct=100   Particula=412   Voz=16   Siempre=1
///
/// GM (Dios): /PODERDIOSESOFF lo apaga, /PODERDIOSES [minutos] lo vuelve a prender (sin minutos
/// = sin límite).
/// Hooks: Facciones.ContarMuerte (traspaso al asesino), Combat.UserDie (muerte sin asesino),
/// UserListManager.CloseUser (deslogueo), Tick 1 Hz (salió del dungeon / vence el tiempo),
/// AreaVisibility (la aureola para quien entra al área después).
/// </summary>
public static class PoderDioses
{
    // Dungeons de nivel alto (rangos de Bots.MapasPoblacion con piso 30+).
    private static readonly Dictionary<int, string> MapasDefault = new()
    {
        [143] = "Veriil 4", [144] = "Veriil 5", [145] = "Veriil 6",
        [209] = "Zero 1", [210] = "Zero 2", [211] = "Zero 3",
        [230] = "Cristal 1", [231] = "Cristal 2", [232] = "Cristal 3",
        [754] = "Fárzhë 1", [755] = "Fárzhë 2", [756] = "Fárzhë 3", [757] = "Fárzhë 4", [760] = "Fárzhë 5",
        [758] = "Krëwh 1", [759] = "Krëwh 2",
    };

    private static Dictionary<int, string> _mapas = new(MapasDefault);
    private static int _expPct = 25;
    private static int _oroPct = 50;
    private static int _danoPct = 100;     // 100 = daño doble
    private static byte _voz = 16;         // EventVoice.Voces: Narrador Épico
    private static short _particula = 412; // Aureola Dorada
    private static bool _siempre = true;   // activo desde el arranque del server, sin límite de tiempo

    public static bool EventoActivo { get; private set; }
    private static int _portador;          // índice de usuario; 0 = nadie (buscando candidato)
    private static double _expira;         // TickCount/1000; 0 = sin límite
    private static double _ultimaVoz;      // la voz no se repite antes de VOZ_COOLDOWN_SEG (PvP = traspasos seguidos)
    private const double VOZ_COOLDOWN_SEG = 20;

    private const byte FONT_INFO = 1, FONT_INFOBOLD = 4;
    private static readonly Random _rng = new();

    // ------------------------------------------------------------------ bonus (usados por Combat)

    // Por referencia y no por u.id: los bots arman User temporales con ids prestados.
    public static bool EsPortador(User u) => EventoActivo && u != null && _portador > 0 && UserListManager.UserList[_portador] == u;

    /// <summary>Exp con el bonus del portador aplicado (sin cambios para el resto).</summary>
    public static int AplicarExp(User u, int exp) => Sumar(u, exp, _expPct);

    /// <summary>Oro con el bonus del portador aplicado (sin cambios para el resto).</summary>
    public static int AplicarOro(User u, int oro) => Sumar(u, oro, _oroPct);

    /// <summary>Daño a un NPC con el bonus del portador aplicado (sin cambios para el resto). Contra
    /// usuarios NO se llama: en PvP el portador pega normal.</summary>
    public static int AplicarDanoANpc(User u, int dano) => Sumar(u, dano, _danoPct);

    private static int Sumar(User u, int valor, int pct) =>
        EsPortador(u) && valor > 0 ? (int)Math.Min(int.MaxValue, valor + (long)valor * pct / 100) : valor;

    // ------------------------------------------------------------------ control del evento

    /// <summary>Al arrancar el server (Program.cs): con Siempre=1 queda activo sin límite y sin anuncio.</summary>
    public static void Init()
    {
        CargarConfig();
        EventoActivo = _siempre;
        _portador = 0;
        _expira = 0;
        Console.WriteLine($"[PoderDioses] {(_siempre ? "activo siempre" : "apagado hasta /poderdioses")} " +
                          $"({_mapas.Count} mapas, exp +{_expPct}%, oro +{_oroPct}%, daño +{_danoPct}%).");
    }

    /// <summary>minutos &lt;= 0 = sin límite.</summary>
    public static void Iniciar(string activadoPor, int minutos)
    {
        CargarConfig();
        EventoActivo = true;
        _portador = 0;
        _expira = minutos > 0 ? Ahora() + minutos * 60.0 : 0;
        _ultimaVoz = 0;
        string cuanto = minutos > 0 ? $"Durante {minutos} minutos, el" : "El";
        Broadcast($"Poder de Dioses> Truenan los cielos y la tierra se estremece: los Dioses han vuelto la mirada hacia " +
                  $"las profundidades más oscuras del mundo. {cuanto} primero que ose recorrer sus dungeons cargará con su poder.",
                  FONT_INFOBOLD);
        Events.SonidoInicioEvento();
        Console.WriteLine($"[PoderDioses] Iniciado por {activadoPor} ({(minutos > 0 ? minutos + " min" : "sin límite")}, {_mapas.Count} mapas).");
        AsignarAlAzar(null);
    }

    public static void Finalizar()
    {
        if (!EventoActivo) return;
        var p = Portador();
        if (p != null) QuitarPoder(p, avisar: false);
        EventoActivo = false;
        _portador = 0;
        Broadcast("Poder de Dioses> ¡Los Dioses han finalizado con su labor y sus poderes han regresado a ellos!", FONT_INFOBOLD);
    }

    public static string Estado()
    {
        if (!EventoActivo) return "El evento poder de dioses está desactivado";
        var p = Portador();
        string quien = p != null ? $"{p.Name} en {NombreMapa(p.Pos.Map)} ({p.Pos.Map})" : "nadie (no hay jugadores en los dungeons del evento)";
        string tiempo = _expira > 0 ? $"Quedan {Math.Max(0, (int)(_expira - Ahora())) / 60} min." : "Sin límite de tiempo.";
        return $"El evento poder de dioses está activo. Portador: {quien}. {tiempo}";
    }

    /// <summary>Tick 1 Hz: vencimiento, portador que dejó de ser válido, y si está libre se lo da al
    /// que haya entrado a un dungeon (a lo sumo 1 s después de pisarlo).</summary>
    public static void Tick()
    {
        if (!EventoActivo) return;
        double ahora = Ahora();
        if (_expira > 0 && ahora >= _expira) { Finalizar(); return; }

        var p = Portador();
        if (p != null && !_mapas.ContainsKey(p.Pos.Map))
        {
            QuitarPoder(p, avisar: true);
            AsignarAlAzar(p, cayo: false);
            return;
        }
        if (p == null) AsignarAlAzar(null);
    }

    // ------------------------------------------------------------------ hooks

    /// <summary>Desde Facciones.ContarMuerte, ANTES de UserDie: el asesino se queda con el poder.</summary>
    public static void OnUsuarioMatado(int muertoIdx, int atacanteIdx)
    {
        if (!EventoActivo || _portador != muertoIdx) return;
        var muerto = UserListManager.UserList[muertoIdx];
        var atacante = UserListManager.UserList[atacanteIdx];
        QuitarPoder(muerto, avisar: true);
        if (Elegible(atacante)) Dar(atacante, anterior: muerto);
        else AsignarAlAzar(muerto);
    }

    /// <summary>Desde Combat.UserDie: murió por NPC, veneno, evento... (el asesino ya se resolvió antes).</summary>
    public static void OnUsuarioMuere(int userIndex)
    {
        if (!EventoActivo || _portador != userIndex) return;
        var u = UserListManager.UserList[userIndex];
        QuitarPoder(u, avisar: true);
        AsignarAlAzar(u);
    }

    /// <summary>Desde UserListManager.CloseUser.</summary>
    public static void OnUsuarioSale(int userIndex)
    {
        if (!EventoActivo || _portador != userIndex) return;
        var u = UserListManager.UserList[userIndex];
        QuitarPoder(u, avisar: false);
        AsignarAlAzar(u, cayo: false);
    }

    /// <summary>Desde AreaVisibility: la aureola se difunde una sola vez, quien entra después al área la necesita.</summary>
    public static void EnviarAureolaA(Connection obsConn, User u)
    {
        if (EsPortador(u)) ServerPackets.EfectoCharParticula(obsConn, u.Char.CharIndex, _particula, -1f, false);
    }

    // ------------------------------------------------------------------ privado

    private static User Portador()
    {
        if (_portador <= 0) return null;
        var u = UserListManager.UserList[_portador];
        if (u == null || !u.flags.UserLogged) { _portador = 0; return null; }
        return u;
    }

    private static bool Elegible(User u) =>
        u != null && u.flags.UserLogged && u.Conn != null && u.flags.Muerto == 0
        && !NpcManager.EsGmIntocable(u) && _mapas.ContainsKey(u.Pos.Map);

    /// <summary>cayo: el anterior murió (false = salió del dungeon o se desconectó).</summary>
    private static void AsignarAlAzar(User anterior, bool cayo = true)
    {
        var candidatos = new List<User>();
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != anterior && Elegible(u)) candidatos.Add(u);
        }
        if (candidatos.Count == 0) { _portador = 0; return; } // libre: lo gana el próximo que entre
        Dar(candidatos[_rng.Next(candidatos.Count)], anterior, cayo);
    }

    private static void Dar(User u, User anterior, bool cayo = true)
    {
        _portador = u.id;
        string donde = $"{NombreMapa(u.Pos.Map)} ({u.Pos.Map})";
        if (anterior != null)
            Broadcast($"Poder de Dioses> {anterior.Name} {(cayo ? "ha caído" : "ha abandonado las profundidades")}, y el fuego divino abandona su cuerpo en busca de un " +
                      $"nuevo portador... ahora arde en {u.Name}, en {donde}. ¿Quién tendrá el valor de derrotarlo y " +
                      "arrebatarle el poder de los Dioses?", FONT_INFOBOLD);
        else
            Broadcast($"Poder de Dioses> Los cielos se abren sobre {donde} y un rayo de luz dorada desciende sobre " +
                      $"{u.Name}. ¡Los Dioses lo han elegido como su campeón! Las bestias caen el doble de rápido ante él, y el oro y la " +
                      "gloria lo siguen a cada paso. ¿Quién tendrá el valor de derrotarlo y arrebatarle su poder?", FONT_INFOBOLD);
        if (u.Conn != null)
            ServerPackets.ConsoleMsg(u.Conn, $"Poder de Dioses> ¡Los Dioses te han bendecido con su poder! Tu daño físico y mágico " +
                $"contra criaturas sube +{_danoPct}% (contra jugadores pegás normal), y ganás +{_expPct}% de experiencia y +{_oroPct}% de oro. ¡Y ten cuidado! " +
                "Lo perderás si mueres o abandonas el dungeon.", FONT_INFOBOLD);
        DifundirAureola(u, quitar: false);
        Anunciar(u);
    }

    /// <summary>Sonido del evento para todo el server (cada vez) y la voz (con cooldown).</summary>
    private static void Anunciar(User u)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null)
                ServerPackets.PlayWave(o.Conn, Sounds.PODER_DIOSES, (byte)o.Pos.X, (byte)o.Pos.Y);
        }
        double ahora = Ahora();
        if (ahora - _ultimaVoz < VOZ_COOLDOWN_SEG) return;
        _ultimaVoz = ahora;
        EventVoice.AnunciarDelSistema(
            $"¡Los cielos rugen! {u.Name} tiene el poder de los Dioses. ¿Quién podrá derrotarlo?", _voz,
            flags: EventVoice.FLAG_MOSTRAR_TEXTO | EventVoice.FLAG_PODER_DIOSES); // el jugador puede apagar el cartel en Opciones
    }

    private static void QuitarPoder(User u, bool avisar)
    {
        if (u == null) return;
        if (_portador == u.id) _portador = 0;
        DifundirAureola(u, quitar: true);
        if (avisar && u.Conn != null)
            ServerPackets.ConsoleMsg(u.Conn, "Poder de Dioses> Has perdido el poder de dioses, todos tus incrementos de fuerza vuelven a la normalidad.", FONT_INFO);
    }

    private static void DifundirAureola(User u, bool quitar)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && AreaVisibility.VeChar(o, u.Pos.Map, u.Char.CharIndex))
                ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, _particula, quitar ? 0f : -1f, quitar);
        }
    }

    private static string NombreMapa(int map) => _mapas.TryGetValue(map, out var n) && !string.IsNullOrEmpty(n) ? n : "Mapa " + map;

    private static double Ahora() => Environment.TickCount64 / 1000.0;

    private static void Broadcast(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }

    /// <summary>Dat/PoderDioses.ini [PoderDioses]; lo que falte queda con los valores de arriba.</summary>
    private static void CargarConfig()
    {
        try
        {
            string iniPath = (string.IsNullOrEmpty(DataPaths.Root) ? "Dat" + Path.DirectorySeparatorChar : DataPaths.Sub("Dat")) + "PoderDioses.ini";
            _mapas = new Dictionary<int, string>(MapasDefault);
            if (!File.Exists(iniPath)) return;
            var ini = new IniFile(iniPath);

            string mapas = ini.Get("PoderDioses", "Mapas").Trim();
            if (!string.IsNullOrEmpty(mapas))
            {
                var nuevos = new Dictionary<int, string>();
                foreach (var s in mapas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(s, out int m) && m > 0)
                        nuevos[m] = MapasDefault.TryGetValue(m, out var n) ? n : "";
                if (nuevos.Count > 0) _mapas = nuevos;
            }
            int v;
            if ((v = ini.GetInt("PoderDioses", "ExpPct")) > 0) _expPct = v;
            if ((v = ini.GetInt("PoderDioses", "OroPct")) > 0) _oroPct = v;
            if ((v = ini.GetInt("PoderDioses", "DanoPct")) > 0) _danoPct = v;
            if ((v = ini.GetInt("PoderDioses", "Voz")) > 0) _voz = (byte)v;
            if ((v = ini.GetInt("PoderDioses", "Particula")) > 0) _particula = (short)v;
            string siempre = ini.Get("PoderDioses", "Siempre").Trim();
            if (siempre.Length > 0) _siempre = siempre != "0";
        }
        catch (Exception ex) { Console.WriteLine($"[PoderDioses] Error leyendo config: {ex.Message}"); }
    }
}
