using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Evento NUEVO (no 1:1 VB6) "El Barrido": una criatura especial (cuerpo 702) aparece aparte en el
/// mapa 238 (31,81) y se desplaza MUY rápido en horizontal de un lado al otro de su carril. Al llegar
/// a un bloqueo (pared / límite) rebota y vuelve. A cualquier usuario que pase por su carril lo golpea
/// y lo mata; a los que ya están en el carril (vivos o muertos) los corre hacia un costado para
/// despejar el camino. GM-only (Dios).
///
/// La criatura NO es un NPC real (no está en NPCs.dat, no es atacable ni respawnea): es un "character"
/// puro manejado por este módulo, que difunde sus CharacterCreate/Move/Remove a los usuarios del mapa.
/// </summary>
public static class BarridoEvento
{
    // ---- Configuración del evento ----
    private const int MAP = 238;
    private const byte START_X = 51, START_Y = 81;
    private const short BODY = 702;          // cuerpo de la criatura
    private const short HEAD = 0;
    private const byte HEADING_E = 2, HEADING_O = 4;   // N=1,E=2,S=3,O=4
    // Cada cuánto avanza un tile (ms). Bien por debajo de los 376ms de animación del cliente: el
    // cliente lo "teletransporta" tile a tile → lectura de barrido veloz (es lo buscado).
    private const long MOVE_INTERVAL_MS = 20;
    // Tope de tiles que se aleja del spawn antes de rebotar, por si el carril no tiene paredes.
    private const int MAX_SPAN = 40;
    // Semi-tamaño del recuadro para el modo "X" (movimiento diagonal): rebota dentro de un cuadro
    // de (2*BOX_HALF+1) tiles centrado en el spawn, además de respetar las paredes del mapa.
    private const int BOX_HALF = 8;
    private const short SND_GOLPE = 10;      // sonido al golpear (impacto genérico)
    private const short FX_SANGRE = 14;      // FX de sangre sobre la víctima

    // Modos de movimiento (lo cambia el GM con /movimiento N).
    public const int MOV_BARRIDO = 1;        // horizontal de lado a lado (><)
    public const int MOV_X = 2;              // diagonal rebotando en el recuadro (forma una X)

    public static bool EventoActivo { get; private set; }
    public static int MovMode { get; private set; } = MOV_BARRIDO;

    private static short _charIndex;
    private static byte _x, _y;
    private static int _dir = 1;             // +1 = este, -1 = oeste
    private static int _dirY = 1;            // +1 = sur, -1 = norte (modo X)
    private static long _nextMoveAt;
    // Usuarios (userIndex) a los que ya se les envió el CharacterCreate de la criatura (para crearla
    // también a quien entre al mapa con el evento ya en curso, y no recrearla en cada tick).
    private static readonly HashSet<int> _creadoPara = new();

    /// <summary>Inicia el evento: spawnea la criatura y la muestra a todos los del mapa.</summary>
    public static void Iniciar(string activadoPor)
    {
        if (EventoActivo) return;
        EventoActivo = true;
        _x = START_X; _y = START_Y; _dir = 1; _dirY = 1;
        _charIndex = CharIndexPool.Next();
        _nextMoveAt = Environment.TickCount64 + MOVE_INTERVAL_MS;
        _creadoPara.Clear();

        CrearParaTodos();

        // Mensaje rolero anunciando que el evento ha dado comienzo.
        BroadcastGlobal("¡El Barrido ha dado comienzo!", 4);
        BroadcastGlobal("La tierra tiembla en las profundidades... una bestia colosal ha despertado de su letargo y avanza arrasando todo a su paso. ¡Que los dioses amparen a quien se cruce en su camino!", 4);
        if (!string.IsNullOrWhiteSpace(activadoPor))
            BroadcastGlobal("(Evento invocado por: " + activadoPor + ")", 4);
        Events.SonidoInicioEvento(); // sonido de inicio de evento (252)
    }

