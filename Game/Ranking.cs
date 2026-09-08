namespace ServidorCS.Game;

/// <summary>
/// RANKING de personajes (NUEVO, no VB6). Alimenta la ventana de `mini/ranking_ui.js`.
///
/// No guarda ningún dato nuevo por personaje: TODO sale de lo que el `.chr` ya tiene
/// (`[STATS] GLD/BANCO/ELV/EXP`, `[MUERTES] UserMuertes/NpcsMuertes`, `[FACCIONES] Status`,
/// `[INIT] Clase`). Lo único propio es la FOTO de arranque de semana (ver más abajo), que va
/// en `Ranking/base_semana.dat` — NUNCA se escribe dentro de `Charfile/`.
///
/// ⚠️ Qué se puede y qué no con estos datos:
///   · HISTÓRICO anda en las 4 categorías: son totales que ya están en el archivo.
///   · SEMANAL sólo tiene sentido en MATADOS y CRIATURAS, que son contadores que sólo suben:
///     restarle la foto del lunes da exactamente "lo que mató esta semana".
///   · En ORO y NIVEL, NO. El oro baja cuando el jugador gasta, así que la resta no es
///     "oro farmeado" sino "oro que le sobró"; y de la experiencia el `.chr` guarda sólo la
///     del nivel en curso, no la acumulada. Para esas dos, `semanal` devuelve el histórico y
///     manda una NOTA que el cliente muestra, en vez de inventar un número que mentiría.
///     Si algún día se quiere el farmeo de verdad, hay que contarlo en `Combat.cs`/`Work.cs`
///     y persistirlo, que es un cambio de formato del `.chr`.
///
/// El escaneo es perezoso y cacheado: se releen los `.chr` recién cuando alguien pide un
/// ranking y la caché tiene más de TTL_SEGUNDOS. Con ~200 personajes tarda milisegundos; si
/// esto crece a miles, el escaneo hay que mudarlo a un hilo de fondo — hoy corre en el hilo
/// que atiende el paquete.
/// </summary>
public static class Ranking
{
    public const byte CAT_ORO = 0, CAT_MATADOS = 1, CAT_NPCS = 2, CAT_NIVEL = 3;
    public const byte PER_SEMANAL = 0, PER_HISTORICO = 1;

    public const int TOP = 10;
    private const int TTL_SEGUNDOS = 120;

    /// <summary>Una fila del ranking, ya lista para el paquete.</summary>
    public readonly struct Fila
    {
        public readonly string Nombre;
        public readonly byte Nivel, Faccion, Clase;
        public readonly int Valor;
        /// <summary>Cabeza y casco equipado ([INIT] Head/Casco): el cliente dibuja el retrato
        /// con los mismos EQUIP.heads/EQUIP.helmets que ya usa para el mundo.</summary>
        public readonly short Cabeza, Casco;
        public Fila(string nombre, byte nivel, int valor, byte faccion, byte clase,
                    short cabeza, short casco)
        {
            Nombre = nombre; Nivel = nivel; Valor = valor; Faccion = faccion; Clase = clase;
            Cabeza = cabeza; Casco = casco;
        }
    }

    /// <summary>Todo lo que hace falta de un personaje, leído una sola vez por escaneo.</summary>
    private sealed class Pj
    {
        public string Nombre = "";
        public byte Nivel, Faccion, Clase;
        public short Cabeza, Casco;
        public int Oro, Matados, Npcs, Exp;
        /// <summary>ELV·1.000.000 + EXP: el orden de "quién va más adelante".</summary>
        public long Progreso => (long)Nivel * 1_000_000L + Exp;
    }

    private static readonly object _lock = new();
    private static List<Pj> _cache = new();
    private static DateTime _cacheAl = DateTime.MinValue;

    // Foto del arranque de la semana: nombre -> (matados, npcs). Sólo se guardan los dos
    // contadores que sí sirven para el semanal.
    private static Dictionary<string, (int Matados, int Npcs)> _base = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _baseInicio = DateTime.MinValue;
    private static bool _baseCargada;

    private static string RutaBase => DataPaths.Sub("Ranking") + "base_semana.dat";

    // ------------------------------------------------------------------ API

    /// <summary>Top 10 de una categoría/período, más los textos del pie de la ventana.</summary>
    public static (List<Fila> Filas, string Inicio, int RestanteSegs, string Actualizado, string Nota)
        Obtener(byte categoria, byte periodo)
    {
        // GameLock va PRIMERO y envuelve todo, incluso el escaneo de disco: `PisarConLosOnline`
        // lee `UserList[]`, y el orden de candados del proyecto es GameLock afuera de todo (ver
        // UserList.cs). Tomarlo adentro de `_lock` armaría un ciclo con cualquier handler que ya
        // lo tenga y llame acá. El precio es que el escaneo (~200 archivos, decenas de ms) frena
        // el mundo, pero pasa como mucho una vez cada TTL_SEGUNDOS.
        lock (UserListManager.GameLock)
        lock (_lock)
        {
            Refrescar();

            string nota = "";
            bool semanal = periodo == PER_SEMANAL;
            if (semanal && (categoria == CAT_ORO || categoria == CAT_NIVEL))
            {
                nota = categoria == CAT_ORO
                    ? "El oro sube y baja: no se puede saber cuánto farmeó esta semana. Se muestra el total."
                    : "El personaje guarda sólo la experiencia del nivel en curso. Se muestra el total.";
                semanal = false;
            }

            var filas = Ordenar(categoria, semanal);
            var (inicio, restante) = VentanaSemanal();
            return (filas,
                    periodo == PER_SEMANAL ? Texto(_baseInicio) : "desde siempre",
                    periodo == PER_SEMANAL ? restante : -1,
                    Texto(_cacheAl),
                    nota);
        }
    }

