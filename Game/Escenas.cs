namespace ServidorCS.Game;

/// <summary>
/// Escenas de cine para grabar trailers (NUEVO, no VB6). Un guion en Dat/Escenas/&lt;nombre&gt;.txt
/// dice a dónde va la cámara (el GM que la lanza), qué bots aparecen, a dónde caminan, a dónde
/// miran y qué dicen, cada cosa en su segundo desde que el GM escribe /escena &lt;nombre&gt;.
///
/// Los guiones llevan número de orden ("01_destierro"): /escena 1, /escena destierro y
/// /escena 01_destierro lanzan la misma. /escena sig lanza la que sigue a la última que corrió,
/// /escena otra la repite. /escena todo [desde] corre todas seguidas: cada "fin" lanza la próxima
/// y teletransporta al GM, así el trailer entero se graba con un solo comando. /escena con &lt;nick&gt;
/// hace que otro usuario (el que graba) viaje en cada "lugar" a la misma baldosa del GM.
///
/// Los bots de escena (NpcInstance.BotEscena) no tienen IA propia (TickBot los saltea), no se
/// pueden atacar y no reviven: se borran con la orden "fin" o con /escena stop.
///
/// Formato (una orden por línea, # comenta, el segundo usa punto decimal):
///   &lt;seg&gt; lugar    &lt;mapa&gt; &lt;x&gt; &lt;y&gt;    lleva al GM ahí. En el segundo 0 se hace al lanzar y el reloj
///                                       espera ESPERA_CARGA_SEG a que el cliente cargue el mapa.
///   &lt;seg&gt; aparece  &lt;id&gt; &lt;clase&gt; &lt;x&gt; &lt;y&gt; &lt;rumbo&gt; [npc=N] [bando=S] [obj=A,B,..] [barca] Nombre visible
///                obj= viste con esos OBJ de obj.dat (ropaje, casco, escudo, arma), por encima de npc=;
///                npc=N copia la pinta y el bando de ese NPC de NPCs.dat; bando=S fija el color del
///                nick (1 gris Renegado, 2 azul Ciudadano, 4 rojo Caos, 5 lila Exordio);
///                barca = navega: pisa agua, pasa a barca al entrar y vuelve a pie al desembarcar.
///   &lt;seg&gt; criatura &lt;id&gt; &lt;npcIndex&gt; &lt;x&gt; &lt;y&gt; [rumbo]   un NPC real (el kraken), quieto y pacífico
///   &lt;seg&gt; toma     &lt;id&gt; &lt;npcIndex&gt;    usa un NPC que ya vive en el mapa (Tharvel, un boss): lo vuelve
///                                       pacífico y al terminar le devuelve hostilidad y rumbo
///   &lt;seg&gt; camina   &lt;id&gt; &lt;x&gt; &lt;y&gt;
///   &lt;seg&gt; mira     &lt;id&gt; &lt;norte|sur|este|oeste&gt;
///   &lt;seg&gt; dice     &lt;id&gt; texto del globo
///   &lt;seg&gt; fx       &lt;id&gt; &lt;fx&gt; [vueltas]  efecto de hechizo sobre el personaje (FXgrh de Hechizos.dat)
///   &lt;seg&gt; clima    &lt;0|1|3&gt;             despejado / lluvia / tormenta; al terminar vuelve a despejado
///   &lt;seg&gt; ataca    &lt;id&gt; &lt;objetivo&gt;     lo persigue y le pega cada ~1 s (se ve, no resta vida) hasta
///                                       que el objetivo muere o llega "para &lt;id&gt;"
///   &lt;seg&gt; golpea   &lt;id&gt; &lt;objetivo&gt; [daño]            un solo golpe
///   &lt;seg&gt; hechizo  &lt;id&gt; &lt;objetivo&gt; &lt;fx&gt; [palabras]   FX sobre el objetivo y las palabras sobre el que lanza
///   &lt;seg&gt; muere    &lt;id&gt;                 sangre y se borra al instante siguiente
///   &lt;seg&gt; quita    &lt;id&gt;
///   &lt;seg&gt; fin
/// </summary>
public static class Escenas
{
    // Un paso cada 0.38 s: el mismo ritmo que la animación de caminata del cliente, así el bot
    // no "patina" ni se detiene entre pasos.
    private const double PASO_SEG = 0.38;

