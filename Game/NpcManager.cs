using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Spawn y visibilidad de NPCs del mapa. Los NPCs vienen en el .csm (posición + npcindex);
/// sus datos visuales (body/head/heading/nombre) salen de NPCs.dat.
///
/// Cada NPC vivo tiene un CharIndex propio, compartido en el mismo espacio que los PJs
/// (el cliente no distingue: todo es "character"). Spawn perezoso: la primera vez que
/// alguien entra a un mapa, se instancian sus NPCs.
/// </summary>
public static class NpcManager
{
    public sealed class NpcInstance
    {
        public short CharIndex;
        public short Body, Head;
        public short WeaponAnim, ShieldAnim, CascoAnim; // equipamiento visible (guardias, etc.)
        public bool AguaValida;    // 1 = puede pisar agua (criaturas marinas); 0 = bloqueado por agua
        public bool TierraInvalida;// 1 = NO puede pisar tierra (criatura solo-agua); mantiene al NPC en agua
        public byte Heading;
        public byte X, Y;          // posición actual
        public byte OldX, OldY;    // posición anterior (FindDirection: evita oscilar)
        public byte SpawnX, SpawnY; // posición original (para respawn)
        public int NpcIndex;        // índice en NPCs.dat (para respawn)
        public int Map;
        public string Name;
        public int MinHP, MaxHP;   // vida actual / máxima
        public int GiveEXP, GiveGLD;
        public bool Dead;
        public double RespawnAt;    // segundos (Environment.TickCount/1000) en que revive; 0 = no programado
        public bool Hostil;
        public bool Attackable;     // Attackable=1 del .dat; 0 = intocable (mercaderes, sacerdotes, banqueros)
        public byte Movement;       // TipoAI: 0=persigue, 1=estático
        public short[] Spells;      // hechizos que lanza (null = no lanza)
        public int Domable;         // puntos requeridos para domar (0 = no domable)
        public int MaestroUser;     // userIndex del dueño si es mascota (0 = salvaje)
        public int MaestroNpc;      // CharIndex del entrenador dueño si es criatura de entrenamiento (0 = no)
        public bool NoRespawn;      // criaturas de entrenador: al morir desaparecen, no reviven
        public int MascotasCount;   // (entrenador) cantidad de criaturas vivas que invocó
        public int[] Criaturas;     // (entrenador) npcindices de criaturas invocables
        public int MascotaTargetNpc;// NPC que la mascota está atacando (0 = ninguno)
        public int MinHIT, MaxHIT;
        public int PoderAtaque, PoderEvasion; // impacto/evasión (NPCs.dat)
        public int ExpCount;        // pool de exp restante (CalcularDarExp); init = GiveEXP al spawnear
        public double NextAiAt;     // próximo tick de IA permitido (cooldown de movimiento/ataque)
        public double ParalizadoHasta; // segundos hasta los que está paralizado (0 = libre)
        public long TimerAtaque;    // TickCount del último golpe/hechizo (IntervaloPermiteAtacarNpc, 3000ms)
        // Aggro/loot (flags del NPC en VB6): estado original para restaurar al perder al atacante,
        // y nombres del atacante actual / primero (dueño del loot/exp).
        public bool OldHostil;      // Hostil original (al spawnear)
        public byte OldMovement;    // Movement original (al spawnear)
        public short Snd1, Snd2, Snd3; // sonidos (NPCs.dat): atacar / ser golpeado / morir
        public string AttackedBy = "";       // usuario que lo está atacando
        public string AttackedFirstBy = "";  // primer atacante (dueño del loot/exp)
        public bool Comercia;
        public byte NpcType, Status;
        public byte Ciudad;         // CIUDAD_* del guardia (1=Imp,2=Rep,3=Caos,5=Rinkel)
        public byte OrigHeading;    // heading original (para restaurar al volver al spawn)
        public (short objIndex, int amount)[] Inventario;
        public (short objIndex, int amount, double prob)[] Drops;

        // ---- Estado de patrulla de guardias (GuardiasAI, AI_NPC.bas) ----
        public (int x, int y)[] PatrolWP = new (int, int)[5]; // 1..4 waypoints (0 sin usar)
        public byte PatrolWPCount;       // cantidad de waypoints generados
        public byte PatrolWPCurrent;     // waypoint destino actual (1..count)
        public byte PatrolRoundsCompleted; // rondas completas (cada 3 regenera ruta)
        public byte PatrolStuckTicks;    // ticks atascado (cede paso / regenera)
        public int PatrolPrevX = -1, PatrolPrevY = -1;   // pos 2 ticks atrás (detección de oscilación A↔B)
        public byte PatrolOscTicks;      // veces seguidas que volvió a la casilla de 2 ticks atrás
        public byte PatrolDialogTicks;   // ticks restantes de diálogo (16=inicio)
        public byte PatrolDialogIdx;     // índice del diálogo (1..6); 0 = es el receptor
        public int PatrolDialogNPC;      // CharIndex/ref del guardia con quien dialoga
        public int PatrolEscortNPC;      // ref del guardia a escoltar tras dialogar
        public byte PatrolEscortTicks;   // ticks restantes de escolta
        public short PatrolTalkTimer;    // ticks hasta la próxima frase de patrulla
        public int TargetUser;           // userIndex perseguido (0 = ninguno)
        // ---- IA inteligente (custom) ----
        public int LastSeenX, LastSeenY;     // última posición conocida del enemigo (investigar)
        public byte InvestigateTicks;        // ticks restantes yendo a la última posición vista
        public byte LastFraseIdx;            // última frase de patrulla dicha (anti-repetición); 1..10, 0=ninguna
        public byte LastDialogIdx;           // último diálogo iniciado (anti-repetición); 1..6, 0=ninguno
        public short GreetTimer;             // ticks hasta el próximo saludo a un ciudadano
        public int LastGreetedUser;          // último usuario saludado (no spamear al mismo)
        // ---- Puertas (custom): el guardia abre puertas cerradas sin llave para cruzar y las cierra al alejarse ----
        public byte OpenedDoorX, OpenedDoorY; // tile ancla de la puerta que este guardia abrió (0 = ninguna)
        public short MoveAsideTimer;          // cooldown (ticks) para pedir paso a un usuario que bloquea (anti-spam)
        public byte TalkPauseTicks;           // ticks que el guardia se queda quieto tras hablarle a un usuario (saludo/pedir paso)
    }

    /// <summary>Busca el NPC vivo en (map,x,y), o null.</summary>
    public static NpcInstance NpcAt(int map, int x, int y)
    {
        if (!_byMap.TryGetValue(map, out var list)) return null;
        foreach (var n in list)
            if (!n.Dead && n.X == x && n.Y == y) return n;
        return null;
    }

    /// <summary>Busca un NPC vivo por su CharIndex en un mapa, o null.</summary>
    public static NpcInstance NpcByCharIndex(int map, int charIndex)
    {
        if (!_byMap.TryGetValue(map, out var list)) return null;
        foreach (var n in list)
            if (!n.Dead && n.CharIndex == charIndex) return n;
        return null;
    }

    public const double RespawnSeconds = 20.0;
    // Cadencia de movimiento/ataque del NPC. CLAVE para la fluidez de la caminata.
    // El cliente Godot anima un tile en 376ms (NPC_MOVE_SPEED=85 → 32px/85). Su cola de
    // movimiento de NPC NO actualiza character.x/y hasta consumir cada paso: si mando MÁS
    // rápido que 376ms la cola acumula, y el siguiente CharacterMove se calcula contra la
    // pos vieja → add=2 tiles → el cliente lo trata como TELEPORT y salta sin animar
    // (protocol_incoming.gd:953). Por eso debe ir un PELÍN por encima de 376ms, igual que
    // el VB6 (TIMER_AI=380ms): la cola se mantiene en 0-1 y la animación corre continua;
    // el micro-gap (~9ms) lo cubre el grace del cliente (NPC_GRACE_PERIOD=0.35).
    // El cliente Godot tiene DOS caminos para mover un NPC (protocol_incoming.gd:912):
    //   - move llega ANTES de 376ms (animación en curso) → lo ENCOLA (camino que se ve trabado).
    //   - move llega DESPUÉS de 376ms → lo aplica INMEDIATO (camino fluido, el que usa VB6).
    // 1:1 con VB6 (TIMER_AI = 380ms). El cliente reconoce al NPC (nombre numérico → is_npc) y lo
    // anima a NPC_MOVE_SPEED=85 (376ms/tile); con 380ms el gap es ~4ms, cubierto por el grace.
    private const double AiIntervalSeconds = 0.38;

    /// <summary>
    /// IA de NPCs hostiles (subset de NPCAI/HostilMalvadoAI): si hay un usuario adyacente
    /// lo ataca; si no, da un paso hacia el usuario más cercano del mapa. Lo llama un timer.
    /// </summary>
    public static void TickAI()
    {
        double now = Environment.TickCount64 / 1000.0;
        foreach (var kv in _byMap)
        {
            int map = kv.Key;
            foreach (var n in kv.Value)
            {
                if (n.Dead) continue;
                if (now < n.NextAiAt) continue;
                // Incremento ABSOLUTO del schedule (no "now + interval"): así el ritmo PROMEDIO
                // es exactamente AiIntervalSeconds aunque el muestreo del loop lo ejecute unos ms
                // tarde. Si "now + interval" se usara, ese retraso se acumularía en la base y el
                // intervalo real subía a ~390ms; con el incremento absoluto promedia 376ms y la
                // caminata encaja con la animación del cliente. Resync si quedó muy atrás (pausa).
                n.NextAiAt += AiIntervalSeconds;
                if (n.NextAiAt < now - AiIntervalSeconds) n.NextAiAt = now + AiIntervalSeconds;

                // VB6: NPC paralizado no se mueve ni ataca.
                if (n.ParalizadoHasta > now) continue;
                if (n.ParalizadoHasta != 0 && n.ParalizadoHasta <= now) n.ParalizadoHasta = 0;

                // Guardias de ciudad (NpcType=2): IA propia (patrulla/diálogo/frases/ataque por facción).
                if (n.NpcType == NPCTYPE_GUARDIASCITY) { GuardiasAI(map, n); continue; }

                // Mascotas (MaestroUser>0): SeguirAmo + atacar NPCs hostiles cercanos.
                if (n.MaestroUser > 0) { TickMascota(map, n); continue; }

                if (!n.Hostil) continue;

                // ¿Usuario adyacente? → atacar (prioridad absoluta, todos los tipos).
                int target = AdjacentUser(n, map, n.X, n.Y, out byte headingToUser);
                if (target > 0)
                {
                    // VB6 (AI_NPC.bas:422-431): gira hacia el usuario y difunde el cambio (ChangeNPCChar)
                    // ANTES de atacar, para que el NPC se vea mirando a quien pega.
                    var uTgt = UserListManager.UserList[target];
                    FaceTarget(map, n, uTgt.Pos.X, uTgt.Pos.Y);
                    n.Heading = headingToUser;
                    // VB6: si lanza hechizos, 50% magia / 50% golpe físico.
                    if (n.Spells != null && n.Spells.Length > 0 && _aiRng.Next(2) == 0)
                        Combat.NpcLanzaSpell(n, target);
                    else
                        Combat.NpcAtacaUsuario(n, target);
                    continue;
                }

                // NPCs que lanzan hechizos pueden atacar a distancia dentro del rango de visión.
                if (n.Spells != null && n.Spells.Length > 0)
                {
                    var uMago = NearestUser(n, map, n.X, n.Y, out _);
                    if (uMago != null && Math.Abs(uMago.Pos.X - n.X) <= RANGO_VISION_X && Math.Abs(uMago.Pos.Y - n.Y) <= RANGO_VISION_Y)
                    {
                        // 50% lanza hechizo a distancia; si no, persigue (salvo estático).
                        // SOLO salta el movimiento si REALMENTE casteó: si el hechizo está en cooldown
                        // (NpcLanzaSpell→false) cae al StepToward de abajo y sigue persiguiendo, sino el
                        // NPC se "trababa" parado medio tiempo esperando el intervalo de casteo.
                        if (_aiRng.Next(2) == 0)
                        {
                            FaceTarget(map, n, uMago.Pos.X, uMago.Pos.Y);
                            if (Combat.NpcLanzaSpell(n, uMago.id)) continue;
                        }
                    }
                }

                // VB6 TipoAI: Movement=1 (ESTATICO) no persigue, solo ataca adyacente.
                if (n.Movement == 1) continue;

                // Movement=0 (persigue): ir al usuario más cercano dentro del rango de visión (8×6).
                var u = NearestUser(n, map, n.X, n.Y, out _);
                if (u != null && Math.Abs(u.Pos.X - n.X) <= RANGO_VISION_X && Math.Abs(u.Pos.Y - n.Y) <= RANGO_VISION_Y)
                    StepToward(map, n, u.Pos.X, u.Pos.Y);
                else if (!n.OldHostil)
                {
                    // NPC pasivo PROVOCADO que se quedó sin enemigos en vista → vuelve a su estado
                    // original (AI_NPC.bas: restaura OldMovement/OldHostil y limpia AttackedBy).
                    n.Hostil = n.OldHostil; n.Movement = n.OldMovement;
                    n.AttackedBy = ""; n.AttackedFirstBy = ""; n.TargetUser = 0;
                }
            }
        }
    }