    // ------------------------------------------------------------------ escaneo

    private static void Refrescar()
    {
        if ((DateTime.Now - _cacheAl).TotalSeconds < TTL_SEGUNDOS && _cache.Count > 0) return;

        var dir = DataPaths.Sub("Charfile");
        var lista = new List<Pj>();
        if (Directory.Exists(dir))
        {
            foreach (var ruta in Directory.EnumerateFiles(dir, "*.chr"))
            {
                var pj = Leer(ruta);
                if (pj != null) lista.Add(pj);
            }
        }

        // Los que están conectados pueden tener el .chr viejo (se graba cada tanto, ver
        // PersistenceWorker). Se pisan con los valores vivos para que el ranking no muestre
        // el oro de hace media hora.
        PisarConLosOnline(lista);

        _cache = lista;
        _cacheAl = DateTime.Now;
        AsegurarBase(lista);
    }

    private static Pj Leer(string ruta)
    {
        string nombre = Path.GetFileNameWithoutExtension(ruta);
        if (string.IsNullOrWhiteSpace(nombre)) return null;
        // Los GM quedan afuera: tienen oro y nivel de prueba, no compiten con nadie.
        if (AdminLoader.EsGM(nombre)) return null;

        var ini = new IniFile(ruta);
        if (!ini.Loaded) return null;

        int elv = ini.GetInt("STATS", "ELV");
        if (elv <= 0) return null;   // .chr a medio crear o corrupto

        return new Pj
        {
            Nombre = nombre,
            Nivel = (byte)Math.Clamp(elv, 0, 255),
            Faccion = (byte)Math.Clamp(ini.GetInt("FACCIONES", "Status"), 0, 255),
            Clase = (byte)Math.Clamp(ini.GetInt("INIT", "Clase"), 0, 255),
            Cabeza = (short)Math.Clamp(ini.GetInt("INIT", "Head"), 0, short.MaxValue),
            Casco = (short)Math.Clamp(ini.GetInt("INIT", "Casco"), 0, short.MaxValue),
            // "Oro" es lo de encima MÁS lo del banco: si no, el que guarda todo en la bóveda
            // aparecería pobre, que es justo al revés de lo que el ranking quiere mostrar.
            Oro = Suma(ini.GetInt("STATS", "GLD"), ini.GetInt("STATS", "BANCO")),
            Matados = Math.Max(0, ini.GetInt("MUERTES", "UserMuertes")),
            Npcs = Math.Max(0, ini.GetInt("MUERTES", "NpcsMuertes")),
            Exp = Math.Max(0, ini.GetInt("STATS", "EXP")),
        };
    }

    /// <summary>Suma que no desborda: el oro de encima + el del banco puede pasarse de int.</summary>
    private static int Suma(int a, int b)
    {
        long s = (long)Math.Max(0, a) + Math.Max(0, b);
        return (int)Math.Min(s, int.MaxValue);
    }

    private static void PisarConLosOnline(List<Pj> lista)
    {
        var porNombre = new Dictionary<string, Pj>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in lista) porNombre[p.Nombre] = p;

