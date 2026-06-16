using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Evento automático Inframundo (modEventoInframundo.bas) 1:1. Cada INTERVALO_EVENTO (60s test),
/// si hay jugadores, invaden 4 Hechiceros Elementales (Fuego/Agua/Tierra/Aire) en 4 ciudades
/// aleatorias distintas, con narrativa. Al matar uno, aparece el siguiente de inmediato; victoria
/// cuando caen los 4. Verificar() se llama 1/seg; OnHechiceroMuere desde Combat.MatarNpc.
/// (La notificación a Discord del VB6 se omite: no hay integración Discord en C#.)
/// </summary>
public static class InframundoEvento
{
    public const int NPC_FUEGO = 512, NPC_AGUA = 516, NPC_AIRE = 517, NPC_TIERRA = 518;

    public static bool EventoActivo { get; private set; }
    private static long _ultimoEvento;
    private static int _hechicerosVivos;
    private static byte _pasoSecuencia;
    private static long _proximoPasoTick;

    private const long INTERVALO_EVENTO = 60000;
    private const long PAUSA_NARRATIVA = 180000;

    private static readonly Random _rng = new();
    private static long Now => Environment.TickCount64;

    private record struct Ciudad(string Nombre, int Map, int X, int Y);
    private static readonly Ciudad[] _ciudades =
    {
        new("Ullathorpe", 1, 50, 50),
        new("Nix", 34, 50, 50),
        new("Banderbill", 59, 50, 50),
        new("Rinkel", 20, 50, 50),
        new("Illiandor", 194, 50, 50),
    };
    private static readonly int[] _ciudadesAsignadas = new int[4]; // índices (0..4) elegidos

    private const byte FONT_WARNING = 2, FONT_INFOBOLD = 4, FONT_INFO = 3, FONT_TALK = 0;

    /// <summary>
    /// Evento DESACTIVADO por pedido del usuario: spawneaba Hechiceros Elementales hostiles en las
    /// ciudades cada 60s y los guardias los atacaban (mataba NPCs de ciudad). En false, Verificar()
    /// nunca arranca el evento. Para reactivarlo, poner true.
    /// </summary>
    public static bool Habilitado = false;

    /// <summary>VerificarEventoInframundo: 1 vez por segundo. Arranca el evento o avanza la secuencia.</summary>
    public static void Verificar()
    {
        if (!Habilitado) return;
        if (!EventoActivo)
        {
            if ((Now - _ultimoEvento >= INTERVALO_EVENTO || _ultimoEvento == 0) && UserListManager.OnlineCount() > 0)
                Iniciar();
            return;
        }
        if (_pasoSecuencia > 0 && (Now >= _proximoPasoTick || _proximoPasoTick == 0))
            ProcesarSecuenciaSpawn();
    }

    private static void Iniciar()
    {
        EventoActivo = true;
        _hechicerosVivos = 0;
        _ultimoEvento = Now;
        _pasoSecuencia = 1;
        SeleccionarCiudadesAleatorias();
        Broadcast("¡ALERTA MUNDIAL! Los sellos del Inframundo se han roto...", FONT_WARNING);
        _proximoPasoTick = Now + 5000;
    }

    private static void SeleccionarCiudadesAleatorias()
    {
        for (int i = 0; i < 4; i++)
        {
            int idx;
            bool repetido;
            do
            {
                idx = _rng.Next(0, 5);
                repetido = false;
                for (int j = 0; j < i; j++) if (_ciudadesAsignadas[j] == idx) { repetido = true; break; }
            } while (repetido);
            _ciudadesAsignadas[i] = idx;
        }
    }