    /// <summary>Finaliza el evento: quita la criatura del mapa.</summary>
    public static void Finalizar()
    {
        if (!EventoActivo) return;
        EventoActivo = false;
        ForEachMapUser(u => ServerPackets.CharacterRemove(u.Conn, _charIndex));
        CharIndexPool.Free(_charIndex);
        _charIndex = 0;
        _creadoPara.Clear();
        BroadcastGlobal("La criatura del Barrido se ha desvanecido.", 4);
    }

    /// <summary>Tick del loop principal (~10ms). Mueve la criatura cada MOVE_INTERVAL_MS y golpea.</summary>
    public static void Tick()
    {
        if (!EventoActivo) return;

        // Crear la criatura para quien haya entrado al mapa con el evento ya en curso.
        ActualizarVisibilidad();

        long now = Environment.TickCount64;
        if (now < _nextMoveAt) return;
        _nextMoveAt += MOVE_INTERVAL_MS;
        if (_nextMoveAt < now) _nextMoveAt = now + MOVE_INTERVAL_MS; // resync si quedó atrás

        if (MovMode == MOV_X) MoverDiagonal();
        else MoverBarrido();
    }

    /// <summary>Cambia el modo de movimiento en caliente (1=barrido, 2=X). Lo llama el GM.</summary>
    public static void SetMovMode(int modo)
    {
        MovMode = modo == MOV_X ? MOV_X : MOV_BARRIDO;
        _dir = 1; _dirY = 1; // reinicia sentidos para arrancar limpio el patrón nuevo

        // Al volver al barrido (><) la criatura tiene que retomar su carril original: el modo X
        // la desvía en Y, así que la reubicamos en START_Y y difundimos el reposicionamiento.
        if (MovMode == MOV_BARRIDO && EventoActivo && _y != START_Y)
        {
            _y = START_Y;
            DifundirMove();
        }
    }

    // ---- privado ----

    /// <summary>Modo barrido (><): avanza horizontal y rebota en bloqueo/límite/carril.</summary>
    private static void MoverBarrido()
    {
        int nx = _x + _dir;
        if (!PosLibre(nx, _y) || Math.Abs(nx - START_X) > MAX_SPAN)
        {
            _dir = -_dir;
            DifundirHeading(); // avisar el giro YA: el heading no viaja en CharacterMove
            return;
        }
        GolpearUsuariosEn((byte)nx, _y);
        _x = (byte)nx;
        DifundirMove();
    }

    /// <summary>Modo X: se mueve en diagonal y rebota en los cuatro lados del recuadro (forma una X).</summary>
    private static void MoverDiagonal()
    {
        // Rebote por eje: si el siguiente paso en X choca/sale del recuadro, invierte _dir; ídem _dirY.
        if (!PosEnRecuadro(_x + _dir, _y)) { _dir = -_dir; DifundirHeading(); }
        if (!PosEnRecuadro(_x, _y + _dirY)) _dirY = -_dirY;

        int nx = _x + _dir, ny = _y + _dirY;

        // Esquina/obstáculo en la diagonal: prueba reflejar el eje que quede trabado.
        if (!PosEnRecuadro(nx, ny))
        {
            if (PosEnRecuadro(_x - _dir, ny)) { _dir = -_dir; nx = _x + _dir; }
            else if (PosEnRecuadro(nx, _y - _dirY)) { _dirY = -_dirY; ny = _y + _dirY; }
            else { _dir = -_dir; _dirY = -_dirY; nx = _x + _dir; ny = _y + _dirY; }
            if (!PosEnRecuadro(nx, ny)) return; // sin salida este tick
        }

        GolpearUsuariosEn((byte)nx, (byte)ny);
        _x = (byte)nx; _y = (byte)ny;
        DifundirMove();
    }

    private static void DifundirMove() => ForEachMapUser(u => ServerPackets.CharacterMove(u.Conn, _charIndex, _x, _y));

    /// <summary>Difunde el rumbo actual (E/O) con CharacterChange. El heading NO viaja en CharacterMove,
    /// así que sin esto el cliente solo deduce el giro al consumir el primer paso en la dirección nueva
    /// → el sprite tardaba un tile en voltear. Esto lo voltea apenas rebota.</summary>
    private static void DifundirHeading()
    {
        byte h = _dir > 0 ? HEADING_E : HEADING_O;
        ForEachMapUser(u => ServerPackets.CharacterChange(u.Conn, _charIndex, BODY, HEAD, h,
            weapon: 0, shield: 0, helmet: 0, fx: 0, fxLoops: 0, weaponObjIndex: 0));
    }

