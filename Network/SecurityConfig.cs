namespace ServidorCS.Network;

/// <summary>
/// Valores centralizados de los límites anti-abuso agregados en la auditoría de seguridad
/// (24-ago-2026). Un solo lugar para tunear umbrales sin tocar la lógica en sí. Nada acá
/// reemplaza AntiDos (conexiones por IP) ni AntiCheat (uso de items): son límites nuevos
/// que faltaban — fuerza bruta de login, tamaño de paquete/backlog, y ráfagas de chat.
/// </summary>
public static class SecurityConfig
{
    // --- Login (fuerza bruta) ---
    /// <summary>Intentos fallidos antes de empezar a bloquear (por cuenta y por IP, por separado).</summary>
    public const int LoginMaxIntentosFallidos = 5;
    /// <summary>Ventana en la que se cuentan los intentos fallidos.</summary>
    public const long LoginVentanaMs = 60_000;
    /// <summary>Bloqueo inicial al superar el máximo. Escala x2 por cada bloqueo consecutivo (ver LoginThrottle).</summary>
    public const long LoginBloqueoBaseMs = 30_000;
    /// <summary>Techo del bloqueo escalado, para no dejar una IP/cuenta bloqueada "para siempre" por error.</summary>
    public const long LoginBloqueoMaxMs = 15 * 60_000;

    // --- Tamaño / backlog de paquetes ---
    /// <summary>
    /// Backlog máximo (bytes) sin parsear en IncomingData antes de cortar la conexión. Un packet
    /// legítimo más grande (ASCIIString con prefijo Int16) nunca pasa de ~32KB por campo; dejar
    /// margen amplio para ráfagas normales (varios packets en el mismo recv) y cortar solo ante
    /// un backlog que sólo se explica por basura que nunca completa un packet válido.
    /// </summary>
    public const int MaxIncomingBacklogBytes = 131_072; // 128 KB

    // --- Chat: solo ráfaga, no cadencia conversacional (ver Chat.cs, el cooldown de 8s se sacó a pedido) ---
    public const int ChatMaxMensajesPorVentana = 8;
    public const long ChatVentanaMs = 2_000;
    /// <summary>Largo máximo de un mensaje de chat (protocolo ya lo acota con el prefijo Int16, esto es una cota razonable de gameplay).</summary>
    public const int ChatMaxLargoMensaje = 250;

    // --- Movimiento: sólo cadencia (Movement.MoveUserChar valida posición/colisión, no ritmo).
    // Un tile real tarda ~238ms (ver [[linkao-caminata-frames]]); el límite deja margen amplio
    // (más del doble del ritmo real) para no rozar el juego legítimo con lag/catch-up.
    public const int MovimientoMaxPorVentana = 10;
    public const long MovimientoVentanaMs = 1_000;

    // --- Comandos GM: ráfaga, no cadencia (un GM tipeando rápido no debería verse afectado). ---
    public const int GmComandoMaxPorVentana = 10;
    public const long GmComandoVentanaMs = 3_000;

    // --- Logging agregado ---
    /// <summary>Mismo (categoría+clave) no se vuelve a escribir en el log antes de este intervalo; se agrega un contador.</summary>
    public const long SecurityLogAgregacionMs = 5_000;

    // ================================================================================
    // Auditoría DDoS/DoS (24-ago-2026): presupuesto de GameLock, rate-limits de gameplay
    // caro, y tope de cola de salida. Ver el informe de auditoría para el detalle de cada
    // hallazgo (C1/C2/C3/H3/H4/M4).
    // ================================================================================

    // --- Fix C1: presupuesto de paquetes por adquisición de GameLock ---
    /// <summary>
    /// Máximo de paquetes que HandleIncomingData procesa en UNA sola adquisición de GameLock
    /// por conexión. Antes se vaciaba TODO el backlog de una conexión bajo un solo lock: una
    /// ráfaga de miles de paquetes chicos (p.ej. ataques a velocidad de red) podía monopolizar
    /// el lock y congelar el tick de IA y a todos los demás jugadores — sin necesitar mucho
    /// ancho de banda. Al llegar al tope, el resto queda en la cola (IncomingData no se toca)
    /// y se re-procesa en una re-adquisición posterior, dándole lugar a otras conexiones y al
    /// GameLoop entre medio. Un jugador legítimo nunca manda cientos de paquetes en un solo
    /// recv(); el valor deja margen amplio (varias veces el pico normal de entrada al mundo:
    /// inventario + hechizos + stats) sin permitir que una ráfaga monopolice el lock.
    /// </summary>
    public const int MaxPaquetesPorAdquisicionDeLock = 300;