    private static void ProcesarSecuenciaSpawn()
    {
        int npcId; string msg; int iCiudad;
        switch (_pasoSecuencia)
        {
            case 1: npcId = NPC_FUEGO;  iCiudad = _ciudadesAsignadas[0];
                msg = $"El aire se calienta insoportablemente en {_ciudades[iCiudad].Nombre}. El Hechicero de Fuego ha emergido de las llamas."; break;
            case 2: npcId = NPC_AGUA;   iCiudad = _ciudadesAsignadas[1];
                msg = $"Las aguas se agitan violentamente en {_ciudades[iCiudad].Nombre}. El Hechicero de Agua reclama su dominio."; break;
            case 3: npcId = NPC_TIERRA; iCiudad = _ciudadesAsignadas[2];
                msg = $"El suelo tiembla bajo {_ciudades[iCiudad].Nombre}. El Hechicero de Tierra ha despertado."; break;
            default: npcId = NPC_AIRE;  iCiudad = _ciudadesAsignadas[3];
                msg = $"Vientos huracanados azotan {_ciudades[iCiudad].Nombre}. El Hechicero de Aire desciende de los cielos."; break;
        }

        var c = _ciudades[iCiudad];
        Broadcast(msg, FONT_INFOBOLD);

        var npc = SpawnEnCiudad(c.Map, c.X, c.Y, npcId);
        if (npc != null)
        {
            _hechicerosVivos++;
            Broadcast($">> Ubicación: Mapa {c.Map} X: {c.X} Y: {c.Y}", FONT_INFO);
            // Frase del hechicero sobre su cabeza, al área del NPC.
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var u = UserListManager.UserList[i];
                if (u.flags.UserLogged && u.Conn != null && u.Pos.Map == c.Map)
                    ServerPackets.ChatOverHead(u.Conn, "¡Destruiré todo lo que esté en mi camino! ¡El Inframundo reinará!", npc.CharIndex, FONT_TALK);
            }
        }
        else Console.WriteLine($"[Inframundo] Falló invocar NpcID {npcId} en {c.Nombre} (¿existe en NPCs.dat?).");

        if (_pasoSecuencia < 4)
        {
            _pasoSecuencia++;
            _proximoPasoTick = Now + PAUSA_NARRATIVA;
        }
        else
        {
            _pasoSecuencia = 0;
            Broadcast("Los cuatro Elementales del Inframundo han tomado posiciones. ¡Defiendan las ciudades antes de que sea tarde!", FONT_WARNING);
        }
    }

    /// <summary>OnHechiceroInframundoMuere: actualiza el conteo, fuerza la siguiente oleada o declara victoria.</summary>
    public static void OnHechiceroMuere(NpcManager.NpcInstance npc, int userIndex)
    {
        int id = npc.NpcIndex;
        if (id != NPC_FUEGO && id != NPC_AGUA && id != NPC_TIERRA && id != NPC_AIRE) return;

        _hechicerosVivos--;
        string elem = id switch { NPC_FUEGO => "Fuego", NPC_AGUA => "Agua", NPC_TIERRA => "Tierra", _ => "Aire" };
        string msg = userIndex > 0
            ? $"¡GLORIA! {UserListManager.UserList[userIndex].Name} ha derrotado al Hechicero de {elem}."
            : $"El Hechicero de {elem} ha caído.";
        Broadcast(msg, FONT_INFOBOLD);

        // Oleadas: si faltan por aparecer, el siguiente avanza de inmediato.
        if (_pasoSecuencia > 0)
        {
            Broadcast("¡Con la caída de este Elemento, el sello se debilita y el siguiente avanza!", FONT_WARNING);
            _proximoPasoTick = 0;
        }

        if (_hechicerosVivos <= 0 && _pasoSecuencia == 0)
        {
            Broadcast("¡VICTORIA! Todos los hechiceros del Inframundo han sido derrotados. El mundo está a salvo... por ahora.", FONT_INFO);
            EventoActivo = false;
        }
        else
        {
            Broadcast($"Aún quedan {_hechicerosVivos} hechiceros amenazando el mundo.", FONT_WARNING);
        }
    }

    /// <summary>Spawnea el hechicero en (map,x,y) o en el tile legal más cercano (NoRespawn).</summary>
    private static NpcManager.NpcInstance SpawnEnCiudad(int map, int cx, int cy, int npcId)
    {
        var md = MapLoader.Get(map);
        if (md == null) return null;
        for (int r = 0; r <= 6; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 1 || x > 100 || y < 1 || y > 100) continue;
                    if (md.IsBlocked(x, y) || NpcManager.NpcAt(map, x, y) != null) continue;
                    var npc = NpcManager.SpawnAt(map, npcId, (byte)x, (byte)y);
                    if (npc != null) { npc.NoRespawn = true; return npc; }
                    return null;
                }
        return null;
    }

    private static void Broadcast(string msg, byte font)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, font);
        }
    }
}