    // Lo que se le da al cliente para cargar el mapa después del teleport del "lugar" inicial,
    // más el cartel con el nombre del mapa (map_title_ui.js, 3,4 s): la acción no arranca debajo.
    private const double ESPERA_CARGA_SEG = 6.0;

    // Barcas propias (web-poc-pixi/importar_barca_exordio.py): la de los Exordianos es BODY1052; la
    // primera que se indexó (barca_2.webp, BODY1051) quedó para los Renegados (pedido del 15-sep).
    private const short BARCA_EXORDIO = 1052;
    private const short BARCA_RENEGADO = 1051;

    // Cabezas de los Exordianos (planchas 20092/20094/20097/20098/20099/20100/20104, pedido del 15-sep):
    // los bots las van rotando para que el ejército no salga con la misma cara.
    private static readonly short[] CABEZAS_EXORDIO = { 820, 822, 825, 826, 827, 829, 833 };
    private static int _cabezaSig;

    // Renegados (bots que copian la pinta del Bandido, npc=530) sin obj= propio: van rotando estos
    // tres equipos (pedido del 15-sep). OBJ de obj.dat: ropaje, casco, escudo, arma.
    private const int NPC_RENEGADO = 530;
    private static readonly int[][] EQUIPOS_RENEGADO =
    {
        new[] { 359, 131, 1000, 569 },   // Cota de Mallas, Almete de Hierro, Escudo de Hierro +2, Spatha
        new[] { 1089, 1236, 1356 },      // Tunica de Nigromante, Gorro de Aprendiz de Mago (gris), Baculo (Newbies)
        new[] { 499, 601, 404, 398 },    // Cota de Mallas (Mujer), Yelmo, Escudo de Tortuga, Sica
    };
    private static int _renegadoSig;

    private sealed class Orden
    {
        public double T;
        public string Verbo;
        public string[] Args;
    }

    private sealed class Actor
    {
        public NpcManager.NpcInstance Npc;
        public int DestX = -1, DestY = -1;
        public double ProxPaso;
        public bool Barca;
        public string Objetivo;       // "ataca": id del actor que persigue y golpea
        public double ProxGolpe;
        public double QuitarEn;       // "muere": se borra en este instante (deja ver la sangre)
        // "toma": el NPC es del mapa, no de la escena. No se borra: se le devuelve cómo estaba.
        public bool Prestado, HostilAntes;
        public byte RumboAntes;
    }

    private static List<Orden> _ordenes;
    private static int _siguiente;
    private static int _map;
    private static int _gm;
    private static double _inicio;
    private static bool _climaTocado;
    // /escena todo: al llegar a "fin" se deja anotada la próxima y el Tick siguiente la lanza
    // (lanzarla dentro del mismo Tick ejecutaría sus órdenes con el reloj de la anterior).
    private static bool _cadena;
    private static string _pendiente;
    // /escena con <nick>: quien graba viaja con el GM. Va a la MISMA baldosa (WarpUser no
    // revisa ocupación): al lado podría quedar en el camino de un bot, que no pisa usuarios.
    private static string _acompanante;
    private static readonly Random _rng = new();

    /// <summary>¿Es quien graba (/escena con)? NpcManager.EsGmIntocable lo usa para que ningún NPC lo ataque.</summary>
    public static bool EsAcompanante(User u) =>
        _acompanante != null && u != null && string.Equals(u.Name, _acompanante, StringComparison.OrdinalIgnoreCase);
    private static string _nombre = "";
    private static readonly Dictionary<string, Actor> _actores = new(StringComparer.OrdinalIgnoreCase);

    private static double Ahora => Environment.TickCount64 / 1000.0;

    private static string Carpeta()
    {
        string[] candidatas =
        {
            Path.Combine(DataPaths.Sub("Dat"), "Escenas"),
            Path.Combine(AppContext.BaseDirectory, "Dat", "Escenas"),
        };
        foreach (var c in candidatas)
            if (Directory.Exists(c)) return c;
        return candidatas[0];
    }