    // --- Fix C2: rate-limit de acciones de gameplay caras (ninguna tenía cooldown de
    // servidor — el ritmo lo marcaba sólo el maná/stamina/UI del cliente). Estos límites NO
    // buscan imponer cadencia de juego: son deliberadamente generosos, muy por encima de
    // cualquier cadencia humana/UI real, y sólo cortan el caso imposible de un script/bot
    // mandando paquetes a velocidad de red (que además dispara broadcasts costosos, ver
    // AreaVisibility/Combat). ---

    /// <summary>Ataque cuerpo a cuerpo (HandleAttack). El arma más rápida del juego pega bastante
    /// más lento que esto; 6/seg da margen &gt;2x sobre cualquier cadencia real de combate.</summary>
    public const int AtaqueMaxPorVentana = 6;
    public const long AtaqueVentanaMs = 1_000;

    /// <summary>Lanzar hechizo (HandleCastSpell). Cada cast real implica animación + gasto de
    /// maná/stamina; 5/seg es imposible de sostener jugando de verdad.</summary>
    public const int HechizoMaxPorVentana = 5;
    public const long HechizoVentanaMs = 1_000;

    /// <summary>Clicks sobre NPCs/tiles: seleccionar (LeftClick), interactuar (DoubleClick) y
    /// trabajar/lanzar-en-tile (WorkLeftClick). Clickear rápido explorando el mapa es normal,
    /// pero 15/seg ya excede cualquier ritmo de mouse humano y cada click puede disparar
    /// pathfinding/IA/combate a distancia.</summary>
    public const int NpcClickMaxPorVentana = 15;
    public const long NpcClickVentanaMs = 1_000;

    /// <summary>Comercio: con NPC (abrir/cerrar/comprar/vender) y entre jugadores (ofertar,
    /// confirmar, cancelar). Armar una oferta a mano —arrastrando items/oro— no pasa de unos
    /// pocos clicks por segundo.</summary>
    public const int ComercioMaxPorVentana = 10;
    public const long ComercioVentanaMs = 1_000;

    /// <summary>Bóveda: depositar/extraer item u oro (incluye bóvedas premium).</summary>
    public const int BancoMaxPorVentana = 10;
    public const long BancoVentanaMs = 1_000;

    /// <summary>Inventario: equipar, mover/soltar/levantar/destruir items.</summary>
    public const int InventarioMaxPorVentana = 15;
    public const long InventarioVentanaMs = 1_000;

    /// <summary>Mensajes de grupo/clan (difusión a todos los miembros — costo de broadcast,
    /// no sólo de proceso). Escribir en el chat de grupo no pasa de unos pocos mensajes cada
    /// pocos segundos, igual que el chat normal.</summary>
    public const int GrupoMensajeMaxPorVentana = 5;
    public const long GrupoMensajeVentanaMs = 2_000;

    // --- Fix C3: tope de OutgoingData (simétrico al de IncomingData) ---
    /// <summary>
    /// Backlog máximo (bytes) sin enviar en OutgoingData antes de cortar la conexión. Sin esto,
    /// un cliente lento/malicioso que nunca lee su socket hace crecer esta cola sin límite
    /// mientras el server le sigue mandando broadcasts (chat, combate, movimiento de otros) —
    /// agota memoria del lado del server sin que el atacante mande un solo byte de más. Mismo
    /// orden de magnitud que el tope de entrada: un jugador real nunca acumula tanto sin enviar,
    /// porque el flush corre cada ~10ms.
    /// </summary>
    public const int MaxOutgoingBacklogBytes = 131_072; // 128 KB
}
