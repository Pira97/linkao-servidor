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
        // Equipo REAL (ObjIndex, no el anim) de los bots: mitiga daño en combate NPC-vs-NPC
        // (NpcAtacaNpc/NpcImpactaNpc), igual que Npcdano ya hace con el equipo de un jugador.
        // 0 = sin esa pieza. Lo setea Bots.Spawn desde el BotClase de la clase invocada.
        public int EquipArmorObj, EquipShieldObj, EquipCascoObj;
        // ---- Progresión de nivel en vivo (bots "progresivos", NUEVO) ----
        // El bot arranca en BotNivelActual con equipo REAL de ese nivel (Bots.MejorEquipoParaNivel,
        // no el set "sacro" fijo) y sube matando NPCs (Bots.DarExpABot/SubirNivelBot), recalculando
        // stats Y re-equipándose solo. BotClaseId/BotRaza quedan guardados porque hacen falta para
        // recalcular todo de nuevo cada vez que sube.
        public bool BotLeveling;
        public byte BotNivelActual;
        public int BotExp;
        public byte BotClaseId, BotRaza;
        // Auras por slot (bots): salen del campo Aura de cada ítem sacro equipado. 0 = sin aura.
        public short Aura;        // aura del cuerpo/armadura (bodyAura)
        public short AuraArma, AuraEscudo, AuraCasco;
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
        public int MinMana, MaxMana; // maná actual / máximo (bots casters: castear consume, potean azul)
        public int GiveEXP, GiveGLD;
        public bool Dead;
        public double RespawnAt;    // segundos (Environment.TickCount/1000) en que revive; 0 = no programado
        public bool Hostil;
        public bool Attackable;     // Attackable=1 del .dat; 0 = intocable (mercaderes, sacerdotes, banqueros)
        public byte Movement;       // TipoAI: 0=persigue, 1=estático
        public short[] Spells;      // hechizos que lanza (null = no lanza)
        public int Domable;         // puntos requeridos para domar (0 = no domable)
        // Nombre del GM que lo invocó a mano (/acc, /racc, /accpos). null/"" = NPC nativo del
        // mapa (.csm) o de un sistema propio (bots, eventos): "matar mis NPCs" solo toca los
        // que tienen este campo seteado al nombre del GM que pide la limpieza.
        public string SpawnedBy;
        public int MaestroUser;     // userIndex del dueño si es mascota (0 = salvaje)
        public int MaestroNpc;      // CharIndex del entrenador dueño si es criatura de entrenamiento (0 = no)
        public bool NoRespawn;      // criaturas de entrenador: al morir desaparecen, no reviven
        // ---- Mascota compañera persistente (Mago/Nigromante: elementales+Ely; Cazador: lobo/oso), NUEVO ----
        // Distinta de las mascotas de Entrenador (MaestroNpc) y de la invocación descartable de hechizo:
        // esta sube de nivel propio con el amo (PetLeveling.cs) y persiste entre reconexiones (User.PetTipo/
        // PetNivel/PetExp en CharSaver/CharLoader). MaestroUser ya la engancha a TickMascota/CheckPets gratis.
        public bool PetOfPlayer;
        public byte PetNivel;       // 1..50
        public int PetExp;
        public byte PetTipo;        // PetLeveling.PetTipo
        public string PetNombre;    // nombre elegido por el dueño ("" = usa el nombre de la especie)
        public int PetHPUltimoAvisado = -1; // último MinHP mandado al dueño (panel de mascota); -1 = nunca
        public int MascotasCount;   // (entrenador) cantidad de criaturas vivas que invocó
        public int[] Criaturas;     // (entrenador) npcindices de criaturas invocables
        public int MascotaTargetNpc;// NPC que la mascota está atacando (0 = ninguno)
        public int MascotaTargetUsuario; // userIndex del jugador que la mascota está atacando en
                                          // defensa del amo (PvP, ver CheckPetsVsUsuario). 0 = ninguno
        public int MinHIT, MaxHIT;
        public int PoderAtaque, PoderEvasion; // impacto/evasión (NPCs.dat)
        public int ExpCount;        // pool de exp restante (CalcularDarExp); init = GiveEXP al spawnear
        public double NextAiAt;     // próximo tick de IA permitido (cooldown de movimiento/ataque)
        public double ParalizadoHasta; // segundos hasta los que está paralizado (0 = libre)
        // ---- Viaje al hogar (mascota compañera con mochila) ----
        // La mascota NO se teletransporta: camina hasta salir de escena y recién ahí deja la
        // mochila en la bóveda y desaparece. Mientras viaja no pelea ni sigue al amo.
        public bool YendoAlHogar;
        public int HogarX, HogarY;      // hacia dónde camina (tile de este mapa)
        public double HogarDeadline;    // corte de seguridad: si se traba, igual llega (segundos)
        public byte ParalisisTipo = 1; // efecto visual: 1 = parálisis (petrificado), 2 = inmovilización (telaraña)
        public double InmovilizadoHasta; // segundos hasta los que está inmovilizado (no se mueve pero SÍ pega); 0 = libre
        public double DormidoHasta;    // segundos hasta los que está dormido por instrumento musical (0 = despierto)
        // ---- Buffs/debuffs temporales de hechizo de bot (SubeFuerza/SubeAgilidad/Ceguera/Estupidez) ----
        // Lazy-read, mismo patrón que ParalizadoHasta: MinHIT/MaxHIT/PoderAtaque/PoderEvasion NUNCA se
        // mutan; el valor "efectivo" se calcula al leer (NpcManager.HitEfectivo/PoderAtaqueEfectivo/
        // PoderEvasionEfectivo) comparando estos timestamps contra Environment.TickCount64/1000.0. Así
        // un bot que muere a mitad de un buff no deja ningún campo de combate sucio para su respawn.
        public double BuffFuerzaHasta;   public int BuffFuerzaDelta;    // % sobre MinHIT/MaxHIT/PoderAtaque (+ = sube, - = baja)
        public double BuffAgilidadHasta; public int BuffAgilidadDelta; // % sobre PoderEvasion (+ = sube, - = baja)
        public double CegueraHasta;      // mientras dura: penaliza PoderAtaque (NpcManager.BOT_CEGUERA_PCT)
        public double EstupidezHasta;    // mientras dura: penaliza PoderEvasion (NpcManager.BOT_ESTUPIDEZ_PCT)
        public bool AfectaParalisis;   // NPCs.dat "Inmunidad": inmune a parálisis y al dormir de instrumentos
        public long EstadoParalisisTick; // TickCount en que se aplicó la parálisis/inmovilización (los bots reaccionan y se la sacan)
        // [[FIX3]] Antes había un único TimerAtaque compartido por golpe físico Y hechizo: un NPC
        // que casteaba consumía el mismo cooldown que su golpe (y viceversa), así que no podía
        // pegar y luego castear en ventanas separadas — era "uno u otro cada 3000ms". Ahora cada
        // acción tiene su propio timer independiente (mismos valores de cooldown que antes, ver
        // Intervals.PuedeAtacarNpc/AttackIntervalFor): un NPC puede golpear y, si su cooldown de
        // hechizo ya venía más avanzado, castear sin esperar el cooldown completo de nuevo.
        // Los bots ya usaban TimerAtaque (golpe) y TimerLanzarSpell (hechizo) por separado —
        // TimerAtaque se renombra 1:1 a TimerAtaqueFisico y su comportamiento para bots no cambia.
        public long TimerAtaqueFisico;   // TickCount del último golpe físico (melee)
        public long TimerAtaqueHechizo;  // TickCount del último hechizo lanzado (NPCs no-bot; los bots siguen usando TimerLanzarSpell)
        public long TimerLanzarSpell; // TickCount del último hechizo (intervalo de magia propio del bot, separado del golpe)
        public long TimerPocion;    // TickCount de la última poción bebida (autopot del bot, intervalo real GolpeUsar=300ms)
        // Aggro/loot (flags del NPC en VB6): estado original para restaurar al perder al atacante,
        // y nombres del atacante actual / primero (dueño del loot/exp).
        public bool OldHostil;      // Hostil original (al spawnear)
        public byte OldMovement;    // Movement original (al spawnear)
        public short Snd1, Snd2, Snd3; // sonidos (NPCs.dat): atacar / ser golpeado / morir
        public string AttackedBy = "";       // usuario que lo está atacando
        public string AttackedFirstBy = "";  // primer atacante (dueño del loot/exp)
        public bool Comercia;
        public bool NoCompra;   // 1 = solo vende, no compra al usuario
        public byte NpcType, Status;
        public byte Ciudad;         // CIUDAD_* del guardia (1=Imp,2=Rep,3=Caos,5=Rinkel)
        public byte OrigHeading;    // heading original (para restaurar al volver al spawn)
        public (short objIndex, int amount)[] Inventario;
        public short Moneda;      // 0 = comercia en oro; >0 = ObjIndex de la divisa propia (NpcData.Moneda)
        public int[] Precios;     // precios en la moneda propia, alineados por slot con Inventario
        public (short objIndex, int amount, double prob)[] Drops;

        // ---- Estado de patrulla de guardias (GuardiasAI, AI_NPC.bas) ----
        public (int x, int y)[] PatrolWP = new (int, int)[5]; // 1..4 waypoints (0 sin usar)
        public byte PatrolWPCount;       // cantidad de waypoints generados
        public byte PatrolWPCurrent;     // waypoint destino actual (1..count)
        public byte PatrolRoundsCompleted; // rondas completas (cada 3 regenera ruta)
        public byte PatrolStuckTicks;    // ticks atascado (cede paso / regenera)
        public int PatrolPrevX = -1, PatrolPrevY = -1;   // pos 2 ticks atrás (detección de oscilación A↔B)
        public byte PatrolOscTicks;      // veces seguidas que volvió a la casilla de 2 ticks atrás
        public int TargetUser;           // userIndex perseguido (0 = ninguno)
        // ---- Bots de prueba (custom) ----
        public int OwnerUserIndex;       // dueño/invocador del bot (no lo ataca, lo sigue). 0 = ninguno
        public int KillStreak;           // racha de usuarios matados seguidos (sonido FIRST_BLOOD/DOUBLE/TRIPLE/SPREE)
        public bool BotAtacar;           // true = el bot ataca a todos menos al dueño (incluso GMs)
        public bool BotSpar;             // true = bot de sparring PvP: ataca AL dueño (acercarse/golpear/inmovilizar) y se remueve si lo paralizan
        public bool BotSparSoloMelee;    // true = el bot SÓLO pega cuerpo a cuerpo (no lanza hechizos de daño a distancia, "no pegar desde cualquier lugar")
        public byte BotFaccion;          // facción del bot: 0=ninguna, 1=Armada, 2=Milicia, 3=Caos (autónomo si >0)
        public short WanderX, WanderY;   // destino de deambulado (faction bots buscando enemigos)
        public byte WanderTicks;         // ticks restantes hacia el destino de deambulado
        public short BotHealSpell;       // hechizo de cura del bot (clérigos curan aliados); 0 = no cura
        public short BotAtaqueParticula; // partícula al golpear (cazador = flecha explosiva 173); 0 = ninguna
        public int FormSlot = -1;        // posición en la fila (-1 = sin formación, clump junto al dueño)
        public int FormTotal;            // total de bots en la fila (para centrar)
        public bool EnBarca;             // true = el bot está en barca (siguiendo al dueño por agua)
        public short LandBody, LandWeapon, LandShield, LandCasco; // apariencia en tierra (para restaurar al bajar)
        // ---- Guerra mundial de facciones (GuerraFacciones.cs) ----
        public bool BotGuerra;           // true = bot de campaña: recorre el mundo buscando a la facción enemiga
        public bool BotDungeon;          // true = guardián permanente de un dungeon (DungeonBots.cs): NUNCA marcha (ViajarGuerra), patrulla local si no hay rival/presa
        public int GuerraDestMap;        // mapa objetivo (ciudad enemiga); 0 = sin objetivo asignado
        public byte GuerraDestX, GuerraDestY;  // tile objetivo dentro de ese mapa
        public byte GuerraStuck;         // pasos seguidos sin lograr moverse (desatasco)
        public double UltimoCombateAt;   // segundos del último golpe/hechizo dado o recibido (cámara automática)
        public byte GuerraMontura;       // 0 = a pie, 1 = montado (body de montura), 2 = con alas (ShieldAnim 88)
        public double GuerraLlegadaAt;   // segundos en que llegó al objetivo (se queda un rato y elige otro)
        public bool IsBot => NpcIndex >= Bots.BOT_INDEX_BASE;
        // ---- IA inteligente (custom) ----
        public int LastSeenX, LastSeenY;     // última posición conocida del enemigo (investigar)
        public byte InvestigateTicks;        // ticks restantes yendo a la última posición vista
        public short GreetTimer;             // ticks hasta el próximo giro (mudo) para mirar a un ciudadano cercano

        // ---- Bot inteligente (prototipo Utility AI, NUEVO) ----
        // Distinto de todos los demás modos: en vez de un if/else fijo, TickBotSmart puntúa un
        // puñado de acciones (atacar/castear/perseguir/retirarse/...) cada tick y ejecuta la de
        // mayor puntaje. La puntuación decide QUÉ quiere hacer el bot; el golpe/hechizo en sí
        // sigue pasando por Combat.NpcAtacaUsuario/NpcLanzaSpell (mismos intervalos reales que
        // cualquier otro bot/NPC) y la poción sigue siendo BotAutoPot sin tocar (mismo intervalo
        // Intervals.GolpeUsar=300ms que un jugador real manteniendo mantenido el autopot). Sólo
        // UN bot (el prototipo) tiene BotSmart=true; todos los demás modos de TickBot siguen
        // exactamente igual.
        public bool BotSmart;
        // Personalidad (0-100, cosmética): SOLO pesa en el puntaje de utilidad, nunca toca un
        // intervalo/cooldown real. Dos bots con la misma clase pero distinta personalidad deciden
        // distinto sin pelear "más rápido" ni "más fuerte" que un jugador legítimo.
        public byte PersAgresividad = 60;  // preferencia por atacar cuerpo a cuerpo
        public byte PersCautela = 40;      // qué tan pronto se retira con poca vida
        public byte PersPersecucion = 50;  // cuánto persigue antes de resignarse
        public byte PersHechizo = 50;      // preferencia por castear en vez de golpear
        public byte PersAyuda = 50;        // cuánto prioriza ir a ayudar a un aliado herido
        public int SmartChaseTicks;        // ticks seguidos persiguiendo al mismo objetivo sin alcanzarlo
        public byte SmartLastAction;       // acción vigente (NpcManager.SmartAction) — persistente entre decisiones, ver TickBotSmart
        public double SmartDecisionNextAt; // segundos: próxima vez que se re-puntúan las 8 acciones (más lento que el movimiento)
        // ---- Flanqueo táctico (NUEVO): de qué lado ataca/castea, para no pegarse siempre al mismo ----
        public byte LastCombatSide;        // NpcManager.CombatSide del último golpe/hechizo conectado (0 = ninguno todavía)
        public double LastCombatSideAt;    // segundos en que se fijó ese lado (memoria temporal, ver SIDE_MEMORY_SECONDS)
        public short FlancoX, FlancoY;     // destino de reposicionamiento elegido por MejorFlanco (0,0 = ninguno pendiente)
        // ---- Puertas (custom): el guardia abre puertas cerradas sin llave para cruzar y las cierra al alejarse ----
        public byte OpenedDoorX, OpenedDoorY; // tile ancla de la puerta que este guardia abrió (0 = ninguna)

        // ---- Cache de camino BFS por NPC (perf, FIX2) ----
        // SeekPathHeading recalculaba un BFS completo (con dos matrices 101x101 nuevas) en CADA
        // llamada, aunque el destino no se hubiera movido desde el tick anterior. Ahora se guarda el
        // camino ya calculado y se consume paso a paso; solo se recalcula si el destino se movió más
        // de 1 tile, pasó demasiado tiempo, el mapa cambió, o el próximo paso cacheado dejó de ser
        // válido (colisión/tile ocupado). Ver SeekPathHeading.
        public (byte x, byte y)[] PathCache;        // pasos del camino cacheado, en orden hacia el destino
        public int PathCacheCount;                  // cantidad de pasos válidos en PathCache
        public int PathCacheIdx;                    // próximo paso a consumir (0..PathCacheCount)
        public byte PathCacheDestX, PathCacheDestY; // destino para el que se calculó el camino cacheado
        public int PathCacheMap;                    // mapa para el que se calculó (invalida si cambia)
        public long PathCacheAtMs;                  // Environment.TickCount64 en que se calculó (expira tras N ticks)
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

    /// <summary>
    /// Segundos que tarda en revivir ESTE NPC. Antes era una constante de 20s para todos, así que
    /// una hormiga y el Rey Dragón reaparecían al mismo ritmo y se podía farmear un jefe parado
    /// en su tile. Ahora el tiempo escala con la exp que da el NPC (su dificultad):
    ///
    ///     segundos = Segundos * (expBase / ExpReferencia) ^ Escala     [clamp a SegundosMax]
    ///
    /// con los defaults de [RESPAWN] (45s / ref 300 / escala 0.35 / techo 600s):
    ///   Hormiga (6 exp) 45s · bicho medio (3.200) ~103s · fuerte (32.000) ~231s · jefes 10 min.
    /// Se le aplica un jitter de ±Jitter% para que un grupo de NPCs muertos junto no reaparezca
    /// todo sincronizado. Un NPC puede pisar el cálculo con "RespawnSegundos=N" en NPCs.dat.
    ///
    /// Usa GiveEXPBase (valor crudo del .dat): GiveEXP ya viene multiplicado por Server.ini [INIT]
    /// Exp (=200 acá), y con ese valor TODO el mundo se iría al techo de 10 minutos.
    /// </summary>
    public static double RespawnSecondsFor(NpcInstance n) => RespawnSecondsFor(n.NpcIndex);

    /// <summary>Igual que el anterior pero por npcIndex (lo usa --respawntest, que no tiene instancias).</summary>
    public static double RespawnSecondsFor(int npcIndex)
    {
        var cfg = BalanceData.Respawn;
        double seg;

        int over = NpcData.Get(npcIndex).RespawnSegundos;
        if (over > 0)
        {
            seg = over;   // override explícito del .dat: se respeta tal cual (sin techo)
        }
        else
        {
            // Los bots sintéticos (NpcData.Register) no tienen GiveEXPBase; caen al piso, que es
            // inofensivo porque además se crean con NoRespawn.
            int exp = NpcData.Get(npcIndex).GiveEXPBase;
            seg = cfg.Segundos;
            if (exp > cfg.ExpReferencia && cfg.Escala > 0)
                seg *= Math.Pow((double)exp / cfg.ExpReferencia, cfg.Escala);
            if (seg > cfg.SegundosMax) seg = cfg.SegundosMax;
        }

        if (cfg.Jitter > 0)
            seg *= 1.0 + (Random.Shared.NextDouble() * 2 - 1) * (cfg.Jitter / 100.0);
        return seg < 1 ? 1 : seg;
    }

    /// <summary>
    /// Herramienta de dev (`dotnet run -- --respawntest`): imprime la tabla de tiempos de respawn
    /// que sale de [RESPAWN] de Balance.dat, ordenada por tiempo. Sirve para tunear los números sin
    /// levantar el servidor ni matar bichos a mano. No arranca nada; sólo lee los .dat.
    /// </summary>
    public static void RespawnSelfTest()
    {
        var cfg = BalanceData.Respawn;
        Console.WriteLine($"[RESPAWN] Segundos={cfg.Segundos} SegundosMax={cfg.SegundosMax} " +
                          $"ExpReferencia={cfg.ExpReferencia} Escala={cfg.Escala:0.00} Jitter={cfg.Jitter}%");
        Console.WriteLine("(el jitter no se muestra: la tabla es el tiempo BASE de cada NPC)\n");

        // Tiempo base sin jitter = el cálculo con Jitter temporalmente ignorado; se recalcula acá
        // para que la tabla sea reproducible corrida a corrida.
        var filas = new List<(double seg, int idx, string name, int exp, bool over)>();
        foreach (var (idx, name, _, _, _, _) in NpcData.All())
        {
            var info = NpcData.Get(idx);
            double seg;
            bool over = info.RespawnSegundos > 0;
            if (over) seg = info.RespawnSegundos;
            else
            {
                seg = cfg.Segundos;
                if (info.GiveEXPBase > cfg.ExpReferencia && cfg.Escala > 0)
                    seg *= Math.Pow((double)info.GiveEXPBase / cfg.ExpReferencia, cfg.Escala);
                if (seg > cfg.SegundosMax) seg = cfg.SegundosMax;
            }
            filas.Add((seg, idx, name, info.GiveEXPBase, over));
        }
        filas.Sort((a, b) => a.seg.CompareTo(b.seg));

        foreach (var f in filas)
        {
            string t = f.seg >= 60 ? $"{f.seg / 60:0.0} min" : $"{f.seg:0} s";
            Console.WriteLine($"{t,10}  NPC{f.idx,-5} exp={f.exp,-9} {f.name}{(f.over ? "   [RespawnSegundos del .dat]" : "")}");
        }
        Console.WriteLine($"\n{filas.Count} NPCs. Antes TODOS respawneaban a los 20 s fijos.");
    }

    // ======================================================================================
    // [[FIXES 1-4 self test]] Verificación manual de los 4 fixes de perf/IA (auditoría de
    // GameServer.FlushLoopAsync/TickAI). No hay infraestructura de tests (xUnit) en el repo;
    // se optó por seguir el mismo patrón que RespawnSelfTest/RegionLoader.SelfTest ya usan acá
    // (Program.cs --fixtest) porque montar xUnit contra este código exigiría mockear
    // MapLoader/UserListManager/Network.Connection de punta a punta, mucho más costoso que
    // ejercitar directo los métodos privados desde ADENTRO de la clase (mismo assembly/clase,
    // sin reflection). Cada TEST imprime PASS/FAIL y el runner devuelve != 0 si algo falló.
    // Uso: dotnet run -- --fixtest
    // ======================================================================================
    public static int FixesSelfTest()
    {
        int fallos = 0;
        void Assert(bool cond, string desc)
        {
            Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + desc);
            if (!cond) fallos++;
        }

        const int TEST_MAP = 30001; // mapa ficticio (cabe en short/WorldPos.Map): nunca cargado por MapLoader → PuedeNpc trata
                                      // todo tile 1..100 como caminable salvo ocupación por user/npc.

        // ---- Helpers para crear usuarios de prueba sin red/DB real ----
        int nextUserSlot = 1;
        int NuevoUsuario(int x, int y, byte faccion, byte facStatus = 0)
        {
            int idx = nextUserSlot++;
            var u = UserListManager.UserList[idx];
            u.id = idx;
            u.Name = $"TestUser{idx}";
            u.flags.UserLogged = true;
            u.flags.Muerto = 0;
            u.flags.Oculto = 0;
            u.flags.Invisible = 0;
            u.Pos.Map = (short)TEST_MAP; u.Pos.X = (short)x; u.Pos.Y = (short)y;
            u.Faccion.Status = faccion;
            u.FaccionStatus = facStatus;
            u.Conn = null; // sin conexión real: cualquier código que dependa de Conn!=null se salteará (documentado abajo)
            UserListManager.LastUser = Math.Max(UserListManager.LastUser, idx);
            UsersByMapIndex.Add(TEST_MAP, idx);
            return idx;
        }

        Console.WriteLine("=== FIX1: guardias, selección de enemigo vía UsersByMapIndex ===");
        {
            // Guardia Imperial (Ciudad=1): enemigo = Caos(4)/Milicia(6)/Renegado(1)/Republicano(3).
            var guardia = new NpcInstance
            {
                CharIndex = CharIndexPool.Next(), Name = "TestGuardia", Map = TEST_MAP,
                X = 50, Y = 50, SpawnX = 50, SpawnY = 50, Heading = 3,
                NpcType = NPCTYPE_GUARDIASCITY, Ciudad = CIUDAD_IMPERIAL, Movement = 1, // estático: no patrulla
                Hostil = false, Attackable = true, MaxHP = 100, MinHP = 100,
            };
            if (!_byMap.TryGetValue(TEST_MAP, out var list)) { list = new List<NpcInstance>(); _byMap[TEST_MAP] = list; }
            list.Add(guardia);

            int lejosCaos = NuevoUsuario(55, 55, FAC_CAOS);      // dist=10, enemigo
            int cercaRep  = NuevoUsuario(52, 50, FAC_REPUBLICANO); // dist=2, enemigo, más cerca → debe ganar
            int cercaCiud = NuevoUsuario(51, 50, FAC_CIUDADANO);   // dist=1... pero NO enemigo del Imperio (aliado) → ignorado

            GuardiasAI(TEST_MAP, guardia);
            Assert(guardia.TargetUser == cercaRep, $"elige al enemigo MÁS CERCANO (esperado={cercaRep} obtuvo={guardia.TargetUser})");

            // Empate de distancia: dos enemigos a la MISMA distancia → debe ganar el de MENOR índice
            // (mismo criterio que el for ascendente 1..LastUser que reemplazó UsersByMapIndex).
            guardia.TargetUser = 0; guardia.InvestigateTicks = 0;
            var guardia2 = new NpcInstance
            {
                CharIndex = CharIndexPool.Next(), Name = "TestGuardia2", Map = TEST_MAP,
                X = 10, Y = 10, SpawnX = 10, SpawnY = 10, Heading = 3,
                NpcType = NPCTYPE_GUARDIASCITY, Ciudad = CIUDAD_IMPERIAL, Movement = 1,
                Hostil = false, Attackable = true, MaxHP = 100, MinHP = 100,
            };
            list.Add(guardia2);
            int empateA = NuevoUsuario(15, 10, FAC_CAOS); // dist=5, índice MENOR (registrado primero)
            int empateB = NuevoUsuario(10, 15, FAC_CAOS); // dist=5, índice MAYOR
            GuardiasAI(TEST_MAP, guardia2);
            Assert(guardia2.TargetUser == empateA, $"empate de distancia: gana el índice menor (esperado={empateA} obtuvo={guardia2.TargetUser})");
        }

        Console.WriteLine("=== FIX2: cache de pathfinding BFS ===");
        {
            var n = new NpcInstance { CharIndex = CharIndexPool.Next(), Name = "TestPather", Map = TEST_MAP, X = 20, Y = 20, Heading = 3 };
            if (!_byMap.TryGetValue(TEST_MAP, out var list)) { list = new List<NpcInstance>(); _byMap[TEST_MAP] = list; }
            list.Add(n);

            byte h1 = SeekPathHeading(TEST_MAP, n, 30, 20, 30); // 10 tiles al Este, terreno abierto (mapa ficticio)
            Assert(h1 == H_E, $"primer paso hacia el Este (obtuvo heading={h1})");
            Assert(n.PathCache != null && n.PathCacheCount > 0, "cachea el camino completo tras el primer cálculo");
            long primerCalculoMs = n.PathCacheAtMs;
            int primerCount = n.PathCacheCount;

            // Simular el paso (igual que MoveNpcChar haría) y pedir el siguiente heading al MISMO
            // destino: debe consumir el PRÓXIMO paso cacheado (PathCacheIdx avanza) SIN recalcular
            // (PathCacheAtMs no cambia porque no volvió a entrar al bloque de recálculo del BFS).
            n.X = 21;
            byte h2 = SeekPathHeading(TEST_MAP, n, 30, 20, 30);
            Assert(h2 == H_E, $"segundo paso también al Este, vía cache (obtuvo heading={h2})");
            Assert(n.PathCacheAtMs == primerCalculoMs, "NO recalculó el BFS (misma marca de tiempo de cache)");
            Assert(n.PathCacheIdx == 2, $"el índice de cache avanzó a 2 (obtuvo {n.PathCacheIdx})");

            // Destino que se movió MÁS de 1 tile → debe invalidar y recalcular (nueva marca de tiempo,
            // o al menos un heading correcto hacia el NUEVO destino).
            n.X = 22;
            byte h3 = SeekPathHeading(TEST_MAP, n, 30, 15, 30); // ty saltó de 20 a 15 (5 tiles)
            bool haciaNuevoDestino = h3 == H_E || h3 == H_N; // según el BFS puede priorizar cualquiera de los dos ejes
            Assert(haciaNuevoDestino, $"destino movido >1 tile invalida cache y recalcula (heading={h3})");
            Assert(n.PathCacheDestX == 30 && n.PathCacheDestY == 15, "el cache quedó apuntando al NUEVO destino");

            // Expiración por edad: forzar que la cache "tenga" más de PATH_CACHE_MAX_AGE_MS y pedir
            // el mismo destino de nuevo → debe recalcular igual (se nota porque PathCacheAtMs cambia).
            long viejaMarca = n.PathCacheAtMs;
            n.PathCacheAtMs -= (PATH_CACHE_MAX_AGE_MS + 500);
            byte h4 = SeekPathHeading(TEST_MAP, n, 30, 15, 30);
            Assert(n.PathCacheAtMs != viejaMarca - (PATH_CACHE_MAX_AGE_MS + 500) || n.PathCacheAtMs > viejaMarca - PATH_CACHE_MAX_AGE_MS,
                "cache expirada por edad se recalcula (nueva marca de tiempo)");
            Assert(h4 != 0, "sigue devolviendo un heading válido tras expirar y recalcular");

            // Paso cacheado bloqueado por otro NPC en el medio → invalida ese paso puntual y recalcula
            // (no debe romper ni devolver un heading hacia un tile ocupado).
            var obstaculo = new NpcInstance { CharIndex = CharIndexPool.Next(), Name = "Obstaculo", Map = TEST_MAP, X = 23, Y = 15, Heading = 3 };
            list.Add(obstaculo);
            n.X = 22; n.Y = 15;
            byte h5 = SeekPathHeading(TEST_MAP, n, 30, 15, 30);
            Assert(h5 != H_E || !(n.X + (h5 == H_E ? 1 : 0) == 23 && n.Y == 15), "no devuelve un paso hacia un tile ahora ocupado por otro NPC");
        }

        Console.WriteLine("=== FIX3: timers de golpe físico y hechizo independientes ===");
        {
            var n = new NpcInstance { CharIndex = CharIndexPool.Next(), Name = "TestCaster", Map = TEST_MAP, X = 40, Y = 40, Heading = 3 };
            // Intervals usa un Stopwatch arrancado al cargar la clase (tiempo relativo al proceso, NO
            // epoch): dejar el timer en 0 significaría "atacó en el instante en que arrancó el
            // proceso", que sigue DENTRO del cooldown si el test corre en los primeros segundos de
            // vida del proceso. Un valor bien negativo simula "hace mucho" sin ambigüedad.
            const long INTERVALO = 3000;
            n.TimerAtaqueFisico = -1_000_000; n.TimerAtaqueHechizo = -1_000_000;

            bool golpe1 = Intervals.PuedeAtacarNpc(ref n.TimerAtaqueFisico, INTERVALO);
            Assert(golpe1, "primer golpe físico permitido (timer hace mucho)");
            // Antes del fix esto habría fallado: golpe y hechizo compartían el mismo campo, así que
            // consumir el golpe dejaba al hechizo en cooldown también.
            bool hechizo1 = Intervals.PuedeAtacarNpc(ref n.TimerAtaqueHechizo, INTERVALO);
            Assert(hechizo1, "hechizo permitido INMEDIATAMENTE después del golpe (timers independientes)");
            bool golpe2 = Intervals.PuedeAtacarNpc(ref n.TimerAtaqueFisico, INTERVALO);
            Assert(!golpe2, "un segundo golpe inmediato SIGUE bloqueado por su propio cooldown (no cambió el valor)");
            bool hechizo2 = Intervals.PuedeAtacarNpc(ref n.TimerAtaqueHechizo, INTERVALO);
            Assert(!hechizo2, "un segundo hechizo inmediato SIGUE bloqueado por su propio cooldown (no cambió el valor)");
        }

        Console.WriteLine("=== FIX4: reacción inmediata a un atacante nuevo ===");
        {
            var n = new NpcInstance
            {
                CharIndex = CharIndexPool.Next(), Name = "TestHostil", Map = TEST_MAP, X = 60, Y = 60, Heading = 3,
                Hostil = true, Attackable = true, MaxHP = 100, MinHP = 100,
            };
            if (!_byMap.TryGetValue(TEST_MAP, out var list)) { list = new List<NpcInstance>(); _byMap[TEST_MAP] = list; }
            list.Add(n);

            int userA = NuevoUsuario(60, 61, FAC_CAOS); // ya trabado con este (adyacente, Sur)
            int userB = NuevoUsuario(61, 60, FAC_CAOS); // atacante NUEVO, adyacente (Este)
            n.TargetUser = userA;
            n.Heading = 9; // sentinela: valor que FaceTarget nunca produce (headings válidos son 1-4)

            int prevTarget = n.TargetUser;
            var uB = UserListManager.UserList[userB];
            ReaccionInmediataANuevoAtacante(n, uB, prevTarget);
            Assert(n.Heading != 9, "gira a encarar al atacante nuevo de inmediato (no espera el próximo TickAI)");
            Assert(n.Heading == H_E, $"encara exactamente hacia userB, que está al Este (heading={n.Heading})");

            // Caso "mismo atacante de siempre": no debería hacer nada especial (early-return).
            n.Heading = 9;
            ReaccionInmediataANuevoAtacante(n, UserListManager.UserList[userA], userA /* prevTarget == atacante */);
            Assert(n.Heading == 9, "si el atacante YA era el target, no reacciona de más (no toca Heading)");

            // Caso "sin target previo" (primera provocación, la maneja ProvocarNpc normal): tampoco reacciona acá.
            n.Heading = 9;
            ReaccionInmediataANuevoAtacante(n, uB, 0 /* prevTarget */);
            Assert(n.Heading == 9, "sin target previo (prevTarget=0) no reacciona (lo cubre ProvocarNpc, no este fix)");

            // Mascotas y bots: EXCLUIDOS a propósito de este fix (tienen su propia IA).
            var mascota = new NpcInstance { CharIndex = CharIndexPool.Next(), Map = TEST_MAP, X = 60, Y = 61, Heading = 9, MaestroUser = userA, Hostil = true };
            ReaccionInmediataANuevoAtacante(mascota, uB, userA);
            Assert(mascota.Heading == 9, "una mascota (MaestroUser>0) queda excluida de este fix");
        }

        Console.WriteLine($"\n=== {(fallos == 0 ? "TODOS los tests pasaron" : $"{fallos} test(s) FALLARON")} ===");
        return fallos;
    }

    // ======================================================================================
    // [[FIXES 1-2 benchmark]] Números reales (Stopwatch), no inventados. No se pudo comparar
    // contra el binario ANTERIOR de forma limpia (el repo tiene, además de este cambio, un
    // montón de trabajo del usuario sin commitear en otros archivos — hacer git stash para
    // levantar un binario "antes" habría arriesgado ese trabajo, así que se descartó). En
    // cambio, cada benchmark corre el algoritmo VIEJO y el NUEVO lado a lado, EN EL MISMO
    // proceso y sobre los MISMOS datos sintéticos: el viejo es una reimplementación textual
    // del for 1..LastUser / recálculo-BFS-siempre que había antes del fix (se puede diffear
    // a ojo contra el código real de antes). Uso: dotnet run -- --benchtest
    // ======================================================================================
    public static void FixesBenchmark()
    {
        const int BENCH_MAP = 30002;

        Console.WriteLine("=== BENCH FIX1: selección de enemigo del guardia — for 1..LastUser (viejo) vs UsersByMapIndex (nuevo) ===");
        {
            // Escenario: 500 usuarios conectados en TOTAL al server, repartidos en 50 mapas (10 por
            // mapa) — el guardia sólo debería mirar los 10 de SU mapa. Es el caso que motiva el fix:
            // un server con varios cientos de jugadores online pero pocos por mapa/ciudad.
            const int TOTAL_USERS = 500, USERS_PER_MAP = 10;
            for (int i = 1; i <= TOTAL_USERS; i++)
            {
                var u = UserListManager.UserList[i];
                u.id = i; u.flags.UserLogged = true; u.flags.Muerto = 0; u.flags.Oculto = 0; u.flags.Invisible = 0;
                int map = BENCH_MAP + (i / USERS_PER_MAP);
                u.Pos.Map = (short)map; u.Pos.X = (short)(10 + i % USERS_PER_MAP); u.Pos.Y = 10;
                u.Faccion.Status = FAC_CAOS; // todos enemigos de un guardia Imperial
                UserListManager.LastUser = i;
                UsersByMapIndex.Add(map, i);
            }
            var guardia = new NpcInstance { CharIndex = CharIndexPool.Next(), Map = BENCH_MAP, X = 5, Y = 10, Ciudad = CIUDAD_IMPERIAL };

            const int REPS = 20000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int sinkOld = 0;
            for (int r = 0; r < REPS; r++)
            {
                int mejor = 0, mejorDist = int.MaxValue;
                for (int i = 1; i <= UserListManager.LastUser; i++) // == código de ANTES del fix
                {
                    var u = UserListManager.UserList[i];
                    if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != BENCH_MAP) continue;
                    if (!NpcVeUsuario(guardia, u)) continue;
                    if (EsGmIntocable(u)) continue;
                    if (Math.Abs(u.Pos.X - guardia.X) > RANGO_VISION_X || Math.Abs(u.Pos.Y - guardia.Y) > RANGO_VISION_Y) continue;
                    if (!EsEnemigoUsuario(guardia.Ciudad, u)) continue;
                    int d = Math.Abs(u.Pos.X - guardia.X) + Math.Abs(u.Pos.Y - guardia.Y);
                    if (d < mejorDist) { mejorDist = d; mejor = i; }
                }
                sinkOld += mejor;
            }
            sw.Stop();
            long msOld = sw.ElapsedMilliseconds;

            sw.Restart();
            int sinkNew = 0;
            for (int r = 0; r < REPS; r++)
            {
                int mejor = 0, mejorDist = int.MaxValue;
                foreach (int i in UsersByMapIndex.Get(BENCH_MAP)) // == código de DESPUÉS del fix
                {
                    var u = UserListManager.UserList[i];
                    if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != BENCH_MAP) continue;
                    if (!NpcVeUsuario(guardia, u)) continue;
                    if (EsGmIntocable(u)) continue;
                    if (Math.Abs(u.Pos.X - guardia.X) > RANGO_VISION_X || Math.Abs(u.Pos.Y - guardia.Y) > RANGO_VISION_Y) continue;
                    if (!EsEnemigoUsuario(guardia.Ciudad, u)) continue;
                    int d = Math.Abs(u.Pos.X - guardia.X) + Math.Abs(u.Pos.Y - guardia.Y);
                    if (d < mejorDist || (d == mejorDist && i < mejor)) { mejorDist = d; mejor = i; }
                }
                sinkNew += mejor;
            }
            sw.Stop();
            long msNew = sw.ElapsedMilliseconds;

            Console.WriteLine($"  {TOTAL_USERS} usuarios online / {USERS_PER_MAP} en el mapa del guardia, {REPS} repeticiones:");
            Console.WriteLine($"    ANTES (for 1..LastUser):     {msOld} ms  ({(double)msOld / REPS * 1000:0.00} µs/tick)  [sink={sinkOld}]");
            Console.WriteLine($"    DESPUÉS (UsersByMapIndex):   {msNew} ms  ({(double)msNew / REPS * 1000:0.00} µs/tick)  [sink={sinkNew}]");
            Console.WriteLine($"    Speedup: {(msNew > 0 ? (double)msOld / msNew : double.PositiveInfinity):0.0}x");

            // Limpieza: sacar los usuarios sintéticos del índice para no ensuciar otro benchmark.
            for (int i = 1; i <= TOTAL_USERS; i++)
                UsersByMapIndex.Remove(BENCH_MAP + (i / USERS_PER_MAP), i);
            UserListManager.LastUser = 0;
        }

        Console.WriteLine("\n=== BENCH FIX2: pathfinding — BFS SIEMPRE (viejo) vs cache de camino (nuevo) ===");
        {
            // Escenario: un NPC recorre un camino de 20 tiles repitiendo el viaje muchas veces,
            // sobre el mapa REAL 1 (no uno sintético): un mapNumber sin .csm hace que MapLoader.Get
            // reintente Load() (con su propio Console.WriteLine) en CADA llamada porque no cachea el
            // resultado null — miles de llamadas por BFS lo vuelven un benchmark de I/O de disco, no
            // de la lógica del fix. El mapa 1 se carga UNA vez y queda cacheado, igual que en el
            // server real. Es el caso ideal para la cache (destino quieto, típico de "volver al
            // spawn"/"investigar última posición vista"); con un objetivo que se mueve tile a tile
            // la ganancia es menor, ver informe.
            const int VIAJES = 200;
            const int BENCH_PATH_MAP = 1;
            const int ORIGEN_X = 10, ORIGEN_Y = 10, DEST_X = 30, DEST_Y = 10;

            if (!_byMap.TryGetValue(BENCH_PATH_MAP, out var list)) { list = new List<NpcInstance>(); _byMap[BENCH_PATH_MAP] = list; }

            // SIN cache: se fuerza a recalcular el BFS completo en CADA paso (comportamiento de antes).
            var nOld = new NpcInstance { CharIndex = CharIndexPool.Next(), Map = BENCH_PATH_MAP, X = ORIGEN_X, Y = ORIGEN_Y };
            list.Add(nOld);
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            int pasosOld = 0;
            for (int v = 0; v < VIAJES; v++)
            {
                nOld.X = ORIGEN_X; nOld.Y = ORIGEN_Y;
                int guard = 0;
                while ((nOld.X != DEST_X || nOld.Y != DEST_Y) && guard++ < 200)
                {
                    nOld.PathCache = null; nOld.PathCacheCount = 0; nOld.PathCacheIdx = 0; // invalida antes de cada paso
                    byte h = SeekPathHeading(BENCH_PATH_MAP, nOld, DEST_X, DEST_Y, 90);
                    if (h == 0) break;
                    switch (h) { case H_N: nOld.Y--; break; case H_E: nOld.X++; break; case H_S: nOld.Y++; break; case H_O: nOld.X--; break; }
                    pasosOld++;
                }
            }
            sw2.Stop();
            long msOldPath = sw2.ElapsedMilliseconds;
            list.Remove(nOld);

            // CON cache: comportamiento real de después del fix (destino quieto → 1 solo BFS por viaje).
            var nNew = new NpcInstance { CharIndex = CharIndexPool.Next(), Map = BENCH_PATH_MAP, X = ORIGEN_X, Y = ORIGEN_Y };
            list.Add(nNew);
            sw2.Restart();
            int pasosNew = 0;
            for (int v = 0; v < VIAJES; v++)
            {
                nNew.X = ORIGEN_X; nNew.Y = ORIGEN_Y;
                int guard = 0;
                while ((nNew.X != DEST_X || nNew.Y != DEST_Y) && guard++ < 200)
                {
                    byte h = SeekPathHeading(BENCH_PATH_MAP, nNew, DEST_X, DEST_Y, 90);
                    if (h == 0) break;
                    switch (h) { case H_N: nNew.Y--; break; case H_E: nNew.X++; break; case H_S: nNew.Y++; break; case H_O: nNew.X--; break; }
                    pasosNew++;
                }
            }
            sw2.Stop();
            long msNewPath = sw2.ElapsedMilliseconds;
            list.Remove(nNew);

            if (pasosOld == 0 || pasosNew == 0)
            {
                Console.WriteLine($"  [aviso] el mapa {BENCH_PATH_MAP} no tiene camino libre de ({ORIGEN_X},{ORIGEN_Y}) a ({DEST_X},{DEST_Y})" +
                    $" (pasosOld={pasosOld} pasosNew={pasosNew}); benchmark no concluyente con estas coordenadas.");
            }
            else
            {
                Console.WriteLine($"  {VIAJES} viajes, mapa {BENCH_PATH_MAP} real ({pasosOld} pasos viejo / {pasosNew} pasos nuevo):");
                Console.WriteLine($"    ANTES (BFS en cada paso):    {msOldPath} ms  ({(double)msOldPath / pasosOld * 1000:0.00} µs/paso)");
                Console.WriteLine($"    DESPUÉS (cache de camino):   {msNewPath} ms  ({(double)msNewPath / pasosNew * 1000:0.00} µs/paso)");
                Console.WriteLine($"    Speedup: {(msNewPath > 0 ? (double)msOldPath / msNewPath : double.PositiveInfinity):0.0}x " +
                    "(caso ideal: destino quieto. Con blanco moviéndose cada tick la cache se invalida más seguido y la ganancia baja.)");
            }
        }
    }

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
    // Los bots usan la MISMA cadencia que un NPC normal (380ms/tile). Antes era 0.26s y el body del
    // cliente (que anima a 376ms/tile) no llegaba: el server mandaba moves más rápido de lo animable,
    // el cliente los ENCOLABA y el body quedaba atrás de la posición lógica → el golpe parecía conectar
    // "de lejos". A 380ms el move se aplica inmediato (camino fluido) y la posición no se adelanta al body.
    private const double BotAiIntervalSeconds = 0.38;

    // Ritmo de paso de un bot MONTADO o CON ALAS. El cliente anima a cada personaje según su
    // propio body/escudo (speedForChar en game.html: 165 px/s montado, 230 volando, sobre tiles de
    // 32 px), así que si el server siguiera mandando un paso cada 380ms el muñeco llegaría al tile
    // y se quedaría esperando: se ve caminando a los saltos. 32000/velocidad da el paso exacto.
    private const double BotIntervalMontado = 32.0 / 165.0;   // ~194ms
    private const double BotIntervalVolando = 32.0 / 230.0;   // ~139ms

    /// <summary>Cada cuánto le toca moverse a este bot, según vaya a pie, montado o volando.</summary>
    private static double IntervaloBot(NpcInstance n)
    {
        // BotSmart: único caso además de guerra/montura que NO usa el ritmo estándar de 380ms —
        // el cliente lo anima como jugador (velocidadDePersonaje/isBotSmart), así que necesita
        // moves más seguido para no quedar esperando inactivo entre paso y paso (ver BotIntervalSmart).
        // Antes que el chequeo de barca: aunque esté en barca, sigue siendo el MISMO personaje que
        // el cliente ya anima a velocidad de jugador (una barca no es un body especial acá).
        if (n.BotSmart) return BotIntervalSmart;
        // En barca el cuerpo es la barca: el cliente la anima a velocidad normal, así que se vuelve
        // al ritmo de a pie mientras navega (si no, la barca se adelanta a su propia animación).
        if (!n.BotGuerra || n.EnBarca) return BotAiIntervalSeconds;
        return n.GuerraMontura switch
        {
            2 => BotIntervalVolando,
            1 => n.Body == 888 ? BotIntervalVolando : BotIntervalMontado,   // 888 = montura voladora
            _ => BotAiIntervalSeconds,
        };
    }

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
                double aiInterval = n.IsBot ? IntervaloBot(n) : AiIntervalSeconds;
                n.NextAiAt += aiInterval;
                if (n.NextAiAt < now - aiInterval) n.NextAiAt = now + aiInterval;

                // Bots: se SACAN solos la parálisis/inmovilización (como poteando "remover parálisis"),
                // tras una pequeña reacción. Mientras reaccionan siguen trabados (se saltea el tick).
                if (n.IsBot && BotCleanseParalisis(n, now)) continue;

                // VB6: NPC paralizado no se mueve ni ataca.
                if (n.ParalizadoHasta > now) continue;
                if (n.ParalizadoHasta != 0 && n.ParalizadoHasta <= now) n.ParalizadoHasta = 0;

                // VB6: NPC dormido por instrumento musical tampoco hace IA (AI_NPC.bas:1289).
                // Al expirar el efecto despierta y se limpia el zZz (General.bas:1206 → CreateFX 0).
                if (n.DormidoHasta > now) continue;
                if (n.DormidoHasta != 0 && n.DormidoHasta <= now) DespertarNpc(n);

                // Guardias de ciudad (NpcType=2): IA propia (patrulla/diálogo/frases/ataque por facción).
                if (n.NpcType == NPCTYPE_GUARDIASCITY) { GuardiasAI(map, n); continue; }

                // Sacerdotes (NpcType=1, "Revividor" del .dat): cura/resucita usuarios aliados cercanos.
                if (n.NpcType == NPCTYPE_SACERDOTE) { SacerdoteAI(map, n); continue; }

                // Mascotas (MaestroUser>0): SeguirAmo + atacar NPCs hostiles cercanos.
                if (n.MaestroUser > 0) { TickMascota(map, n); continue; }

                if (!n.Hostil) continue;

                // Bots de prueba: IA propia (siguen al dueño, atacan a todos los demás si BotAtacar).
                // Protegido: un error en la IA de un bot NO debe tirar todo el servidor.
                if (n.IsBot) { try { TickBot(map, n); } catch (Exception ex) { Console.WriteLine($"[Bot AI] ERROR: {ex}"); } continue; }

                // ¿Mascota adyacente? → pegarle a ELLA en vez de ignorarla (la mascota "tanquea":
                // si está pegándole al NPC cuerpo a cuerpo, el NPC se defiende de ella primero,
                // no solo persigue al dueño). Antes que el chequeo de usuario adyacente.
                var petAdyacente = AdjacentPet(n, map, n.X, n.Y, out byte headingToPet);
                if (petAdyacente != null)
                {
                    FaceTarget(map, n, petAdyacente.X, petAdyacente.Y);
                    n.Heading = headingToPet;
                    if (n.Spells != null && n.Spells.Length > 0 && _aiRng.Next(2) == 0)
                        Combat.NpcLanzaSpellANpc(n, petAdyacente);
                    else
                        NpcAtacaNpc(map, n, petAdyacente);
                    continue;
                }

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

        // Aplicar warps de bots encolados (no se puede mutar _byMap durante el foreach de arriba).
        try { ApplyPendingBotWarps(); } catch (Exception ex) { Console.WriteLine($"[Bot warp] ERROR: {ex}"); }

        // Estado de los eventos de batalla de facciones (parley → carga).
        try { TickBattles(); } catch (Exception ex) { Console.WriteLine($"[Bot battle] ERROR: {ex}"); }

        // Guerra mundial de facciones: repone las bajas de cada ejército.
        try { GuerraFacciones.Tick(); } catch (Exception ex) { Console.WriteLine($"[Guerra] ERROR: {ex}"); }

        // Guardianes de dungeon: repone las bajas de cada dungeon poblado.
        try { DungeonBots.Tick(); } catch (Exception ex) { Console.WriteLine($"[DungeonBots] ERROR: {ex}"); }

        // Pantalla de quien esté ESPECTANDO un bot desde el panel (se arma a mano: un NPC no
        // tiene stream que espejar). Va al final, con las posiciones del tick ya finales.
        try { Espia.RefrescarObservadores(); } catch (Exception ex) { Console.WriteLine($"[Espia bot] ERROR: {ex}"); }
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

    /// <summary>Paraliza un NPC por 'segundos' (lo usa la magia). VB6: NPC no se mueve ni ataca.
    /// tipo solo cambia el efecto visual del cliente: 1 = petrificado, 2 = telaraña verde
    /// (hechizos con Inmoviliza); el comportamiento del NPC es el mismo (1:1 con VB6).</summary>
    public static void ParalizarNpc(NpcInstance npc, double segundos, byte tipo = 1)
    {
        npc.ParalizadoHasta = Environment.TickCount64 / 1000.0 + segundos;
        npc.ParalisisTipo = tipo;
        npc.EstadoParalisisTick = Environment.TickCount64;   // marca cuándo empezó (para la reacción del bot al desparalizarse)
        // Difunde la barra de progreso de parálisis a todos los del mapa (se dibuja bajo el NPC).
        byte segs = (byte)Math.Min(255, (int)Math.Ceiling(segundos));
        DifundirParalisisNpc(npc, segs, tipo);
    }

    /// <summary>Duerme un NPC por 'segundos' (instrumento musical, InvUsuario.bas:2033). VB6: no se
    /// mueve, no ataca, no lanza hechizos; despierta al recibir daño o al expirar el tiempo.</summary>
    public static void DormirNpc(NpcInstance npc, double segundos)
    {
        npc.DormidoHasta = Environment.TickCount64 / 1000.0 + segundos;
    }

    /// <summary>Despierta un NPC dormido (daño recibido o fin del efecto). Ya no se difunde
    /// CreateFX(0): el zZz del VB6 (FX 64) se removió a pedido, no hay nada visual que limpiar.</summary>
    public static void DespertarNpc(NpcInstance npc)
    {
        npc.DormidoHasta = 0;
    }

    /// <summary>Difunde la barra de parálisis del NPC a los usuarios del mapa (segs=0 la oculta).
    /// tipo: 1 = parálisis (petrificado), 2 = inmovilización (telaraña verde).</summary>
    public static void DifundirParalisisNpc(NpcInstance npc, byte segs, byte tipo = 1)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == npc.Map)
                ServerPackets.NpcParalysisProgress(o.Conn, npc.CharIndex, segs, tipo);
        }
    }

    // Correa: la mascota no persigue/castea sobre nada que esté a más de esta distancia DEL AMO
    // (no del propio bicho) — sin esto podía irse detrás de un enemigo que huye y terminar del
    // otro lado del mapa, sin volver nunca. Bastante más grande que RANGO_VISION para no sentirse
    // corta en combates normales, donde el amo se mueve mientras pelea.
    private const int LEASH_DIST_AMO = 14;

    /// <summary>
    /// IA de mascota (SeguirAmo + SeguirAgresor): prioriza defender al amo (CheckPets) y, si no
    /// tiene un objetivo asignado, se suma al NPC que el amo está peleando ANTES que buscar uno
    /// propio (así ataca junto al amo en vez de dispersarse). Sin enemigos, sigue al amo
    /// manteniendo distancia ≤3. Todo objetivo (asignado o libre) se abandona si se aleja
    /// demasiado del amo (LEASH_DIST_AMO), para no perderse persiguiendo algo que huye.
    /// </summary>
    private static void TickMascota(int map, NpcInstance pet)
    {
        // Volviendo al hogar: tiene prioridad sobre TODO (no pelea, no sigue al amo, no le
        // importa la correa). Se resuelve acá y se corta el tick.
        if (pet.YendoAlHogar) { PasoHaciaElHogar(map, pet); return; }

        var amo = UserListManager.UserList[pet.MaestroUser];
        if (amo == null || !amo.flags.UserLogged || amo.Pos.Map != map)
        { pet.MascotaTargetNpc = 0; pet.MascotaTargetUsuario = 0; return; } // amo lejos/desconectado: se queda quieta

        // El amo entró a zona segura (caminando a un tile ZONASEGURA, teleport dentro del mismo
        // mapa, o cualquier otra vía que no pase por MoverMascotaConDueño): la mascota no lo sigue
        // adentro, se desinvoca sin perder progreso. Único chequeo centralizado del caminar, así
        // no hay que hookear cada camino de movimiento.
        if (pet.PetOfPlayer && Combat.ChequearMascotaZonaSegura(amo)) return;

        // Panel de mascota del cliente: avisa al dueño si el HP cambió desde el último tick
        // (golpe recibido/curado en combate). No hace falta hookear cada función de daño.
        if (pet.PetOfPlayer && pet.MinHP != pet.PetHPUltimoAvisado)
        { Combat.EnviarPetInfo(amo); pet.PetHPUltimoAvisado = pet.MinHP; }

        bool DentroDeCorrea(int x, int y) => Math.Abs(x - amo.Pos.X) + Math.Abs(y - amo.Pos.Y) <= LEASH_DIST_AMO;

        // 0) Target asignado por CheckPets/órdenes (defiende al amo): prioridad sobre la búsqueda libre.
        // Si está atascada (sin camino posible) NO se queda congelada mirándolo: abandona el
        // objetivo y sigue de largo a la búsqueda libre EN EL MISMO TICK (bug reportado: "si no le
        // doy paso la mascota no busca el objetivo y no lo ataca").
        if (pet.MascotaTargetNpc > 0)
        {
            var objetivo = NpcByCharIndex(map, pet.MascotaTargetNpc);
            if (objetivo != null && !objetivo.Dead && DentroDeCorrea(objetivo.X, objetivo.Y))
            {
                int d0 = Math.Abs(objetivo.X - pet.X) + Math.Abs(objetivo.Y - pet.Y);
                if (AtacarObjetivoMascota(map, pet, objetivo, d0)) return;
            }
            pet.MascotaTargetNpc = 0; // objetivo muerto/desaparecido/fuera de correa/atascada: vuelve a la IA libre
        }

        // 0b) Target USUARIO asignado por CheckPetsVsUsuario (un jugador atacó al amo, PvP):
        // misma prioridad que defender de un NPC.
        if (pet.MascotaTargetUsuario > 0)
        {
            var objetivoU = UserListManager.UserList[pet.MascotaTargetUsuario];
            if (objetivoU != null && objetivoU.flags.UserLogged && objetivoU.flags.Muerto == 0
                && objetivoU.Pos.Map == map && DentroDeCorrea(objetivoU.Pos.X, objetivoU.Pos.Y))
            {
                int d0 = Math.Abs(objetivoU.Pos.X - pet.X) + Math.Abs(objetivoU.Pos.Y - pet.Y);
                if (AtacarUsuarioMascota(map, pet, objetivoU, d0)) return;
            }
            pet.MascotaTargetUsuario = 0; // se desconectó/murió/se fue/fuera de correa/atascada: vuelve a la IA libre
        }

        // 1) Sin objetivo asignado: sumarse al NPC que el AMO está peleando ahora mismo (si está
        // a la vista y sigue vivo/hostil) ANTES que buscar uno propio — se siente más "en equipo"
        // que cada uno peleando con lo que le queda más cerca.
        NpcInstance enemigo = null; int mejorDist = int.MaxValue;
        if (amo.flags.NPCAtacado > 0)
        {
            var objetivoAmo = NpcByCharIndex(map, amo.flags.NPCAtacado);
            if (objetivoAmo != null && !objetivoAmo.Dead && objetivoAmo.Hostil
                && Math.Abs(objetivoAmo.X - pet.X) <= RANGO_VISION_X && Math.Abs(objetivoAmo.Y - pet.Y) <= RANGO_VISION_Y)
            { enemigo = objetivoAmo; mejorDist = Math.Abs(objetivoAmo.X - pet.X) + Math.Abs(objetivoAmo.Y - pet.Y); }
        }

        // 2) Si el amo no está peleando nada (o no se ve desde acá): buscar el NPC hostil más
        // cercano a la MASCOTA, en rango de visión. A igual distancia, prefiere el más HERIDO
        // (rematarlo primero) en vez de elegir cualquiera al azar — se ve más "con criterio".
        if (enemigo == null)
        {
            foreach (var o in _byMap[map])
            {
                if (o.Dead || o == pet || o.MaestroUser > 0 || !o.Hostil) continue;
                if (Math.Abs(o.X - pet.X) > RANGO_VISION_X || Math.Abs(o.Y - pet.Y) > RANGO_VISION_Y) continue;
                int d = Math.Abs(o.X - pet.X) + Math.Abs(o.Y - pet.Y);
                bool mejor = d < mejorDist || (d == mejorDist && enemigo != null && o.MinHP < enemigo.MinHP);
                if (mejor) { mejorDist = d; enemigo = o; }
            }
        }

        if (enemigo != null && DentroDeCorrea(enemigo.X, enemigo.Y))
        {
            // Se COMPROMETE con este enemigo (igual que un objetivo asignado por CheckPets): los
            // próximos ticks van derecho al paso 0 y terminan la pelea, en vez de re-evaluar "cuál
            // está más cerca" en CADA tick — sin esto, con dos hostiles a la misma distancia la
            // mascota podía quedar oscilando entre uno y otro sin rematar a ninguno (se ve "tonta").
            pet.MascotaTargetNpc = enemigo.CharIndex;
            AtacarObjetivoMascota(map, pet, enemigo, mejorDist);
            return;
        }

        // 3) Sin enemigos válidos (o el único candidato está fuera de correa): seguir al amo si
        // está a más de 3 tiles.
        int distAmo = Math.Abs(amo.Pos.X - pet.X) + Math.Abs(amo.Pos.Y - pet.Y);
        if (distAmo > 3) StepToward(map, pet, amo.Pos.X, amo.Pos.Y, PET_PATHFIND_STEPS, evitarUsuarios: true);
    }

    /// <summary>
    /// Ataca al objetivo de la mascota: si tiene hechizos propios (PetLeveling.SpellsPorNivel —
    /// ej. Ely desde nivel 10 lanza Descarga Eléctrica), alterna golpe/hechizo en melee y castea
    /// desde lejos en vez de acercarse (como cualquier NPC caster). Sin hechizos: golpe simple,
    /// se acerca si no está al lado (comportamiento de siempre, Lobo/Oso/etc).
    /// </summary>
    /// <summary>Devuelve false solo cuando el único recurso posible era acercarse y no pudo dar
    /// NINGÚN paso (StepToward heading==0: sin camino/atascada) — el caller lo usa para abandonar
    /// ese objetivo y reintentar la búsqueda libre en vez de quedarse congelada mirándolo.</summary>
    private static bool AtacarObjetivoMascota(int map, NpcInstance pet, NpcInstance objetivo, int dist)
    {
        bool puedeCastear = pet.Spells != null && pet.Spells.Length > 0
            && Math.Abs(objetivo.X - pet.X) <= RANGO_VISION_X && Math.Abs(objetivo.Y - pet.Y) <= RANGO_VISION_Y
            // Ya paralizado O inmune (NPCs.dat "Inmunidad"): un hechizo que sólo paraliza no
            // aporta nada. Sin lo de la inmunidad, contra un bicho inmune la mascota casteaba
            // para siempre y no pegaba nunca.
            && !HechizoYaAplicado(pet, objetivo.ParalizadoHasta > Environment.TickCount64 / 1000.0
                                       || objetivo.AfectaParalisis);
        if (dist <= 1)
        {
            if (puedeCastear && _aiRng.Next(2) == 0) Combat.NpcLanzaSpellANpc(pet, objetivo);
            else NpcAtacaNpc(map, pet, objetivo);
            return true;
        }
        if (puedeCastear) { Combat.NpcLanzaSpellANpc(pet, objetivo); return true; }
        return StepToward(map, pet, objetivo.X, objetivo.Y, PET_PATHFIND_STEPS, evitarUsuarios: true) != 0;
    }

    /// <summary>Igual que AtacarObjetivoMascota pero contra un JUGADOR hostil (defensa PvP del amo,
    /// ver CheckPetsVsUsuario): golpe/hechizo con Combat.NpcAtacaUsuario/NpcLanzaSpell, que ya
    /// validan todo lo propio de atacar a un usuario (muerto, oculto/invisible, GM intocable).</summary>
    private static bool AtacarUsuarioMascota(int map, NpcInstance pet, User objetivo, int dist)
    {
        bool puedeCastear = pet.Spells != null && pet.Spells.Length > 0
            && Math.Abs(objetivo.Pos.X - pet.X) <= RANGO_VISION_X && Math.Abs(objetivo.Pos.Y - pet.Y) <= RANGO_VISION_Y
            && !HechizoYaAplicado(pet, objetivo.flags.Paralizado == 1 || objetivo.flags.Inmovilizado == 1);
        if (dist <= 1)
        {
            if (puedeCastear && _aiRng.Next(2) == 0) Combat.NpcLanzaSpell(pet, objetivo.id);
            else Combat.NpcAtacaUsuario(pet, objetivo.id);
            return true;
        }
        if (puedeCastear) { Combat.NpcLanzaSpell(pet, objetivo.id); return true; }
        return StepToward(map, pet, objetivo.Pos.X, objetivo.Pos.Y, PET_PATHFIND_STEPS, evitarUsuarios: true) != 0;
    }

    /// <summary>
    /// ¿El hechizo de la mascota ya no aporta nada contra este objetivo? Hoy aplica a un solo
    /// caso, el del elemental de agua: su hechizo **sólo paraliza, no hace daño**, así que contra
    /// alguien YA paralizado castearlo es tirar el turno. Devolviendo true, el que llama cae al
    /// golpe cuerpo a cuerpo — que es justamente el gesto pedido: **paralizar y después pegar**.
    /// Un hechizo que hace daño (Ely, elemental de fuego) siempre aporta: devuelve false.
    /// </summary>
    private static bool HechizoYaAplicado(NpcInstance pet, bool objetivoParalizado)
    {
        if (!objetivoParalizado || pet.Spells == null || pet.Spells.Length == 0) return false;
        var sp = SpellData.Get(pet.Spells[0]);
        return (sp.Paraliza || sp.Inmoviliza) && sp.SubeHP != 2;
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

    /// <summary>Igual que CheckPets pero cuando el agresor del amo es OTRO JUGADOR (PvP), no un NPC:
    /// las mascotas del atacado que no tengan ya un objetivo pasan a atacar a ese jugador. Se llama
    /// SOLO tras validar que el ataque original ya era legal (PuedeAtacar), ver Combat.cs.</summary>
    public static void CheckPetsVsUsuario(int atacanteUserIndex, int victimaUserIndex)
    {
        var victima = UserListManager.UserList[victimaUserIndex];
        if (victima == null) return;
        int map = victima.Pos.Map;
        if (!_byMap.TryGetValue(map, out var list)) return;
        foreach (var pet in list)
        {
            if (pet.Dead || pet.MaestroUser != victimaUserIndex) continue;
            if (pet.MascotaTargetNpc == 0 && pet.MascotaTargetUsuario == 0) pet.MascotaTargetUsuario = atacanteUserIndex;
        }
    }

    // ---- Buffs/debuffs temporales de hechizo de bot (SubeFuerza/SubeAgilidad/Ceguera/Estupidez) ----
    // Porcentajes placeholder: ajustar jugando, no hay un equivalente 1:1 verificable contra el
    // sistema de atributos de usuarios (los NPC no tienen Fuerza/Agilidad base, solo MinHIT/MaxHIT/
    // PoderAtaque/PoderEvasion ya resueltos).
    public const int BOT_BUFF_FUERZA_PCT = 20, BOT_BUFF_AGILIDAD_PCT = 20;
    public const int BOT_CEGUERA_PCT = 30, BOT_ESTUPIDEZ_PCT = 30;

    /// <summary>MinHIT/MaxHIT del NPC con el buff/debuff de fuerza vigente aplicado (o los crudos si no hay).</summary>
    public static (int min, int max) HitEfectivo(NpcInstance n)
    {
        double now = Environment.TickCount64 / 1000.0;
        if (n.BuffFuerzaHasta <= now) return (n.MinHIT, n.MaxHIT);
        int min = n.MinHIT + n.MinHIT * n.BuffFuerzaDelta / 100;
        int max = n.MaxHIT + n.MaxHIT * n.BuffFuerzaDelta / 100;
        return (Math.Max(1, min), Math.Max(1, max));
    }

    /// <summary>PoderAtaque del NPC con el buff de fuerza y/o la penalización de ceguera vigentes.</summary>
    public static int PoderAtaqueEfectivo(NpcInstance n)
    {
        double now = Environment.TickCount64 / 1000.0;
        int p = n.PoderAtaque;
        if (n.BuffFuerzaHasta > now) p += p * n.BuffFuerzaDelta / 100;
        if (n.CegueraHasta > now) p -= p * BOT_CEGUERA_PCT / 100;
        return Math.Max(0, p);
    }

    /// <summary>PoderEvasion del NPC con el buff de agilidad y/o la penalización de estupidez vigentes.</summary>
    public static int PoderEvasionEfectivo(NpcInstance n)
    {
        double now = Environment.TickCount64 / 1000.0;
        int p = n.PoderEvasion;
        if (n.BuffAgilidadHasta > now) p += p * n.BuffAgilidadDelta / 100;
        if (n.EstupidezHasta > now) p -= p * BOT_ESTUPIDEZ_PCT / 100;
        return Math.Max(0, p);
    }

    // Mismos valores que Combat.FX_GOLPE_ACIERTO/FALLO (privados ahí) — animación de sangre/impacto
    // y de fallo sobre la víctima, para que el golpe de una mascota se vea igual que el de un jugador.
    private const short FX_GOLPE_ACIERTO_PET = 89, FX_GOLPE_FALLO_PET = 90;

    /// <summary>La mascota golpea a un NPC hostil (melee simple). Lo mata si HP llega a 0.</summary>
    private static void NpcAtacaNpc(int map, NpcInstance atacante, NpcInstance victima)
    {
        // VB6 NpcAtacaNpc (SistemaCombate.bas:1047): dormido por instrumento no puede atacar.
        if (atacante.DormidoHasta > Environment.TickCount64 / 1000.0) return;

        // Intervalo de ataque del NPC (IntervaloPermiteAtacarNpc, 3000ms; guardias 2000ms), igual que contra usuarios.
        if (!Intervals.PuedeAtacarNpc(ref atacante.TimerAtaqueFisico, AttackIntervalFor(atacante))) return;

        // VB6 NpcDaño (SistemaCombate.bas:1007): la víctima dormida despierta al recibir daño.
        DespertarNpc(victima);

        // Gira hacia el objetivo ANTES de pegar (VB6 AI_NPC.bas:422-431), igual que el ataque de un
        // NPC salvaje contra un usuario — sin esto la mascota pegaba mirando para cualquier lado.
        FaceTarget(map, atacante, victima.X, victima.Y);

        // Bots Y mascotas tiran chance de fallo y muestran feedback visual (golpe/fallo); los
        // guardias atacando monstruos siguen 1:1 como antes (siempre pegan, sin animación).
        bool esMascota = atacante.MaestroUser > 0;
        if ((atacante.IsBot || esMascota) && !NpcImpactaNpc(map, atacante, victima))
        {
            if (atacante.IsBot) BroadcastChatOverHead(map, "¡Falló!", atacante.CharIndex, 3);
            BotPlayWave(map, atacante.X, atacante.Y, Sounds.SWING); // "golpe al aire", mismo sonido que un jugador fallando
            Combat.BroadcastFX(map, victima.CharIndex, FX_GOLPE_FALLO_PET, 0); // animación de fallo sobre la víctima
            return;
        }

        var (hitMin, hitMax) = HitEfectivo(atacante);
        int max = Math.Max(hitMin, hitMax);
        int dano = max > 0 ? _aiRng.Next(hitMin, max + 1) : 1;
        if (dano < 1) dano = 1;

        // Mitigación de armadura de la VÍCTIMA (BotDañoBot, ModBotSistCombate.bas): antes esto no
        // existía para NPC-vs-NPC (un bot con armadura completa aguantaba igual que uno desnudo),
        // sólo estaba portado para NPC-vs-usuario (Combat.Npcdano). Mismo criterio de "parte del
        // cuerpo" que Npcdano: 1/6 a la cabeza (casco), el resto al cuerpo (armadura+escudo).
        if (victima.EquipArmorObj > 0 || victima.EquipCascoObj > 0 || victima.EquipShieldObj > 0)
        {
            int lugar = _aiRng.Next(1, 7);
            int absorbido = 0;
            if (lugar == 1)
            {
                if (victima.EquipCascoObj > 0)
                { var c = ObjData.Get(victima.EquipCascoObj); absorbido = RangoNpc(c.MinDef, c.MaxDef); }
            }
            else if (victima.EquipArmorObj > 0)
            {
                var a = ObjData.Get(victima.EquipArmorObj);
                if (victima.EquipShieldObj > 0)
                { var e = ObjData.Get(victima.EquipShieldObj); absorbido = RangoNpc(a.MinDef + e.MinDef, a.MaxDef + e.MaxDef); }
                else absorbido = RangoNpc(a.MinDef, a.MaxDef);
            }
            dano -= absorbido;
            if (dano < 1) dano = 1;
        }

        // EXP proporcional al daño (repartida mascota/dueño), ANTES de restar HP — mismo orden que
        // CalcularDarExp para el daño de un jugador (cap interno a MinHP todavía sin tocar).
        if (esMascota) Combat.CalcularDarExpMascota(atacante, victima, dano);
        victima.MinHP -= dano;
        if (atacante.IsBot)
        {
            BroadcastChatOverHead(map, dano.ToString(), atacante.CharIndex, 5);
            BotPlayWave(map, victima.X, victima.Y, Sounds.IMPACTO); // golpe que conecta
        }
        if (atacante.IsBot || esMascota)
            Combat.BroadcastFX(map, victima.CharIndex, FX_GOLPE_ACIERTO_PET, 0); // animación de impacto/sangre sobre la víctima
        if (victima.MinHP <= 0) MatarNpcInstance(victima, atacante);
    }

    /// <summary>Tirada uniforme [min,max] para mitigación de daño NPC-vs-NPC (min si max&lt;=min).</summary>
    private static int RangoNpc(int min, int max) => max > min ? _aiRng.Next(min, max + 1) : min;

    /// <summary>¿El bot atacante impacta al NPC/bot víctima? Mismo criterio que NpcImpacto (Combat.cs,
    /// npc-vs-usuario) pero comparando PoderAtaque/PoderEvasion de ambos NPCs. Si falla y la víctima
    /// tiene escudo, tira "rechazo" igual que BotImpactoBot (VB6): sólo cosmético (sonido), el golpe
    /// sigue siendo un fallo. Con los bots siempre a skill 100 en Tácticas y Defensa, la fórmula de
    /// VB6 (100*SkillDefensa/(SkillDefensa+SkillTacticas)) da 50% fijo.</summary>
    private static bool NpcImpactaNpc(int map, NpcInstance atacante, NpcInstance victima)
    {
        var cc = BalanceData.Combate;
        long prob = Math.Max(cc.ImpactoMin, Math.Min(cc.ImpactoMax, cc.ImpactoBase + (PoderAtaqueEfectivo(atacante) - PoderEvasionEfectivo(victima))));
        bool impacto = _aiRng.Next(1, 101) <= prob;

        if (!impacto && victima.EquipShieldObj > 0)
        {
            const int probRechazo = 50; // 100*100/(100+100) clampeado [10,90]: el clamp no cambia el resultado
            if (_aiRng.Next(1, 101) <= probRechazo)
                BotPlayWave(map, victima.X, victima.Y, Sounds.ESCUDO);
        }
        return impacto;
    }

    /// <summary>Mata un NPC por daño (físico/mágico de otro NPC o bot): respawnea y libera el charindex.
    /// atacante (opcional): si es un bot "progresivo" (BotLeveling), recibe el GiveEXP de la víctima
    /// y puede subir de nivel (Bots.DarExpABot) — ver Game/Bots.cs.</summary>
    public static void MatarNpcInstance(NpcInstance victima, NpcInstance atacante = null)
    {
        if (atacante != null && atacante.IsBot && atacante.BotLeveling)
            Bots.DarExpABot(atacante, victima.GiveEXP);

        victima.Dead = true;
        victima.RespawnAt = Environment.TickCount64 / 1000.0 + RespawnSecondsFor(victima);
        // Mascota compañera muerta: avisa al panel (HP a 0) y por consola. NoRespawn=true evita
        // que el scan de respawn general la reviva sola — se reinvoca con el hechizo.
        if (victima.PetOfPlayer && victima.MaestroUser > 0)
        {
            var duenio = UserListManager.UserList[victima.MaestroUser];
            if (duenio != null)
            {
                duenio.PetDead = true; // hasta que la revivan (Veterinaria, Accion.cs) o recasteen el hechizo
                // Limpiar el CharIndex YA (no esperar a la próxima invocación): el pool reusa índices
                // libres y sin este reset u.PetCharIndex podía terminar apuntando, por casualidad, a
                // otro NPC vivo no relacionado — EnviarPetInfo mostraba datos ajenos y, peor, la
                // próxima invocación podía terminar borrando ESE NPC creyendo que era la mascota vieja.
                duenio.PetCharIndex = 0;
                if (duenio.flags.UserLogged)
                {
                    Combat.EnviarPetInfo(duenio);
                    if (duenio.Conn != null)
                    {
                        string nombreMostrar = !string.IsNullOrEmpty(victima.PetNombre) ? victima.PetNombre : victima.Name;
                        ServerPackets.ConsoleMsg(duenio.Conn, $"¡{nombreMostrar} ha muerto!", 1);
                    }
                }
            }
        }
        // Sin un User "matador" (mascota o bot vs bot/npc): igual suelta sus Drops (armadura/arma
        // equipada, en el caso de un bot) al piso, TirarDrops acepta killer=null.
        try { Combat.TirarDrops(victima, null); } catch (Exception ex) { Console.WriteLine($"[Drops] ERROR: {ex}"); }
        AreaVisibility.OnNpcRemoved(victima);
        CharIndexPool.Free(victima.CharIndex);   // reusar el índice; respawn pide uno nuevo
        victima.CharIndex = 0;
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
    public const long BOT_ATAQUE_MS = 1100;   // intervalo de GOLPE del bot (melee)
    public const long BOT_SPELL_MS  = 1100;   // intervalo de HECHIZO del bot (separado del golpe, como un jugador)

    /// <summary>Intervalo de ataque (ms) propio del NPC: guardias 2000ms, el resto default (3000ms).</summary>
    public static long AttackIntervalFor(NpcInstance n)
        => n.IsBot ? BOT_ATAQUE_MS : (n.NpcType == NPCTYPE_GUARDIASCITY ? GUARDIA_ATAQUE_MS : 0);

    /// <summary>
    /// Gate de GOLPE del bot: respeta su intervalo de melee Y el cruce con la magia (no golpear apenas
    /// casteó, IntervaloMagiaGolpe), igual que un jugador. Consume el timer si pasa.
    /// </summary>
    public static bool BotPuedeGolpear(NpcInstance n)
    {
        long now = Environment.TickCount64;
        if (now - n.TimerAtaqueFisico < BOT_ATAQUE_MS) return false;
        if (now - n.TimerLanzarSpell < Intervals.MagiaGolpe) return false;
        n.TimerAtaqueFisico = now;
        return true;
    }

    /// <summary>
    /// Gate de HECHIZO del bot: respeta su intervalo de magia Y el cruce con el golpe (no castear apenas
    /// pegó, IntervaloGolpeMagia), igual que un jugador. Consume el timer si pasa.
    /// </summary>
    public static bool BotPuedeCastear(NpcInstance n)
    {
        long now = Environment.TickCount64;
        if (now - n.TimerLanzarSpell < BOT_SPELL_MS) return false;
        if (now - n.TimerAtaqueFisico < Intervals.GolpeMagia) return false;
        n.TimerLanzarSpell = now;
        return true;
    }

    public const int BOT_POT_HP = 30;   // cuánto cura una poción roja del bot (estándar AO)

    /// <summary>
    /// Autopot del bot (potas INFINITAS) al intervalo real (IntervaloGolpeUsar = 300ms). Si le bajaron la
    /// vida (le pegaste), toma roja (+30); si es caster y le falta maná, toma azul (fórmula VB6). Una sola
    /// poción por intervalo, y suena el SND_BEBER, igual que un jugador poteando.
    /// </summary>
    private static void BotAutoPot(NpcInstance n)
    {
        long now = Environment.TickCount64;
        if (now - n.TimerPocion < Intervals.GolpeUsar) return;

        bool bebio = false;
        // Poción roja: cura vida (potas infinitas).
        if (n.MaxHP > 0 && n.MinHP < n.MaxHP)
        {
            n.MinHP = Math.Min(n.MaxHP, n.MinHP + BOT_POT_HP);
            bebio = true;
        }
        // Poción azul: recupera maná (fórmula real, ELV 50: 4% del máx + 25). Se potea ADEMÁS de la roja,
        // si no, un caster bajo ataque potearía siempre vida y nunca maná → se quedaría sin lanzar.
        if (n.MaxMana > 0 && n.MinMana < n.MaxMana)
        {
            int rec = n.MaxMana * 4 / 100 + 25;
            n.MinMana = Math.Min(n.MaxMana, n.MinMana + rec);
            bebio = true;
        }

        if (bebio)
        {
            n.TimerPocion = now;
            BotPlayWave(n.Map, (byte)n.X, (byte)n.Y, Sounds.BEBER);  // sonido de poteo
        }
    }

    public const long BOT_DESPARALIZA_MS = 1200; // reacción del bot para sacarse la parálisis/inmovilización (potea "remover parálisis")

    /// <summary>
    /// El bot se saca solo la parálisis/inmovilización tras una pequeña reacción (BOT_DESPARALIZA_MS),
    /// como un jugador que potea "remover parálisis". Devuelve true si TODAVÍA está trabado (reaccionando),
    /// para que el caller saltee el tick; false si ya está libre (o nunca estuvo).
    /// </summary>
    private static bool BotCleanseParalisis(NpcInstance n, double now)
    {
        bool paral = n.ParalizadoHasta > now;
        bool inmov = n.InmovilizadoHasta > now;
        if (!paral && !inmov) return false;

        // Todavía dentro de la ventana de reacción: sigue trabado.
        if (Environment.TickCount64 - n.EstadoParalisisTick < BOT_DESPARALIZA_MS) return true;

        if (paral) { n.ParalizadoHasta = 0; DifundirParalisisNpc(n, 0); }
        if (inmov) { n.InmovilizadoHasta = 0; }
        BotPlayWave(n.Map, (byte)n.X, (byte)n.Y, Sounds.BEBER);  // sonido de poción al sacarse la parálisis
        BroadcastChatOverHead(n.Map, "Remover Parálisis", n.CharIndex, 3);
        return false;
    }

    /// <summary>Difunde un sonido en (x,y) a todos los usuarios del mapa (sonido de poteo del bot, etc.)
    /// y a los espectadores del panel (Espia.cs no es un User real, así que el loop de abajo no los alcanza).</summary>
    private static void BotPlayWave(int map, byte x, byte y, short wave)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u != null && u.flags.UserLogged && u.Conn != null && u.Pos.Map == map)
                ServerPackets.PlayWave(u.Conn, wave, x, y);
        }
        Espia.ParaObservadoresDe(map, x, y, c => ServerPackets.PlayWave(c, wave, x, y));
    }

    /// <summary>Racha de kills de un bot que mata JUGADORES (mismo criterio y sonidos que
    /// Facciones.SonarKillstreak para PvP: 262 1ª, 261 2da, 270 3ra, 175 desde la 7ma).</summary>
    internal static void SonarKillstreakBot(NpcInstance atacante)
    {
        atacante.KillStreak++;
        short snd = atacante.KillStreak switch
        {
            1 => Sounds.FIRST_BLOOD,
            2 => Sounds.DOUBLE_KILL,
            3 => Sounds.TRIPLE_KILL,
            >= 7 => Sounds.KILL_SPREE,
            _ => 0,
        };
        if (snd == 0) return;
        BotPlayWave(atacante.Map, (byte)atacante.X, (byte)atacante.Y, snd);
    }

    /// <summary>Burbuja con las palabras mágicas sobre la cabeza del NPC al castear (modo 3, igual que un jugador).</summary>
    public static void NpcDicePalabrasMagicas(NpcInstance n, string palabras)
    {
        if (n == null || string.IsNullOrEmpty(palabras)) return;
        BroadcastChatOverHead(n.Map, palabras, n.CharIndex, 3);
    }
    private const byte NPCTYPE_SACERDOTE = 1; // eNPCType.Revividor (NPCs.dat: NPC5 "Sacerdote", NPC101 "Sacerdote Malvado")

    private const byte CIUDAD_IMPERIAL = 1, CIUDAD_REPUBLICANA = 2, CIUDAD_CAOTICA = 3, CIUDAD_RINKEL = 5;
    // Mapa de la ciudad neutral de Rinkel: todo guardia dentro queda neutral (no se mueve ni ataca).
    private const int MAPA_RINKEL = 20;

    /// <summary>
    /// IA de guardia de ciudad (GuardiasAI 1:1). Orden: atacar NPC hostil cercano → volver al
    /// origen si estático → atacar/perseguir usuario enemigo → patrulla por waypoints con
    /// pathfinding. Los guardias no hablan (custom: se les sacó todo el diálogo/saludos/frases).
    /// </summary>
    private static void GuardiasAI(int map, NpcInstance n)
    {
        // ---- 0) Guardias de Rinkel: decorado puro. No patrullan, no persiguen ----
        // Ciudad neutral por MAPA (20=Rinkel), no solo por facción: cualquier guardia (sea cual
        // sea su Ciudad) spawneado en Rinkel queda quieto y no ataca a nadie.
        if (n.Ciudad == CIUDAD_RINKEL || map == MAPA_RINKEL) return;

        // ---- 0.5) Cerrar la puerta que el guardia abrió, al alejarse (custom). La puerta cubre el ancla
        // y ancla.x-1; cerramos cuando el guardia ya no ocupa ni está adyacente a ninguno de esos tiles. ----
        CerrarPuertaSiSeAlejo(map, n);

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
        // [[b4_usersbymap FIX1]] Antes: for 1..LastUser (todos los usuarios conectados al server).
        // Ahora: solo los usuarios de ESTE mapa (mismo patrón que NearestUser/AdjacentUser). Mismo
        // criterio de filtrado y de desempate (a igual distancia, gana el de menor índice, igual que
        // el for ascendente de antes) — el resultado elegido es idéntico, solo más barato de calcular.
        int mejor = 0, mejorDist = int.MaxValue;
        foreach (int i in UsersByMapIndex.Get(map))
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != map) continue;
            if (!NpcVeUsuario(n, u)) continue;
            if (EsGmIntocable(u)) continue; // los guardias no persiguen a GMs/Dioses
            if (Math.Abs(u.Pos.X - n.X) > RANGO_VISION_X || Math.Abs(u.Pos.Y - n.Y) > RANGO_VISION_Y) continue;
            if (!EsEnemigoUsuario(n.Ciudad, u)) continue;
            int d = Math.Abs(u.Pos.X - n.X) + Math.Abs(u.Pos.Y - n.Y);
            if (d < mejorDist || (d == mejorDist && i < mejor)) { mejorDist = d; mejor = i; }
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

        // ---- 9.5) Conciencia: gira (mudo) a mirar a un ciudadano que pase cerca ----
        if (n.GreetTimer > 0) n.GreetTimer--;
        else
        {
            // ciudadano (no enemigo) más cercano dentro de 4 tiles, vivo y visible
            // [[b4_usersbymap FIX1]] Antes: for 1..LastUser. Ahora: solo usuarios de este mapa.
            // Mismo criterio de filtrado/desempate (a igual distancia, gana el de menor índice,
            // como el for ascendente original) — mismo ciudadano elegido, solo más barato.
            User cerca = null; int dCerca = int.MaxValue; int cercaIdx = int.MaxValue;
            foreach (int i in UsersByMapIndex.Get(map))
            {
                var u = UserListManager.UserList[i];
                if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != map || u.flags.Oculto == 1 || u.flags.Invisible == 1) continue;
                if (EsEnemigoUsuario(n.Ciudad, u)) continue;
                int d = Math.Abs(u.Pos.X - n.X) + Math.Abs(u.Pos.Y - n.Y);
                if (d <= 4 && (d < dCerca || (d == dCerca && i < cercaIdx))) { dCerca = d; cerca = u; cercaIdx = i; }
            }
            if (cerca != null)
            {
                FaceTarget(map, n, cerca.Pos.X, cerca.Pos.Y); // gira a mirarlo (se siente atento), sin hablar
                n.GreetTimer = 140; // vuelve a mirar alrededor en ~1 min
            }
            else n.GreetTimer = 20; // nadie cerca: reintenta pronto
        }

        // ---- 10) Si es estático, no se mueve ----
        if (n.Movement == 1) return;

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
            if (h != 0)
            {
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

        // ---- Mascotas y criaturas invocadas POR UN JUGADOR ----
        // Van por las reglas de PvP de su DUEÑO, no por su `Attackable` del .dat. Sin esto eran
        // intocables: las versiones FAMILIAR (NPC126-133) no traen `Attackable` en NPCs.dat, o sea
        // que `Attackable` queda en false y el chequeo de abajo las declaraba "no atacables" —
        // aunque el dueño fuera de facción enemiga y te estuviera pegando.
        // Delegar en Combat.PuedeAtacar (y no reimplementar el criterio) hace que la mascota herede
        // TODO: zona segura, misma facción o aliados, party, clan, pareja, torneo, /todosvstodos.
        if (n.MaestroUser > 0)
        {
            if (n.MaestroUser == u.id)
            { motivo = "No puedes atacar a tu propia mascota."; return false; }
            var dueño = UserListManager.UserList[n.MaestroUser];
            // Dueño desconectado/inválido: la criatura quedó suelta, se trata como bicho común.
            if (dueño == null || !dueño.flags.UserLogged) return n.Attackable || n.NpcType != NPCTYPE_GUARDIASCITY;
            // PuedeAtacar ya le explica al jugador por qué no (zona segura, facción, party...),
            // así que no se devuelve motivo: el caller no tiene que escribir un segundo mensaje.
            return Combat.PuedeAtacar(u.id, n.MaestroUser);
        }

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
    /// Ciudad de facción real (Imperial/Republicana) del MAPA donde está parado el sacerdote. Las
    /// dos únicas definiciones de sacerdote en NPCs.dat (NPC5 "Sacerdote", NPC101 "Sacerdote
    /// Malvado") NO traen Status/Ciudad propio — Ciudad queda en 0 (verificado) porque el mismo
    /// NPC5 se reutiliza tal cual en Nix/Ullathorpe/Banderbill (imperial) Y en Illiandor/Lindos/
    /// Suramei (republicana), así que `n.Ciudad` no sirve para distinguir de qué ciudad es ESTE
    /// sacerdote. Se deriva del mapa en el que está parado, con los mismos mapas capitales que ya
    /// usa el sistema de Hogar (Social.cs:SeleccionarHogar) para clasificar Imperial/Republicana.
    /// Cualquier otro mapa (Rinkel, ciudades secundarias, mazmorras) queda neutral: ese sacerdote
    /// ayuda a cualquiera.
    /// </summary>
    private static byte CiudadFaccionDelMapa(int map) => map switch
    {
        1 or 34 or 59 => CIUDAD_IMPERIAL,       // Ullathorpe, Nix, Banderbill
        194 or 63 or 184 => CIUDAD_REPUBLICANA, // Illiandor, Lindos, Suramei
        _ => 0,
    };

    /// <summary>
    /// IA del sacerdote de ciudad (NpcType=1, "Revividor" del .dat): dentro de su rango de visión,
    /// resucita al aliado muerto más cercano (prioridad) o cura al instante (a full HP) al aliado
    /// vivo más cercano al que le falte vida, aunque sea un solo punto. Respeta la facción del
    /// usuario según la ciudad del MAPA (CiudadFaccionDelMapa, no n.Ciudad): un enemigo de esa
    /// ciudad (EsEnemigoUsuario) nunca es curado ni resucitado, igual que un usuario sin facción sí
    /// lo es (nunca "enemigo"). En Rinkel/ciudades sin facción, ayuda a cualquiera.
    /// </summary>
    private static void SacerdoteAI(int map, NpcInstance n)
    {
        byte ciudad = CiudadFaccionDelMapa(map);
        User muerto = null; int dMuerto = int.MaxValue;
        foreach (int i in UsersByMapIndex.Get(map))
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.flags.Muerto != 1) continue;
            if (EsEnemigoUsuario(ciudad, u)) continue;
            int dx = Math.Abs(u.Pos.X - n.X), dy = Math.Abs(u.Pos.Y - n.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy;
            if (d < dMuerto) { dMuerto = d; muerto = u; }
        }
        if (muerto != null)
        {
            if (Combat.NpcResucitaAUsuario(n, muerto)) return;
        }

        User herido = null; int dHerido = int.MaxValue;
        foreach (int i in UsersByMapIndex.Get(map))
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.flags.Muerto != 0) continue;
            if (u.Stats.MinHP >= u.Stats.MaxHP) continue; // sano: nada que curar
            if (EsEnemigoUsuario(ciudad, u)) continue;
            int dx = Math.Abs(u.Pos.X - n.X), dy = Math.Abs(u.Pos.Y - n.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy;
            if (d < dHerido) { dHerido = d; herido = u; }
        }
        if (herido != null)
        {
            Combat.NpcCuraAUsuario(n, herido);
        }
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
            if (o.TargetUser != 0) continue;   // ocupado: no interrumpir
            if (Math.Abs(o.X - u.Pos.X) > GUARDIA_ALERTA_RADIO || Math.Abs(o.Y - u.Pos.Y) > GUARDIA_ALERTA_RADIO) continue;
            o.TargetUser = userIndex;
        }
    }

    /// <summary>Difunde un ChatOverHead a todos los usuarios del mapa (SendData ToNPCArea).</summary>
    internal static void BroadcastChatOverHead(int map, string chat, short charIndex, byte mode)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            // Mismo mapa (clásico) O — mundo continuo — cualquiera que tenga el NPC visible (mapa vecino):
            // así los diálogos de guardias/mercaderes se ven también desde el mapa de al lado.
            if (u.flags.UserLogged && u.Conn != null && (u.Pos.Map == map || u.VisibleNpcs.Contains(charIndex)))
                ServerPackets.ChatOverHead(u.Conn, chat, charIndex, mode);
        }
        Espia.ParaObservadoresDeChar(map, charIndex, c => ServerPackets.ChatOverHead(c, chat, charIndex, mode));
    }

    /// <summary>Si hay un usuario en un tile adyacente, devuelve su userIndex y el heading hacia él.</summary>
    // ============================================================
    //  IA de BOTS de prueba: siguen al dueño y atacan al resto (incl. GMs).
    // ============================================================
    private const int BOT_FOLLOW_TELEPORT = 11;  // si el dueño queda más lejos que esto, el bot se teletransporta (no se pierde)

    private const short BOAT_BODY = 838;  // cuerpo de Barca VIVA (Ropaje del obj Barca 474); 87 era el fantasma

    // --- Taunts de guerra de facciones (bots de evento que atacan ciudades enemigas) ---
    private static readonly string[] TauntsArmada = {
        "¡Por el Imperio! ¡Las Hordas del Caos arderán!",
        "¡Muerte al Caos! ¡Gloria a la Armada Real!",
        "¡Ríndanse, escoria del Caos!",
        "¡El Imperio aplastará vuestra rebelión!",
        "¡Esta ciudad caerá ante la Armada!",
    };
    private static readonly string[] TauntsMilicia = {
        "¡Por la República! ¡La libertad no caerá!",
        "¡Milicianos, al ataque! ¡Muerte a los tiranos!",
        "¡Vuestra ciudad será nuestra!",
        "¡Avancen, hermanos de la Milicia!",
        "¡Ni Imperio ni Caos: solo la República!",
    };
    private static readonly string[] TauntsCaos = {
        "¡El Caos os consumirá a todos!",
        "¡Muerte al Imperio! ¡Por las Hordas!",
        "¡Vuestras murallas caerán en llamas!",
        "¡Sangre y fuego para los imperiales!",
        "¡Arrasaremos esta ciudad!",
    };

    // Throttle GLOBAL de taunts: como máximo uno cada MIN_TAUNT_GAP seg (evita que las burbujas
    // de muchos bots tapen toda la pantalla en un evento masivo).
    private static double _lastTauntAt;
    private const double MIN_TAUNT_GAP = 3.5;

    /// <summary>Grita un taunt de facción sobre la cabeza del bot (evento de ataque a ciudades).</summary>
    private static void ShoutTaunt(int map, NpcInstance n)
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastTauntAt < MIN_TAUNT_GAP) return;   // sólo uno cada tanto en TODO el mundo
        string[] pool = n.BotFaccion switch { 1 => TauntsArmada, 2 => TauntsMilicia, 3 => TauntsCaos, _ => null };
        if (pool == null || pool.Length == 0) return;
        _lastTauntAt = now;
        BroadcastChatOverHead(map, pool[_aiRng.Next(pool.Length)], (short)n.CharIndex, 8); // modo 8 = taunt color facción
    }

    /// <summary>Inicializa un bot recién spawneado: dueño, nick, apariencia de tierra y dirección (heading).</summary>
    public static void InitBot(NpcInstance bot, int owner, string nick, byte heading = 0)
    {
        bot.OwnerUserIndex = owner;
        bot.Name = nick;
        bot.LandBody = bot.Body; bot.LandWeapon = bot.WeaponAnim;
        bot.LandShield = bot.ShieldAnim; bot.LandCasco = bot.CascoAnim;
        bot.NoRespawn = true;   // si muere, NO respawnea
        if (heading >= 1 && heading <= 4 && heading != bot.Heading)
        { bot.Heading = heading; BroadcastNpcAppearance(bot.Map, bot); }  // mira en la dirección del invocador
    }

    /// <summary>Cambia el arma visible del bot (ej: estandarte de facción en mano) y la difunde.</summary>
    public static void SetBotWeaponAnim(NpcInstance bot, short anim)
    {
        bot.WeaponAnim = anim; bot.LandWeapon = anim;
        BroadcastNpcAppearance(bot.Map, bot);
    }

    /// <summary>
    /// Maneja la barca de los bots: EMBARCAN al pisar agua y DESEMBARCAN al pisar tierra (no antes).
    /// Sólo pueden cruzar agua mientras el dueño navega (para ir hasta él) o si todavía siguen sobre
    /// agua (para volver a la costa). Difunde el cambio de apariencia.
    /// </summary>
    private static void ReconcileBoat(int map, NpcInstance n, User owner)
    {
        bool ownerNav = owner != null && owner.flags.Navegando;
        // Puede pisar agua si el dueño navega (acercarse) o si todavía está sobre agua (volver a tierra).
        ReconcileBoatVisual(map, n, ownerNav);
    }

    /// <summary>
    /// Parte visual/física de la barca, compartida por los bots que siguen a un dueño y por los de la
    /// guerra de facciones (que llevan barca propia, puedeAgua siempre true): permite pisar agua,
    /// cambia el cuerpo a barca al entrar y lo restaura al volver a tierra.
    /// </summary>
    private static void ReconcileBoatVisual(int map, NpcInstance n, bool puedeAgua)
    {
        bool sobreAgua = MapLoader.Get(map)?.HasWater(n.X, n.Y) == true;

        // Si ya está sobre agua puede seguir pisándola aunque no "pueda" (para volver a la costa).
        n.AguaValida = puedeAgua || sobreAgua;

        if (sobreAgua && !n.EnBarca)
        {
            // Pisó agua → embarca (cuerpo de barca, sin equipo visible).
            n.EnBarca = true;
            n.Body = BOAT_BODY; n.WeaponAnim = 0; n.ShieldAnim = 0; n.CascoAnim = 0;
            BroadcastNpcAppearance(map, n);
        }
        else if (!sobreAgua && n.EnBarca)
        {
            // Pisó tierra → desembarca (recupera el sacro).
            n.EnBarca = false;
            n.Body = n.LandBody; n.WeaponAnim = n.LandWeapon; n.ShieldAnim = n.LandShield; n.CascoAnim = n.LandCasco;
            BroadcastNpcAppearance(map, n);
        }
    }

    /// <summary>Difunde la apariencia actual del NPC (body/anims/heading) a todos los del mapa.</summary>
    public static void BroadcastNpcAppearance(int map, NpcInstance n)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CharacterChange(o.Conn, (short)n.CharIndex, n.Body, n.Head,
                    n.Heading, n.WeaponAnim, n.ShieldAnim, n.CascoAnim, 0, 0, 0);
        }
    }

    // Grupo de facción de un bot: 1=Imperial(Armada), 2=República(Milicia), 3=Caos.
    private static int GrupoBot(byte botFaccion) => botFaccion switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };

    // ============================================================
    //  Evento de batalla de facciones: PARLEY (campeones al frente + diálogo) → CARGA.
    // ============================================================
    private sealed class BotBattle
    {
        public int GrupoA, GrupoB;
        public NpcInstance CampA, CampB;    // campeones de cada bando (refs)
        public int CenAx, CenAy, CenBx, CenBy;
        public bool Talking;                // ya se encontraron en el medio → dialogan
        public double NextLineAt;
        public int LineIdx;
        public bool Charged;
    }
    private static readonly Dictionary<int, BotBattle> _battles = new();
    private const double PARLEY_LINE_GAP = 4.0;   // espacio entre líneas para que no se encimen
    private const int PARLEY_LINES = 9;

    private static readonly string[] LineasImperial = {
        "¡Esta ciudad pertenece al Imperio!",
        "¡Deponed las armas y quizá viváis!",
        "¡La Armada Real no conoce el miedo!",
        "¡Vuestra rebelión termina hoy!",
        "¡Por el Rey, por el honor, a la carga!",
    };
    private static readonly string[] LineasRepublica = {
        "¡La República jamás se arrodilla!",
        "¡Esta tierra la regamos con sangre libre!",
        "¡No tememos a vuestras coronas!",
        "¡El pueblo se alza contra los tiranos!",
        "¡Por la libertad, defended cada palmo!",
    };
    private static readonly string[] LineasCaos = {
        "¡El Caos lo devora todo a su paso!",
        "¡Vuestras súplicas alimentan mi furia!",
        "¡Arderéis junto a vuestras murallas!",
        "¡No habrá piedad, solo fuego!",
        "¡Sangre y muerte, mortales insignificantes!",
    };

    private static string LineaDeGrupo(int grupo, int i)
    {
        var pool = grupo switch { 1 => LineasImperial, 2 => LineasRepublica, 3 => LineasCaos, _ => LineasImperial };
        return pool[i % pool.Length];
    }

    /// <summary>Devuelve la batalla del mapa si está en fase de parley (todavía no cargaron).</summary>
    private static BotBattle BattleEnParley(int map)
        => _battles.TryGetValue(map, out var b) && !b.Charged ? b : null;

    /// <summary>Mantiene el estado de batalla por mapa: arma el parley, mueve el diálogo y da la carga.</summary>
    private static void TickBattles()
    {
        double now = Environment.TickCount64 / 1000.0;
        foreach (var kv in _byMap)
        {
            int map = kv.Key;
            // Por grupo: conteo, suma de posiciones (centroide) y campeón (instancia de menor CharIndex vivo).
            var cnt = new Dictionary<int, int>(); var sx = new Dictionary<int, int>(); var sy = new Dictionary<int, int>();
            var champ = new Dictionary<int, NpcInstance>();
            foreach (var n in kv.Value)
            {
                // Los bots de la guerra mundial NO arman parley: marchan y pelean al toque (si no,
                // dos ejércitos que se cruzan de casualidad se congelarían esperando el diálogo).
                if (n.Dead || !n.IsBot || n.BotFaccion == 0 || n.BotGuerra) continue;
                int g = GrupoBot(n.BotFaccion);
                cnt[g] = cnt.GetValueOrDefault(g) + 1;
                sx[g] = sx.GetValueOrDefault(g) + n.X; sy[g] = sy.GetValueOrDefault(g) + n.Y;
                if (!champ.TryGetValue(g, out var c) || n.CharIndex < c.CharIndex) champ[g] = n;
            }
            if (cnt.Count < 2) { _battles.Remove(map); continue; }

            // Dos bandos con más bots.
            int gA = 0, gB = 0, cA = -1, cB = -1;
            foreach (var g in cnt.Keys)
            {
                if (cnt[g] > cA) { gB = gA; cB = cA; gA = g; cA = cnt[g]; }
                else if (cnt[g] > cB) { gB = g; cB = cnt[g]; }
            }

            if (!_battles.TryGetValue(map, out var b))
            { b = new BotBattle { GrupoA = gA, GrupoB = gB, LineIdx = 0 }; _battles[map] = b; }

            // Actualizar campeones y centroides (los bots se mueven).
            b.CampA = champ.GetValueOrDefault(b.GrupoA); b.CampB = champ.GetValueOrDefault(b.GrupoB);
            b.CenAx = sx.GetValueOrDefault(b.GrupoA) / Math.Max(1, cnt.GetValueOrDefault(b.GrupoA));
            b.CenAy = sy.GetValueOrDefault(b.GrupoA) / Math.Max(1, cnt.GetValueOrDefault(b.GrupoA));
            b.CenBx = sx.GetValueOrDefault(b.GrupoB) / Math.Max(1, cnt.GetValueOrDefault(b.GrupoB));
            b.CenBy = sy.GetValueOrDefault(b.GrupoB) / Math.Max(1, cnt.GetValueOrDefault(b.GrupoB));

            if (b.Charged || b.CampA == null || b.CampB == null) continue;

            // ¿Los campeones ya se encontraron en el medio? (distancia chica) → arrancar el diálogo.
            int distCamp = Math.Abs(b.CampA.X - b.CampB.X) + Math.Abs(b.CampA.Y - b.CampB.Y);
            if (!b.Talking)
            {
                if (distCamp <= 2) { b.Talking = true; b.NextLineAt = now + 0.7; }
                else continue; // todavía caminando hasta quedar frente a frente
            }

            // Mientras dialogan, se MIRAN entre ellos.
            FaceTarget(map, b.CampA, b.CampB.X, b.CampB.Y);
            FaceTarget(map, b.CampB, b.CampA.X, b.CampA.Y);

            // Diálogo alternado entre campeones: al hablar uno, se BORRA el cartel del otro
            // (así nunca se enciman y siempre se lee el que está hablando).
            if (now >= b.NextLineAt && b.LineIdx < PARLEY_LINES)
            {
                bool aHabla = (b.LineIdx % 2) == 0;
                var campeon = aHabla ? b.CampA : b.CampB;
                var otro    = aHabla ? b.CampB : b.CampA;
                int grupo   = aHabla ? b.GrupoA : b.GrupoB;
                BroadcastChatOverHead(map, "", (short)otro.CharIndex, 0);   // borra el diálogo del otro
                BroadcastChatOverHead(map, LineaDeGrupo(grupo, b.LineIdx / 2), (short)campeon.CharIndex, 8);
                b.LineIdx++;
                b.NextLineAt = now + PARLEY_LINE_GAP;
            }
            // Terminó el diálogo → ¡a la carga!
            else if (b.LineIdx >= PARLEY_LINES && now >= b.NextLineAt)
            {
                BroadcastChatOverHead(map, "", (short)b.CampA.CharIndex, 0);
                BroadcastChatOverHead(map, "", (short)b.CampB.CharIndex, 0);
                BroadcastChatOverHead(map, "¡AL ATAQUEEE!", (short)b.CampA.CharIndex, 8);
                BroadcastChatOverHead(map, "¡AL ATAQUEEE!", (short)b.CampB.CharIndex, 8);
                b.Charged = true;
            }
        }
    }

    // Grupo de facción de un usuario (Facciones: CIUDADANO=2,REPUBLICANO=3,CAOS=4,ARMADA=5,MILICIA=6).
    private static int GrupoUsuario(User u) => u.Faccion.Status switch
    {
        Facciones.CIUDADANO or Facciones.ARMADA => 1,
        Facciones.REPUBLICANO or Facciones.MILICIA => 2,
        Facciones.CAOS => 3,
        _ => 0,
    };

    /// <summary>IA autónoma de un bot de facción: busca y ataca a la facción enemiga; si no hay, deambula.</summary>
    private static void TickBotFaccion(int map, NpcInstance n)
    {
        int myGroup = GrupoBot(n.BotFaccion);

        // FASE PARLEY: antes de cruzar a pelear, un campeón de cada bando pasa al frente y el resto
        // se queda atrás agrupado mientras dialogan. No se ataca hasta el "¡Al ataque!".
        var parley = BattleEnParley(map);
        if (parley != null)
        {
            bool esCampeon = parley.CampA == n || parley.CampB == n;
            if (esCampeon)
            {
                if (!parley.Talking)
                {
                    // Caminar DIRECTO hacia el otro campeón hasta quedar frente a frente (adyacentes).
                    var otro = parley.CampA == n ? parley.CampB : parley.CampA;
                    if (otro != null && Math.Abs(otro.X - n.X) + Math.Abs(otro.Y - n.Y) > 1)
                        StepToward(map, n, otro.X, otro.Y);
                }
                // Si ya se encontraron (Talking), quedan parados mirándose (lo orienta TickBattles).
            }
            // El resto se queda QUIETO donde fue invocado (solo el campeón pasa al frente).
            return; // sin moverse ni pelear durante el parley
        }

        // Antes de la carga (o sin batalla armada) TODOS quietos: no pelean ni se mueven.
        if (!(_battles.TryGetValue(map, out var batt) && batt.Charged)) return;

        // Sin enemigos a la vista (ya en plena carga): deambular buscando rezagados.
        if (!CombateDeFaccion(map, n)) TryStepRandom(map, n);
    }

    /// <summary>
    /// Sella la hora del último combate del bot (y de su víctima). Lo usa la CÁMARA AUTOMÁTICA
    /// del espectador para saltar sola a donde se están peleando (ver Espia: modo "acción").
    /// </summary>
    private static void MarcarCombate(NpcInstance atacante, NpcInstance victima)
    {
        double now = Environment.TickCount64 / 1000.0;
        if (atacante != null) atacante.UltimoCombateAt = now;
        if (victima != null) victima.UltimoCombateAt = now;
    }

    /// <summary>
    /// Núcleo de combate de un bot de facción: cura aliados heridos y ataca/persigue al enemigo de
    /// facción más cercano (usuario o bot). Devuelve true si hizo algo; false si no hay nadie a la
    /// vista y el que llama decide qué hacer (deambular en el evento de batalla, viajar en la guerra).
    /// </summary>
    private static bool CombateDeFaccion(int map, NpcInstance n)
    {
        int myGroup = GrupoBot(n.BotFaccion);

        // Enemigo más cercano: usuario de facción distinta o bot de facción distinta (ambos >0).
        // Éste sigue siendo el pool de candidatos PROPIO de facción/guerra (sólo el bando enemigo,
        // nunca un NPC salvaje ni un jugador sin facción) — la actividad de cada modo, no el combate.
        User enemyUser = null; int duE = int.MaxValue;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.flags.Muerto != 0) continue;
            int g = GrupoUsuario(u);
            if (g == 0 || g == myGroup) continue;          // sin facción o aliado → ignorar
            if (u.Pos.Map != map || !NpcVeUsuario(n, u)) continue;
            int dx = Math.Abs(u.Pos.X - n.X), dy = Math.Abs(u.Pos.Y - n.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy; if (d < duE) { duE = d; enemyUser = u; }
        }

        NpcInstance enemyBot = null; int dbE = int.MaxValue;
        foreach (var o in _byMap[map])
        {
            if (o.Dead || o == n || !o.IsBot || o.BotFaccion == 0) continue;
            if (GrupoBot(o.BotFaccion) == myGroup) continue;   // mismo bando
            int dx = Math.Abs(o.X - n.X), dy = Math.Abs(o.Y - n.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy; if (d < dbE) { dbE = d; enemyBot = o; }
        }

        if (enemyUser == null && enemyBot == null) return false;   // no hay nadie a la vista

        // Una vez resuelto el objetivo (enemigo de facción), TODO el combate — incluida la curación
        // a un aliado herido, ahora parte de TickCombateBot vía HelpAlly/BotHealSpell — lo maneja el
        // cerebro estándar. Ya no hace falta el chequeo de curación aparte de antes.
        bool targetIsUser = enemyUser != null && duE <= dbE;
        return TickCombateBot(map, n, enemyUser, enemyBot, targetIsUser);
    }

    // ============================================================
    //  IA de CAMPAÑA: bots de la guerra mundial de facciones.
    //  A diferencia de los bots del evento de batalla (que esperan quietos el parley en su mapa),
    //  estos marchan por el mundo hacia la ciudad de una facción enemiga, cruzando de mapa por los
    //  TileExits y embarcándose solos al pisar agua.
    // ============================================================

    /// <summary>Cada cuántos pasos fallidos seguidos el bot de guerra se replantea la ruta.</summary>
    private const int GUERRA_STUCK_MAX = 6;
    /// <summary>Segundos que se queda dando vueltas en la ciudad objetivo antes de elegir otra.</summary>
    private const double GUERRA_ESTADIA = 45.0;

    private static void TickBotGuerra(int map, NpcInstance n)
    {
        // Los bots de campaña llevan barca propia: pueden pisar agua siempre (embarcan al entrar y
        // desembarcan al volver a tierra, igual que los bots que siguen a un dueño que navega).
        ReconcileBoatVisual(map, n, puedeAgua: true);

        // Cerrar la puerta que este bot haya abierto, si ya se alejó (mismo criterio que los
        // guardias). Se llama siempre, no solo tras un paso: si el bot se puso a pelear o a
        // patrullar justo después de cruzar, la puerta igual se cierra sola.
        CerrarPuertaSiSeAlejo(map, n);

        // Taunt ocasional en el camino (el throttle global evita que tapen la pantalla).
        if (_aiRng.Next(12) == 0) ShoutTaunt(map, n);

        // Pelear siempre gana: si hay enemigo de facción a la vista, la marcha espera.
        if (CombateDeFaccion(map, n)) return;

        // Sin enemigos de facción: si se cruzó con una CRIATURA hostil, la pelea igual (un ejército
        // que atraviesa el mundo esquivando lobos quedaba raro). Solo criaturas salvajes: nunca
        // mascotas de jugador ni otros bots (de esos ya se ocupa CombateDeFaccion).
        if (AtacarCriaturaCercana(map, n)) return;

        // Guardianes de dungeon (DungeonBots.cs): no tienen un destino de marcha fijo, pero SÍ
        // pueden cruzar si el deambulado los lleva justo a un portal o a un TileExit (piso de
        // arriba, pasillo a otra ala del dungeon, etc.) — antes quedaban encerrados en su piso.
        if (n.BotDungeon)
        {
            if (CruzarSiHayPortal(map, n)) return;
            if (CruzarSiHaySalida(map, n)) return;
            if (LootCercano(map, n)) return;
            TryStepRandom(map, n);
            return;
        }

        ViajarGuerra(map, n);
    }

    /// <summary>Si el bot está PARADO justo sobre un objeto portal (ObjData.MapaDestino>0, ej. una
    /// runa de teletransporte en el piso), lo cruza — mismo warp diferido que TileExits/ViajarGuerra.</summary>
    private static bool CruzarSiHayPortal(int map, NpcInstance n)
    {
        var md = MapLoader.Get(map);
        if (md == null) return false;
        short objIndex = (short)md.FloorObj[n.X, n.Y];
        if (objIndex <= 0) return false;
        var od = ObjData.Get(objIndex);
        if (od.MapaDestino <= 0 || od.DestinoX <= 0 || od.DestinoY <= 0) return false;
        QueueBotWarpTo(n, od.MapaDestino, (byte)od.DestinoX, (byte)od.DestinoY);
        return true;
    }

    /// <summary>Si el bot está PARADO justo sobre un TileExit de este mapa, lo cruza (mismo mecanismo
    /// que ViajarGuerra: el warp se aplica al final del tick, vía ApplyPendingBotWarps).</summary>
    private static bool CruzarSiHaySalida(int map, NpcInstance n)
    {
        foreach (var s in BotPathing.SalidasDe(map))
        {
            if (s.X != n.X || s.Y != n.Y) continue;
            QueueBotWarpTo(n, s.DestMap, s.DestX, s.DestY);
            return true;
        }
        return false;
    }

    // ============================================================
    //  Loot: un bot guardián de dungeon recoge el equipo que soltó un rival muerto y se lo pone.
    // ============================================================

    // Cuánto tiempo tiene que verse un ítem tirado en el piso ANTES de que algún bot lo levante:
    // sin esto el pickup pasaba en el mismo instante que el drop y era imposible de ver a simple
    // vista. Se trackea por tile la primera vez que un bot lo detecta.
    private const double LOOT_DELAY_SECONDS = 5.0;
    private static readonly Dictionary<(int map, int x, int y), double> _lootVistoDesde = new();

    /// <summary>
    /// Busca el ítem tirado más cercano en rango de visión. Si lleva menos de LOOT_DELAY_SECONDS
    /// en el piso todavía no lo toca (para que se vea caer); si ya está "maduro", lo persigue y,
    /// al llegar, lo recoge y se lo equipa (visual). Devuelve true si hizo algo (o decidió esperar).
    /// </summary>
    private static bool LootCercano(int map, NpcInstance n)
    {
        var md = MapLoader.Get(map);
        if (md == null) return false;

        int bestX = 0, bestY = 0, bestD = int.MaxValue;
        for (int dy = -RANGO_VISION_Y; dy <= RANGO_VISION_Y; dy++)
        {
            int ny = n.Y + dy;
            if (ny < 1 || ny > 100) continue;
            for (int dx = -RANGO_VISION_X; dx <= RANGO_VISION_X; dx++)
            {
                int nx = n.X + dx;
                if (nx < 1 || nx > 100) continue;
                if (md.FloorObj[nx, ny] <= 0) continue;
                int d = Math.Abs(dx) + Math.Abs(dy);
                if (d < bestD) { bestD = d; bestX = nx; bestY = ny; }
            }
        }
        if (bestX == 0) return false;

        double now = Environment.TickCount64 / 1000.0;
        var key = (map, bestX, bestY);
        if (!_lootVistoDesde.TryGetValue(key, out double vistoDesde)) { _lootVistoDesde[key] = now; return false; }
        if (now - vistoDesde < LOOT_DELAY_SECONDS) return false; // todavía "fresco": que se vea tirado

        if (bestD == 0) { EquiparDelPiso(map, md, n, bestX, bestY); _lootVistoDesde.Remove(key); return true; }
        if (StepToward(map, n, bestX, bestY) == 0) TryStepRandom(map, n);
        return true;
    }

    /// <summary>Levanta el objeto en (x,y), lo saca del piso y actualiza la apariencia visual del bot.</summary>
    private static void EquiparDelPiso(int map, MapData md, NpcInstance n, int x, int y)
    {
        short objIndex = (short)md.FloorObj[x, y];
        md.FloorObj[x, y] = 0;
        md.FloorAmount[x, y] = 0;
        AreaVisibility.ObjectRemoved(map, x, y);

        var od = ObjData.Get(objIndex);
        switch (od.Type)
        {
            case ObjType.Weapon:
                n.WeaponAnim = n.LandWeapon = (short)od.WeaponAnim;
                n.AuraArma = (short)od.Aura;
                break;
            case ObjType.Escudo:
                n.ShieldAnim = n.LandShield = (short)od.ShieldAnim;
                n.AuraEscudo = (short)od.Aura;
                break;
            case ObjType.Casco:
                n.CascoAnim = n.LandCasco = (short)od.CascoAnim;
                n.AuraCasco = (short)od.Aura;
                break;
            default: // Armadura: cambia el cuerpo si trae Ropaje.
                if (od.Ropaje > 0) n.Body = n.LandBody = (short)od.Ropaje;
                n.Aura = (short)od.Aura;
                break;
        }
        BroadcastNpcAppearance(map, n);
    }

    /// <summary>
    /// Da un paso PERPENDICULAR al que le tocaba, si el tile está libre. Devuelve true si se movió.
    /// Sirve para que una columna de bots no camine en línea recta perfecta.
    /// </summary>
    private static bool PasoAlCostado(int map, NpcInstance n, byte headingPrevisto, int tx, int ty)
    {
        // Perpendiculares del heading: N/S ↔ E/O.
        byte a, b;
        if (headingPrevisto == H_N || headingPrevisto == H_S) { a = H_E; b = H_O; }
        else { a = H_N; b = H_S; }
        byte elegido = _aiRng.Next(2) == 0 ? a : b;

        for (int intento = 0; intento < 2; intento++)
        {
            int nx = n.X, ny = n.Y;
            switch (elegido) { case H_N: ny--; break; case H_E: nx++; break; case H_S: ny++; break; case H_O: nx--; break; }
            // Solo si desde el costado SIGUE habiendo camino al mismo objetivo: no vale desviarse
            // a un callejón sin salida ni cruzar a nado por atajar.
            if (PuedeNpc(map, nx, ny, n.AguaValida, n.TierraInvalida) && UserAtTile(map, nx, ny) == 0
                && BotPathing.NextHeading(map, nx, ny, tx, ty, agua: true) != 0)
            {
                byte ax = n.X, ay = n.Y;
                EmbarcarSiElPasoEsAgua(map, n, elegido);
                MoveNpcChar(map, n, elegido);
                return n.X != ax || n.Y != ay;
            }
            elegido = elegido == a ? b : a;
        }
        return false;
    }

    /// <summary>
    /// El bot de guerra se pelea con la criatura hostil que se le cruce en el camino. Devuelve true
    /// si hizo algo (pegar o acercarse). Ojo con el alcance: solo criaturas SALVAJES y hostiles —
    /// nada de mercaderes, sacerdotes ni mascotas, que arruinaría las ciudades por las que pasa.
    /// </summary>
    private static bool AtacarCriaturaCercana(int map, NpcInstance n)
    {
        var presa = NearestEnemyNpcForBot(map, n);
        if (presa == null) return false;
        // Solo lo que ya es hostil y atacable: un ejército no asesina NPCs de servicio al pasar.
        if (!presa.Hostil || !presa.Attackable) return false;

        int d = Math.Abs(presa.X - n.X) + Math.Abs(presa.Y - n.Y);
        bool esCaster = n.Spells != null && n.Spells.Length > 0;
        if (d <= 1)
        {
            // De vez en cuando se reposiciona (no queda "duro" pegado al enemigo).
            if (_aiRng.Next(4) == 0 && TryStepRandom(map, n)) return true;
            FaceTarget(map, n, presa.X, presa.Y);
            MarcarCombate(n, presa);
            if (esCaster && _aiRng.Next(2) == 0) Combat.NpcLanzaSpellANpc(n, presa);
            else { NpcAtacaNpc(map, n, presa); if (n.BotAtaqueParticula > 0) Combat.ParticulaEnChar(map, presa.CharIndex, n.BotAtaqueParticula); }
            return true;
        }
        if (esCaster && _aiRng.Next(2) == 0)
        {
            FaceTarget(map, n, presa.X, presa.Y);
            if (Combat.NpcLanzaSpellANpc(n, presa)) { MarcarCombate(n, presa); return true; }
        }
        // Acercarse solo si está REALMENTE cerca: si no, el ejército se desviaría por cada bicho
        // que asome en el borde del rango de visión y no llegaría nunca a la ciudad enemiga.
        if (d > 4) return false;
        if (StepToward(map, n, presa.X, presa.Y) == 0) return false;
        return true;
    }

    /// <summary>
    /// Si el próximo tile en esa dirección es agua y el bot todavía no está en barca, lo embarca
    /// AHORA (cuerpo de barca, sin equipo visible). Así nunca se lo ve caminando sobre el agua.
    /// </summary>
    private static void EmbarcarSiElPasoEsAgua(int map, NpcInstance n, byte heading)
    {
        int nx = n.X, ny = n.Y;
        switch (heading) { case H_N: ny--; break; case H_E: nx++; break; case H_S: ny++; break; case H_O: nx--; break; default: return; }
        var md = MapLoader.Get(map);
        if (md == null) return;
        bool destinoAgua = md.HasWater(nx, ny);

        if (destinoAgua && !n.EnBarca)
        {
            n.EnBarca = true;
            n.Body = BOAT_BODY; n.WeaponAnim = 0; n.ShieldAnim = 0; n.CascoAnim = 0;
            BroadcastNpcAppearance(map, n);
        }
        else if (!destinoAgua && n.EnBarca)
        {
            // Simétrico: bajarse ANTES de pisar tierra, para no ver una barca arriba del pasto.
            n.EnBarca = false;
            n.Body = n.LandBody; n.WeaponAnim = n.LandWeapon; n.ShieldAnim = n.LandShield; n.CascoAnim = n.LandCasco;
            BroadcastNpcAppearance(map, n);
        }
    }

    /// <summary>Marcha del bot de guerra hacia su objetivo: cruzar mapas por TileExits y llegar al tile destino.</summary>
    /// <summary>
    /// Cruza mapas hacia n.GuerraDestMap/X/Y (BotPathing), patrulla un rato al llegar
    /// (GUERRA_ESTADIA) y elige destino nuevo. asignarObjetivo decide CUÁL es el próximo destino
    /// cuando hace falta uno — default GuerraFacciones.AsignarObjetivo (ciudad enemiga, bots de
    /// guerra); los bots "progresivos" (TickBotLeveling) pasan AsignarCiudadLeveling para ciclar
    /// entre las 3 ciudades en vez de perseguir una facción. El resto de la función (cruce de
    /// mapas, patrulla, reintento sin ruta) es 100% genérico, sin cambios de comportamiento para
    /// los bots de guerra existentes.
    /// </summary>
    private static void ViajarGuerra(int map, NpcInstance n, Action<NpcInstance> asignarObjetivo = null)
    {
        asignarObjetivo ??= GuerraFacciones.AsignarObjetivo;
        if (n.GuerraDestMap <= 0) { asignarObjetivo(n); if (n.GuerraDestMap <= 0) return; }
        double now = Environment.TickCount64 / 1000.0;

        // Ya está en el mapa objetivo.
        if (map == n.GuerraDestMap)
        {
            int d = Math.Abs(n.X - n.GuerraDestX) + Math.Abs(n.Y - n.GuerraDestY);
            if (d > 3)
            {
                PasoDeGuerra(map, n, n.GuerraDestX, n.GuerraDestY);
                return;
            }
            // Llegó: patrulla la ciudad un rato y después se va a buscar otra.
            if (n.GuerraLlegadaAt == 0) n.GuerraLlegadaAt = now;
            if (now - n.GuerraLlegadaAt > GUERRA_ESTADIA) { asignarObjetivo(n); return; }
            TryStepRandom(map, n);
            return;
        }

        // Otro mapa: ir hasta la salida que acerca al objetivo.
        if (!BotPathing.SalidaHacia(map, n.X, n.Y, n.GuerraDestMap, agua: true, out var salida))
        {
            // Sin ruta conocida desde acá (mapa aislado, o la salida quedó del otro lado de un
            // bloqueo): elegir otro objetivo en vez de quedarse duro para siempre.
            asignarObjetivo(n);
            TryStepRandom(map, n);
            return;
        }

        if (n.X == salida.X && n.Y == salida.Y)
        {
            // Parado en el TileExit: cruzar. El warp se aplica al final del tick (ApplyPendingBotWarps).
            QueueBotWarpTo(n, salida.DestMap, salida.DestX, salida.DestY);
            n.GuerraStuck = 0;
            return;
        }
        PasoDeGuerra(map, n, salida.X, salida.Y);
    }

    // Mapas de las 3 ciudades principales (GuerraFacciones.cs: CIUDAD_ARMADA=3→Ullathorpe,
    // CIUDAD_REPUBLICA=2→Illiandor, CIUDAD_CAOS=5→Rinkel).
    private static readonly int[] CIUDADES_LEVELING = { 1, 194, 20 };

    /// <summary>asignarObjetivo para bots "progresivos" (TickBotLeveling): en vez de perseguir a
    /// una facción enemiga, elige al azar una de las 3 ciudades principales — así, si no hay
    /// NPCs cerca (por ejemplo recién invocado dentro de un dungeon newbie), salen a recorrer el
    /// mundo en vez de quedarse dando vueltas en el mismo mapa.</summary>
    private static void AsignarCiudadLeveling(NpcInstance n)
    {
        int destMap = CIUDADES_LEVELING[_aiRng.Next(CIUDADES_LEVELING.Length)];
        var (dx, dy) = GuerraFacciones.TileLibreCerca(destMap, 50, 50, 45);
        if (dx == 0) return; // sin tile libre encontrado: se reintenta el próximo tick
        n.GuerraDestMap = destMap; n.GuerraDestX = dx; n.GuerraDestY = dy;
        n.GuerraLlegadaAt = 0;
    }

    /// <summary>
    /// Un paso del bot de guerra hacia (tx,ty) usando el campo de flujo del mapa. Si el paso no se
    /// concreta (otro bot en el tile, puerta, etc.) cuenta el atasco y se sacude con un paso al azar.
    /// </summary>
    /// <summary>
    /// Si hay una puerta cerrada sin llave en cualquiera de los 2 tiles adyacentes que apuntan
    /// hacia (tx,ty) (el eje X y el eje Y por separado), la abre. A propósito NO usa PuedeNpc/
    /// FindDirection: esos EVITAN los tiles bloqueados —incluida una puerta cerrada— eligiendo un
    /// desvío, así que nunca "eligen" ir hacia la puerta para poder detectarla. Acá se mira
    /// directo si hay una puerta ahí, sin importar que ahora mismo esté bloqueada.
    /// </summary>
    private static void AbrirPuertaCercaDelCamino(int map, NpcInstance n, int tx, int ty)
    {
        int sx = Math.Sign(tx - n.X), sy = Math.Sign(ty - n.Y);
        if (sx != 0) AbrirPuertaSiBloquea(map, n, sx > 0 ? H_E : H_O);
        if (sy != 0) AbrirPuertaSiBloquea(map, n, sy > 0 ? H_S : H_N);
    }

    private static void PasoDeGuerra(int map, NpcInstance n, int tx, int ty)
    {
        byte antesX = n.X, antesY = n.Y;

        // Si el bot tiene una puerta cerrada sin llave justo camino a (tx,ty), abrirla ANTES de
        // pedirle el heading al campo de flujo: ese campo es estático (calculado una vez con la
        // puerta cerrada) y nunca "elige" cruzarla — sin esto un bot de guerra quedaba trabado
        // para siempre contra cualquier puerta cerrada.
        AbrirPuertaCercaDelCamino(map, n, tx, ty);

        // Romper la FILA INDIA: el campo de flujo da el camino más corto, así que todos los que van
        // al mismo lado pisaban exactamente los mismos tiles y se veían marchando en una línea
        // perfecta. Cada tanto el bot da un paso al costado (perpendicular al que le tocaba) si el
        // tile está libre: el rumbo general no cambia —el campo de flujo lo recupera en el paso
        // siguiente— pero la tropa se ve desparramada y no clonada.
        byte h = BotPathing.NextHeading(map, n.X, n.Y, tx, ty, agua: true);
        if (h != 0 && _aiRng.Next(4) == 0 && PasoAlCostado(map, n, h, tx, ty)) { n.GuerraStuck = 0; return; }
        // Embarcar ANTES de pisar el agua, no después: reconciliar sobre el tile actual dejaba
        // un tick (380ms) en el que se veía al bot parado sobre el agua en persona. Ahora, si el
        // próximo tile es agua, la barca aparece en el mismo paquete que el paso.
        if (h != 0) EmbarcarSiElPasoEsAgua(map, n, h);
        if (h != 0) MoveNpcChar(map, n, h);
        else
        {
            // El campo de flujo no encontró camino (probablemente una puerta cerrada de por medio,
            // ya intentada arriba, o simplemente el destino está detrás de un obstáculo que el BFS
            // estático no rodea). Fallback: BFS PROPIO de este bot, que SÍ sabe abrir puertas.
            byte hp = SeekPathHeading(map, n, tx, ty, 30, puertasAbribles: true);
            if (hp == 0) hp = FindDirection(map, n, tx, ty);
            if (hp != 0)
            {
                AbrirPuertaSiBloquea(map, n, hp);
                EmbarcarSiElPasoEsAgua(map, n, hp);
                MoveNpcChar(map, n, hp);
            }
            else { n.GuerraStuck++; TryStepRandom(map, n); return; }
        }

        if (n.X == antesX && n.Y == antesY)
        {
            n.GuerraStuck++;
            if (n.GuerraStuck >= GUERRA_STUCK_MAX)
            {
                // Trabado de verdad: sacudirse; si ni eso sale (spawn dentro de una pared, cuarto
                // cerrado), cambiar de objetivo y saltar a un tile libre cercano para no quedar
                // de estatua para siempre.
                n.GuerraStuck = 0;
                if (!TryStepRandom(map, n))
                {
                    GuerraFacciones.AsignarObjetivo(n);
                    var (rx, ry) = GuerraFacciones.TileLibreCerca(map, n.X, n.Y, 12);
                    if (rx != 0) QueueBotWarpTo(n, map, rx, ry);
                }
            }
        }
        else n.GuerraStuck = 0;
    }

    private static void TickBot(int map, NpcInstance n)
    {
        // Autopot del bot (potas infinitas): cura vida si le pegaste, recupera maná si castea. Suena al beber.
        // Mismo mecanismo para TODOS los bots, incluido el prototipo inteligente (BotSmart) de abajo:
        // no hay una poción "especial" para él, es la misma que ya usan los otros 4 modos.
        BotAutoPot(n);

        // Bot inteligente (prototipo, Utility AI): un único bot puede tener este flag. Va ANTES que
        // los demás modos para que quede completamente aislado (nunca cae en TickBotFaccion/Guerra/etc).
        if (n.BotSmart) { TickBotSmart(map, n); return; }

        // Bots de la GUERRA MUNDIAL: recorren el mundo entero buscando a la facción enemiga.
        if (n.BotGuerra) { TickBotGuerra(map, n); return; }

        // Bot "progresivo": busca y mata NPCs salvajes (y rivales de facción, si tiene una) para
        // subir de nivel (Bots.DarExpABot). Va ANTES que el chequeo de BotFaccion de abajo: un
        // bot progresivo puede tener facción asignada (población del mundo, Bots.PoblarMundo) y
        // tiene que seguir cazando/subiendo de nivel, no caer en el comportamiento de ejército
        // fijo de TickBotFaccion.
        if (n.BotLeveling) { TickBotLeveling(map, n); return; }

        // Bots de evento de facción: IA autónoma (atacan a la facción enemiga, deambulan, taunt).
        if (n.BotFaccion > 0)
        {
            // Nadie habla hasta que la otra facción aparece Y empieza la pelea: los taunts random sólo
            // salen cuando la batalla ya cargó (durante el parley sólo hablan los 2 campeones).
            if (_battles.TryGetValue(map, out var bch) && bch.Charged && _aiRng.Next(8) == 0) ShoutTaunt(map, n);
            TickBotFaccion(map, n);
            return;
        }

        // Bot de sparring PvP: pelea contra su PROPIO dueño (para testear PvP).
        if (n.BotSpar) { TickBotSpar(map, n); return; }

        var owner = (n.OwnerUserIndex > 0 && n.OwnerUserIndex <= UserListManager.LastUser)
                    ? UserListManager.UserList[n.OwnerUserIndex] : null;

        // Barca: subir/bajar según el dueño (antes de moverse, para poder pisar agua).
        ReconcileBoat(map, n, owner);

        // Prioridad: si el dueño cambió de mapa, TODOS los bots lo siguen (warp diferido).
        if (owner != null && owner.flags.UserLogged && owner.Pos.Map != n.Map)
        { var (wx, wy) = BotFollowTile(n, owner); QueueBotWarpTo(n, owner.Pos.Map, wx, wy); return; }

        if (n.BotAtacar)
        {
            // Objetivo más cercano entre usuarios (no el dueño) y NPCs/bots/mascotas rivales — mismo
            // pool de siempre. Una vez resuelto, TODO el combate lo decide/ejecuta TickCombateBot (el
            // cerebro estándar, ex-exclusivo de BotSmart): misma Utility AI, flanqueo, hechizos, etc.
            var userTgt = NearestUserBot(n, map, n.X, n.Y);
            var npcTgt  = NearestEnemyNpcOrRivalForBot(map, n);
            int dU = userTgt != null ? Math.Abs(userTgt.Pos.X - n.X) + Math.Abs(userTgt.Pos.Y - n.Y) : int.MaxValue;
            int dN = npcTgt  != null ? Math.Abs(npcTgt.X - n.X) + Math.Abs(npcTgt.Y - n.Y) : int.MaxValue;
            bool targetIsUser = userTgt != null && dU <= dN;
            bool hayObjetivo = targetIsUser ? userTgt != null : npcTgt != null;
            if (hayObjetivo && TickCombateBot(map, n, userTgt, npcTgt, targetIsUser)) return;
        }

        // Sin objetivo (o pasivo): seguir al dueño (en fila si está formado). Si quedó lejos, teletransportar.
        if (owner != null && owner.flags.UserLogged && owner.flags.Muerto == 0)
        {
            var (tx, ty) = BotFollowTile(n, owner);
            int dx = Math.Abs(tx - n.X), dy = Math.Abs(ty - n.Y);
            int thresh = n.FormSlot >= 0 ? 0 : 1;   // en fila: ocupar la celda exacta; normal: con estar a ≤1 alcanza
            if (dx > BOT_FOLLOW_TELEPORT || dy > BOT_FOLLOW_TELEPORT) QueueBotWarpTo(n, owner.Pos.Map, tx, ty);
            else if (dx > thresh || dy > thresh) StepToward(map, n, tx, ty);
        }
    }

    // Acción vigente de TickBotSmart. SÍ es estado persistente (a diferencia del comentario viejo):
    // se re-puntúa sólo cada SmartDecisionIntervalSeconds; entre una decisión y la siguiente, el
    // movimiento la sigue ejecutando tick a tick a la cadencia de BotIntervalSmart. Ver TickBotSmart.
    private enum SmartAction : byte { None = 0, Attack, CastSpell, Chase, Retreat, Reposition, HelpAlly, Explore, Return }

    // ============================ Flanqueo táctico del BotSmart (NUEVO) ============================
    // Los 8 puntos cardinales relativos a un objetivo, para que el bot pueda "elegir un ángulo" en
    // vez de pegarse siempre al mismo lado. NpcInstance.LastCombatSide guarda el último usado.
    private enum CombatSide : byte { None = 0, N, NE, E, SE, S, SW, W, NW }

    private static readonly (sbyte dx, sbyte dy, CombatSide side)[] FLANCO_DIRS =
    {
        (0, -1, CombatSide.N), (1, -1, CombatSide.NE), (1, 0, CombatSide.E), (1, 1, CombatSide.SE),
        (0, 1, CombatSide.S), (-1, 1, CombatSide.SW), (-1, 0, CombatSide.W), (-1, -1, CombatSide.NW),
    };

    private static CombatSide SideFromDelta(int dx, int dy) => (Math.Sign(dx), Math.Sign(dy)) switch
    {
        (0, -1) => CombatSide.N, (1, -1) => CombatSide.NE, (1, 0) => CombatSide.E, (1, 1) => CombatSide.SE,
        (0, 1) => CombatSide.S, (-1, 1) => CombatSide.SW, (-1, 0) => CombatSide.W, (-1, -1) => CombatSide.NW,
        _ => CombatSide.None,
    };

    // Cuánto tiempo penaliza seguir atacando/casteando desde el mismo lado (memoria de flanco).
    private const double SIDE_MEMORY_SECONDS = 8.0;

    /// <summary>¿El objetivo (usuario o NPC/bot) está paralizado o inmovilizado ahora mismo?</summary>
    private static bool ObjetivoInmovilizado(bool targetIsUser, User userTgt, NpcInstance npcTgt)
    {
        if (targetIsUser) return userTgt != null && (userTgt.flags.Paralizado == 1 || userTgt.flags.Inmovilizado == 1);
        double now = Environment.TickCount64 / 1000.0;
        return npcTgt != null && (npcTgt.ParalizadoHasta > now || npcTgt.InmovilizadoHasta > now);
    }

    /// <summary>
    /// Evalúa las 8 direcciones alrededor de (tx,ty) a distancia `radio` (1 = pegado/melee; más para
    /// un caster que quiere mantener distancia) y devuelve la de mejor puntaje SI mejora con
    /// claridad la posición actual del bot — null si ninguna vale la pena (así no "gira" sin motivo
    /// real). Reusa PuedeNpc/UserAtTile/NpcAt para validar: las MISMAS reglas de colisión que
    /// cualquier otro movimiento del juego — esto sólo ELIGE el destino, caminar hasta ahí lo sigue
    /// haciendo StepToward/SeekPathHeading en el llamador (TickBotSmart), sin pathfinding propio.
    /// </summary>
    private static (int x, int y, CombatSide side)? MejorFlanco(int map, NpcInstance n, int tx, int ty, int radio)
    {
        // Puntaje de UNA posición candidata: penaliza seguir en el mismo lado que ya viene usando
        // (memoria de flanco), el costo de caminar hasta ahí (preferir el candidato más barato), y
        // amontonarse con un aliado que ya cubre ese ángulo o exponerse a OTRO hostil distinto del
        // objetivo (no sólo el propio, evita "flanquear" hacia el rango de un tercero).
        float ScorePosicion(int cx, int cy, CombatSide side, int costoMovimiento)
        {
            float s = 30f;
            if (side == (CombatSide)n.LastCombatSide && n.LastCombatSideAt > 0
                && Environment.TickCount64 / 1000.0 - n.LastCombatSideAt < SIDE_MEMORY_SECONDS)
                s -= 22f;
            s -= costoMovimiento * 2.5f;
            if (_byMap.TryGetValue(map, out var lista))
            {
                foreach (var o in lista)
                {
                    if (o == n || o.Dead) continue;
                    int dTile = Math.Max(Math.Abs(o.X - cx), Math.Abs(o.Y - cy));
                    if (dTile > 1) continue;
                    bool esAliado = o.IsBot && ((n.OwnerUserIndex > 0 && o.OwnerUserIndex == n.OwnerUserIndex)
                                                 || (n.BotFaccion > 0 && o.BotFaccion == n.BotFaccion));
                    if (esAliado) s -= 10f;                                    // ya hay un aliado cubriendo ese ángulo
                    else if (o.Hostil || o.BotFaccion > 0 || o.BotAtacar) s -= 15f; // otro hostil distinto del objetivo, cerca
                }
            }
            return s;
        }

        float scoreActual = ScorePosicion(n.X, n.Y, SideFromDelta(n.X - tx, n.Y - ty), 0);
        (int x, int y, CombatSide side, float score)? mejor = null;

        foreach (var (ddx, ddy, side) in FLANCO_DIRS)
        {
            int cx = tx + ddx * radio, cy = ty + ddy * radio;
            if (cx == n.X && cy == n.Y) continue; // ya está exactamente ahí
            if (!PuedeNpc(map, cx, cy, n.AguaValida, n.TierraInvalida)) continue;
            if (UserAtTile(map, cx, cy) > 0) continue;
            var ocupante = NpcAt(map, cx, cy);
            if (ocupante != null && ocupante != n) continue;

            int costo = Math.Abs(cx - n.X) + Math.Abs(cy - n.Y);
            float s = ScorePosicion(cx, cy, side, costo);
            if (mejor == null || s > mejor.Value.score) mejor = (cx, cy, side, s);
        }

        // Umbral: sólo vale la pena moverse si la mejora es clara, no por una diferencia de ruido.
        const float UMBRAL_MEJORA = 12f;
        if (mejor != null && mejor.Value.score - scoreActual >= UMBRAL_MEJORA)
            return (mejor.Value.x, mejor.Value.y, mejor.Value.side);
        return null;
    }

    // Cadencia de "pensar" del BotSmart: cada cuánto se re-puntúan las 8 acciones. Deliberadamente
    // IGUAL al AiIntervalSeconds de siempre (el ritmo de "cerebro" no cambió) — lo que cambió es que
    // ahora el MOVIMIENTO (StepToward/TryStepRandom) se ejecuta en cada invocación de TickBotSmart,
    // que pasa mucho más seguido (BotIntervalSmart), en vez de una vez por decisión.
    private const double SmartDecisionIntervalSeconds = AiIntervalSeconds; // 0.38s, sin cambios

    // Cadencia de MOVIMIENTO/invocación del BotSmart (IntervaloBot lo usa en vez de BotAiIntervalSeconds).
    // Misma técnica exacta que BotIntervalMontado/BotIntervalVolando (32000/velocidad del cliente):
    // el cliente YA anima a este bot en particular a PLAYER_SPEED_PXS (game.html, velocidadDePersonaje,
    // gracias al marcador de protocolo "#*nick"/isBotSmart) en vez de NPC_SPEED_PXS — mandarle un
    // CharacterMove cada ~200,9ms es lo que mantiene al cliente sin cola (mandar más rápido la
    // acumularía; más lento, el cliente llega y queda esperando inactivo — el bug reportado).
    private const double BotIntervalSmart = 32.0 / 159.3; // ~200.9ms == 32000 / PLAYER_SPEED_PXS

    /// <summary>
    /// CEREBRO DE COMBATE ESTÁNDAR de todos los bots (25-ago-2026: dejó de ser exclusivo de
    /// BotSmart — ahora TickBotGuerra/TickBotFaccion (vía CombateDeFaccion), TickBotLeveling,
    /// TickBotSpar y la rama BotAtacar genérica delegan acá). El llamador sólo resuelve el POOL de
    /// candidatos válido para su propia actividad (Guerra/Facción = sólo la facción enemiga,
    /// Leveling = NPCs salvajes + rivales, Spar = sólo el dueño, genérico/Smart = cualquiera menos
    /// el dueño) — quién es un objetivo LEGAL sigue siendo parte de la actividad de cada modo, no
    /// del combate. Una vez resuelto un candidato, TODO lo demás es idéntico para cualquier bot:
    /// - Objetivo ya resuelto por el llamador (userTgt/npcTgt/targetIsUser).
    /// - Golpe/hechizo: Combat.NpcAtacaUsuario/NpcLanzaSpell (y sus versiones "ANpc"), que YA
    ///   gatean el intervalo real de ataque/casteo (Intervals.PuedeAtacarNpc) — llamarlos todos los
    ///   ticks es seguro: si el cooldown no venció, simplemente no pegan, la IA no puede "forzarlo".
    /// - Poción: BotAutoPot(n), invocado sin condiciones al principio de TickBot para TODOS los
    ///   modos por igual desde antes de esta unificación — no hay nada que tocar acá.
    /// - Movimiento: StepToward (BFS/SeekPathHeading) y TryStepRandom, iguales para cualquier bot.
    /// - Flanqueo de 8 posiciones (MejorFlanco), memoria de lado (LastCombatSide), aprovechamiento
    ///   de objetivo inmovilizado, ayuda a aliado (con curación real si el bot tiene BotHealSpell,
    ///   ver más abajo) y retirada por HP bajo: todo lo que antes sólo tenía BotSmart.
    ///
    /// Devuelve false si el objetivo que le pasaron ya no es válido (murió/desconectó entre que el
    /// llamador lo encontró y este método corrió) — el llamador entonces hace su propia actividad de
    /// "sin objetivo" (viajar, deambular, parley, etc., que sigue siendo específica de cada modo).
    ///
    /// DECISIÓN vs MOVIMIENTO desacoplados: la parte cara (8 puntajes + flanqueo) sólo corre cada
    /// SmartDecisionIntervalSeconds (~380ms, gateado por n.SmartDecisionNextAt); el resto de los
    /// ticks simplemente sigue ejecutando la MISMA acción con el objetivo recalculado en fresco. Para
    /// BotSmart esto importa (se invoca cada BotIntervalSmart~200ms); para los demás modos, que
    /// siguen tickeando a SU intervalo de siempre (sin cambios — no se tocó ningún intervalo), el gate
    /// simplemente se cumple en cada llamada y deciden en cada tick, como siempre hicieron.
    /// </summary>
    private static bool TickCombateBot(int map, NpcInstance n, User userTgt, NpcInstance npcTgt, bool targetIsUser)
    {
        if (targetIsUser) { if (userTgt == null || !userTgt.flags.UserLogged || userTgt.flags.Muerto != 0) return false; }
        else { if (npcTgt == null || npcTgt.Dead) return false; }

        double now = Environment.TickCount64 / 1000.0;
        bool esCaster = n.Spells != null && n.Spells.Length > 0 && !n.BotSparSoloMelee;
        float hpPct = n.MaxHP > 0 ? (float)n.MinHP / n.MaxHP : 1f;
        float manaPct = n.MaxMana > 0 ? (float)n.MinMana / n.MaxMana : 0f;
        int distTarget = targetIsUser
            ? Math.Abs(userTgt.Pos.X - n.X) + Math.Abs(userTgt.Pos.Y - n.Y)
            : Math.Abs(npcTgt.X - n.X) + Math.Abs(npcTgt.Y - n.Y);
        bool adyacente = distTarget <= 1;
        int tgtX = targetIsUser ? userTgt.Pos.X : npcTgt.X, tgtY = targetIsUser ? userTgt.Pos.Y : npcTgt.Y;
        // Inmovilizado (no paralizado): puede golpear/castear pero NO caminar. Antes sólo lo
        // respetaba TickBotSpar a mano; ahora es parte del cerebro compartido, para todos por igual.
        bool inmovilPropio = n.InmovilizadoHasta > now;

        NpcInstance aliadoHerido = null;
        // Fuerza una decisión nueva si la acción vigente no es una de combate (p.ej. venía de
        // deambular/explorar sin objetivo): evita quedarse un tick sin actuar al recién encontrar
        // un objetivo mientras n.SmartLastAction todavía apuntaba a Explore/Return/None.
        bool accionVigenteEsDeCombate = n.SmartLastAction >= 1 && n.SmartLastAction <= 6; // Attack..HelpAlly
        if (now >= n.SmartDecisionNextAt || !accionVigenteEsDeCombate)
        {
            n.SmartDecisionNextAt = now + SmartDecisionIntervalSeconds;
            n.FlancoX = 0; n.FlancoY = 0; // se re-arma esta decisión si corresponde

            // Aliados/enemigos en visión (un solo recorrido del mapa): "aliado" = mismo dueño o misma
            // facción de bot; el resto de bots hostiles/de facción cuentan como "enemigos cerca" para
            // escalar la urgencia de retirada (1 rival no asusta, 3 sí).
            int enemigosCerca = 0;
            float peorHpAliado = 1f;
            if (_byMap.TryGetValue(map, out var listaMapa))
            {
                foreach (var o in listaMapa)
                {
                    if (o == n || o.Dead || !o.IsBot) continue;
                    if (Math.Abs(o.X - n.X) > RANGO_VISION_X || Math.Abs(o.Y - n.Y) > RANGO_VISION_Y) continue;
                    bool mismoBando = (n.OwnerUserIndex > 0 && o.OwnerUserIndex == n.OwnerUserIndex) ||
                                       (n.BotFaccion > 0 && o.BotFaccion == n.BotFaccion);
                    if (mismoBando)
                    {
                        float hpAl = o.MaxHP > 0 ? (float)o.MinHP / o.MaxHP : 1f;
                        if (hpAl < peorHpAliado) { peorHpAliado = hpAl; aliadoHerido = o; }
                    }
                    else if (o.Hostil || o.BotFaccion > 0 || o.BotAtacar) enemigosCerca++;
                }
            }

            // Ruido chico (±15%) en cada puntaje: la misma situación no produce SIEMPRE la misma
            // decisión, sin que la decisión deje de tener sentido (variación humana, no aleatoriedad pura).
            float R() => 0.85f + (float)_aiRng.NextDouble() * 0.30f;

            float sAttack = 0, sCast = 0, sChase = 0, sRetreat = 0, sReposition = 0, sHelp = 0;
            bool objetivoInmovil = ObjetivoInmovilizado(targetIsUser, userTgt, npcTgt);

            if (adyacente)
            {
                sAttack = (40 + n.PersAgresividad * 0.4f) * R();
                if (esCaster && manaPct > 0.15f) sCast = (30 + n.PersHechizo * 0.5f) * R();
                // Más pasivo (poca agresividad) reposiciona más seguido en vez de quedar "duro" pegado.
                sReposition = (8 + (100 - n.PersAgresividad) * 0.12f) * R();
                // Flanqueo táctico: ¿conviene atacar desde OTRO lado? (memoria de flanco, aliados/
                // enemigos cercanos, distancia). Sólo sube el puntaje si MejorFlanco encontró una
                // posición realmente mejor — si no, sReposition queda en su valor base de siempre.
                if (!inmovilPropio)
                {
                    var flanco = MejorFlanco(map, n, tgtX, tgtY, 1);
                    if (flanco != null)
                    {
                        n.FlancoX = (short)flanco.Value.x; n.FlancoY = (short)flanco.Value.y;
                        // El objetivo inmovilizado no puede perseguirlo mientras se reposiciona: es
                        // el momento ideal para cambiar de ángulo, así que pesa más.
                        sReposition = (30 + (100 - n.PersAgresividad) * 0.15f + (objetivoInmovil ? 22f : 0f)) * R();
                    }
                }
            }
            else
            {
                // A distancia (no adyacente): un caster con maná y el objetivo dentro de rango de
                // hechizo NO debería tener que cerrar distancia primero — tira desde lejos.
                bool puedeCastearYa = esCaster && manaPct > 0.2f && distTarget <= RANGO_VISION_X;
                if (puedeCastearYa)
                {
                    sCast = (45 + n.PersHechizo * 0.45f) * R();
                    sChase = (15 + n.PersPersecucion * 0.25f - Math.Min(n.SmartChaseTicks, 20) * 1.2f) * R();
                    // Caster demasiado cerca (2 tiles): busca un ángulo a distancia de hechizo en vez
                    // de quedarse ahí o seguir cerrando — "no debe entrar cuerpo a cuerpo".
                    if (distTarget <= 2 && !inmovilPropio)
                    {
                        var flanco = MejorFlanco(map, n, tgtX, tgtY, Math.Min(3, RANGO_VISION_X));
                        if (flanco != null)
                        {
                            n.FlancoX = (short)flanco.Value.x; n.FlancoY = (short)flanco.Value.y;
                            sReposition = (25 + n.PersCautela * 0.3f + (objetivoInmovil ? 15f : 0f)) * R();
                        }
                    }
                }
                else
                {
                    sChase = (35 + n.PersPersecucion * 0.4f - Math.Min(n.SmartChaseTicks, 20) * 1.2f) * R();
                }
            }

            // Umbral de retirada propio (15%..~75% HP según cuán cauteloso es) — sube con enemigos cerca.
            float umbralRetirada = 0.15f + n.PersCautela * 0.006f;
            if (hpPct < umbralRetirada) sRetreat = (55 + (umbralRetirada - hpPct) * 150 + enemigosCerca * 12) * R();

            // Ayudar a un aliado herido: sólo si el propio bot no está también en apuros.
            if (aliadoHerido != null && peorHpAliado < 0.4f && hpPct > 0.3f) sHelp = (25 + n.PersAyuda * 0.5f) * R();

            // Histéresis: la acción de MOVIMIENTO que ya venía ejecutando suma un bonus, para que el
            // ruido ±15% no la haga "temblar" entre dos casi-empatadas decisión a decisión. Attack/
            // CastSpell/Retreat NO llevan bonus a propósito: reaccionan de inmediato.
            const float STICKY_BONUS = 18f;
            switch ((SmartAction)n.SmartLastAction)
            {
                case SmartAction.Chase:    sChase += STICKY_BONUS; break;
                case SmartAction.HelpAlly: sHelp  += STICKY_BONUS; break;
            }

            SmartAction best = SmartAction.None; float bestScore = 0f;
            void Consider(SmartAction a, float s) { if (s > bestScore) { bestScore = s; best = a; } }
            Consider(SmartAction.Attack, sAttack); Consider(SmartAction.CastSpell, sCast);
            Consider(SmartAction.Chase, sChase);   Consider(SmartAction.Retreat, sRetreat);
            Consider(SmartAction.Reposition, sReposition); Consider(SmartAction.HelpAlly, sHelp);
            n.SmartLastAction = (byte)best;
        }

        switch ((SmartAction)n.SmartLastAction)
        {
            case SmartAction.Attack:
                n.SmartChaseTicks = 0;
                if (targetIsUser)
                {
                    FaceTarget(map, n, userTgt.Pos.X, userTgt.Pos.Y);
                    n.LastCombatSide = (byte)SideFromDelta(n.X - userTgt.Pos.X, n.Y - userTgt.Pos.Y); n.LastCombatSideAt = now;
                    MarcarCombate(n, null);
                    Combat.NpcAtacaUsuario(n, userTgt.id);
                    if (n.BotAtaqueParticula > 0) Combat.ParticulaEnChar(map, (short)userTgt.Char.CharIndex, n.BotAtaqueParticula);
                }
                else
                {
                    FaceTarget(map, n, npcTgt.X, npcTgt.Y);
                    n.LastCombatSide = (byte)SideFromDelta(n.X - npcTgt.X, n.Y - npcTgt.Y); n.LastCombatSideAt = now;
                    MarcarCombate(n, npcTgt);
                    NpcAtacaNpc(map, n, npcTgt);
                    if (n.BotAtaqueParticula > 0) Combat.ParticulaEnChar(map, npcTgt.CharIndex, n.BotAtaqueParticula);
                }
                break;

            case SmartAction.CastSpell:
                n.SmartChaseTicks = 0;
                if (targetIsUser)
                {
                    FaceTarget(map, n, userTgt.Pos.X, userTgt.Pos.Y);
                    n.LastCombatSide = (byte)SideFromDelta(n.X - userTgt.Pos.X, n.Y - userTgt.Pos.Y); n.LastCombatSideAt = now;
                    MarcarCombate(n, null);
                    if (!Combat.NpcLanzaSpell(n, userTgt.id) && !adyacente && !inmovilPropio) StepToward(map, n, userTgt.Pos.X, userTgt.Pos.Y);
                }
                else
                {
                    FaceTarget(map, n, npcTgt.X, npcTgt.Y);
                    n.LastCombatSide = (byte)SideFromDelta(n.X - npcTgt.X, n.Y - npcTgt.Y); n.LastCombatSideAt = now;
                    MarcarCombate(n, npcTgt);
                    if (!Combat.NpcLanzaSpellANpc(n, npcTgt) && !adyacente && !inmovilPropio) StepToward(map, n, npcTgt.X, npcTgt.Y);
                }
                break;

            case SmartAction.Chase:
                n.SmartChaseTicks++;
                if (!inmovilPropio)
                {
                    if (targetIsUser) StepToward(map, n, userTgt.Pos.X, userTgt.Pos.Y);
                    else StepToward(map, n, npcTgt.X, npcTgt.Y);
                }
                break;

            case SmartAction.Retreat:
                n.SmartChaseTicks = 0;
                if (!inmovilPropio)
                {
                    int fx = Math.Clamp(n.X + Math.Sign(n.X - tgtX) * 3, 1, 99);
                    int fy = Math.Clamp(n.Y + Math.Sign(n.Y - tgtY) * 3, 1, 99);
                    StepToward(map, n, fx, fy);
                }
                break;

            case SmartAction.Reposition:
                n.SmartChaseTicks = 0;
                if (inmovilPropio) { /* no puede moverse: espera a la próxima decisión */ }
                else if (n.FlancoX != 0 || n.FlancoY != 0)
                {
                    // Con flanco elegido (MejorFlanco): camina ahí con el pathing normal (StepToward/
                    // SeekPathHeading), NO un paso suelto. Al llegar (o sin camino), suelta el flanco.
                    if (StepToward(map, n, n.FlancoX, n.FlancoY) == 0) { n.FlancoX = 0; n.FlancoY = 0; }
                }
                else if (!TryStepRandom(map, n) && targetIsUser) Combat.NpcAtacaUsuario(n, userTgt.id);
                break;

            case SmartAction.HelpAlly:
                n.SmartChaseTicks = 0;
                // aliadoHerido sólo se recalcula en el tick de decisión: en un tick de solo-ejecución
                // puede venir null aunque la acción vigente sea HelpAlly — espera a la próxima decisión.
                if (aliadoHerido != null)
                {
                    FaceTarget(map, n, aliadoHerido.X, aliadoHerido.Y);
                    // Si el bot tiene hechizo de cura (clérigos de facción, etc.) intenta curar de
                    // verdad en vez de sólo acompañar — mismo NpcCuraANpc que ya usaban los bots de
                    // facción, ahora disponible para cualquier bot con BotHealSpell>0.
                    if (n.BotHealSpell > 0 && Math.Abs(aliadoHerido.X - n.X) <= RANGO_VISION_X && Math.Abs(aliadoHerido.Y - n.Y) <= RANGO_VISION_Y
                        && Combat.NpcCuraANpc(n, aliadoHerido, n.BotHealSpell)) { }
                    else if (!inmovilPropio) StepToward(map, n, aliadoHerido.X, aliadoHerido.Y);
                }
                break;
        }
        return true;
    }

    /// <summary>
    /// Wrapper de BotSmart: resuelve el mismo pool de candidatos de siempre (cualquiera menos el
    /// dueño) y delega TODO el combate a TickCombateBot (el cerebro estándar). Cuando no hay
    /// objetivo, mantiene su propia actividad (deambular cerca / volver con el dueño o al spawn) —
    /// eso sigue siendo específico de este modo, igual que cada Tick* tiene la suya.
    /// </summary>
    private static void TickBotSmart(int map, NpcInstance n)
    {
        // Cruza agua sola igual que los bots progresivos/de guerra (mismo mecanismo, sin esto
        // quedaría trabada en la orilla si algún día se le asigna un objetivo cruzando un lago).
        ReconcileBoatVisual(map, n, puedeAgua: true);

        var userTgt = NearestUserBot(n, map, n.X, n.Y);
        var npcTgt  = NearestEnemyNpcOrRivalForBot(map, n);
        int dU = userTgt != null ? Math.Abs(userTgt.Pos.X - n.X) + Math.Abs(userTgt.Pos.Y - n.Y) : int.MaxValue;
        int dN = npcTgt  != null ? Math.Abs(npcTgt.X - n.X) + Math.Abs(npcTgt.Y - n.Y) : int.MaxValue;
        bool targetIsUser = userTgt != null && dU <= dN;
        bool hayObjetivo = targetIsUser ? userTgt != null : npcTgt != null;

        if (hayObjetivo && TickCombateBot(map, n, userTgt, npcTgt, targetIsUser)) return;

        // Sin objetivo: deambular cerca o volver con el dueño/al spawn (sólo esto sigue siendo
        // "actividad" propia de BotSmart, no combate).
        double now = Environment.TickCount64 / 1000.0;
        bool accionVigenteEsDeambular = n.SmartLastAction == (byte)SmartAction.Explore || n.SmartLastAction == (byte)SmartAction.Return;
        if (now >= n.SmartDecisionNextAt || !accionVigenteEsDeambular)
        {
            n.SmartDecisionNextAt = now + SmartDecisionIntervalSeconds;
            float R() => 0.85f + (float)_aiRng.NextDouble() * 0.30f;
            float sExplore = 18f * R(), sReturn = 10f * R();
            if (n.SmartLastAction == (byte)SmartAction.Explore) sExplore += 18f;
            if (n.SmartLastAction == (byte)SmartAction.Return) sReturn += 18f;
            n.SmartLastAction = (byte)(sExplore >= sReturn ? SmartAction.Explore : SmartAction.Return);
        }

        switch ((SmartAction)n.SmartLastAction)
        {
            case SmartAction.Return:
                var owner2 = (n.OwnerUserIndex > 0 && n.OwnerUserIndex <= UserListManager.LastUser)
                             ? UserListManager.UserList[n.OwnerUserIndex] : null;
                if (owner2 != null && owner2.flags.UserLogged && owner2.Pos.Map == n.Map)
                { var (tx, ty) = BotFollowTile(n, owner2); StepToward(map, n, tx, ty); }
                else StepToward(map, n, n.SpawnX, n.SpawnY);
                break;

            default: // Explore
                // Se compromete a UN destino varios ticks (WanderX/Y/Ticks) en vez de un paso suelto
                // por tick: así camina SEGUIDO en una dirección en vez de tambalear.
                if (n.WanderTicks == 0 || (n.X == n.WanderX && n.Y == n.WanderY))
                {
                    n.WanderX = (short)Math.Clamp(n.X + _aiRng.Next(11) - 5, 1, 99);
                    n.WanderY = (short)Math.Clamp(n.Y + _aiRng.Next(11) - 5, 1, 99);
                    n.WanderTicks = 8;
                }
                if (StepToward(map, n, n.WanderX, n.WanderY) == 0) n.WanderTicks = 0;
                else n.WanderTicks--;
                break;
        }
    }

    /// <summary>
    /// IA de los bots "progresivos" (BotLeveling, Bots.cs): buscan y matan NPCs SALVAJES solos —
    /// no siguen al dueño ni atacan usuarios/bots rivales, el único objetivo es farmear NPCs para
    /// subir de nivel (Bots.DarExpABot al matar). Mismo criterio de ataque/casteo que la rama
    /// BotAtacar contra NPCs (adyacente=golpe/hechizo cuerpo a cuerpo, lejos=acercarse o tirar
    /// hechizo si es caster). Si no hay ningún NPC en rango de visión, deambula al azar en vez de
    /// quedarse plantado esperando que se le acerque uno — "vayan buscando" de verdad.
    /// </summary>
    private static void TickBotLeveling(int map, NpcInstance n)
    {
        // Embarcan/desembarcan solos al pisar agua (igual que los bots de guerra, TickBotGuerra) —
        // sin esto, ViajarGuerra calcula un camino que cruza agua (BotPathing con agua:true) pero
        // el bot se queda trabado en la orilla porque AguaValida nunca se actualiza.
        ReconcileBoatVisual(map, n, puedeAgua: true);

        // Prioridad 1: si tiene facción asignada (Bots.PoblarMundo), un rival de OTRA facción a
        // la vista gana siempre — "si se encuentran que se ataquen los de diferente facción".
        var npcTgt = (n.BotFaccion > 0 ? NearestRivalFaccionBot(map, n) : null) ?? NearestEnemyNpcForBot(map, n);
        if (npcTgt == null)
        {
            // Nada para cazar cerca: recorrer las ciudades (mismo mecanismo de cruce de mapas que
            // los bots de guerra, pero ciclando ciudades en vez de perseguir una facción) — así
            // salen solos de un dungeon si arrancaron ahí, en vez de quedarse dando vueltas.
            ViajarGuerra(map, n, AsignarCiudadLeveling);
            return;
        }

        // Objetivo resuelto (NPC salvaje o rival de facción, según arriba) → cerebro estándar.
        TickCombateBot(map, n, null, npcTgt, targetIsUser: false);
    }

    /// <summary>
    /// IA del bot de sparring PvP: persigue y pelea contra SU PROPIO dueño, como un usuario en combate.
    /// Se acerca, golpea cuerpo a cuerpo (golpes/fallos) y, si es caster, lo inmoviliza/le lanza hechizos
    /// a distancia. Usa los intervalos reales (PuedeAtacarNpc en NpcAtacaUsuario/NpcLanzaSpell). Si el
    /// jugador lo paraliza, TickAI lo remueve (manejado aparte).
    /// </summary>
    private static void TickBotSpar(int map, NpcInstance n)
    {
        // Inmovilizado (no paralizado) caduca solo; el resto (no caminar pero sí poder golpear/
        // castear) ya lo respeta TickCombateBot para cualquier bot.
        double now = Environment.TickCount64 / 1000.0;
        if (n.InmovilizadoHasta != 0 && n.InmovilizadoHasta <= now) n.InmovilizadoHasta = 0;

        var owner = (n.OwnerUserIndex > 0 && n.OwnerUserIndex <= UserListManager.LastUser)
                    ? UserListManager.UserList[n.OwnerUserIndex] : null;
        // Sin dueño vivo en el mismo mapa: queda quieto (no persigue a nadie más — el ÚNICO
        // objetivo legal de un bot de sparring es su propio dueño, eso sigue siendo su actividad).
        if (owner == null || !owner.flags.UserLogged || owner.flags.Muerto == 1 || owner.Pos.Map != n.Map) return;

        TickCombateBot(map, n, owner, null, targetIsUser: true);
    }

    /// <summary>Tile donde el bot quiere pararse al seguir: en fila (FormSlot) o junto al dueño.</summary>
    private static (byte x, byte y) BotFollowTile(NpcInstance n, User owner)
    {
        if (n.FormSlot >= 0 && n.FormTotal > 0)
        {
            int x = Math.Clamp(owner.Pos.X - n.FormTotal / 2 + n.FormSlot, 1, 99);
            int y = Math.Clamp(owner.Pos.Y + 1, 1, 99);   // fila horizontal una celda debajo del dueño
            return ((byte)x, (byte)y);
        }
        return ((byte)owner.Pos.X, (byte)owner.Pos.Y);
    }

    /// <summary>Cantidad de bots vivos (todos los dueños). Para el tope anti-spam.</summary>
    public static int CountBots()
    {
        int c = 0;
        foreach (var kv in _byMap)
            foreach (var n in kv.Value)
                if (!n.Dead && n.IsBot) c++;
        return c;
    }

    /// <summary>Busca un tile libre (sin NPC vivo) cerca de (x,y) para no apilar bots en la misma celda.</summary>
    public static (byte x, byte y) FreeTileNear(int map, byte x, byte y)
    {
        for (int r = 0; r <= 6; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // sólo el "anillo" del radio r
                    int nx = x + dx, ny = y + dy;
                    if (nx < 1 || nx > 99 || ny < 1 || ny > 99) continue;
                    if (NpcAt(map, nx, ny) == null) return ((byte)nx, (byte)ny);
                }
        return (x, y);
    }

    /// <summary>Bots vivos de la guerra mundial de facciones (lista materializada: el que la recorre
    /// suele matar/spawnear bots, y eso mutaría la colección interna).</summary>
    public static List<NpcInstance> BotsDeGuerra()
    {
        var res = new List<NpcInstance>();
        foreach (var kv in _byMap)
            foreach (var n in kv.Value)
                if (!n.Dead && n.IsBot && n.BotGuerra) res.Add(n);
        return res;
    }

    /// <summary>Bots "mirables" desde el panel espía: de guerra (BotGuerra, incluye los de
    /// DungeonBots) Y progresivos (BotLeveling, Bots.PoblarMundo). Distinta de BotsDeGuerra()
    /// A PROPÓSITO: GuerraFacciones usa BotsDeGuerra() para DETENER/CONTAR la guerra (/guerraoff
    /// mata todo lo que devuelve) — si se le sumaran los progresivos, se los borraría solos.</summary>
    public static List<NpcInstance> BotsEspectables()
    {
        var res = new List<NpcInstance>();
        foreach (var kv in _byMap)
            foreach (var n in kv.Value)
                if (!n.Dead && n.IsBot && (n.BotGuerra || n.BotLeveling)) res.Add(n);
        return res;
    }

    /// <summary>Forma a los bots del dueño en una fila (asigna FormSlot/FormTotal).</summary>
    public static void FormarBots(int owner)
    {
        var bots = new List<NpcInstance>();
        foreach (var kv in _byMap)
            foreach (var b in kv.Value)
                if (!b.Dead && b.IsBot && b.OwnerUserIndex == owner) bots.Add(b);
        for (int i = 0; i < bots.Count; i++) { bots[i].FormSlot = i; bots[i].FormTotal = bots.Count; }
    }

    private static HashSet<int> _mapasCiudad;

    /// <summary>¿Este mapa es una de las 14 ciudades reales (Ciudades.dat; el índice 15 es
    /// Intermundia, no cuenta)? Los bots nunca cazan NPCs salvajes/de servicio ahí adentro — la
    /// única pelea permitida dentro de una ciudad es bot-vs-bot rival (NearestRivalFaccionBot).</summary>
    private static bool EsMapaDeCiudad(int map)
    {
        if (_mapasCiudad == null)
        {
            _mapasCiudad = new HashSet<int>();
            for (int i = 1; i <= 14; i++) { var c = CityData.Get(i); if (c.Map > 0) _mapasCiudad.Add(c.Map); }
        }
        return _mapasCiudad.Contains(map);
    }

    /// <summary>¿Puede un bot cazar a este NPC? No si: no es hostil/atacable (mercaderes,
    /// banqueros, guardias, entrenadores — igual criterio que ya usaba AtacarCriaturaCercana),
    /// es un dador de misión (aunque por error tenga Hostil/Attackable=1 en NPCs.dat), o está
    /// parado en una ciudad (ahí ningún NPC de servicio ni salvaje es blanco válido).</summary>
    private static bool EsPresaSalvajeValida(NpcInstance o)
    {
        if (!o.Hostil || !o.Attackable) return false;
        if (QuestSystem.NpcHasQuests(o.NpcIndex)) return false;
        if (EsMapaDeCiudad(o.Map)) return false;
        return true;
    }

    // NPC enemigo más cercano para un bot (cualquier NPC que no sea bot ni mascota), en rango de visión.
    // La usan TickBotFaccion/TickBotGuerra/TickBotLeveling: sólo apuntan a criaturas salvajes
    // hostiles y atacables fuera de las ciudades, nunca a otros bots ni a NPCs de servicio/quest.
    private static NpcInstance NearestEnemyNpcForBot(int map, NpcInstance bot)
    {
        NpcInstance best = null; int bestD = int.MaxValue;
        foreach (var o in _byMap[map])
        {
            if (o.Dead || o == bot || o.IsBot || o.MaestroUser > 0) continue;
            if (!EsPresaSalvajeValida(o)) continue;
            int dx = Math.Abs(o.X - bot.X), dy = Math.Abs(o.Y - bot.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy;
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    /// <summary>Bot rival (de otra facción, cualquier subtipo: progresivo, de guerra o de
    /// dungeon) más cercano en rango de visión. Lo usa TickBotLeveling para que los bots de
    /// Bots.PoblarMundo se ataquen entre sí al cruzarse, sin importar contra qué tipo de bot
    /// rival se topen.</summary>
    private static NpcInstance NearestRivalFaccionBot(int map, NpcInstance bot)
    {
        NpcInstance best = null; int bestD = int.MaxValue;
        foreach (var o in _byMap[map])
        {
            if (o.Dead || o == bot || !o.IsBot || o.BotFaccion == 0 || o.BotFaccion == bot.BotFaccion) continue;
            int dx = Math.Abs(o.X - bot.X), dy = Math.Abs(o.Y - bot.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy;
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    /// <summary>
    /// Igual que <see cref="NearestEnemyNpcForBot"/> pero además deja atacar bots y mascotas de
    /// OTRO dueño (bots rivales de dos jugadores distintos se pelean entre sí). Sólo la usa la
    /// rama BotAtacar de TickBot: los bots de facción/guerra (BotFaccion/BotGuerra) ya tienen su
    /// propio targeting bot-vs-bot separado y no pasan por acá.
    /// </summary>
    private static NpcInstance NearestEnemyNpcOrRivalForBot(int map, NpcInstance bot)
    {
        NpcInstance best = null; int bestD = int.MaxValue;
        foreach (var o in _byMap[map])
        {
            if (o.Dead || o == bot) continue;
            bool esSalvaje = !o.IsBot && o.MaestroUser == 0 && EsPresaSalvajeValida(o);
            bool esBotRival = o.IsBot && o.OwnerUserIndex > 0 && o.OwnerUserIndex != bot.OwnerUserIndex
                               && o.BotFaccion == 0 && !o.BotGuerra;
            bool esMascotaRival = o.MaestroUser > 0 && o.MaestroUser != bot.OwnerUserIndex;
            if (!esSalvaje && !esBotRival && !esMascotaRival) continue;

            int dx = Math.Abs(o.X - bot.X), dy = Math.Abs(o.Y - bot.Y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy;
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    // --- Warp de bots hacia el dueño (diferido: no se puede mutar _byMap durante el TickAI) ---
    private static readonly List<(NpcInstance bot, int map, byte x, byte y)> _pendingBotWarps = new();

    private static void QueueBotWarpTo(NpcInstance n, int map, byte x, byte y)
    {
        _pendingBotWarps.Add((n, map, x, y));
    }

    private static void ApplyPendingBotWarps()
    {
        if (_pendingBotWarps.Count == 0) return;
        foreach (var (bot, newMap, nx, ny) in _pendingBotWarps)
        {
            if (bot.Dead) continue;
            if (bot.Map != newMap)
            {
                if (_byMap.TryGetValue(bot.Map, out var oldList)) oldList.Remove(bot);
                AreaVisibility.OnNpcRemoved(bot);
                bot.Map = newMap; bot.X = nx; bot.Y = ny;
                if (!_byMap.TryGetValue(newMap, out var newList)) { newList = new List<NpcInstance>(); _byMap[newMap] = newList; }
                newList.Add(bot);
                AreaVisibility.OnNpcSpawn(bot);
            }
            else
            {
                // mismo mapa, salto largo (se había perdido): recrear en la nueva posición.
                AreaVisibility.OnNpcRemoved(bot);
                bot.X = nx; bot.Y = ny;
                AreaVisibility.OnNpcSpawn(bot);
            }
        }
        _pendingBotWarps.Clear();
    }

    // Adyacente para bots: cualquier usuario menos el dueño (incluye GMs).
    private static int AdjacentUserBot(NpcInstance npc, int map, int x, int y, out byte heading)
    {
        // [[b4_usersbymap]] Antes: for 1..LastUser (todo el server). Ahora: solo los usuarios de
        // ESTE mapa. Las posiciones son únicas por tile, así que no hay desempate que preservar acá.
        (int dx, int dy, byte h)[] dirs = { (0,-1,1),(1,0,2),(0,1,3),(-1,0,4) };
        var usersInMap = UsersByMapIndex.Get(map);
        foreach (var (dx, dy, h) in dirs)
        {
            int ux = x + dx, uy = y + dy;
            foreach (int i in usersInMap)
            {
                var u = UserListManager.UserList[i];
                if (u != null && u.flags.UserLogged && u.flags.Muerto == 0 && i != npc.OwnerUserIndex
                    && NpcVeUsuario(npc, u) && u.Pos.Map == map && u.Pos.X == ux && u.Pos.Y == uy)
                { heading = h; return i; }
            }
        }
        heading = npc.Heading; return 0;
    }

    // Más cercano para bots: cualquier usuario menos el dueño (incluye GMs), en rango de visión.
    private static User NearestUserBot(NpcInstance npc, int map, int x, int y)
    {
        // [[b4_usersbymap]] Desempate preservado a propósito: ante distancia IGUAL, gana el usuario
        // de menor índice, igual que el for 1..LastUser de antes (que encontraba primero al de
        // índice más bajo). El HashSet de UsersByMapIndex no garantiza orden ascendente, así que el
        // criterio de desempate se hace EXPLÍCITO acá (bestIdx) en vez de depender del orden de
        // iteración — verificado con un test dedicado de empate (NpcSearchTests).
        User best = null; int bestD = int.MaxValue; int bestIdx = int.MaxValue;
        foreach (int i in UsersByMapIndex.Get(map))
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || u.flags.Muerto != 0 || i == npc.OwnerUserIndex) continue;
            if (u.Pos.Map != map || !NpcVeUsuario(npc, u)) continue;
            int dx = Math.Abs(u.Pos.X - x), dy = Math.Abs(u.Pos.Y - y);
            if (dx > RANGO_VISION_X || dy > RANGO_VISION_Y) continue;
            int d = dx + dy;
            if (d < bestD || (d == bestD && i < bestIdx)) { bestD = d; best = u; bestIdx = i; }
        }
        return best;
    }

    private static int AdjacentUser(NpcInstance npc, int map, int x, int y, out byte heading)
    {
        // N=1,E=2,S=3,O=4
        // [[b4_usersbymap]] Antes: for 1..LastUser. Ahora: solo los usuarios de este mapa. Sin
        // desempate que preservar (posiciones únicas por tile).
        (int dx, int dy, byte h)[] dirs = { (0,-1,1),(1,0,2),(0,1,3),(-1,0,4) };
        var usersInMap = UsersByMapIndex.Get(map);
        foreach (var (dx, dy, h) in dirs)
        {
            int ux = x + dx, uy = y + dy;
            foreach (int i in usersInMap)
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

    /// <summary>Igual que AdjacentUser pero busca una MASCOTA (MaestroUser>0) adyacente — para que
    /// un NPC hostil le pegue a la mascota que lo está atacando en vez de ignorarla y perseguir
    /// solo al dueño (ver TickAI: se chequea ANTES que AdjacentUser, la mascota "tanquea").</summary>
    private static NpcInstance AdjacentPet(NpcInstance npc, int map, int x, int y, out byte heading)
    {
        if (_byMap.TryGetValue(map, out var list))
        {
            (int dx, int dy, byte h)[] dirs = { (0,-1,1),(1,0,2),(0,1,3),(-1,0,4) };
            foreach (var (dx, dy, h) in dirs)
            {
                int px = x + dx, py = y + dy;
                foreach (var o in list)
                {
                    if (!o.Dead && o.MaestroUser > 0 && o != npc && o.X == px && o.Y == py)
                    { heading = h; return o; }
                }
            }
        }
        heading = 0; return null;
    }

    private static User NearestUser(NpcInstance npc, int map, int x, int y, out int dist)
    {
        // [[b4_usersbymap]] Mismo cuidado de desempate que NearestUserBot: ante distancia IGUAL,
        // gana el usuario de menor índice (igual que el for 1..LastUser de antes), hecho explícito
        // porque el HashSet no garantiza orden ascendente.
        User best = null; dist = int.MaxValue; int bestIdx = int.MaxValue;
        foreach (int i in UsersByMapIndex.Get(map))
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.flags.Muerto == 1 || u.Pos.Map != map) continue;
            if (EsGmIntocable(u)) continue; // los NPCs no persiguen a GMs/Dioses
            if (!NpcVeUsuario(npc, u)) continue; // invisible/oculto: indetectable (salvo dragón)
            int d = Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y);
            if (d < dist || (d == dist && i < bestIdx)) { dist = d; best = u; bestIdx = i; }
        }
        return best;
    }

    /// <summary>
    /// Da un paso del NPC hacia (tx,ty). Usa A*/BFS (SeekPath) para rodear obstáculos;
    /// si no encuentra camino, cae a FindDirection (greedy 1:1 VB6). maxSteps más grande = rodea
    /// obstáculos más grandes antes de rendirse (la mascota compañera usa uno mayor que el default,
    /// ver AtacarObjetivoMascota/AtacarUsuarioMascota/TickMascota).
    /// </summary>
    private static byte StepToward(int map, NpcInstance n, int tx, int ty, int maxSteps = 30, bool evitarUsuarios = false)
    {
        // Pathfinding BFS (VB6 SeekPath): primer paso del camino más corto al target.
        byte heading = SeekPathHeading(map, n, tx, ty, maxSteps, evitarUsuarios: evitarUsuarios);
        // Fallback greedy si no hay camino calculado.
        if (heading == 0) heading = FindDirection(map, n, tx, ty);
        if (heading == 0) return 0; // ya al lado, mismo tile, o sin salida
        MoveNpcChar(map, n, heading);
        return heading;
    }

    // La mascota compañera busca camino más lejos que un NPC salvaje común antes de darse por
    // vencida: tiene que poder rodear una casa/muralla entera para llegar hasta el amo o su
    // objetivo, no solo esquivar un árbol suelto.
    private const int PET_PATHFIND_STEPS = 60;

    /// <summary>Mueve el bot a un tile adyacente LIBRE al azar (desatasca cuando está amontonado).</summary>
    private static bool TryStepRandom(int map, NpcInstance n)
    {
        Span<byte> dirs = stackalloc byte[] { H_N, H_E, H_S, H_O };
        for (int i = 3; i > 0; i--) { int j = _aiRng.Next(i + 1); (dirs[i], dirs[j]) = (dirs[j], dirs[i]); }
        foreach (var d in dirs)
        {
            int nx = n.X, ny = n.Y;
            switch (d) { case H_N: ny--; break; case H_E: nx++; break; case H_S: ny++; break; case H_O: nx--; break; }
            if (PuedeNpc(map, nx, ny, n.AguaValida, n.TierraInvalida) && UserAtTile(map, nx, ny) == 0)
            { MoveNpcChar(map, n, d); return true; }
        }
        return false;
    }

    // Edad máxima del camino cacheado antes de forzar un recálculo completo: ~4 ticks de IA por NPC
    // (AiIntervalSeconds=0.38s en TickAI), conservador para que el NPC no persiga una ruta stale
    // por mucho tiempo si el objetivo se movió sin que se notara (destino sigue "aprox" el mismo).
    private const long PATH_CACHE_MAX_AGE_MS = 4 * 380;

    /// <summary>
    /// SeekPath (PathFinding.bas:230) portado como BFS. Devuelve el heading del PRIMER paso
    /// del camino más corto de (npc) a (tx,ty) esquivando bloqueos/NPCs. 0 = sin camino.
    /// [[FIX2 pathcache]] Antes de recalcular, intenta reusar el camino cacheado en n.PathCache del
    /// tick anterior (mismo destino aprox., no expiró, próximo paso todavía válido). El resultado
    /// (a qué tile se mueve el NPC) es equivalente a recalcular siempre: solo cambia CUÁNDO se
    /// vuelve a correr el BFS completo.
    /// </summary>
    private static byte SeekPathHeading(int map, NpcInstance n, int tx, int ty, int maxSteps,
        bool puertasAbribles = false, bool evitarUsuarios = false)
    {
        if (n.X == tx && n.Y == ty) return 0;
        if (tx < 1 || tx > 100 || ty < 1 || ty > 100) return 0;

        long nowMs = Environment.TickCount64;
        if (n.PathCache != null && n.PathCacheMap == map && n.PathCacheIdx < n.PathCacheCount
            && Math.Abs(tx - n.PathCacheDestX) <= 1 && Math.Abs(ty - n.PathCacheDestY) <= 1
            && (nowMs - n.PathCacheAtMs) < PATH_CACHE_MAX_AGE_MS)
        {
            var (sx, sy) = n.PathCache[n.PathCacheIdx];
            int adx = sx - n.X, ady = sy - n.Y;
            bool esTargetCached = sx == tx && sy == ty;
            bool esAdyacente = (Math.Abs(adx) == 1 && ady == 0) || (adx == 0 && Math.Abs(ady) == 1);
            if (esAdyacente && (esTargetCached || PuedeNpc(map, sx, sy, n.AguaValida, n.TierraInvalida, puertasAbribles, evitarUsuarios)))
            {
                n.PathCacheIdx++;
                if (ady < 0) return H_N;
                if (adx > 0) return H_E;
                if (ady > 0) return H_S;
                if (adx < 0) return H_O;
            }
            // Paso cacheado ya no válido (o el NPC no está donde el camino esperaba): recalcular abajo.
        }

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
                if (!esTarget && !PuedeNpc(map, nx, ny, n.AguaValida, n.TierraInvalida, puertasAbribles, evitarUsuarios)) continue;
                visited[nx, ny] = true;
                prev[nx, ny] = (cx, cy);
                q.Enqueue((nx, ny));
            }
        }
        if (!found) { n.PathCache = null; n.PathCacheCount = 0; n.PathCacheIdx = 0; return 0; }

        // Reconstruir: retroceder desde el target hasta el primer paso saliendo del NPC, guardando
        // TODO el camino recorrido (no solo el primer paso) para poder cachearlo. bxs/bys quedan en
        // orden target→...→primerPaso (reverso del orden de viaje); se invierten al guardar en cache.
        Span<byte> bxs = stackalloc byte[220];
        Span<byte> bys = stackalloc byte[220];
        int len = 0;
        int rx = tx, ry = ty;
        while (true)
        {
            if (len < bxs.Length) { bxs[len] = (byte)rx; bys[len] = (byte)ry; }
            len++;
            if (prev[rx, ry].px == n.X && prev[rx, ry].py == n.Y) break; // (rx,ry) = primer paso
            var p = prev[rx, ry];
            if (p.px == 0 && p.py == 0) { n.PathCache = null; n.PathCacheCount = 0; n.PathCacheIdx = 0; return 0; } // sin reconstrucción válida
            rx = p.px; ry = p.py;
        }
        int pathLen = Math.Min(len, bxs.Length);

        // Cachear el camino completo (orden de viaje: primer paso en [0], target en [pathLen-1]).
        if (n.PathCache == null || n.PathCache.Length < pathLen) n.PathCache = new (byte, byte)[Math.Max(pathLen, 16)];
        for (int k = 0; k < pathLen; k++) n.PathCache[k] = (bxs[pathLen - 1 - k], bys[pathLen - 1 - k]);
        n.PathCacheCount = pathLen;
        n.PathCacheIdx = 1; // el paso [0] es el que devolvemos ahora mismo
        n.PathCacheDestX = (byte)tx; n.PathCacheDestY = (byte)ty;
        n.PathCacheMap = map;
        n.PathCacheAtMs = nowMs;

        // (rx,ry) es el tile adyacente al NPC en el camino → heading hacia él (idéntico a antes).
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
    private static bool PuedeNpc(int map, int x, int y, bool aguaValida = false, bool tierraInvalida = false,
        bool puertasAbribles = false, bool evitarUsuarios = false)
    {
        if (x < 1 || x > 100 || y < 1 || y > 100) return false;
        // La mascota compañera esquiva a los usuarios al calcular el camino (no solo al ejecutar el
        // paso, ver MoveNpcChar): sin esto el BFS le seguía marcando "seguí derecho" a través de
        // vos, y como MoveNpcChar se niega a pisar tu tile, quedaba trabada girando en el lugar en
        // vez de rodearte. El propio tile DESTINO sigue permitido (SeekPathHeading lo exceptúa),
        // así que igual puede llegar hasta un usuario si ESE es su objetivo real (PvP).
        if (evitarUsuarios && UserAtTile(map, x, y) > 0) return false;
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

        // Pasos de golem: cada vez que un golem se mueve, suena un paso (220/221/222) a los del mapa.
        if (n.Name != null && n.Name.Contains("Golem", StringComparison.OrdinalIgnoreCase))
        {
            short paso = (System.Random.Shared.Next(3)) switch
            { 0 => Sounds.GOLEM_PASO1, 1 => Sounds.GOLEM_PASO2, _ => Sounds.GOLEM_PASO3 };
            BotPlayWave(map, n.X, n.Y, paso);
        }
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
                if (n.NoRespawn) continue;   // bots de evento y mascotas de entrenador no reviven
                if (!n.Dead || n.RespawnAt == 0 || now < n.RespawnAt) continue;
                n.Dead = false;
                n.RespawnAt = 0;
                n.MinHP = n.MaxHP;
                // Recargar el pool de exp (CalcularDarExp lo drena por golpe); sin esto el NPC
                // respawneado queda con ExpCount=0 y no vuelve a dar experiencia nunca.
                n.ExpCount = n.GiveEXP;
                // Limpiar estado de parálisis/inmovilización de la vida anterior: si moría paralizado,
                // ParalizadoHasta quedaba en el futuro y SendOne re-enviaba NpcParalysisProgress al
                // respawneado → aparecía el conteo de parálisis bajo el NPC nuevo.
                n.ParalizadoHasta = 0; n.InmovilizadoHasta = 0; n.DormidoHasta = 0;
                n.X = n.SpawnX; n.Y = n.SpawnY;
                n.CharIndex = CharIndexPool.Next();
                // Restaurar estado original (un NPC provocado no debe revivir hostil) + limpiar aggro.
                n.Hostil = n.OldHostil; n.Movement = n.OldMovement;
                n.AttackedBy = ""; n.AttackedFirstBy = ""; n.TargetUser = 0;
                // Reiniciar el cooldown de IA: tras el tiempo muerto, NextAiAt quedó congelado en el
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
        // Mundo continuo: posición en global para el envío (identidad si el flag está apagado).
        var (lx, ly) = Continuous.Pos(map, lejana.X, lejana.Y);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                ServerPackets.CharacterMove(o.Conn, lejana.CharIndex, lx, ly);
        }
        return true;
    }

    /// <summary>Reubica la mascota compañera VIVA de un usuario al mapa nuevo cuando el dueño
    /// cambia de mapa (teletransporte, portal, cruce continuo) — antes se quedaba en el mapa
    /// viejo, invisible/inalcanzable, hasta que el amo volvía o la reinvocaba (bug reportado:
    /// "si paso de mapa no pasa de mapa la mascota"). No hace nada si no tiene mascota viva
    /// (muerta o nunca invocada: no hay nada que mover, se reinvoca donde esté cuando corresponda).</summary>
    public static void MoverMascotaConDueño(User u, int oldMap, int newMap, byte newX, byte newY)
    {
        if (u.PetCharIndex <= 0 || oldMap == newMap) return;
        var pet = NpcByCharIndex(oldMap, u.PetCharIndex);
        if (pet == null) return; // muerta o no invocada

        // El mapa nuevo es zona segura: la mascota NO lo sigue adentro, se desinvoca en el mapa
        // viejo (sin perder progreso). Se resuelve acá, antes de moverla, para que no llegue ni a
        // aparecer un instante en la ciudad. El caso "camina hasta un tile ZONASEGURA del mismo
        // mapa" lo cubre TickMascota.
        if (Combat.EnZonaSegura(newMap, newX, newY))
        {
            RemoveNpc(pet);
            u.PetCharIndex = 0;
            if (u.Conn != null)
                ServerPackets.ConsoleMsg(u.Conn, "Tu mascota no puede entrar en zona segura. Invocala de nuevo al salir.", 1);
            Combat.EnviarPetInfo(u);
            return;
        }

        if (_byMap.TryGetValue(oldMap, out var oldList)) oldList.Remove(pet);
        AreaVisibility.OnNpcRemoved(pet); // desaparece para los que la veían en el mapa viejo

        pet.Map = newMap;
        pet.X = newX; pet.Y = newY; pet.SpawnX = newX; pet.SpawnY = newY;
        // Objetivos del mapa viejo ya no existen acá; vuelve a la IA libre (seguir al amo/buscar).
        pet.MascotaTargetNpc = 0; pet.MascotaTargetUsuario = 0;

        if (!_byMap.TryGetValue(newMap, out var newList)) { newList = new List<NpcInstance>(); _byMap[newMap] = newList; }
        newList.Add(pet);
        AreaVisibility.OnNpcSpawn(pet); // aparece para los del mapa nuevo
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

    /// <summary>Activa/desactiva el modo "atacar" de los bots del dueño (owner=0 = todos).</summary>
    public static void SetBotsAtacar(int owner, bool atacar)
    {
        foreach (var kv in _byMap)
            foreach (var n in kv.Value)
                if (!n.Dead && n.IsBot && (owner == 0 || n.OwnerUserIndex == owner))
                    n.BotAtacar = atacar;
    }

    /// <summary>Elimina todos los bots (owner=0 = de todos; sino solo los del dueño). Devuelve cuántos.</summary>
    public static int KillAllBots(int owner)
    {
        int count = 0;
        foreach (var kv in _byMap)
            foreach (var n in kv.Value)
                if (!n.Dead && n.IsBot && (owner == 0 || n.OwnerUserIndex == owner))
                { RemoveNpc(n); count++; }
        return count;
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
        // VB6 NPCAtacado (Modulo_UsUaRiOs.bas:1571): si estaba dormido por instrumento, despierta.
        DespertarNpc(npc);

        // ---- Mascota de un jugador ----
        // NO puede pasar por el camino de abajo: `Hostil = true; Movement = 0` la convertiría en un
        // bicho salvaje para siempre (deja de seguir al amo, `Movement = 8` SigueAmo se pierde) y
        // encima `TargetUser` es del AI de NPC común, que la mascota no usa. Se defiende con SU
        // propia IA: se engancha con el agresor igual que cuando le pegan al amo
        // (CheckPetsVsUsuario), sin dejar de ser mascota.
        if (npc.MaestroUser > 0 && npc.PetOfPlayer)
        {
            if (npc.MascotaTargetUsuario == 0) npc.MascotaTargetUsuario = atacante.id;
            npc.MascotaTargetNpc = 0;             // el jugador que le pega manda sobre un NPC previo
            atacante.flags.NPCAtacado = npc.CharIndex;
            // Y el resto de las mascotas del dueño también se suman contra el agresor.
            CheckPetsVsUsuario(atacante.id, npc.MaestroUser);
            return;
        }
        if (string.IsNullOrEmpty(npc.AttackedFirstBy) || npc.AttackedFirstBy == atacante.Name)
            npc.AttackedBy = atacante.Name;
        if (string.IsNullOrEmpty(npc.AttackedFirstBy))
            npc.AttackedFirstBy = atacante.Name;
        if (!npc.Hostil) { npc.Hostil = true; npc.Movement = 0; } // pasivo → hostil y persigue
        npc.TargetUser = atacante.id;
        atacante.flags.NPCAtacado = npc.CharIndex;

        // Alarma de ciudad (139): si un usuario de facción enemiga agrede a un guardia de una ciudad,
        // todos los miembros de la facción dueña de esa ciudad escuchan la alarma.
        AlarmaCiudadSiCorresponde(npc, atacante);
    }

    /// <summary>
    /// [[FIX4]] Reacción inmediata (sin esperar el próximo TickAI, ~380ms) cuando un NPC que YA
    /// estaba trabado en combate (tenía un TargetUser distinto) recibe daño de un atacante NUEVO.
    /// Criterio simple y consistente con ProvocarNpc: "el último que pegó, si sigue en rango, pasa a
    /// ser el target" — ProvocarNpc ya lo hace de forma incondicional (npc.TargetUser = atacante.id)
    /// pero solo como dato; acá además se actúa en el momento: gira a encararlo y, si ya está
    /// adyacente, pega/castea ya mismo (respeta su propio cooldown vía PuedeAtacarNpc, así que si
    /// ya había golpeado hace poco esta llamada simplemente no hace nada extra).
    ///
    /// Llamar DESPUÉS de ProvocarNpc, en el mismo punto de Combat.cs donde se aplica el daño de un
    /// usuario a un NPC, pasando el TargetUser que tenía ANTES de esa llamada (prevTarget). No toca
    /// mascotas (MaestroUser&gt;0, tienen su propia IA en TickMascota) ni bots (TickBot ya reacciona
    /// solo vía sus propios timers/lógica) — a propósito, fuera del alcance de este fix.
    /// </summary>
    public static void ReaccionInmediataANuevoAtacante(NpcInstance npc, User atacante, int prevTarget)
    {
        if (npc == null || npc.Dead || npc.IsBot || npc.MaestroUser > 0) return;
        if (prevTarget <= 0 || prevTarget == atacante.id) return; // sin target previo, o ya era este mismo: nada que reevaluar
        if (!NpcVeUsuario(npc, atacante)) return; // invisible/oculto sin dragón: no lo puede reaccionar

        int dist = Math.Abs(atacante.Pos.X - npc.X) + Math.Abs(atacante.Pos.Y - npc.Y);
        if (dist > RANGO_VISION_X + RANGO_VISION_Y) return; // demasiado lejos, no reacciona de golpe

        FaceTarget(npc.Map, npc, atacante.Pos.X, atacante.Pos.Y);
        if (dist == 1)
        {
            // Adyacente: pega/castea de una, mismo criterio 50/50 que el resto de la IA de NPC hostil.
            if (npc.Spells != null && npc.Spells.Length > 0 && _aiRng.Next(2) == 0)
                Combat.NpcLanzaSpell(npc, atacante.id);
            else
                Combat.NpcAtacaUsuario(npc, atacante.id);
        }
    }

    // Cooldown de la alarma de ciudad por ciudad (no spamear el sonido en cada golpe).
    private static readonly Dictionary<byte, double> _alarmaCiudadHasta = new();
    private const double ALARMA_CIUDAD_COOLDOWN = 30.0; // segundos

    /// <summary>Si el NPC es un guardia de una ciudad real y el atacante es enemigo de esa ciudad,
    /// difunde el sonido de alarma (139) a todos los usuarios online de la facción dueña de la ciudad.</summary>
    private static void AlarmaCiudadSiCorresponde(NpcInstance npc, User atacante)
    {
        if (npc.NpcType != NPCTYPE_GUARDIASCITY) return;
        if (npc.Ciudad == CIUDAD_RINKEL || npc.Map == MAPA_RINKEL) return; // neutral: sin alarma
        if (!EsEnemigoUsuario(npc.Ciudad, atacante)) return;               // aliado/sin facción: no es ataque

        double ahora = Environment.TickCount64 / 1000.0;
        if (_alarmaCiudadHasta.TryGetValue(npc.Ciudad, out double hasta) && ahora < hasta) return;
        _alarmaCiudadHasta[npc.Ciudad] = ahora + ALARMA_CIUDAD_COOLDOWN;

        // Facciones defensoras de cada ciudad.
        bool EsDefensor(byte f) => npc.Ciudad switch
        {
            CIUDAD_IMPERIAL    => f == FAC_CIUDADANO || f == FAC_ARMADA,
            CIUDAD_REPUBLICANA => f == FAC_REPUBLICANO || f == FAC_MILICIA,
            CIUDAD_CAOTICA     => f == FAC_CAOS,
            _ => false,
        };
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o == null || !o.flags.UserLogged || o.Conn == null) continue;
            if (!EsDefensor(o.Faccion.Status)) continue;
            ServerPackets.PlayWave(o.Conn, Sounds.ALARMA_CIUDAD, (byte)o.Pos.X, (byte)o.Pos.Y);
            ServerPackets.ConsoleMsg(o.Conn, "¡Tu ciudad está siendo atacada!", 4);
        }
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
    /// (MuereNpc) en todos los mapas. Devuelve la cantidad liberada.
    /// NO afecta a la mascota compañera persistente (PetOfPlayer): esa es un compañero, no un
    /// consumible de combate, y sobrevive a la muerte del amo (se queda quieta, ver TickMascota).</summary>
    public static int LiberarMascotasDe(int userIndex)
    {
        int n = 0;
        foreach (var kv in _byMap)
            foreach (var pet in kv.Value)
                if (!pet.Dead && pet.MaestroUser == userIndex && !pet.PetOfPlayer) { RemoveNpc(pet); n++; }
        return n;
    }

    /// <summary>Una criatura de entrenador murió: descuenta el contador de su maestro (QuitarMascotaNpc).</summary>
    public static void QuitarMascotaNpc(int map, int maestroCharIndex)
    {
        var maestro = NpcByCharIndex(map, maestroCharIndex);
        if (maestro != null && maestro.MascotasCount > 0) maestro.MascotasCount--;
    }

    /// <summary>Tile libre más cercano a (x,y) (incluye el propio); espiral radio 1..3. false si ninguno.</summary>
    // Público: lo usa Accion.cs (Veterinaria) para reaparecer la mascota junto al NPC, no al jugador.
    public static bool ClosestFreeTile(int map, int x, int y, out int fx, out int fy)
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

    /// <summary>
    /// Manda a la mascota de vuelta al hogar A PIE. No se teletransporta ni desaparece en el
    /// acto: se le fija un destino y camina hasta allá con el mismo pathfinding que usa para
    /// perseguir, así el dueño la ve irse.
    ///
    /// El destino es el borde del mapa más cercano en dirección a la ciudad de su dueño: la
    /// ciudad casi siempre está en OTRO mapa, y hacerla caminar mapas enteros sería un viaje de
    /// minutos que nadie va a mirar (y que habría que sostener con el dueño desconectándose, la
    /// mascota cruzando mapas, etc.). Camina hasta salir de escena y ahí termina el viaje.
    /// </summary>
    public static void IniciarViajeAlHogar(NpcInstance pet, int destinoX, int destinoY, double segundosTope)
    {
        pet.YendoAlHogar = true;
        pet.HogarX = destinoX; pet.HogarY = destinoY;
        pet.HogarDeadline = Environment.TickCount64 / 1000.0 + segundosTope;
        pet.MascotaTargetNpc = 0; pet.MascotaTargetUsuario = 0;
    }

    /// <summary>Un paso del viaje. Llega (y termina el viaje) al pisar el destino, al quedar
    /// pegada a él, o al vencerse el tope de tiempo — este último cubre que se quede trabada
    /// detrás de una pared: el viaje TIENE que terminar, si no la mochila queda en el limbo.</summary>
    private static void PasoHaciaElHogar(int map, NpcInstance pet)
    {
        double ahora = Environment.TickCount64 / 1000.0;
        int dist = Math.Abs(pet.X - pet.HogarX) + Math.Abs(pet.Y - pet.HogarY);
        if (dist <= 1 || ahora >= pet.HogarDeadline)
        {
            var duenio = UserListManager.UserList[pet.MaestroUser];
            if (duenio != null) PetInventory.LlegoAlHogar(duenio, pet);
            else RemoveNpc(pet);
            return;
        }
        // evitarUsuarios: mismo criterio que el resto del pathing de la mascota (ver el fix del
        // BFS que la dejaba trabada contra su propio dueño).
        StepToward(map, pet, pet.HogarX, pet.HogarY, PET_PATHFIND_STEPS, evitarUsuarios: true);
    }

    /// <summary>
    /// Tile libre AL LADO de (x,y) para que algo aparezca junto a un personaje y no ENCIMA suyo.
    /// A diferencia de ClosestFreeTile, nunca devuelve el tile propio: empieza por el que el
    /// personaje tiene enfrente (heading 1=N 2=E 3=S 4=O), sigue por los otros tres lados y
    /// después por las diagonales. Recién si los 8 están ocupados cae a la espiral de
    /// ClosestFreeTile (que sí puede devolver el propio, como último recurso).
    /// </summary>
    public static bool TileLibreAlLado(int map, int x, int y, byte heading, out int fx, out int fy)
    {
        // Orden: enfrente primero — es donde el jugador está mirando, así que es donde "espera"
        // que aparezca. Después los otros lados y por último las diagonales.
        (int dx, int dy)[] lados = heading switch
        {
            1 => new[] { (0, -1), (-1, 0), (1, 0), (0, 1) },   // norte
            2 => new[] { (1, 0), (0, -1), (0, 1), (-1, 0) },   // este
            4 => new[] { (-1, 0), (0, -1), (0, 1), (1, 0) },   // oeste
            _ => new[] { (0, 1), (-1, 0), (1, 0), (0, -1) },   // sur (default)
        };
        foreach (var (dx, dy) in lados)
        {
            int nx = x + dx, ny = y + dy;
            if (PuedeNpc(map, nx, ny) && UserAtTile(map, nx, ny) == 0) { fx = nx; fy = ny; return true; }
        }
        foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            int nx = x + dx, ny = y + dy;
            if (PuedeNpc(map, nx, ny) && UserAtTile(map, nx, ny) == 0) { fx = nx; fy = ny; return true; }
        }
        return ClosestFreeTile(map, x, y, out fx, out fy);
    }

    /// <summary>Crea un NPC en (map,x,y) en runtime y lo muestra a los usuarios del mapa (debug/eventos).</summary>
    // botSmart: DEBE quedar seteado en el object initializer de abajo, ANTES de que
    // AreaVisibility.OnNpcSpawn(n) mande el primer CharacterCreate (unas líneas más abajo, todavía
    // dentro de este mismo método). Si se setea DESPUÉS de que este método retorne (como hacía
    // antes: Bots.Spawn llamaba a SpawnAt y el caller recién ponía bot.BotSmart=true al final), ese
    // primer CharacterCreate sale con el nombre SIN el marcador "#*" (NombreBotProtocolo lee
    // n.BotSmart en el momento del envío) — el cliente lo toma por bot NORMAL (anima a
    // NPC_SPEED_PXS), mientras el server YA le manda CharacterMove cada ~200ms (IntervaloBot lee
    // n.BotSmart en cada tick de IA, que sí corre después de que el flag quedó puesto) → el
    // cliente recibe moves más rápido de lo que puede animar, el moveQueue se desborda (tope de 3,
    // incoming.js) y fuerza un warp → se ve como si el bot "se teletransportara" justo al invocarlo.
    // Recargar el cliente lo arregla porque ahí sí llega un CharacterCreate fresco con
    // n.BotSmart ya en true desde hace rato (FullUpdate re-crea todo con el estado ACTUAL).
    public static NpcInstance SpawnAt(int map, int npcIndex, byte x, byte y, string spawnedBy = null, bool botSmart = false)
    {
        var info = NpcData.Get(npcIndex);
        if (string.IsNullOrEmpty(info.Name)) return null;

        var n = new NpcInstance
        {
            CharIndex = CharIndexPool.Next(),
            SpawnedBy = spawnedBy,
            BotSmart = botSmart,
            Body = info.Body, Head = info.Head,
            WeaponAnim = info.WeaponAnim, ShieldAnim = info.ShieldAnim, CascoAnim = info.CascoAnim,
            Aura = info.Aura, AuraArma = info.AuraArma, AuraEscudo = info.AuraEscudo, AuraCasco = info.AuraCasco,
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
            Comercia = info.Comercia, NoCompra = info.NoCompra, Inventario = info.Inventario,
            Moneda = info.Moneda, Precios = info.Precios,
            NpcType = info.NpcType, Status = info.Status, Ciudad = info.Ciudad,
            OrigHeading = info.Heading == 0 ? (byte)3 : info.Heading,
            Drops = info.Drops, Criaturas = info.Criaturas,
            OldHostil = info.Hostil, OldMovement = info.Movement, // estado original (MODULO_NPCs.bas:1179)
            Snd1 = info.Snd1, Snd2 = info.Snd2, Snd3 = info.Snd3,
            AfectaParalisis = info.AfectaParalisis,
        };

        if (!_byMap.TryGetValue(map, out var list)) { list = new List<NpcInstance>(); _byMap[map] = list; }
        list.Add(n);

        AreaVisibility.OnNpcSpawn(n);   // crear sólo para los usuarios cuyo área lo cubre
        return n;
    }

    /// <summary>Manda el CharacterCreate de un NPC a una conexión (usado por AreaVisibility al entrar al área).</summary>
    public static void SendNpcCreate(Connection conn, NpcInstance n) => SendOne(conn, n);

    // Nombre tal como va por protocolo para un bot: "#nick" (lo de siempre) o "#*nick" para el
    // prototipo BotSmart — un marcador extra DENTRO del mismo ASCIIString de siempre, sin agregar
    // ningún campo ni byte al paquete CharacterCreate. Un cliente que no conoce el marcador sigue
    // viendo "startsWith('#')" = true (sigue siendo un bot para él, anima como NPC como siempre);
    // sólo el cliente actualizado (incoming.js) lo distingue para animarlo como jugador.
    private static string NombreBotProtocolo(NpcInstance n) => (n.BotSmart ? "#*" : "#") + n.Name;

    private static void SendOne(Connection conn, NpcInstance n)
    {
        // VB6 MakeNPCChar (MODULO_NPCs.bas:657) manda el NÚMERO del NPC como "nombre". El cliente
        // detecta el nombre numérico (is_valid_int) → is_npc=true → lo anima con velocidad de NPC
        // y resuelve el nombre real vía locale_npc. Si mandáramos el nombre real, el cliente lo
        // tomaría por JUGADOR y lo movería con velocidad de jugador (caminata rápida y trabada).
        // Mundo continuo: posición local→global si está activo (identidad si no).
        var (nx, ny) = Continuous.Pos(n.Map, n.X, n.Y);
        // Mascota compañera con nombre propio: se muestra igual que un bot ("#Nombre", el cliente
        // le saca el "#" y lo pinta directo) en vez del índice numérico que resuelve locale_npc.
        bool nombrePropio = n.IsBot || (n.PetOfPlayer && !string.IsNullOrEmpty(n.PetNombre));
        string nombreMostrado = n.IsBot ? n.Name : n.PetNombre;
        ServerPackets.CharacterCreate(conn,
            charIndex: n.CharIndex, body: n.Body, head: n.Head, heading: n.Heading,
            x: nx, y: ny, weapon: n.WeaponAnim, shield: n.ShieldAnim, helmet: n.CascoAnim, fx: 0, fxLoops: 0,
            // VB6 MakeNPCChar (MODULO_NPCs.bas:657) manda flags.Status como color de nick del NPC.
            name: !nombrePropio ? n.NpcIndex.ToString() : (n.IsBot ? NombreBotProtocolo(n) : "#" + nombreMostrado), privileges: (byte)n.Status, donador: 0, particulaFx: 0,
            armaAura: (byte)n.AuraArma, bodyAura: (byte)n.Aura, escudoAura: (byte)n.AuraEscudo, headAura: (byte)n.AuraCasco, otraAura: 0, anilloAura: 0,
            isTopGold: false, weaponObjIndex: 0);

        // Estado visual no incluido en CharacterCreate: si el NPC sigue paralizado, reenviar la barra
        // de progreso al observador (sino al volver al mapa / entrar al área el NPC se recrea sin barra).
        double restante = n.ParalizadoHasta - Environment.TickCount64 / 1000.0;
        if (restante > 0)
            ServerPackets.NpcParalysisProgress(conn, n.CharIndex, (byte)Math.Min(255, (int)Math.Ceiling(restante)), n.ParalisisTipo);
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
            Aura = info.Aura, AuraArma = info.AuraArma, AuraEscudo = info.AuraEscudo, AuraCasco = info.AuraCasco,
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
                    NoCompra = info.NoCompra,
                    Inventario = info.Inventario,
                    Moneda = info.Moneda,
                    Precios = info.Precios,
                    NpcType = info.NpcType,
                    Status = info.Status,
                    Ciudad = info.Ciudad,
                    OrigHeading = info.Heading == 0 ? (byte)3 : info.Heading,
                    Drops = info.Drops,
                    Criaturas = info.Criaturas,
                    OldHostil = info.Hostil, OldMovement = info.Movement, // estado original
                    Snd1 = info.Snd1, Snd2 = info.Snd2, Snd3 = info.Snd3,
                    AfectaParalisis = info.AfectaParalisis,
                });
            }
        }
        _byMap[mapNumber] = list;
        // Los vendedores 31/32/33 ya no se agregan automáticamente en cada ciudad:
        // solo aparecen donde se los coloque en el editor de mapas (.csm).
        return list;
    }

    /// <summary>Envía a una conexión todos los NPCs del mapa (CharacterCreate por cada uno).</summary>
    public static void SendMapNpcs(Connection conn, int mapNumber)
    {
        foreach (var n in GetMapNpcs(mapNumber))
        {
            if (n.Dead) continue;
            var (nx, ny) = Continuous.Pos(n.Map, n.X, n.Y);
            ServerPackets.CharacterCreate(conn,
                charIndex: n.CharIndex,
                body: n.Body,
                head: n.Head,
                heading: n.Heading,
                x: nx, y: ny,
                weapon: n.WeaponAnim, shield: n.ShieldAnim, helmet: n.CascoAnim, fx: 0, fxLoops: 0,
                name: n.IsBot ? NombreBotProtocolo(n) : n.NpcIndex.ToString(), // bots: "#nick"/"#*nick" (BotSmart); resto: número
                privileges: (byte)n.Status, donador: 0, particulaFx: 0, // flags.Status = color de nick (VB6)
                armaAura: (byte)n.AuraArma, bodyAura: (byte)n.Aura, escudoAura: (byte)n.AuraEscudo, headAura: (byte)n.AuraCasco, otraAura: 0, anilloAura: 0,
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