    /// <summary>true si la criatura puede ocupar (x,y): dentro de límites y sin bloqueo de mapa.</summary>
    private static bool PosLibre(int x, int y)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return false;
        var md = MapLoader.Get(MAP);
        if (md != null && md.IsBlocked(x, y)) return false;
        return true;
    }

    /// <summary>PosLibre + dentro del recuadro de BOX_HALF tiles centrado en el spawn (modo X).</summary>
    private static bool PosEnRecuadro(int x, int y)
        => PosLibre(x, y) && Math.Abs(x - START_X) <= BOX_HALF && Math.Abs(y - START_Y) <= BOX_HALF;

    /// <summary>Mata a los usuarios vivos en (x,y) y corre a un costado a todos (vivos→muertos y muertos).</summary>
    private static void GolpearUsuariosEn(byte x, byte y)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.Conn == null) continue;
            if (u.Pos.Map != MAP || u.Pos.X != x || u.Pos.Y != y) continue;
            if (NpcManager.EsGmIntocable(u)) continue; // los GMs/Dioses no se ven afectados

            bool estabaVivo = u.flags.Muerto == 0;
            if (estabaVivo)
            {
                // FX + sonido de impacto y muerte instantánea.
                ForEachMapUser(o => { ServerPackets.PlayWave(o.Conn, SND_GOLPE, x, y); ServerPackets.CreateFX(o.Conn, u.Char.CharIndex, FX_SANGRE, 0); });
                u.Stats.MinHP = 0;
                ServerPackets.UpdateHP(u.Conn, 0);
                ServerPackets.ConsoleMsg(u.Conn, "¡La criatura te ha aplastado!", 2);
                Combat.UserDie(i);
            }

            // Correr el cuerpo hacia un costado libre (perpendicular al carril) para despejar el camino.
            CorrerACostado(i, u);
        }
    }

    /// <summary>Empuja al usuario a un tile libre arriba/abajo del carril (mismo mapa).</summary>
    private static void CorrerACostado(int userIndex, User u)
    {
        int px = u.Pos.X;
        foreach (int dy in new[] { 1, -1, 2, -2 })
        {
            int ny = u.Pos.Y + dy;
            if (PosLibre(px, ny) && UsuarioEn(px, ny) == 0)
            {
                Movement.WarpUser(userIndex, MAP, (short)px, (short)ny);
                return;
            }
        }
    }

    private static int UsuarioEn(int x, int y)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Pos.Map == MAP && u.Pos.X == x && u.Pos.Y == y) return i;
        }
        return 0;
    }

    private static void CrearParaTodos() => ForEachMapUser(u => { CrearPara(u); _creadoPara.Add(u.id); });

    private static void CrearPara(User u)
    {
        ServerPackets.CharacterCreate(u.Conn,
            charIndex: _charIndex, body: BODY, head: HEAD, heading: _dir > 0 ? HEADING_E : HEADING_O,
            x: _x, y: _y, weapon: 0, shield: 0, helmet: 0, fx: 0, fxLoops: 0,
            name: "0", privileges: 0, donador: 0, particulaFx: 0,
            armaAura: 0, bodyAura: 0, escudoAura: 0, headAura: 0, otraAura: 0, anilloAura: 0,
            isTopGold: false, weaponObjIndex: 0);
    }

    /// <summary>Crea la criatura para usuarios nuevos en el mapa y limpia los que se fueron.</summary>
    private static void ActualizarVisibilidad()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            bool enMapa = u != null && u.flags.UserLogged && u.Conn != null && u.Pos.Map == MAP;
            if (enMapa && !_creadoPara.Contains(i)) { CrearPara(u); _creadoPara.Add(i); }
            else if (!enMapa && _creadoPara.Contains(i)) _creadoPara.Remove(i);
        }
    }

    private static void ForEachMapUser(Action<User> action)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null && u.Pos.Map == MAP) action(u);
        }
    }

    private static void BroadcastGlobal(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }
}