    /// <summary>Nombres de los guiones, en el orden de su número.</summary>
    private static string[] Guiones()
    {
        string dir = Carpeta();
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.txt").Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>"01_destierro" responde a "01_destierro", a "1" y a "destierro".</summary>
    private static string Resolver(string[] todas, string arg)
    {
        foreach (var n in todas)
            if (n.Equals(arg, StringComparison.OrdinalIgnoreCase)) return n;
        if (int.TryParse(arg, out int num))
            foreach (var n in todas)
            {
                int k = 0;
                while (k < n.Length && char.IsDigit(n[k])) k++;
                if (k > 0 && int.Parse(n.AsSpan(0, k)) == num) return n;
            }
        foreach (var n in todas)
        {
            int g = n.IndexOf('_');
            if (g > 0 && n[(g + 1)..].Equals(arg, StringComparison.OrdinalIgnoreCase)) return n;
        }
        return null;
    }

    /// <summary>Punto de entrada de /escena. Devuelve el mensaje para el GM.</summary>
    public static string Comando(User u, string arg)
    {
        var partes = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string a = partes.Length > 0 ? partes[0].ToLowerInvariant() : "";
        if (a == "stop")
        {
            _cadena = false;
            _pendiente = null;
            return $"Escena cortada ({Terminar()} bots borrados).";
        }

        var todas = Guiones();
        if (a == "lista")
            return todas.Length == 0 ? "No hay escenas en Dat/Escenas." : "Escenas: " + string.Join(", ", todas);

        if (a is "con" or "acompañante" or "acompanante")
        {
            if (partes.Length < 2) { _acompanante = null; return "Ya nadie viaja con vos en las escenas."; }
            string nick = string.Join(' ', partes.Skip(1));
            var otro = BuscarUsuario(nick);
            if (otro == null) return $"'{nick}' no está conectado.";
            if (otro.id == u.id) return "Ese sos vos: poné el nick de quien graba.";
            _acompanante = otro.Name;
            return $"{otro.Name} viaja con vos: cada escena lo lleva a tu misma baldosa. (/escena con, sin nick, lo saca)";
        }

        string nombre;
        bool cadena = false;
        if (a is "todo" or "iniciar" or "trailer")
        {
            nombre = partes.Length > 1 ? Resolver(todas, partes[1]) : todas.FirstOrDefault();
            cadena = true;
        }
        else if (a is "sig" or "siguiente")
        {
            int i = Array.FindIndex(todas, n => n.Equals(_nombre, StringComparison.OrdinalIgnoreCase));
            if (i + 1 >= todas.Length) return "No hay más escenas: esa era la última.";
            nombre = todas[i + 1];   // sin ninguna corrida todavía, i = -1: arranca por la primera
        }
        else if (a is "otra" or "repetir")
        {
            if (_nombre.Length == 0) return "Todavía no corriste ninguna escena.";
            nombre = _nombre;
        }
        else nombre = Resolver(todas, a);
        if (nombre == null) return $"No existe la escena '{arg}'. Probá /escena lista.";

        _cadena = cadena;
        _pendiente = null;
        string msg = Lanzar(u, nombre);
        if (_ordenes == null) _cadena = false;   // no arrancó: no hay cadena que seguir
        return _cadena ? msg + " Siguen todas solas hasta la última (/escena stop corta)." : msg;
    }

    /// <summary>Carga un guion y lo pone en marcha con la cámara en el GM u.</summary>
    private static string Lanzar(User u, string nombre)
    {
        Terminar();
        try { _ordenes = Parsear(File.ReadAllLines(Path.Combine(Carpeta(), nombre + ".txt"))); }
        catch (Exception ex) { _ordenes = null; return $"Error en el guion '{nombre}': {ex.Message}"; }

        _gm = u.id;
        _map = u.Pos.Map;
        _nombre = nombre;
        _siguiente = 0;
        _inicio = Ahora;

        // El "lugar" del segundo 0 se hace ya, y el reloj espera a que el cliente cargue el mapa.
        // Las "toma" y el "clima" del segundo 0 también van ya: un boss tiene que quedar pacífico
        // antes de que el GM aparezca a su lado.
        if (_ordenes.Count > 0 && _ordenes[0].Verbo == "lugar" && _ordenes[0].T <= 0)
        {
            try
            {
                Ejecutar(_ordenes[0]);
                _siguiente = 1;
                while (_siguiente < _ordenes.Count && _ordenes[_siguiente].T <= 0
                       && _ordenes[_siguiente].Verbo is "toma" or "clima")
                    Ejecutar(_ordenes[_siguiente++]);
            }
            catch (Exception ex)
            {
                string msg = $"Error en el guion '{nombre}' al arrancar: {ex.Message}";
                Terminar();
                return msg;
            }
            _inicio = Ahora + ESPERA_CARGA_SEG;
        }
        return $"Escena '{nombre}' en marcha en el mapa {_map} ({_ordenes.Count} órdenes).";
    }

    private static List<Orden> Parsear(string[] lineas)
    {
        var lista = new List<Orden>();
        for (int i = 0; i < lineas.Length; i++)
        {
            string l = lineas[i].Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            var t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2 || !double.TryParse(t[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seg))
                throw new FormatException($"línea {i + 1}: tiene que empezar con el segundo");
            lista.Add(new Orden { T = seg, Verbo = t[1].ToLowerInvariant(), Args = t.Skip(2).ToArray() });
        }
        // OrderBy es estable: dos órdenes del mismo segundo respetan el orden del archivo.
        return lista.OrderBy(o => o.T).ToList();
    }

    /// <summary>Lo llama el ciclo principal (~10 ms): dispara las órdenes vencidas y da los pasos.</summary>
    public static void Tick()
    {
        if (_pendiente != null)
        {
            string proxima = _pendiente;
            _pendiente = null;
            var gm = _gm >= 1 && _gm <= UserListManager.LastUser ? UserListManager.UserList[_gm] : null;
            if (gm == null || !gm.flags.UserLogged) { _cadena = false; return; }
            string msg = Lanzar(gm, proxima);
            if (_ordenes == null) { _cadena = false; Console.WriteLine($"[Escena] se cortó la cadena: {msg}"); }
            return;
        }
        if (_ordenes == null) return;

        double t = Ahora - _inicio;
        while (_ordenes != null && _siguiente < _ordenes.Count && _ordenes[_siguiente].T <= t)
        {
            var o = _ordenes[_siguiente++];
            try { Ejecutar(o); }
            catch (Exception ex) { Console.WriteLine($"[Escena {_nombre}] {o.Verbo} en {o.T}s: {ex.Message}"); }
        }
        if (_ordenes == null) return;   // la orden "fin" ya limpió todo

        double ahora = Ahora;
        List<string> muertos = null;
        foreach (var (id, a) in _actores)
        {
            if (a.Npc.Dead) continue;
            if (a.QuitarEn > 0)
            {
                if (ahora >= a.QuitarEn) (muertos ??= new()).Add(id);   // se borran después del foreach
                continue;
            }
            if (a.Objetivo != null && Pelear(a, ahora)) continue;
            if (a.DestX < 0 || ahora < a.ProxPaso) continue;
            if (a.Npc.X == a.DestX && a.Npc.Y == a.DestY) { a.DestX = -1; continue; }
            NpcManager.EscenaPaso(a.Npc.Map, a.Npc, a.DestX, a.DestY);
            if (a.Barca) NpcManager.EscenaBarca(a.Npc.Map, a.Npc);
            a.ProxPaso = ahora + PASO_SEG;
        }
        if (muertos != null)
            foreach (var id in muertos) Quitar(id);
    }

    /// <summary>"ataca": al lado del objetivo le pega cada ~1 s; lejos, camina hacia él. Devuelve false
    /// si el objetivo ya no está (murió o se borró) y el actor queda libre.</summary>
    private static bool Pelear(Actor a, double ahora)
    {
        if (!_actores.TryGetValue(a.Objetivo, out var obj) || obj.Npc.Dead || obj.QuitarEn > 0)
        {
            a.Objetivo = null;
            return false;
        }
        var n = a.Npc;
        var v = obj.Npc;
        if (Math.Abs(v.X - n.X) + Math.Abs(v.Y - n.Y) <= 1)
        {
            if (ahora >= a.ProxGolpe)
            {
                NpcManager.EscenaGolpe(n.Map, n, v, _rng.Next(140, 361));
                a.ProxGolpe = ahora + 0.9 + _rng.NextDouble() * 0.6;
            }
        }
        else if (ahora >= a.ProxPaso)
        {
            NpcManager.EscenaPaso(n.Map, n, v.X, v.Y);
            if (a.Barca) NpcManager.EscenaBarca(n.Map, n);
            a.ProxPaso = ahora + PASO_SEG;
        }
        return true;
    }

    private static void Ejecutar(Orden o)
    {
        string[] a = o.Args;
        switch (o.Verbo)
        {
            case "lugar":
            {
                short mapa = short.Parse(a[0]);
                var gm = _gm >= 1 && _gm <= UserListManager.LastUser ? UserListManager.UserList[_gm] : null;
                if (gm == null || !gm.flags.UserLogged) throw new InvalidOperationException("el GM que la lanzó ya no está conectado");
                Movement.WarpUser(_gm, mapa, short.Parse(a[1]), short.Parse(a[2]), fx: false, sonido: false);
                if (_acompanante != null)
                {
                    var otro = BuscarUsuario(_acompanante);
                    if (otro != null && otro.id != _gm)
                        Movement.WarpUser(otro.id, mapa, short.Parse(a[1]), short.Parse(a[2]), fx: false, sonido: false);
                    else Console.WriteLine($"[Escena {_nombre}] {_acompanante} no está conectado: no viajó");
                }
                _map = mapa;
                NpcManager.GetMapNpcs(mapa);   // que los NPCs del mapa existan antes de sumar actores
                break;
            }
            case "aparece":
            {
                string id = a[0];
                byte clase = Bots.ClasePorNombre(a[1]);
                byte x = byte.Parse(a[2]), y = byte.Parse(a[3]);
                byte rumbo = NpcManager.RumboPorNombre(a[4]);
                int copiar = 0, estado = -1, k = 5;
                bool barca = false;
                int[] objetos = null;
                for (; k < a.Length; k++)
                {
                    if (a[k].StartsWith("npc=", StringComparison.OrdinalIgnoreCase)) copiar = int.Parse(a[k].AsSpan(4));
                    else if (a[k].StartsWith("bando=", StringComparison.OrdinalIgnoreCase)) estado = int.Parse(a[k].AsSpan(6));
                    else if (a[k].StartsWith("obj=", StringComparison.OrdinalIgnoreCase))
                        objetos = a[k][4..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    else if (a[k].Equals("barca", StringComparison.OrdinalIgnoreCase)) barca = true;
                    else break;
                }
                string nombre = a.Length > k ? string.Join(' ', a.Skip(k)) : id;
                // Vetas y aura verde del Exordio: el cliente (exordio_fx.js::esExordiano) se las pone a
                // los bots con privileges 15/16, no 5 (el 5 es el bando de un NPC REAL). Un bot que copia
                // a un guardia del Exordio trae el 5 del guardia y salía sin aura: pasa a 15.
                if (estado < 0 && copiar > 0 && NpcData.Get(copiar).Status == 5) estado = 15;
                if (estado == 5) estado = 15;
                // No a Tharvel (712, cabeza propia) ni a quien copia un NPC sin cabeza (Guardián/Heraldo 724).
                int cabeza = -1;
                if (estado == 15 && (copiar == 0 || (copiar != 712 && NpcData.Get(copiar).Head != 0)))
                    cabeza = CABEZAS_EXORDIO[_cabezaSig++ % CABEZAS_EXORDIO.Length];
                if (objetos == null && copiar == NPC_RENEGADO)
                    objetos = EQUIPOS_RENEGADO[_renegadoSig++ % EQUIPOS_RENEGADO.Length];

                Quitar(id);
                var bot = Bots.Spawn(_map, x, y, clase, heading: rumbo, nick: nombre, copiarNpc: copiar, estado: estado, cabeza: cabeza, objetos: objetos);
                if (bot == null) { Console.WriteLine($"[Escena {_nombre}] no pude crear '{id}' (clase {a[1]})"); return; }
                bot.BotEscena = true;
                bot.Hostil = false;
                bot.Attackable = false;
                bot.Movement = 1;
                // Cada bando en su barca: Exordianos (15/16) y Renegados (pinta del Bandido o nick gris).
                if (barca && (estado == 15 || estado == 16)) bot.BarcaBody = BARCA_EXORDIO;
                else if (barca && (copiar == NPC_RENEGADO || estado == 1)) bot.BarcaBody = BARCA_RENEGADO;
                if (barca) NpcManager.EscenaBarca(_map, bot);
                _actores[id] = new Actor { Npc = bot, Barca = barca };
                break;
            }
            case "criatura":
            {
                string id = a[0];
                Quitar(id);
                var n = NpcManager.SpawnAt(_map, int.Parse(a[1]), byte.Parse(a[2]), byte.Parse(a[3]))
                        ?? throw new KeyNotFoundException($"no existe el NPC {a[1]}");
                n.Hostil = false;
                n.Attackable = false;
                n.NoRespawn = true;
                if (a.Length > 4) NpcManager.EscenaMirar(_map, n, NpcManager.RumboPorNombre(a[4]));
                _actores[id] = new Actor { Npc = n };
                break;
            }
            case "toma":
            {
                string id = a[0];
                int idx = int.Parse(a[1]);
                Quitar(id);
                var n = NpcManager.GetMapNpcs(_map).FirstOrDefault(c => !c.Dead && c.NpcIndex == idx && !_actores.Values.Any(v => v.Npc == c))
                        ?? throw new KeyNotFoundException($"no hay un NPC {idx} libre en el mapa {_map}");
                _actores[id] = new Actor { Npc = n, Prestado = true, HostilAntes = n.Hostil, RumboAntes = n.Heading };
                n.Hostil = false;
                break;
            }
            case "camina":
            {
                var act = ActorDe(a[0]);
                act.DestX = int.Parse(a[1]);
                act.DestY = int.Parse(a[2]);
                act.ProxPaso = 0;
                break;
            }
            case "mira":
            {
                var n = ActorDe(a[0]).Npc;
                NpcManager.EscenaMirar(n.Map, n, NpcManager.RumboPorNombre(a[1]));
                break;
            }
            case "dice":
            {
                var n = ActorDe(a[0]).Npc;
                NpcManager.BroadcastChatOverHead(n.Map, string.Join(' ', a.Skip(1)), (short)n.CharIndex, 1);
                break;
            }
            case "fx":
            {
                var n = ActorDe(a[0]).Npc;
                NpcManager.EscenaFx(n.Map, n, short.Parse(a[1]), a.Length > 2 ? short.Parse(a[2]) : (short)0);
                break;
            }
            case "clima":
            {
                byte tipo = byte.Parse(a[0]);
                NpcManager.EscenaClima(tipo);
                _climaTocado = tipo != 0;
                break;
            }
            case "ataca":
            {
                var act = ActorDe(a[0]);
                ActorDe(a[1]);   // que el objetivo exista ya: el error sale en el segundo de esta orden
                act.Objetivo = a[1];
                act.DestX = -1;
                act.ProxGolpe = 0;
                break;
            }
            case "para":
                ActorDe(a[0]).Objetivo = null;
                break;
            case "golpea":
            {
                var n = ActorDe(a[0]).Npc;
                var v = ActorDe(a[1]).Npc;
                NpcManager.EscenaGolpe(n.Map, n, v, a.Length > 2 ? int.Parse(a[2]) : _rng.Next(140, 361));
                break;
            }
            case "hechizo":
            {
                var n = ActorDe(a[0]).Npc;
                var v = ActorDe(a[1]).Npc;
                NpcManager.EscenaHechizo(n.Map, n, v, short.Parse(a[2]), string.Join(' ', a.Skip(3)));
                break;
            }
            case "muere":
            {
                var act = ActorDe(a[0]);
                NpcManager.EscenaFx(act.Npc.Map, act.Npc, 14, 0);
                act.Objetivo = null;
                act.DestX = -1;
                act.QuitarEn = Ahora + 0.7;
                break;
            }
            case "quita":
                Quitar(a[0]);
                break;
            case "fin":
                Terminar();
                if (_cadena)
                {
                    var todas = Guiones();
                    int i = Array.FindIndex(todas, n => n.Equals(_nombre, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0 && i + 1 < todas.Length) _pendiente = todas[i + 1];
                    else _cadena = false;   // era la última
                }
                break;
            default:
                throw new FormatException($"orden desconocida '{o.Verbo}'");
        }
    }

    private static User BuscarUsuario(string nombre)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return null;
    }

    private static Actor ActorDe(string id) =>
        _actores.TryGetValue(id, out var act) ? act : throw new KeyNotFoundException($"no apareció '{id}'");

    private static void Quitar(string id)
    {
        if (!_actores.TryGetValue(id, out var act)) return;
        Soltar(act);
        _actores.Remove(id);
    }

    /// <summary>Borra un actor de la escena, o devuelve como estaba al NPC prestado.</summary>
    private static void Soltar(Actor act)
    {
        if (!act.Prestado) { NpcManager.RemoveNpc(act.Npc); return; }
        act.Npc.Hostil = act.HostilAntes;
        if (!act.Npc.Dead) NpcManager.EscenaMirar(act.Npc.Map, act.Npc, act.RumboAntes);
    }

    /// <summary>Corta la escena, borra sus bots y suelta los NPCs prestados. Devuelve cuántos borró.</summary>
    private static int Terminar()
    {
        int borrados = 0;
        foreach (var act in _actores.Values)
        {
            if (!act.Prestado) borrados++;
            Soltar(act);
        }
        _actores.Clear();
        _ordenes = null;
        if (_climaTocado) { NpcManager.EscenaClima(0); _climaTocado = false; }
        return borrados;
    }
}