    // Rango de visión de NPCs (AI_NPC.bas:50-51).
    private const int RANGO_VISION_X = 8, RANGO_VISION_Y = 6;
    private static readonly Random _aiRng = new();

    // ---- IA inteligente de guardias (custom, NO 1:1 VB6) ----
    // Distancia Manhattan máxima desde su puesto que un guardia recorrerá persiguiendo a un
    // enemigo antes de abandonar y volver (evita que abandone la ciudad detrás de un señuelo).
    private const int GUARDIA_LEASH = 12;
    // Radio (en tiles) dentro del cual un guardia que detecta un enemigo alerta a otros guardias
    // de su misma ciudad para que converjan sobre el mismo objetivo (efecto enjambre).
    private const int GUARDIA_ALERTA_RADIO = 10;

    /// <summary>Heading (N=1,E=2,S=3,O=4) desde (fx,fy) hacia (tx,ty). 0 si es el mismo tile.</summary>
    public static byte HeadingTo(int fx, int fy, int tx, int ty)
    {
        if (tx > fx) return H_E;
        if (tx < fx) return H_O;
        if (ty > fy) return H_S;
        if (ty < fy) return H_N;
        return 0;
    }

    /// <summary>
    /// Hace que el NPC mire hacia (tx,ty) y, si el heading cambió, difunde el CharacterChange al mapa
    /// (VB6 ChangeNPCChar, MODULO_NPCs.bas:691). Así el NPC gira hacia el usuario al atacar/castear.
    /// </summary>
    public static void FaceTarget(int map, NpcInstance n, int tx, int ty)
    {
        byte h = HeadingTo(n.X, n.Y, tx, ty);
        if (h == 0 || h == n.Heading) return;
        n.Heading = h;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CharacterChange(o.Conn, (short)n.CharIndex, n.Body, n.Head,
                    n.Heading, n.WeaponAnim, n.ShieldAnim, n.CascoAnim, 0, 0, 0);
        }
    }

    /// <summary>Paraliza un NPC por 'segundos' (lo usa la magia). VB6: NPC no se mueve ni ataca.</summary>
    public static void ParalizarNpc(NpcInstance npc, double segundos)
    {
        npc.ParalizadoHasta = Environment.TickCount64 / 1000.0 + segundos;
        // Difunde la barra de progreso de parálisis a todos los del mapa (se dibuja bajo el NPC).
        byte segs = (byte)Math.Min(255, (int)Math.Ceiling(segundos));
        DifundirParalisisNpc(npc, segs);
    }

    /// <summary>Difunde la barra de parálisis del NPC a los usuarios del mapa (segs=0 la oculta).</summary>
    public static void DifundirParalisisNpc(NpcInstance npc, byte segs)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == npc.Map)
                ServerPackets.NpcParalysisProgress(o.Conn, npc.CharIndex, segs);
        }
    }

    /// <summary>
    /// IA de mascota (SeguirAmo + SeguirAgresor): ataca el NPC hostil más cercano;
    /// si no hay enemigos, sigue al amo manteniendo distancia ≤3 (VB6 SeguirAmo).
    /// </summary>
    private static void TickMascota(int map, NpcInstance pet)
    {
        var amo = UserListManager.UserList[pet.MaestroUser];
        if (amo == null || !amo.flags.UserLogged || amo.Pos.Map != map)
        { pet.MascotaTargetNpc = 0; return; } // amo lejos/desconectado: se queda quieta

        // 0) Target asignado por CheckPets/órdenes (defiende al amo): prioridad sobre la búsqueda libre.
        if (pet.MascotaTargetNpc > 0)
        {
            var objetivo = NpcByCharIndex(map, pet.MascotaTargetNpc);
            if (objetivo != null && !objetivo.Dead)
            {
                int d0 = Math.Abs(objetivo.X - pet.X) + Math.Abs(objetivo.Y - pet.Y);
                if (d0 <= 1) NpcAtacaNpc(map, pet, objetivo);
                else StepToward(map, pet, objetivo.X, objetivo.Y);
                return;
            }
            pet.MascotaTargetNpc = 0; // objetivo muerto/desaparecido: vuelve a la IA libre
        }

        // 1) Buscar NPC hostil cercano para atacar (en rango de visión del pet).
        NpcInstance enemigo = null; int mejorDist = int.MaxValue;
        foreach (var o in _byMap[map])
        {
            if (o.Dead || o == pet || o.MaestroUser > 0 || !o.Hostil) continue;
            int d = Math.Abs(o.X - pet.X) + Math.Abs(o.Y - pet.Y);
            if (d < mejorDist && Math.Abs(o.X - pet.X) <= RANGO_VISION_X && Math.Abs(o.Y - pet.Y) <= RANGO_VISION_Y)
            { mejorDist = d; enemigo = o; }
        }

        if (enemigo != null)
        {
            if (mejorDist <= 1) NpcAtacaNpc(map, pet, enemigo);   // adyacente: atacar
            else StepToward(map, pet, enemigo.X, enemigo.Y);      // acercarse
            return;
        }

        // 2) Sin enemigos: seguir al amo si está a más de 3 tiles.
        int distAmo = Math.Abs(amo.Pos.X - pet.X) + Math.Abs(amo.Pos.Y - pet.Y);
        if (distAmo > 3) StepToward(map, pet, amo.Pos.X, amo.Pos.Y);
    }

    // Criaturas elementales (AI_NPC.bas:45-46): no defienden al amo si CheckElementales=False.
    private const int ELEMENTALFUEGO = 93, ELEMENTALTIERRA = 94;

    /// <summary>
    /// CheckPets (SistemaCombate.bas:754) 1:1. Cuando el NPC 'attacker' ataca a 'userIndex', sus
    /// mascotas que no estén ya con objetivo lo atacan automáticamente (defienden al amo).
    /// CheckElementales=False excluye a los elementales (fuego/tierra).
    /// </summary>
    public static void CheckPets(NpcInstance attacker, int userIndex, bool checkElementales = true)
    {
        int map = attacker.Map;
        if (!_byMap.TryGetValue(map, out var list)) return;
        foreach (var pet in list)
        {
            if (pet.Dead || pet.MaestroUser != userIndex || pet == attacker) continue;
            if (!checkElementales && (pet.NpcIndex == ELEMENTALFUEGO || pet.NpcIndex == ELEMENTALTIERRA)) continue;
            if (pet.MascotaTargetNpc == 0) pet.MascotaTargetNpc = attacker.CharIndex;
        }
    }

    /// <summary>La mascota golpea a un NPC hostil (melee simple). Lo mata si HP llega a 0.</summary>
    private static void NpcAtacaNpc(int map, NpcInstance atacante, NpcInstance victima)
    {
        // Intervalo de ataque del NPC (IntervaloPermiteAtacarNpc, 3000ms; guardias 2000ms), igual que contra usuarios.
        if (!Intervals.PuedeAtacarNpc(ref atacante.TimerAtaque, AttackIntervalFor(atacante))) return;

        int max = Math.Max(atacante.MinHIT, atacante.MaxHIT);
        int dano = max > 0 ? _aiRng.Next(atacante.MinHIT, max + 1) : 1;
        if (dano < 1) dano = 1;
        victima.MinHP -= dano;
        if (victima.MinHP <= 0)
        {
            victima.Dead = true;
            victima.RespawnAt = Environment.TickCount64 / 1000.0 + RespawnSeconds;
            AreaVisibility.OnNpcRemoved(victima);
            CharIndexPool.Free(victima.CharIndex);   // reusar el índice; respawn pide uno nuevo
            victima.CharIndex = 0;
        }
    }

    // ===================================================================================
    //  GUARDIAS DE CIUDAD (GuardiasAI, AI_NPC.bas:65). Patrulla por waypoints, diálogos
    //  entre guardias, frases por facción, escolta, regreso al origen y ataque por ciudad.
    // ===================================================================================
    private const byte NPCTYPE_GUARDIASCITY = 2;
    private const byte NPCTYPE_DRAGON = 20; // eNPCType.Dragon: VE a través de invisibilidad/ocultar (VB6 AI_NPC)

    /// <summary>VB6: un usuario oculto/invisible es indetectable para los NPCs, EXCEPTO los dragones.</summary>
    private static bool NpcVeUsuario(NpcInstance n, User u)
        => n.NpcType == NPCTYPE_DRAGON || (u.flags.Oculto == 0 && u.flags.Invisible == 0);

    // Intervalo de ataque más corto para guardias (custom): pegan más seguido que un NPC común,
    // así dejan de ser inútiles en combate. El resto de NPCs sigue con NpcAtacar (3000ms) 1:1.
    public const long GUARDIA_ATAQUE_MS = 2000;

    /// <summary>Intervalo de ataque (ms) propio del NPC: guardias 2000ms, el resto default (3000ms).</summary>
    public static long AttackIntervalFor(NpcInstance n) => n.NpcType == NPCTYPE_GUARDIASCITY ? GUARDIA_ATAQUE_MS : 0;
    private const byte CIUDAD_IMPERIAL = 1, CIUDAD_REPUBLICANA = 2, CIUDAD_CAOTICA = 3, CIUDAD_RINKEL = 5;
    // Mapa de la ciudad neutral de Rinkel: todo guardia dentro queda neutral (no se mueve ni ataca).
    private const int MAPA_RINKEL = 20;

    // --- Saludos a ciudadanos (conciencia, custom). {0} = nombre del jugador ---
    private static readonly string[] SaludosGuardia =
    {
        "¡Salud, {0}!",
        "Buenas, {0}. Andá con cuidado.",
        "{0}, que los dioses te acompañen.",
        "¡Bienvenido, {0}!",
        "Todo en orden por aquí, {0}.",
        "Manténganse alerta, {0}.",
    };

    // --- Frases de patrulla por ciudad (10 por facción) — AI_NPC.bas:82 ---
    private static readonly string[] FrasesImperial =
    {
        "*mira los tejados* Nada se mueve. Por ahora.",
        "¡Por el Trono Dorado y la Llama Eterna!",
        "*aprieta el puño en el pecho* Gloria al Imperio.",
        "Los renegados pagan con sangre. Siempre.",
        "*golpea el escudo* ¡Firmes hasta el final!",
        "Juré lealtad ante el Altar Imperial. No lo olvido.",
        "*murmura* Dicen que hay espías del Caos entre nosotros...",
        "El capitán Ravnak fue ejecutado por deserción. Que sirva de lección.",
        "*detiene el paso* ...Ese crujido no era el viento.",
        "El Imperio no perdona la debilidad. Ni yo tampoco.",
    };
    private static readonly string[] FrasesRepublicana =
    {
        "*observa el horizonte* La República vigila a los suyos.",
        "¡Por el Consejo y la Voluntad del Pueblo!",
        "*frota las manos* Larga noche la de hoy...",
        "Los imperiales toman lo que no es suyo. Nosotros lo defendemos.",
        "*revisa el filo del arma* Siempre lista. Siempre.",
        "El senador Arveth prometió más raciones. Sigo esperando.",
        "*baja la voz* Anoche soñé con la guerra. Mal presagio.",
        "Libre o muerta. Eso juré. Y lo cumplo.",
        "*se detiene y escucha* ...No, era el viento.",
        "Mi padre murió por la República. Yo haré lo mismo si hace falta.",
    };
    private static readonly string[] FrasesCaotica =
    {
        "*escupe al suelo* Otro día más en este mundo miserable.",
        "El Caos no elige a sus siervos. Los forja.",
        "*afila el arma lentamente* Hace días que no pruebo sangre...",
        "*ríe entre dientes* Los débiles no sobreviven aquí. Como debe ser.",
        "Sigo vivo porque soy el más peligroso de la sala. Siempre.",
        "*olfatea el aire* Alguien que no conozco ronda por acá...",
        "El Caos me dio poder. El Imperio me dio una razón para usarlo.",
        "*mira sus manos* Esta cicatriz me la hice yo mismo. Para no olvidar.",
        "Demasiada calma. La calma siempre miente.",
        "*clava la vista en la oscuridad* Vení... si te animás.",
    };

    // --- Pedir paso: el guardia le habla a un ciudadano que le corta el camino en la patrulla (custom) ---
    private static readonly string[] FrasesCorrerse =
    {
        "Hazte a un lado, ciudadano. Estoy de servicio.",
        "Permiso, dejá pasar.",
        "Apartate, tengo una ronda que cumplir.",
        "Mové, que estás en mi camino.",
        "Despejá el paso, por favor.",
        "Circulando, circulando. No bloquees el camino.",
    };

    // --- Diálogos entre guardias (6 intercambios) — AI_NPC.bas:119 ---
    private static readonly string[] DialogoLinea1 =
    {
        "¿Tu sector despejado?",
        "¿Cuánto falta para el relevo?",
        "Escuché movimiento al este. Exploradores.",
        "Si entra alguien sin permiso, ¿qué hacemos?",
        "Tres turnos seguidos. Se me cierran los ojos.",
        "Vi sombras en los callejones del sur.",
    };
    private static readonly string[] DialogoLinea2 =
    {
        "Por acá sí... aunque encontré huellas que no deberían estar.",
        "Demasiado. Queda más noche que paciencia.",
        "Lo sé. Duplicaron patrullas y nadie nos avisó. Como siempre.",
        "Lo que hacemos siempre. Preguntamos después.",
        "Aguantá. Si caés, caemos todos.",
        "Callejones del sur... Eso no es bueno. Avisá al capitán.",
    };

    /// <summary>
    /// IA de guardia de ciudad (GuardiasAI 1:1). Orden: timers de diálogo/escolta → atacar
    /// NPC hostil cercano → volver al origen si estático → atacar/perseguir usuario enemigo
    /// → patrulla (diálogo entre guardias, frases, escolta, waypoints con pathfinding).
    /// </summary>
    private static void GuardiasAI(int map, NpcInstance n)
    {
        // ---- 0) Guardias de Rinkel: decorado puro. No patrullan, no dialogan, no persiguen ----
        // Ciudad neutral por MAPA (20=Rinkel), no solo por facción: cualquier guardia (sea cual
        // sea su Ciudad) spawneado en Rinkel queda quieto y no ataca a nadie.
        if (n.Ciudad == CIUDAD_RINKEL || map == MAPA_RINKEL) return;

        // ---- 0.5) Cerrar la puerta que el guardia abrió, al alejarse (custom). La puerta cubre el ancla
        // y ancla.x-1; cerramos cuando el guardia ya no ocupa ni está adyacente a ninguno de esos tiles. ----
        CerrarPuertaSiSeAlejo(map, n);

        // ---- 1) Timer de diálogo: en el tick 10 el iniciador dispara la respuesta del otro ----
        if (n.PatrolDialogTicks > 0)
        {
            n.PatrolDialogTicks--;
            if (n.PatrolDialogTicks == 10 && n.PatrolDialogIdx > 0)
            {
                var otro = NpcByCharIndex(map, n.PatrolDialogNPC);
                if (otro != null && !otro.Dead)
                {
                    BroadcastChatOverHead(map, "", n.CharIndex, 1);
                    BroadcastChatOverHead(map, DialogoLinea2[n.PatrolDialogIdx - 1], otro.CharIndex, 7);
                }
            }
            if (n.PatrolDialogTicks == 0)
            {
                n.PatrolDialogNPC = 0; n.PatrolDialogIdx = 0;
                n.PatrolEscortNPC = 0; n.PatrolEscortTicks = 0;
            }
        }

        // ---- 2) Timer de escolta ----
        if (n.PatrolEscortTicks > 0 && n.PatrolDialogTicks == 0)
        {
            n.PatrolEscortTicks--;
            if (n.PatrolEscortTicks == 0) n.PatrolEscortNPC = 0;
        }

        // ---- 3) NPC hostil en rango de visión → atacar (adyacente) o acercarse (BFS) ----
        var hostil = HostilEnRango(map, n);
        if (hostil != null)
        {
            int hDx = hostil.X - n.X, hDy = hostil.Y - n.Y;
            if ((Math.Abs(hDx) == 1 && hDy == 0) || (hDx == 0 && Math.Abs(hDy) == 1))
            {
                n.Heading = HeadingHacia(hDx, hDy);
                NpcAtacaNpc(map, n, hostil);
            }
            else
            {
                byte h = SeekPathHeading(map, n, hostil.X, hostil.Y, 6);
                if (h != 0) MoveNpcChar(map, n, h);
            }
            return;
        }

        // ---- 4) Guardia estático fuera de su origen → volver caminando ----
        // (salvo que esté persiguiendo un enemigo: el paso 6 valida/limpia el target y el leash
        //  lo trae de vuelta; así no oscila entre perseguir y regresar cada tick).
        if (n.Movement == 1 && n.TargetUser == 0 && (n.X != n.SpawnX || n.Y != n.SpawnY))
        {
            byte h = SeekPathHeading(map, n, n.SpawnX, n.SpawnY, 6);
            if (h != 0)
            {
                MoveNpcChar(map, n, h);
                if (n.X == n.SpawnX && n.Y == n.SpawnY && n.OrigHeading > 0) n.Heading = n.OrigHeading;
            }
            return;
        }

        // ---- 5) Usuario enemigo adyacente → atacar (hechizo 50% si lanza, si no físico) ----
        int adjU = AdjacentUser(n, map, n.X, n.Y, out byte hU);
        if (adjU > 0)
        {
            var u = UserListManager.UserList[adjU];
            if (EsEnemigoUsuario(n.Ciudad, u))
            {
                n.TargetUser = adjU;
                n.LastSeenX = u.Pos.X; n.LastSeenY = u.Pos.Y; n.InvestigateTicks = 14;
                AlertarGuardiasCercanos(map, n, adjU);   // enjambre: llama a los guardias cercanos
                FaceTarget(map, n, u.Pos.X, u.Pos.Y);    // gira hacia el enemigo (visible)
                n.Heading = hU;
                if (n.Spells != null && n.Spells.Length > 0 && _aiRng.Next(2) == 0)
                    Combat.NpcLanzaSpell(n, adjU);
                else
                    Combat.NpcAtacaUsuario(n, adjU);
                return;
            }
        }

        // ---- 6) Perseguir target ya fijado (leash + casteo a distancia + giro) ----
        if (n.TargetUser > 0)
        {
            var u = UserListManager.UserList[n.TargetUser];
            if (u != null && u.flags.UserLogged && u.flags.Muerto == 0 && u.Pos.Map == map
                && NpcVeUsuario(n, u) && !EsGmIntocable(u) && EsEnemigoUsuario(n.Ciudad, u))
            {
                n.LastSeenX = u.Pos.X; n.LastSeenY = u.Pos.Y; n.InvestigateTicks = 14; // recuerda dónde está
                // Leash: si se alejó demasiado de su puesto, abandona la persecución y vuelve.
                if (Math.Abs(n.X - n.SpawnX) + Math.Abs(n.Y - n.SpawnY) > GUARDIA_LEASH)
                {
                    n.TargetUser = 0; n.InvestigateTicks = 0;
                    byte hr = SeekPathHeading(map, n, n.SpawnX, n.SpawnY, 30);
                    if (hr != 0) MoveNpcChar(map, n, hr);
                    return;
                }
                int dist = Math.Abs(u.Pos.X - n.X) + Math.Abs(u.Pos.Y - n.Y);
                if (dist > 1)
                {
                    // Castea mientras persigue si tiene hechizos y el enemigo está en visión.
                    if (n.Spells != null && n.Spells.Length > 0
                        && Math.Abs(u.Pos.X - n.X) <= RANGO_VISION_X && Math.Abs(u.Pos.Y - n.Y) <= RANGO_VISION_Y
                        && _aiRng.Next(2) == 0)
                    {
                        FaceTarget(map, n, u.Pos.X, u.Pos.Y);
                        if (Combat.NpcLanzaSpell(n, n.TargetUser)) return;
                    }
                    FaceTarget(map, n, u.Pos.X, u.Pos.Y);
                    StepToward(map, n, u.Pos.X, u.Pos.Y);
                }
                return;
            }
            n.TargetUser = 0;
        }

        // ---- 7) Buscar nuevos enemigos en visión: prioriza el MÁS CERCANO (amenaza inmediata) ----
        int mejor = 0, mejorDist = int.MaxValue;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != map) continue;
            if (!NpcVeUsuario(n, u)) continue;
            if (EsGmIntocable(u)) continue; // los guardias no persiguen a GMs/Dioses
            if (Math.Abs(u.Pos.X - n.X) > RANGO_VISION_X || Math.Abs(u.Pos.Y - n.Y) > RANGO_VISION_Y) continue;
            if (!EsEnemigoUsuario(n.Ciudad, u)) continue;
            int d = Math.Abs(u.Pos.X - n.X) + Math.Abs(u.Pos.Y - n.Y);
            if (d < mejorDist) { mejorDist = d; mejor = i; }
        }
        if (mejor > 0)
        {
            var u = UserListManager.UserList[mejor];
            n.TargetUser = mejor;
            n.LastSeenX = u.Pos.X; n.LastSeenY = u.Pos.Y; n.InvestigateTicks = 14;
            AlertarGuardiasCercanos(map, n, mejor);   // enjambre: convergen los guardias cercanos
            if (mejorDist > 1)
            {
                FaceTarget(map, n, u.Pos.X, u.Pos.Y);
                StepToward(map, n, u.Pos.X, u.Pos.Y);
            }
            return;
        }

        // ---- 7.5) Investigar: perdió de vista al enemigo → ir a su última posición conocida ----
        // (solo si no es estático y la posición está dentro del leash desde su puesto).
        if (n.InvestigateTicks > 0)
        {
            n.InvestigateTicks--;
            bool dentroLeash = Math.Abs(n.LastSeenX - n.SpawnX) + Math.Abs(n.LastSeenY - n.SpawnY) <= GUARDIA_LEASH;
            if (n.Movement != 1 && dentroLeash && (n.X != n.LastSeenX || n.Y != n.LastSeenY))
            {
                FaceTarget(map, n, n.LastSeenX, n.LastSeenY);
                StepToward(map, n, n.LastSeenX, n.LastSeenY);
                return;
            }
            n.InvestigateTicks = 0; // llegó, es estático, o quedó fuera de rango: deja de investigar
        }

        // ============================ PATRULLA ============================

        // ---- 8) Guardia adyacente sin dialogar → iniciar diálogo (quieto este tick) ----
        if (n.PatrolDialogTicks == 0)
        {
            var adj = GuardiaAdyacente(map, n);
            if (adj != null && adj.PatrolDialogTicks == 0)
            {
                int dIdx = _aiRng.Next(1, 7); // 1..6
                if (dIdx == n.LastDialogIdx) dIdx = (dIdx % 6) + 1; // anti-repetición: no el mismo de la última vez
                n.LastDialogIdx = (byte)dIdx;
                BroadcastChatOverHead(map, DialogoLinea1[dIdx - 1], n.CharIndex, 7);
                n.PatrolDialogNPC = adj.CharIndex; n.PatrolDialogIdx = (byte)dIdx; n.PatrolDialogTicks = 16;
                adj.PatrolDialogNPC = n.CharIndex; adj.PatrolDialogIdx = 0; adj.PatrolDialogTicks = 16;
                n.PatrolEscortNPC = adj.CharIndex; n.PatrolEscortTicks = 20;
                adj.PatrolEscortNPC = 0; adj.PatrolEscortTicks = 0;
                return;
            }
        }
        if (n.PatrolDialogTicks > 0) return; // dialogando: quieto

        // ---- 9) Frases de patrulla aleatorias ----
        if (n.PatrolTalkTimer > 0) n.PatrolTalkTimer--;
        else
        {
            n.PatrolTalkTimer = (short)(40 + _aiRng.Next(40));
            string[] frases = n.Ciudad switch
            {
                CIUDAD_IMPERIAL => FrasesImperial,
                CIUDAD_REPUBLICANA => FrasesRepublicana,
                CIUDAD_CAOTICA => FrasesCaotica,
                _ => null,
            };
            if (frases != null)
            {
                int fIdx = _aiRng.Next(frases.Length);
                if (fIdx == n.LastFraseIdx - 1) fIdx = (fIdx + 1) % frases.Length; // no repetir la última
                n.LastFraseIdx = (byte)(fIdx + 1);
                BroadcastChatOverHead(map, frases[fIdx], n.CharIndex, 7); // modo 7 = ámbar dorado (diálogo NPC)
            }
        }

        // ---- 9.5) Conciencia: gira a mirar y saluda por nombre a un ciudadano que pase cerca ----
        if (n.GreetTimer > 0) n.GreetTimer--;
        else
        {
            // ciudadano (no enemigo) más cercano dentro de 4 tiles, vivo y visible
            User cerca = null; int dCerca = int.MaxValue;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var u = UserListManager.UserList[i];
                if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != map || u.flags.Oculto == 1 || u.flags.Invisible == 1) continue;
                if (EsEnemigoUsuario(n.Ciudad, u)) continue; // a los enemigos no se los saluda
                int d = Math.Abs(u.Pos.X - n.X) + Math.Abs(u.Pos.Y - n.Y);
                if (d <= 4 && d < dCerca) { dCerca = d; cerca = u; }
            }
            if (cerca != null)
            {
                FaceTarget(map, n, cerca.Pos.X, cerca.Pos.Y); // gira a mirarlo (se siente atento)
                if (cerca.id != n.LastGreetedUser)            // no saludar dos veces al mismo seguido
                {
                    BroadcastChatOverHead(map, string.Format(SaludosGuardia[_aiRng.Next(SaludosGuardia.Length)], cerca.Name), n.CharIndex, 7); // ámbar dorado (diálogo NPC)
                    n.LastGreetedUser = cerca.id;
                    n.TalkPauseTicks = 7; // se queda quieto ~2.5s mientras saluda
                }
                n.GreetTimer = (short)(140 + _aiRng.Next(120)); // ~50-100s hasta el próximo saludo
            }
            else { n.LastGreetedUser = 0; n.GreetTimer = 20; } // nadie cerca: reintenta pronto
        }

        // ---- 10) Si es estático, solo dialoga y dice frases, no se mueve ----
        if (n.Movement == 1) return;

        // ---- 10.5) Tras hablarle a un usuario (saludo / pedir paso) se queda quieto unos segundos ----
        if (n.TalkPauseTicks > 0) { n.TalkPauseTicks--; n.TargetUser = 0; return; }

        // ---- 11) Escolta a otro guardia tras dialogar ----
        if (n.PatrolEscortNPC > 0)
        {
            var esc = NpcByCharIndex(map, n.PatrolEscortNPC);
            if (esc != null && !esc.Dead)
            {
                if (Math.Abs(esc.X - n.X) + Math.Abs(esc.Y - n.Y) > 1)
                {
                    byte h = FindDirection(map, n, esc.X, esc.Y);
                    if (h != 0) MoveNpcChar(map, n, h);
                }
                n.TargetUser = 0;
                return;
            }
            n.PatrolEscortNPC = 0; n.PatrolEscortTicks = 0;
        }

        // ---- 12) Patrulla por waypoints ----
        // Si ya estamos encima del waypoint, avanzamos al siguiente y damos el paso EN EL
        // MISMO TICK (continue), en vez de gastar un tick sin moverse: ese tick perdido era
        // lo que producía el plantón (~780ms) cada vez que un tramo terminaba sobre un wp.
        if (n.PatrolWPCount == 0) GenerarWaypointsGuardia(map, n);

        for (int intento = 0; intento < 4; intento++)
        {
            int wpIdx = n.PatrolWPCurrent;
            if (wpIdx < 1 || wpIdx > n.PatrolWPCount) { wpIdx = 1; n.PatrolWPCurrent = 1; }
            var (wx, wy) = n.PatrolWP[wpIdx];

            if (n.X == wx && n.Y == wy)
            {
                if (wx == n.SpawnX && wy == n.SpawnY && n.OrigHeading > 0) n.Heading = n.OrigHeading;
                n.PatrolWPCurrent = (byte)((n.PatrolWPCurrent % n.PatrolWPCount) + 1);
                if (n.PatrolWPCurrent == 1)
                {
                    n.PatrolRoundsCompleted++;
                    if (n.PatrolRoundsCompleted >= 3)
                    {
                        n.PatrolRoundsCompleted = 0; n.PatrolWPCount = 0;
                        GenerarWaypointsGuardia(map, n);
                    }
                }
                n.PatrolStuckTicks = 0;
                continue; // reevaluar el nuevo waypoint y moverse este mismo tick
            }

            byte h = SeekPathHeading(map, n, wx, wy, 50, puertasAbribles: true);
            if (h == 0) h = FindDirection(map, n, wx, wy); // fallback greedy
            byte bx = n.X, by = n.Y;
            if (n.MoveAsideTimer > 0) n.MoveAsideTimer--;
            if (h != 0)
            {
                // Si un usuario le corta el camino, gira a mirarlo y le pide paso (con cooldown anti-spam).
                int dx = 0, dy = 0;
                switch (h) { case H_N: dy = -1; break; case H_E: dx = 1; break; case H_S: dy = 1; break; case H_O: dx = -1; break; }
                int destUser = UserAtTile(map, n.X + dx, n.Y + dy);
                if (destUser > 0 && n.MoveAsideTimer == 0)
                {
                    var bloqueador = UserListManager.UserList[destUser];
                    FaceTarget(map, n, bloqueador.Pos.X, bloqueador.Pos.Y);
                    BroadcastChatOverHead(map, FrasesCorrerse[_aiRng.Next(FrasesCorrerse.Length)], n.CharIndex, 7); // ámbar dorado (diálogo NPC)
                    n.MoveAsideTimer = (short)(12 + _aiRng.Next(8)); // ~4.5-7.5s antes de volver a pedir paso
                    n.TalkPauseTicks = 7; // se queda quieto ~2.5s tras pedir paso (no re-rutea de inmediato)
                }
                AbrirPuertaSiBloquea(map, n, h); // si el paso choca con puerta cerrada sin llave, la abre
                MoveNpcChar(map, n, h);
            }
            bool seMovio = n.X != bx || n.Y != by;

            // Oscilación A↔B: el guardia SÍ se mueve pero vuelve a la casilla en la que estaba 2
            // ticks atrás (va y vuelve, "se mueve en el mismo lugar"). FindDirection greedy lo
            // produce contra paredes. No lo detecta PatrolStuckTicks (que sólo mira el plantón),
            // así que lo tratamos como atasco: saltamos de waypoint o regeneramos la ruta.
            if (seMovio && n.X == n.PatrolPrevX && n.Y == n.PatrolPrevY)
            {
                n.PatrolOscTicks++;
                if (n.PatrolOscTicks >= 2)
                {
                    n.PatrolWPCurrent = (byte)((n.PatrolWPCurrent % n.PatrolWPCount) + 1);
                    n.PatrolOscTicks = 0; n.PatrolStuckTicks = 0;
                    n.PatrolWPCount = 0; n.PatrolRoundsCompleted = 0;
                    n.PatrolPrevX = -1; n.PatrolPrevY = -1;
                    break;
                }
            }
            else if (seMovio)
            {
                n.PatrolOscTicks = 0;
            }
            // Historial de posición para la próxima evaluación (pos antes de este paso).
            n.PatrolPrevX = bx; n.PatrolPrevY = by;

            if (seMovio)
            {
                n.PatrolStuckTicks = 0;
            }
            else
            {
                // Atascado: tras varios ticks salta de waypoint o regenera la ruta.
                n.PatrolStuckTicks++;
                if (n.PatrolStuckTicks >= 8)
                {
                    n.PatrolWPCurrent = (byte)((n.PatrolWPCurrent % n.PatrolWPCount) + 1);
                    n.PatrolStuckTicks = 0; n.PatrolWPCount = 0; n.PatrolRoundsCompleted = 0;
                }
                else if (n.PatrolStuckTicks >= 2)
                {
                    n.PatrolWPCurrent = (byte)((n.PatrolWPCurrent % n.PatrolWPCount) + 1);
                    n.PatrolStuckTicks = 0;
                }
            }
            break; // ya intentó moverse este tick
        }
        n.TargetUser = 0;
    }

    /// <summary>Genera hasta 3 waypoints a ±8 del origen + el origen (cierra el circuito). Custom:
    /// radio más amplio y separación mínima entre puntos para que la ronda sea una RUTA real por
    /// la zona, en vez de temblar a 1-2 tiles del spawn (sensación de patrulla con propósito).</summary>
    private static void GenerarWaypointsGuardia(int map, NpcInstance n)
    {
        const int RADIO = 8, SEP_MIN = 5; // separación Manhattan mínima entre waypoints
        byte total = 0;
        for (int wp = 1; wp <= 3; wp++)
        {
            bool ok = false; int attempts = 0, wx = 0, wy = 0;
            while (!ok && attempts < 40)
            {
                attempts++;
                wx = n.SpawnX + (_aiRng.Next(RADIO * 2 + 1) - RADIO);
                wy = n.SpawnY + (_aiRng.Next(RADIO * 2 + 1) - RADIO);
                if (wx < 1) wx = 1; if (wy < 1) wy = 1;
                if (wx > 100) wx = 100; if (wy > 100) wy = 100;
                if (!PuedeNpc(map, wx, wy, n.AguaValida, n.TierraInvalida)) continue;
                // separado del spawn y de los waypoints ya elegidos (ruta amplia, no jitter)
                if (Math.Abs(wx - n.SpawnX) + Math.Abs(wy - n.SpawnY) < SEP_MIN) continue;
                bool lejos = true;
                for (int k = 1; k <= total; k++)
                    if (Math.Abs(wx - n.PatrolWP[k].x) + Math.Abs(wy - n.PatrolWP[k].y) < SEP_MIN) { lejos = false; break; }
                if (lejos) ok = true;
            }
            if (ok) { total++; n.PatrolWP[total] = (wx, wy); }
        }
        if (total < 3) total++;
        n.PatrolWP[total] = (n.SpawnX, n.SpawnY); // origen como último waypoint
        n.PatrolWPCount = total;
        n.PatrolWPCurrent = 1;
        n.PatrolRoundsCompleted = 0;
    }

    /// <summary>Primer NPC hostil (no guardia) dentro del rango de visión del guardia, o null.</summary>
    private static NpcInstance HostilEnRango(int map, NpcInstance guardia)
    {
        foreach (var o in _byMap[map])
        {
            if (o.Dead || o == guardia || !o.Hostil) continue;
            if (o.NpcType == NPCTYPE_GUARDIASCITY) continue; // los guardias no se atacan entre sí
            if (o.MaestroUser > 0) continue;
            if (Math.Abs(o.X - guardia.X) <= RANGO_VISION_X && Math.Abs(o.Y - guardia.Y) <= RANGO_VISION_Y)
                return o;
        }
        return null;
    }

    /// <summary>Guardia de ciudad en un tile adyacente (para iniciar diálogo), o null.</summary>
    private static NpcInstance GuardiaAdyacente(int map, NpcInstance n)
    {
        (int dx, int dy)[] dirs = { (0,-1),(1,0),(0,1),(-1,0) };
        foreach (var (dx, dy) in dirs)
        {
            var o = NpcAt(map, n.X + dx, n.Y + dy);
            if (o != null && o != n && o.NpcType == NPCTYPE_GUARDIASCITY) return o;
        }
        return null;
    }

    /// <summary>Heading cardinal hacia un desplazamiento (dx,dy) priorizando el eje dominante.</summary>
    private static byte HeadingHacia(int dx, int dy)
    {
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx > 0 ? H_E : H_O;
        return dy > 0 ? H_S : H_N;
    }

    // Facción del jugador (VB6 GameLogic.bas:17-43, UserList().Faccion.Status).
    private const byte FAC_RENEGADO = 1, FAC_CIUDADANO = 2, FAC_REPUBLICANO = 3,
                       FAC_CAOS = 4, FAC_ARMADA = 5, FAC_MILICIA = 6;

    /// <summary>
    /// VB6 GuardiasAI (AI_NPC.bas:402-411): ¿el usuario es enemigo de la ciudad del guardia?
    /// Cada ciudad ataca a las facciones rivales. Rinkel (neutral) no ataca a nadie.
    /// </summary>
    /// <summary>
    /// ¿Puede el usuario 'u' atacar al NPC 'n'? (VB6 PuedeAtacarNPC, SistemaCombate.bas:2763). Reglas:
    ///  - Guardias de Rinkel (Ciudad Rinkel o mapa 20): intocables para TODOS.
    ///  - Resto de guardias: solo se puede atacar a guardias ENEMIGOS de la facción del usuario;
    ///    nunca a guardias aliados (un imperial no puede pegarle a un guardia imperial, etc.).
    ///    El guardia ignora el chequeo Attackable (su .dat no trae Attackable=1; deciden las facciones).
    ///  - No-guardias con Attackable=0 (mercaderes, sacerdotes, banqueros, etc.): intocables
    ///    (VB6 SistemaCombate.bas:2867).
    /// 'motivo' lleva el mensaje a mostrar cuando devuelve false.
    /// </summary>
    public static bool UsuarioPuedeAtacarNpc(User u, NpcInstance n, out string motivo)
    {
        motivo = "";
        if (n.NpcType != NPCTYPE_GUARDIASCITY)
        {
            if (!n.Attackable)
            { motivo = "No puedes atacar a esa criatura."; return false; }
            return true;
        }
        if (n.Ciudad == CIUDAD_RINKEL || n.Map == MAPA_RINKEL)
        { motivo = "Los guardias de Rinkel son neutrales y no pueden ser atacados."; return false; }
        if (!EsEnemigoUsuario(n.Ciudad, u))
        { motivo = "No puedes atacar a un guardia de tu misma facción."; return false; }
        return true;
    }

    /// <summary>
    /// VB6 NpcAtacaUser/IA: los NPCs ignoran a los GMs (Consejero/SemiDios/Dios/Soporte) — no los
    /// targetean, no los persiguen y no los atacan. NO se usa en UsuarioPuedeAtacarNpc: el GM sí
    /// puede atacar NPCs.
    /// </summary>
    internal static bool EsGmIntocable(User u) => u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO;

    private static bool EsEnemigoUsuario(byte ciudad, User u)
    {
        byte f = u.Faccion.Status;
        if (f == 0) return false; // sin facción: nunca enemigo
        return ciudad switch
        {
            CIUDAD_IMPERIAL    => f == FAC_CAOS || f == FAC_MILICIA || f == FAC_RENEGADO || f == FAC_REPUBLICANO,
            CIUDAD_REPUBLICANA => f == FAC_CAOS || f == FAC_CIUDADANO || f == FAC_ARMADA || f == FAC_RENEGADO,
            CIUDAD_CAOTICA     => f == FAC_CIUDADANO || f == FAC_REPUBLICANO || f == FAC_ARMADA || f == FAC_MILICIA,
            _ => false, // CIUDAD_RINKEL (5) y neutrales: no agreden
        };
    }

    /// <summary>
    /// Enjambre: un guardia que detectó un enemigo alerta a los demás guardias de su misma ciudad
    /// que tengan al enemigo dentro de GUARDIA_ALERTA_RADIO y que aún no estén persiguiendo a nadie,
    /// para que converjan sobre el mismo objetivo. Custom (no existe en el VB6 original).
    /// </summary>
    private static void AlertarGuardiasCercanos(int map, NpcInstance origen, int userIndex)
    {
        if (!_byMap.TryGetValue(map, out var list)) return;
        var u = UserListManager.UserList[userIndex];
        if (u == null || !u.flags.UserLogged) return;
        foreach (var o in list)
        {
            if (o.Dead || o == origen) continue;
            if (o.NpcType != NPCTYPE_GUARDIASCITY || o.Ciudad != origen.Ciudad) continue;
            if (o.TargetUser != 0 || o.PatrolDialogTicks > 0) continue;   // ocupado: no interrumpir
            if (Math.Abs(o.X - u.Pos.X) > GUARDIA_ALERTA_RADIO || Math.Abs(o.Y - u.Pos.Y) > GUARDIA_ALERTA_RADIO) continue;
            o.TargetUser = userIndex;
        }
    }

    /// <summary>Difunde un ChatOverHead a todos los usuarios del mapa (SendData ToNPCArea).</summary>
    private static void BroadcastChatOverHead(int map, string chat, short charIndex, byte mode)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null && u.Pos.Map == map)
                ServerPackets.ChatOverHead(u.Conn, chat, charIndex, mode);
        }
    }

    /// <summary>Si hay un usuario en un tile adyacente, devuelve su userIndex y el heading hacia él.</summary>
    private static int AdjacentUser(NpcInstance npc, int map, int x, int y, out byte heading)
    {
        // N=1,E=2,S=3,O=4
        (int dx, int dy, byte h)[] dirs = { (0,-1,1),(1,0,2),(0,1,3),(-1,0,4) };
        foreach (var (dx, dy, h) in dirs)
        {
            int ux = x + dx, uy = y + dy;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var u = UserListManager.UserList[i];
                if (u.flags.UserLogged && u.flags.Muerto == 0 && !EsGmIntocable(u)
                    && NpcVeUsuario(npc, u)
                    && u.Pos.Map == map && u.Pos.X == ux && u.Pos.Y == uy)
                { heading = h; return i; }
            }
        }
        heading = 0; return 0;
    }

    private static User NearestUser(NpcInstance npc, int map, int x, int y, out int dist)
    {
        User best = null; dist = int.MaxValue;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != map) continue;
            if (EsGmIntocable(u)) continue; // los NPCs no persiguen a GMs/Dioses
            if (!NpcVeUsuario(npc, u)) continue; // invisible/oculto: indetectable (salvo dragón)
            int d = Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y);
            if (d < dist) { dist = d; best = u; }
        }
        return best;
    }

    /// <summary>
    /// Da un paso del NPC hacia (tx,ty). Usa A*/BFS (SeekPath) para rodear obstáculos;
    /// si no encuentra camino, cae a FindDirection (greedy 1:1 VB6).
    /// </summary>
    private static void StepToward(int map, NpcInstance n, int tx, int ty)
    {
        // Pathfinding BFS (VB6 SeekPath): primer paso del camino más corto al target.
        byte heading = SeekPathHeading(map, n, tx, ty, 30);
        // Fallback greedy si no hay camino calculado.
        if (heading == 0) heading = FindDirection(map, n, tx, ty);
        if (heading == 0) return; // ya al lado, mismo tile, o sin salida
        MoveNpcChar(map, n, heading);
    }

    /// <summary>
    /// SeekPath (PathFinding.bas:230) portado como BFS. Devuelve el heading del PRIMER paso
    /// del camino más corto de (npc) a (tx,ty) esquivando bloqueos/NPCs. 0 = sin camino.
    /// </summary>
    private static byte SeekPathHeading(int map, NpcInstance n, int tx, int ty, int maxSteps, bool puertasAbribles = false)
    {
        if (n.X == tx && n.Y == ty) return 0;
        if (tx < 1 || tx > 100 || ty < 1 || ty > 100) return 0;

        // BFS desde el NPC. prev[] reconstruye el camino. Limitado a una ventana de maxSteps.
        var prev = new (int px, int py)[101, 101];
        var visited = new bool[101, 101];
        var q = new Queue<(int x, int y)>();
        visited[n.X, n.Y] = true;
        q.Enqueue((n.X, n.Y));
        int expanded = 0;
        bool found = false;

        // N=1,E=2,S=3,O=4
        (int dx, int dy)[] dirs = { (0,-1),(1,0),(0,1),(-1,0) };

        while (q.Count > 0 && expanded <= maxSteps * maxSteps)
        {
            var (cx, cy) = q.Dequeue();
            if (cx == tx && cy == ty) { found = true; break; }
            expanded++;
            foreach (var (dx, dy) in dirs)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 1 || nx > 100 || ny < 1 || ny > 100) continue;
                if (visited[nx, ny]) continue;
                // El tile destino del camino debe ser caminable (salvo que sea el propio target).
                bool esTarget = nx == tx && ny == ty;
                if (!esTarget && !PuedeNpc(map, nx, ny, n.AguaValida, n.TierraInvalida, puertasAbribles)) continue;
                visited[nx, ny] = true;
                prev[nx, ny] = (cx, cy);
                q.Enqueue((nx, ny));
            }
        }
        if (!found) return 0;

        // Reconstruir: retroceder desde el target hasta el primer paso saliendo del NPC.
        int rx = tx, ry = ty;
        while (!(prev[rx, ry].px == n.X && prev[rx, ry].py == n.Y))
        {
            var p = prev[rx, ry];
            if (p.px == 0 && p.py == 0) return 0; // sin reconstrucción válida
            rx = p.px; ry = p.py;
        }
        // (rx,ry) es el tile adyacente al NPC en el camino → heading hacia él.
        if (ry < n.Y) return H_N;
        if (rx > n.X) return H_E;
        if (ry > n.Y) return H_S;
        if (rx < n.X) return H_O;
        return 0;
    }

    // N=1,E=2,S=3,O=4
    private const byte H_N = 1, H_E = 2, H_S = 3, H_O = 4;

    /// <summary>VB6 LegalPosNPC: true si el NPC puede pisar (x,y): dentro de límites, no bloqueado, sin otro
    /// NPC y —si no es criatura de agua (aguaValida=0)— que el tile NO sea agua (HayAgua).</summary>
    private static bool PuedeNpc(int map, int x, int y, bool aguaValida = false, bool tierraInvalida = false, bool puertasAbribles = false)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return false;
        var md = MapLoader.Get(map);
        if (md != null && md.IsBlocked(x, y))
        {
            // Para la IA de guardias, una puerta cerrada SIN llave se considera transitable: el guardia
            // la abrirá al dar el paso (AbrirPuertaSiBloquea). Cualquier otro bloqueo (pared) sigue firme.
            if (!(puertasAbribles && PuertaCerradaEn(map, x, y).x != 0)) return false;
        }
        if (!aguaValida && md != null && md.HasWater(x, y)) return false;   // NPC terrestre no pisa agua
        // Criatura solo-agua (TierraInvalida): no pisa tierra. (MODULO_NPCs.bas:780)
        if (tierraInvalida && md != null && !md.HasWater(x, y)) return false;
        if (NpcAt(map, x, y) != null) return false;
        return true;
    }

    /// <summary>Si (tx,ty) está cubierto por una puerta CERRADA sin llave, devuelve su tile ancla
    /// (donde vive el FloorObj). La puerta ocupa el ancla y ancla.x-1, así que el tile puede ser el
    /// ancla mismo o ancla-1 (entonces el FloorObj está en tx+1). (0,0) si no hay puerta operable.</summary>
    private static (byte x, byte y) PuertaCerradaEn(int map, int tx, int ty)
    {
        var md = MapLoader.Get(map);
        if (md == null) return (0, 0);
        for (int dx = 0; dx <= 1; dx++)
        {
            int ax = tx + dx;
            if (ax < 1 || ax > 100 || ty < 1 || ty > 100) continue;
            short obj = md.FloorObj[ax, ty];
            if (obj <= 0) continue;
            var od = ObjData.Get(obj);
            if (od.Type == ObjType.Puertas && od.Cerrada == 1 && od.Llave == 0)
                return ((byte)ax, (byte)ty);
        }
        return (0, 0);
    }

    /// <summary>IA de guardia (custom): si el próximo paso en 'heading' choca con una puerta cerrada
    /// sin llave, la abre y recuerda cuál, para poder cruzarla. La cerrará al alejarse.</summary>
    private static void AbrirPuertaSiBloquea(int map, NpcInstance n, byte heading)
    {
        int nx = n.X, ny = n.Y;
        switch (heading)
        {
            case H_N: ny--; break;
            case H_E: nx++; break;
            case H_S: ny++; break;
            case H_O: nx--; break;
            default: return;
        }
        var (ax, ay) = PuertaCerradaEn(map, nx, ny);
        if (ax == 0) return;
        if (Accion.OperarPuerta((short)map, ax, ay, abrir: true))
        {
            n.OpenedDoorX = ax; n.OpenedDoorY = ay;
        }
    }

    /// <summary>Cierra la puerta que el guardia abrió cuando ya se alejó (no ocupa ni está adyacente a
    /// ninguno de los dos tiles que cubre: ancla y ancla-1). Custom para la IA de guardias.</summary>
    private static void CerrarPuertaSiSeAlejo(int map, NpcInstance n)
    {
        if (n.OpenedDoorX == 0) return;
        int ax = n.OpenedDoorX, ay = n.OpenedDoorY;
        // Distancia Chebyshev a ambos tiles de la puerta; si en alguno sigue ≤1, no cerrar todavía.
        int d1 = Math.Max(Math.Abs(n.X - ax), Math.Abs(n.Y - ay));
        int d2 = Math.Max(Math.Abs(n.X - (ax - 1)), Math.Abs(n.Y - ay));
        if (d1 <= 1 || d2 <= 1) return;
        Accion.OperarPuerta((short)map, n.OpenedDoorX, n.OpenedDoorY, abrir: false);
        n.OpenedDoorX = 0; n.OpenedDoorY = 0;
    }

    /// <summary>
    /// FindDirection (GameLogic.bas:979) 1:1 VB6. Devuelve heading hacia el target esquivando
    /// obstáculos; usa oldPos para no oscilar. 0 = ya al lado / mismo tile / rodeado.
    /// </summary>
    private static byte FindDirection(int map, NpcInstance n, int tx, int ty)
    {
        int x = n.X - tx;            // Sgn según VB6 (npc - target)
        int y = n.Y - ty;
        int sx = Math.Sign(x), sy = Math.Sign(y);
        int px = n.X, py = n.Y;

        if (sx == 0 && sy == 0) return 0;
        if (Math.Abs(n.X - tx) + Math.Abs(n.Y - ty) == 1) return 0; // al lado
        // Rodeado: ningún tile adyacente libre
        if (!PuedeNpc(map, px + 1, py) && !PuedeNpc(map, px - 1, py)
            && !PuedeNpc(map, px, py + 1) && !PuedeNpc(map, px, py - 1)) return 0;

        bool puedeX, puedeY;

        // SUR (target abajo): sx=0, sy=-1
        if (sx == 0 && sy == -1)
        {
            if (!PuedeNpc(map, px, py + 1))
                return _aiRng.Next(1, 11) > 5
                    ? (PuedeNpc(map, px - 1, py) ? H_O : H_E)
                    : (PuedeNpc(map, px + 1, py) ? H_E : H_O);
            return H_S;
        }
        // NORTE: sx=0, sy=1
        if (sx == 0 && sy == 1)
        {
            if (!PuedeNpc(map, px, py - 1))
                return _aiRng.Next(1, 11) > 5
                    ? (PuedeNpc(map, px - 1, py) ? H_O : H_E)
                    : (PuedeNpc(map, px + 1, py) ? H_E : H_O);
            return H_N;
        }
        // OESTE: sx=1, sy=0
        if (sx == 1 && sy == 0)
        {
            if (!PuedeNpc(map, px - 1, py))
                return _aiRng.Next(1, 11) > 5
                    ? (PuedeNpc(map, px, py - 1) ? H_N : H_S)
                    : (PuedeNpc(map, px, py + 1) ? H_S : H_N);
            return H_O;
        }
        // ESTE: sx=-1, sy=0
        if (sx == -1 && sy == 0)
        {
            if (!PuedeNpc(map, px + 1, py))
                return _aiRng.Next(1, 11) > 5
                    ? (PuedeNpc(map, px, py - 1) ? H_N : H_S)
                    : (PuedeNpc(map, px, py + 1) ? H_S : H_N);
            return H_E;
        }
        // NW: sx=1, sy=1 → preferir O o N
        if (sx == 1 && sy == 1)
        {
            puedeX = PuedeNpc(map, px - 1, py); puedeY = PuedeNpc(map, px, py - 1);
            if (puedeX && puedeY)
            {
                bool nbX = n.OldX != px - 1, nbY = n.OldY != py - 1;
                if (nbX && nbY) return _aiRng.Next(1, 21) < 10 ? H_O : H_N;
                if (nbX) return H_O; if (nbY) return H_N;
            }
            else if (puedeX) return H_O;
            else if (puedeY) return H_N;
            puedeY = PuedeNpc(map, px, py + 1);
            if (!puedeY || n.OldY == py + 1) return H_E;
            return H_S;
        }
        // NE: sx=-1, sy=1 → preferir E o N
        if (sx == -1 && sy == 1)
        {
            puedeX = PuedeNpc(map, px + 1, py); puedeY = PuedeNpc(map, px, py - 1);
            if (puedeX && puedeY)
            {
                bool nbX = n.OldX != px + 1, nbY = n.OldY != py - 1;
                if (nbX && nbY) return _aiRng.Next(1, 21) < 10 ? H_E : H_N;
                if (nbX) return H_E; if (nbY) return H_N;
            }
            else if (puedeX) return H_E;
            else if (puedeY) return H_N;
            puedeY = PuedeNpc(map, px, py + 1);
            if (!puedeY || n.OldY == py + 1) return H_O;
            return H_S;
        }
        // SW: sx=1, sy=-1 → preferir O o S
        if (sx == 1 && sy == -1)
        {
            puedeX = PuedeNpc(map, px - 1, py); puedeY = PuedeNpc(map, px, py + 1);
            if (puedeX && puedeY)
            {
                bool nbX = n.OldX != px - 1, nbY = n.OldY != py + 1;
                if (nbX && nbY) return _aiRng.Next(1, 21) < 10 ? H_O : H_S;
                if (nbX) return H_O; if (nbY) return H_S;
            }
            else if (puedeX) return H_O;
            else if (puedeY) return H_S;
            puedeY = PuedeNpc(map, px, py - 1);
            if (!puedeY || n.OldY == py - 1) return H_E;
            return H_N;
        }
        // SE: sx=-1, sy=-1 → preferir E o S
        if (sx == -1 && sy == -1)
        {
            puedeX = PuedeNpc(map, px + 1, py); puedeY = PuedeNpc(map, px, py + 1);
            if (puedeX && puedeY)
            {
                bool nbX = n.OldX != px + 1, nbY = n.OldY != py + 1;
                if (nbX && nbY) return _aiRng.Next(1, 21) < 10 ? H_E : H_S;
                if (nbX) return H_E; if (nbY) return H_S;
            }
            else if (puedeX) return H_E;
            else if (puedeY) return H_S;
            puedeY = PuedeNpc(map, px, py - 1);
            if (!puedeY || n.OldY == py - 1) return H_O;
            return H_N;
        }
        return 0;
    }

    /// <summary>
    /// MoveNPCChar (MODULO_NPCs.bas:749) 1:1 VB6. Mueve el NPC en la dirección dada si el
    /// destino es legal; guarda oldPos, actualiza heading y difunde CharacterMove al área.
    /// </summary>
    private static void MoveNpcChar(int map, NpcInstance n, byte heading)
    {
        if (n.ParalizadoHasta > Environment.TickCount64 / 1000.0) return;

        int nx = n.X, ny = n.Y;
        switch (heading)
        {
            case H_N: ny--; break;
            case H_E: nx++; break;
            case H_S: ny++; break;
            case H_O: nx--; break;
            default: return;
        }
        // Destino legal y libre (NPC). Usuario en el destino: el VB6 lo empuja; acá no movemos.
        if (!PuedeNpc(map, nx, ny, n.AguaValida, n.TierraInvalida)) { n.Heading = heading; return; }
        if (UserAtTile(map, nx, ny) > 0) { n.Heading = heading; return; }

        // Guardar posición anterior (para FindDirection) y mover.
        n.OldX = n.X; n.OldY = n.Y;
        n.X = (byte)nx; n.Y = (byte)ny; n.Heading = heading;

        // Visibilidad por área (AOI): CharacterMove a quienes lo ven; CharacterCreate/Remove a los
        // usuarios cuyo área el NPC entra/sale. Reemplaza el filtro por distancia anterior (que sólo
        // filtraba moves pero nunca creaba/removía → el NPC lejano quedaba congelado, no fantasma).
        AreaVisibility.OnNpcMoved(n);
    }

    /// <summary>userIndex del usuario en (x,y) del mapa, o 0.</summary>
    private static int UserAtTile(int map, int x, int y)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Pos.Map == map && u.Pos.X == x && u.Pos.Y == y) return i;
        }
        return 0;
    }

    /// <summary>
    /// Revisa todos los NPCs muertos con respawn vencido y los revive: restaura HP,
    /// nuevo CharIndex y CharacterCreate a los usuarios del mapa. Lo llama un timer.
    /// </summary>
    public static void TickRespawns()
    {
        double now = Environment.TickCount64 / 1000.0;
        foreach (var kv in _byMap)
        {
            foreach (var n in kv.Value)
            {
                if (!n.Dead || n.RespawnAt == 0 || now < n.RespawnAt) continue;
                n.Dead = false;
                n.RespawnAt = 0;
                n.MinHP = n.MaxHP;
                // Recargar el pool de exp (CalcularDarExp lo drena por golpe); sin esto el NPC
                // respawneado queda con ExpCount=0 y no vuelve a dar experiencia nunca.
                n.ExpCount = n.GiveEXP;
                n.X = n.SpawnX; n.Y = n.SpawnY;
                n.CharIndex = CharIndexPool.Next();
                // Restaurar estado original (un NPC provocado no debe revivir hostil) + limpiar aggro.
                n.Hostil = n.OldHostil; n.Movement = n.OldMovement;
                n.AttackedBy = ""; n.AttackedFirstBy = ""; n.TargetUser = 0;
                // Reiniciar el cooldown de IA: tras 20s muerto, NextAiAt quedó congelado en el
                // valor previo a morir; ponerlo a 0 garantiza que el NPC evalúe enemigos en el
                // próximo TickAI sin pasar por el re-sync (evita un salto de ~1 intervalo).
                n.NextAiAt = 0;
                AreaVisibility.OnNpcSpawn(n);   // mostrar sólo a los usuarios cuyo área lo cubre
            }
        }
    }

    // VB6 Declares.bas:253: tope de criaturas vivas por entrenador.
    public const int MAXMASCOTASENTRENADOR = 7;

    /// <summary>
    /// HandleTrain (Protocol.bas:4930) 1:1. El entrenador 'trainer' invoca la criatura
    /// petIndex (1..NroCriaturas) cerca suyo si no llegó al tope (MAXMASCOTASENTRENADOR).
    /// La criatura queda con MaestroNpc=trainer y NoRespawn (desaparece al morir).
    /// Devuelve true si la invocó; false si estaba al tope (el caller manda el LocaleMsg 593).
    /// </summary>
    public static bool Train(int map, NpcInstance trainer, byte petIndex)
    {
        if (trainer.Criaturas == null) return true; // sin criaturas: nada que invocar (no es error de tope)
        if (trainer.MascotasCount >= MAXMASCOTASENTRENADOR) return false; // tope alcanzado

        // petIndex válido (1..NroCriaturas). VB6: PetIndex > 0 And PetIndex < NroCriaturas + 1.
        if (petIndex < 1 || petIndex >= trainer.Criaturas.Length) return true;
        int npcIndex = trainer.Criaturas[petIndex];
        if (npcIndex <= 0) return true;

        // Buscar tile libre cerca del entrenador (ClosestLegalPos): primero su tile, luego espiral.
        if (!ClosestFreeTile(map, trainer.X, trainer.Y, out int sx, out int sy)) return true;

        var pet = SpawnAt(map, npcIndex, (byte)sx, (byte)sy);
        if (pet == null) return true;

        pet.MaestroNpc = trainer.CharIndex;
        pet.NoRespawn = true;
        trainer.MascotasCount++;

        // FX de invocación (SpawnNpc FX=True): sonido de warp + FXWARP sobre la criatura.
        const short SND_WARP = 3, FXWARP = 1;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && u.Conn != null && u.Pos.Map == map)
            {
                ServerPackets.PlayWave(u.Conn, SND_WARP, (byte)sx, (byte)sy);
                ServerPackets.CreateFX(u.Conn, pet.CharIndex, FXWARP, 0);
            }
        }
        return true;
    }

    /// <summary>
    /// WarpMascota (modHechizos.bas:687): acerca al amo la mascota MÁS LEJANA del usuario en el mapa,
    /// reubicándola en un tile libre adyacente y difundiendo el movimiento. true si warpeó alguna.
    /// </summary>
    public static bool WarpFarthestPet(int userIndex, int map, int ux, int uy)
    {
        if (!_byMap.TryGetValue(map, out var list)) return false;
        NpcInstance lejana = null; int maxDist = -1;
        foreach (var pet in list)
        {
            if (pet.Dead || pet.MaestroUser != userIndex) continue;
            int d = Math.Abs(pet.X - ux) + Math.Abs(pet.Y - uy);
            if (d > maxDist) { maxDist = d; lejana = pet; }
        }
        if (lejana == null) return false;
        if (!ClosestFreeTile(map, ux, uy, out int tx, out int ty)) return false;

        lejana.OldX = lejana.X; lejana.OldY = lejana.Y;
        lejana.X = (byte)tx; lejana.Y = (byte)ty;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CharacterMove(o.Conn, lejana.CharIndex, lejana.X, lejana.Y);
        }
        return true;
    }

    /// <summary>QuitarNPC: elimina un NPC del mapa (lo marca muerto sin respawn y avisa CharacterRemove).</summary>
    public static void RemoveNpc(NpcInstance npc)
    {
        if (npc == null) return;
        npc.Dead = true; npc.RespawnAt = 0; npc.NoRespawn = true;
        AreaVisibility.OnNpcRemoved(npc);
        CharIndexPool.Free(npc.CharIndex);
        npc.CharIndex = 0;
    }

    /// <summary>PerdioNpc (Modulo_UsUaRiOs.bas): los NPCs que perseguían a este usuario sueltan el
    /// target (al morir, deja de ser un objetivo válido). Inmediato, sin esperar al re-scan de la IA.</summary>
    public static void PerdioNpc(int userIndex)
    {
        foreach (var kv in _byMap)
            foreach (var n in kv.Value)
                if (n.TargetUser == userIndex) n.TargetUser = 0;
    }

    /// <summary>UsuarioAtacaNpc (Modulo_UsUaRiOs.bas:1583): el usuario provoca al NPC. Registra el
    /// AttackedBy y, si es el primer atacante, el AttackedFirstBy (dueño del loot/exp). Si el NPC era
    /// pasivo, lo vuelve hostil (guardando su estado original ya en OldHostil/OldMovement) y lo pone a
    /// perseguir. Marca user.NPCAtacado con el CharIndex del NPC.</summary>
    public static void ProvocarNpc(NpcInstance npc, User atacante)
    {
        if (npc == null || npc.Dead) return;
        if (string.IsNullOrEmpty(npc.AttackedFirstBy) || npc.AttackedFirstBy == atacante.Name)
            npc.AttackedBy = atacante.Name;
        if (string.IsNullOrEmpty(npc.AttackedFirstBy))
            npc.AttackedFirstBy = atacante.Name;
        if (!npc.Hostil) { npc.Hostil = true; npc.Movement = 0; } // pasivo → hostil y persigue
        npc.TargetUser = atacante.id;
        atacante.flags.NPCAtacado = npc.CharIndex;
    }

    /// <summary>UserDie (Modulo_UsUaRiOs.bas:1798) 1:1: reset de aggro al morir. Restaura el NPC que lo
    /// atacaba (Movement/Hostil originales + limpia AttackedBy + suelta target) y libera el loot del NPC
    /// que el usuario atacaba si era suyo (AttackedFirstBy). Luego PerdioNpc.</summary>
    public static void ResetAggroAlMorir(User u)
    {
        if (u.flags.AtacadoPorNpc > 0)
        {
            var n = NpcByCharIndex(u.Pos.Map, (short)u.flags.AtacadoPorNpc);
            if (n != null) { n.Movement = n.OldMovement; n.Hostil = n.OldHostil; n.AttackedBy = ""; n.TargetUser = 0; }
        }
        if (u.flags.NPCAtacado > 0)
        {
            var n = NpcByCharIndex(u.Pos.Map, (short)u.flags.NPCAtacado);
            if (n != null && n.AttackedFirstBy == u.Name) n.AttackedFirstBy = "";
        }
        u.flags.AtacadoPorNpc = 0;
        u.flags.NPCAtacado = 0;
        PerdioNpc(u.id);
    }

    /// <summary>UserDie (Modulo_UsUaRiOs.bas:1972): al morir el amo, mueren todas sus mascotas
    /// (MuereNpc) en todos los mapas. Devuelve la cantidad liberada.</summary>
    public static int LiberarMascotasDe(int userIndex)
    {
        int n = 0;
        foreach (var kv in _byMap)
            foreach (var pet in kv.Value)
                if (!pet.Dead && pet.MaestroUser == userIndex) { RemoveNpc(pet); n++; }
        return n;
    }

    /// <summary>Una criatura de entrenador murió: descuenta el contador de su maestro (QuitarMascotaNpc).</summary>
    public static void QuitarMascotaNpc(int map, int maestroCharIndex)
    {
        var maestro = NpcByCharIndex(map, maestroCharIndex);
        if (maestro != null && maestro.MascotasCount > 0) maestro.MascotasCount--;
    }

    /// <summary>Tile libre más cercano a (x,y) (incluye el propio); espiral radio 1..3. false si ninguno.</summary>
    private static bool ClosestFreeTile(int map, int x, int y, out int fx, out int fy)
    {
        for (int r = 0; r <= 3; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (PuedeNpc(map, nx, ny) && UserAtTile(map, nx, ny) == 0) { fx = nx; fy = ny; return true; }
                }
        fx = 0; fy = 0; return false;
    }

    /// <summary>Crea un NPC en (map,x,y) en runtime y lo muestra a los usuarios del mapa (debug/eventos).</summary>
    public static NpcInstance SpawnAt(int map, int npcIndex, byte x, byte y)
    {
        var info = NpcData.Get(npcIndex);
        if (string.IsNullOrEmpty(info.Name)) return null;

        var n = new NpcInstance
        {
            CharIndex = CharIndexPool.Next(),
            Body = info.Body, Head = info.Head,
            WeaponAnim = info.WeaponAnim, ShieldAnim = info.ShieldAnim, CascoAnim = info.CascoAnim,
            AguaValida = info.AguaValida,
            TierraInvalida = info.TierraInvalida,
            Heading = info.Heading == 0 ? (byte)3 : info.Heading,
            X = x, Y = y, SpawnX = x, SpawnY = y,
            NpcIndex = npcIndex, Map = map, Name = info.Name,
            MaxHP = info.MaxHP, MinHP = info.MaxHP,
            GiveEXP = info.GiveEXP, GiveGLD = info.GiveGLD, ExpCount = info.GiveEXP,
            Hostil = info.Hostil, Attackable = info.Attackable, MinHIT = info.MinHIT, MaxHIT = info.MaxHIT,
            PoderAtaque = info.PoderAtaque, PoderEvasion = info.PoderEvasion,
            Movement = info.Movement, Spells = info.Spells, Domable = info.Domable,
            Comercia = info.Comercia, Inventario = info.Inventario,
            NpcType = info.NpcType, Status = info.Status, Ciudad = info.Ciudad,
            OrigHeading = info.Heading == 0 ? (byte)3 : info.Heading,
            Drops = info.Drops, Criaturas = info.Criaturas,
            OldHostil = info.Hostil, OldMovement = info.Movement, // estado original (MODULO_NPCs.bas:1179)
            Snd1 = info.Snd1, Snd2 = info.Snd2, Snd3 = info.Snd3,
        };

        if (!_byMap.TryGetValue(map, out var list)) { list = new List<NpcInstance>(); _byMap[map] = list; }
        list.Add(n);

        AreaVisibility.OnNpcSpawn(n);   // crear sólo para los usuarios cuyo área lo cubre
        return n;
    }

    /// <summary>Manda el CharacterCreate de un NPC a una conexión (usado por AreaVisibility al entrar al área).</summary>
    public static void SendNpcCreate(Connection conn, NpcInstance n) => SendOne(conn, n);

    private static void SendOne(Connection conn, NpcInstance n)
    {
        // VB6 MakeNPCChar (MODULO_NPCs.bas:657) manda el NÚMERO del NPC como "nombre". El cliente
        // detecta el nombre numérico (is_valid_int) → is_npc=true → lo anima con velocidad de NPC
        // y resuelve el nombre real vía locale_npc. Si mandáramos el nombre real, el cliente lo
        // tomaría por JUGADOR y lo movería con velocidad de jugador (caminata rápida y trabada).
        ServerPackets.CharacterCreate(conn,
            charIndex: n.CharIndex, body: n.Body, head: n.Head, heading: n.Heading,
            x: n.X, y: n.Y, weapon: n.WeaponAnim, shield: n.ShieldAnim, helmet: n.CascoAnim, fx: 0, fxLoops: 0,
            // VB6 MakeNPCChar (MODULO_NPCs.bas:657) manda flags.Status como color de nick del NPC.
            name: n.NpcIndex.ToString(), privileges: (byte)n.Status, donador: 0, particulaFx: 0,
            armaAura: 0, bodyAura: 0, escudoAura: 0, headAura: 0, otraAura: 0, anilloAura: 0,
            isTopGold: false, weaponObjIndex: 0);

        // Estado visual no incluido en CharacterCreate: si el NPC sigue paralizado, reenviar la barra
        // de progreso al observador (sino al volver al mapa / entrar al área el NPC se recrea sin barra).
        double restante = n.ParalizadoHasta - Environment.TickCount64 / 1000.0;
        if (restante > 0)
            ServerPackets.NpcParalysisProgress(conn, n.CharIndex, (byte)Math.Min(255, (int)Math.Ceiling(restante)));
    }

    // NPCs vivos por mapa. Se crean la primera vez que se pide el mapa.
    private static readonly Dictionary<int, List<NpcInstance>> _byMap = new();

    /// <summary>Devuelve (instanciando si hace falta) los NPCs vivos de un mapa.</summary>
    public static List<NpcInstance> GetMapNpcs(int mapNumber)
    {
        if (_byMap.TryGetValue(mapNumber, out var list)) return list;

        list = new List<NpcInstance>();
        var map = MapLoader.Get(mapNumber);
        if (map != null)
        {
            foreach (var mn in map.Npcs)
            {
                var info = NpcData.Get(mn.NpcIndex);
                if (string.IsNullOrEmpty(info.Name)) continue; // npc sin datos: omitir
                list.Add(new NpcInstance
                {
                    CharIndex = CharIndexPool.Next(),
                    Body = info.Body,
                    Head = info.Head,
                    WeaponAnim = info.WeaponAnim, ShieldAnim = info.ShieldAnim, CascoAnim = info.CascoAnim,
                    AguaValida = info.AguaValida,
            TierraInvalida = info.TierraInvalida,
                    Heading = info.Heading == 0 ? (byte)3 : info.Heading,
                    X = (byte)mn.X,
                    Y = (byte)mn.Y,
                    SpawnX = (byte)mn.X,
                    SpawnY = (byte)mn.Y,
                    NpcIndex = mn.NpcIndex,
                    Map = mapNumber,
                    Name = info.Name,
                    MaxHP = info.MaxHP,
                    MinHP = info.MaxHP,
                    GiveEXP = info.GiveEXP,
                    GiveGLD = info.GiveGLD,
                    ExpCount = info.GiveEXP,
                    Hostil = info.Hostil,
                    Attackable = info.Attackable,
                    Movement = info.Movement,
                    Spells = info.Spells,
                    Domable = info.Domable,
                    MinHIT = info.MinHIT,
                    MaxHIT = info.MaxHIT,
                    PoderAtaque = info.PoderAtaque,
                    PoderEvasion = info.PoderEvasion,
                    Comercia = info.Comercia,
                    Inventario = info.Inventario,
                    NpcType = info.NpcType,
                    Status = info.Status,
                    Ciudad = info.Ciudad,
                    OrigHeading = info.Heading == 0 ? (byte)3 : info.Heading,
                    Drops = info.Drops,
                    Criaturas = info.Criaturas,
                    OldHostil = info.Hostil, OldMovement = info.Movement, // estado original
                    Snd1 = info.Snd1, Snd2 = info.Snd2, Snd3 = info.Snd3,
                });
            }
        }
        _byMap[mapNumber] = list;
        return list;
    }

    /// <summary>Envía a una conexión todos los NPCs del mapa (CharacterCreate por cada uno).</summary>
    public static void SendMapNpcs(Connection conn, int mapNumber)
    {
        foreach (var n in GetMapNpcs(mapNumber))
        {
            if (n.Dead) continue;
            ServerPackets.CharacterCreate(conn,
                charIndex: n.CharIndex,
                body: n.Body,
                head: n.Head,
                heading: n.Heading,
                x: n.X, y: n.Y,
                weapon: n.WeaponAnim, shield: n.ShieldAnim, helmet: n.CascoAnim, fx: 0, fxLoops: 0,
                name: n.NpcIndex.ToString(), // número → el cliente lo reconoce como NPC (ver SendOne)
                privileges: (byte)n.Status, donador: 0, particulaFx: 0, // flags.Status = color de nick (VB6)
                armaAura: 0, bodyAura: 0, escudoAura: 0, headAura: 0, otraAura: 0, anilloAura: 0,
                isTopGold: false, weaponObjIndex: 0);
        }
    }
}