        var users = UserListManager.UserList;
        if (users == null) return;
        for (int i = 0; i < users.Length; i++)
        {
            var u = users[i];
            if (u == null || !u.flags.UserLogged || string.IsNullOrEmpty(u.Name)) continue;
            if (AdminLoader.EsGM(u.Name)) continue;
            if (!porNombre.TryGetValue(u.Name, out var p)) continue;

            p.Nivel = (byte)Math.Clamp((int)u.Stats.ELV, 0, 255);
            p.Faccion = (byte)Math.Clamp((int)u.Faccion.Status, 0, 255);
            p.Clase = (byte)Math.Clamp((int)u.Clase, 0, 255);
            // OrigChar, no Char: si el jugador está navegando o metamorfoseado, Char.Head es
            // la cabeza del barco o del bicho, no la suya. El retrato del ranking tiene que
            // mostrar al personaje.
            p.Cabeza = u.OrigChar.Head > 0 ? u.OrigChar.Head : u.Char.Head;
            p.Casco = u.Char.CascoAnim;
            p.Oro = Suma(u.Stats.GLD, u.Stats.Banco);
            // El cast a int no es cosmético: Stats.UsuariosMatados/NPCsMuertos son short y
            // Math.Max(0, short) es ambiguo entre las sobrecargas short e int.
            p.Matados = Math.Max(0, (int)u.Stats.UsuariosMatados);
            p.Npcs = Math.Max(0, (int)u.Stats.NPCsMuertos);
            p.Exp = (int)Math.Clamp(u.Stats.Exp, 0, int.MaxValue);   // Stats.Exp es double
        }
    }

    // ------------------------------------------------------------------ orden

    private static List<Fila> Ordenar(byte categoria, bool semanal)
    {
        var filas = new List<(Pj P, long V)>(_cache.Count);
        foreach (var p in _cache)
        {
            long v = categoria switch
            {
                CAT_ORO => p.Oro,
                CAT_MATADOS => semanal ? Delta(p.Nombre, p.Matados, true) : p.Matados,
                CAT_NPCS => semanal ? Delta(p.Nombre, p.Npcs, false) : p.Npcs,
                _ => p.Nivel,
            };
            if (v <= 0) continue;    // sin puntaje no entra: la tabla no muestra ceros
            filas.Add((p, v));
        }

        // Desempate por progreso (nivel+exp) para que el orden sea estable entre pedidos y no
        // baile cuando dos tienen el mismo valor.
        filas.Sort((a, b) =>
        {
            int c = b.V.CompareTo(a.V);
            if (c != 0) return c;
            c = b.P.Progreso.CompareTo(a.P.Progreso);
            return c != 0 ? c : string.CompareOrdinal(a.P.Nombre, b.P.Nombre);
        });

        var salida = new List<Fila>(Math.Min(TOP, filas.Count));
        for (int i = 0; i < filas.Count && i < TOP; i++)
        {
            var (p, v) = filas[i];
            salida.Add(new Fila(p.Nombre, p.Nivel, (int)Math.Min(v, int.MaxValue), p.Faccion,
                                p.Clase, p.Cabeza, p.Casco));
        }
        return salida;
    }

    /// <summary>Lo hecho desde la foto del lunes. Un personaje que no estaba en la foto es
    /// nuevo: cuenta todo lo suyo.</summary>
    private static long Delta(string nombre, int total, bool matados)
    {
        if (!_base.TryGetValue(nombre, out var b)) return total;
        return Math.Max(0, total - (matados ? b.Matados : b.Npcs));
    }

    // ------------------------------------------------------------------ semana

    /// <summary>Lunes 00:00 de la semana en curso, hora del servidor.</summary>
    private static DateTime LunesDeEstaSemana()
    {
        var hoy = DateTime.Now.Date;
        int dias = ((int)hoy.DayOfWeek + 6) % 7;   // domingo=0 en .NET, acá el lunes es el 0
        return hoy.AddDays(-dias);
    }

    private static (DateTime Inicio, int RestanteSegs) VentanaSemanal()
    {
        var inicio = LunesDeEstaSemana();
        var fin = inicio.AddDays(7);
        return (inicio, (int)Math.Max(0, (fin - DateTime.Now).TotalSeconds));
    }

    /// <summary>Si la foto guardada es de otra semana (o no hay), saca una nueva y la graba.</summary>
    private static void AsegurarBase(List<Pj> actuales)
    {
        if (!_baseCargada) { CargarBase(); _baseCargada = true; }

        var lunes = LunesDeEstaSemana();
        if (_baseInicio == lunes && _base.Count > 0) return;

        _base = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in actuales) _base[p.Nombre] = (p.Matados, p.Npcs);
        _baseInicio = lunes;
        GuardarBase();
    }

    private static void CargarBase()
    {
        try
        {
            if (!File.Exists(RutaBase)) return;
            var ini = new IniFile(RutaBase);
            if (!ini.Loaded) return;
            if (!DateTime.TryParse(ini.Get("INFO", "Inicio"), out var inicio)) return;
            _baseInicio = inicio.Date;

            var sec = ini.Section("BASE");
            if (sec == null) return;
            foreach (var kv in sec)
            {
                var partes = (kv.Value ?? "").Split('|');
                if (partes.Length < 2) continue;
                int.TryParse(partes[0], out int m);
                int.TryParse(partes[1], out int n);
                _base[kv.Key] = (m, n);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[Ranking] no se pudo leer la foto de la semana: " + e.Message);
        }
    }

    private static void GuardarBase()
    {
        try
        {
            var carpeta = DataPaths.Sub("Ranking");
            Directory.CreateDirectory(carpeta);
            var sb = new System.Text.StringBuilder();
            sb.Append("[INFO]\r\nInicio=").Append(_baseInicio.ToString("yyyy-MM-dd")).Append("\r\n[BASE]\r\n");
            foreach (var kv in _base)
                sb.Append(kv.Key).Append('=').Append(kv.Value.Matados).Append('|')
                  .Append(kv.Value.Npcs).Append("\r\n");
            // Cp1252 como el resto de los .dat del server: los nombres pueden traer acentos.
            File.WriteAllBytes(RutaBase, Network.Cp1252.GetBytes(sb.ToString()));
        }
        catch (Exception e)
        {
            Console.WriteLine("[Ranking] no se pudo grabar la foto de la semana: " + e.Message);
        }
    }

    private static string Texto(DateTime d) =>
        d == DateTime.MinValue ? "—" : d.ToString("HH:mm — dd/MM/yyyy");
}