/// <summary>
/// Asignador global de CharIndex compartido por PJs y NPCs (el cliente los trata igual).
/// RECICLA los índices liberados (Free): el cliente tiene char_list de tamaño fijo (10001) y
/// descarta cualquier CharIndex &gt; 10000, así que un contador siempre-creciente (los respawns
/// de NPC pedían Next() sin liberar) terminaba pasándose de 10000 → NPCs invisibles (y como es
/// short, eventualmente negativo). Ahora se reusa el primer slot libre, acotado a MAX_CHARS.
/// </summary>
public static class CharIndexPool
{
    private const int MAX_CHARS = 10000;            // coincide con el char_list.resize(10001) del cliente
    private static readonly bool[] _used = new bool[MAX_CHARS + 1];
    private static int _cursor = 1;                 // rota para no escanear siempre desde 1
    private static readonly object _lock = new();
    private static int _liveCount;                  // índices actualmente en uso

    public static short Next()
    {
        lock (_lock)
        {
            for (int i = 0; i < MAX_CHARS; i++)
            {
                int idx = _cursor + i;
                if (idx > MAX_CHARS) idx -= MAX_CHARS;
                if (!_used[idx])
                {
                    _used[idx] = true;
                    _cursor = idx + 1; if (_cursor > MAX_CHARS) _cursor = 1;
                    _liveCount++;
                    return (short)idx;
                }
            }
            // POOL LLENO: el NPC/PJ recibiría CharIndex=0 (invisible en el cliente). Aviso de bug real.
            Console.WriteLine($"[CharIndexPool] ¡POOL LLENO! vivos={_liveCount}. Se devolvió CharIndex=0 → carácter INVISIBLE en el cliente.");
            return 0; // pool lleno (no debería ocurrir con <10000 chars vivos)
        }
    }

    /// <summary>Devuelve un CharIndex al pool para que pueda reutilizarse (NPC muerto/quitado, PJ deslogueado).</summary>
    public static void Free(short idx)
    {
        if (idx <= 0 || idx > MAX_CHARS) return;
        lock (_lock) { if (_used[idx]) { _used[idx] = false; _liveCount--; } }
    }
}
