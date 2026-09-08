namespace ServidorCS.Game;

/// <summary>
/// Cooldowns / intervalos de acciones. Valores reales de Server.ini (LinkAO 1.4.5).
/// Porta modNuevoTimer.bas: cada IntervaloPermite* devuelve true si el intervalo ya pasó
/// (y actualiza el timer), false si está en cooldown. 1:1 con el comportamiento VB6.
/// </summary>
public static class Intervals
{
    // Valores en milisegundos (Server.ini). Atacar/LanzarSpell viven en Balance.dat [INTERVALOS]
    // (editables en vivo desde el panel GM, ver BalanceEditor.cs) — el resto sigue fijo en código.
    public static long Atacar => BalanceData.Intervalos.Atacar;             // IntervaloUserPuedeAtacar
    public static long LanzarSpell => BalanceData.Intervalos.LanzarSpell;   // IntervaloLanzaHechizo
    public const long Trabajar = 700;       // IntervaloUserPuedeTrabajar (default)
    public const long Usar = 125;           // IntervaloUserPuedeUsar
    public const long ClicsMouse = 200;     // IntervaloClicsMouse (anti-autoclicker)
    public const long MagiaGolpe = 400;     // IntervaloMagiaGolpe
    public const long GolpeMagia = 400;     // IntervaloGolpeMagia
    public const long GolpeUsar = 290;      // IntervaloGolpeUsar (pociones) — autopot ~3.4/seg (225 -> 190 -> 240 -> 260 -> 300 -> 280 -> 290 el 15-sep-2026)
    public const long UsarArco = 1100;      // IntervaloFlechasCazadores
    public const long NpcAtacar = 3000;     // IntervaloPermiteAtacarNpc hardcodea 3000ms (ignora el .ini=2500)

    // Reloj de alta resolución (Stopwatch). Sub-milisegundo y monotónico, a diferencia de
    // Environment.TickCount64 que en Windows salta de a ~15.6ms (tick del scheduler).
    // Esto hace que los intervalos de golpe/magia sean precisos y reproducibles.
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static long Now => _clock.ElapsedMilliseconds;

    /// <summary>
    /// EL MISMO reloj con el que se miden todos los intervalos, para quien tenga que agendar algo
    /// contra ellos (el casteo diferido, ver Combat.TickCastDiferido). Programar una espera con
    /// Environment.TickCount64 y después chequear el gate con este Stopwatch NO es equivalente:
    /// TickCount64 avanza a saltos de ~15.6ms, así que la espera podía vencer hasta un salto ANTES
    /// que el intervalo real y el reintento se descartaba. Ese error depende de en qué punto del
    /// salto caiga cada clic, o sea que el bug aparece y desaparece solo mientras el server corre.
    /// </summary>
    public static long NowMs => Now;

    /// <summary>Devuelve true si pasó 'intervalo' desde 'lastTimer'. Si 'actualizar', lo pone en ahora.</summary>
    private static bool Check(ref long lastTimer, long intervalo, bool actualizar = true)
    {
        long now = Now;
        if (now - lastTimer >= intervalo) { if (actualizar) lastTimer = now; return true; }
        return false;
    }

    /// <summary>
    /// Cadencia REAL de golpe de este usuario: el intervalo base (Balance.dat) MÁS el ExtraTimer
    /// del arma equipada (neg = más rápido, pos = más lento), con piso de 50ms. NO incluye el
    /// ×0.5 de Furor Ígneo a propósito: ese factor lo espeja el cliente por su cuenta
    /// (game.html::intAtaque, que ya sigue FUROR_IGNEO_TIMERS).
    ///
    /// Es el número que hay que mandarle al cliente con IntervalConfig. Antes se le mandaba el
    /// intervalo base pelado y el cliente no sabía nada del ExtraTimer: con un arma lenta
    /// (hay 35 en obj.dat, de +50 a +300ms) el gate local vencía ANTES que el del server, el
    /// golpe salía, el server lo descartaba en silencio acá abajo y el cliente igual arrancaba
    /// su cooldown — o sea que esa tecla se perdía entera y el golpe recién entraba al intento
    /// siguiente. Eso es el "el golpe sale muy tarde" que reportaron los jugadores.
    /// </summary>
    public static long IntervaloAtaque(User u)
    {
        long intervalo = Atacar;
        int w = u.Invent.WeaponEqpObjIndex;
        if (w > 0)
        {
            int extra = ObjData.Get(w).ExtraTimer;
            if (extra != 0) intervalo += extra;
        }
        return intervalo < 50 ? 50 : intervalo;
    }

    /// <summary>
    /// Manda IntervalConfig con la cadencia real de golpe SÓLO si cambió desde el último envío
    /// (compare-and-send: llamarlo de más no cuesta un paquete). Hay que llamarlo en todo punto
    /// donde pueda cambiar el arma equipada o el balance; GameTimer.Tick lo llama una vez por
    /// segundo por usuario como red de seguridad, para que ningún camino de equipar/romper arma
    /// que se agregue después deje al cliente con el número viejo.
    /// </summary>
    public static void SyncConfig(User u)
    {
        if (u?.Conn == null || !u.flags.UserLogged) return;
        long ataque = IntervaloAtaque(u);
        long spell = LanzarSpell;
        // El compare tiene que mirar LOS DOS números que viajan en el paquete. Cuando miraba
        // sólo el de golpe, cambiar en el panel GM únicamente el cooldown de hechizo no mandaba
        // nada: el cliente seguía con el valor viejo, su gate local vencía antes que el del
        // server, el clic llegaba temprano y Combat.LanzarHechizoEn lo descartaba en silencio.
        // Desde el juego se ve como "el intervalo se desconfigura y el hechizo no sale", y no lo
        // arregla ni esperar, porque el reenvío por segundo de GameTimer caía en este mismo
        // compare y también se devolvía.
        if (ataque == u.UltimoIntervaloAtaqueEnviado && spell == u.UltimoIntervaloSpellEnviado) return;
        u.UltimoIntervaloAtaqueEnviado = ataque;
        u.UltimoIntervaloSpellEnviado = spell;
        Network.ServerPackets.IntervalConfig(u.Conn, ataque, spell);
    }

    /// <summary>IntervaloPermiteAtacar 1:1: aplica ExtraTimer del arma (neg=más rápido) y Furor Ígneo
    /// (×0.5), con piso de 50ms. 'actualizar=false' = chequeo read-only (no consume el timer).</summary>
    public static bool PuedeAtacar(User u, bool actualizar = true)
    {
        long intervalo = IntervaloAtaque(u);
        if (u.flags.FurorIgneo) intervalo = (long)(intervalo * 0.5);
        // Tolerancia de red, la MISMA que las pociones (ver PuedeGolpeUsar, que ya nombra a este
        // intervalo como el caso original del problema... pero nunca la tuvo aplicada).
        //
        // El cliente arranca SU cooldown cuando MANDA el paquete; el server lo mide cuando LLEGA.
        // O sea que la separación que ve el server es la del cliente ± la diferencia de jitter
        // entre dos paquetes. Con los dos en el mismo número pelado, el golpe que salió en tiempo
        // llega unos ms temprano, el server lo DESCARTA EN SILENCIO — y el cliente ya gastó su
        // cooldown igual. Ese golpe se pierde entero y el hueco real pasa a ser el doble.
        //
        // Desde el juego se ve exactamente como lo reportaron: "el ataque sale pero no toma el
        // daño, como que ataco al aire". No hay swing ni mensaje porque el paquete no llegó a
        // ejecutarse: murió en el gate.
        //
        // NO acelera el golpe: el que marca el paso sigue siendo el cooldown del cliente, que usa
        // el intervalo autoritativo que este mismo server le sincroniza (SyncConfig →
        // IntervalConfig). Sólo evita descartar por unos pocos ms al golpe que YA venía en tiempo.
        // El rate-limit por ventana de AntiCheat sigue siendo el techo duro contra un cliente
        // modificado.
        intervalo -= GOLPE_USAR_TOLERANCIA;
        if (intervalo < 50) intervalo = 50;
        return Check(ref u.TimerAtacar, intervalo, actualizar);
    }

    public static bool PuedeLanzarSpell(User u, bool actualizar = true) => Check(ref u.TimerLanzarSpell, LanzarSpell, actualizar);
    public static bool PuedeTrabajar(User u)   => Check(ref u.TimerTrabajar, Trabajar);
    public static bool PuedeUsar(User u)       => Check(ref u.TimerUsar, Usar);
    public static bool PuedeClicsMouse(User u) => Check(ref u.TimerClicsMouse, ClicsMouse);
    public static bool PuedeUsarArco(User u, bool actualizar = true) => Check(ref u.TimerUsarArco, UsarArco, actualizar);
    /// <summary>
    /// Gate de pociones. Descuenta GOLPE_USAR_TOLERANCIA del intervalo porque el cliente
    /// arranca SU cooldown cuando manda el paquete, y el server lo mide cuando llega: con los
    /// dos en el mismo número pelado, cualquier jitter de red hace que la poción entre a los
    /// 198ms de la anterior, el server la descarte en silencio, y el cliente igual ya gastó su
    /// cooldown — esa poción se pierde entera y el hueco real pasa a ser el doble del
    /// intervalo. Eso es el "el autopot se corta" que reportaron los jugadores, y es el mismo
    /// bug que ya estaba documentado para el intervalo de golpe en IntervaloAtaque.
    ///
    /// La tolerancia NO acelera el poteo: el que marca el paso sigue siendo el cooldown del
    /// cliente (MacroLogic.COOLDOWN_AUTOPOT), y el rate-limit de 10/seg de AntiCheat sigue
    /// siendo el techo duro contra un cliente modificado. Sólo evita descartar por unos pocos
    /// ms al paquete que YA venía en tiempo.
    /// </summary>
    public const long GOLPE_USAR_TOLERANCIA = 40;
    public static bool PuedeGolpeUsar(User u)  => Check(ref u.TimerGolpeUsar, GolpeUsar - GOLPE_USAR_TOLERANCIA);

    /// <summary>IntervaloPermiteAtacarNpc 1:1: gate por NPC (3000ms), mismo cooldown de siempre.
    /// [[FIX3]] Antes se pasaba SIEMPRE el mismo campo TimerAtaque del NPC (compartido por golpe
    /// físico y casteo): ahora cada caller pasa el timer que corresponde (TimerAtaqueFisico o
    /// TimerAtaqueHechizo del NpcInstance), así el NPC puede golpear y castear en ventanas
    /// independientes en vez de "uno u otro cada 3000ms". Los valores de cooldown NO cambiaron.</summary>
    public static bool PuedeAtacarNpc(ref long timerAtaque) => Check(ref timerAtaque, NpcAtacar);

    /// <summary>Variante con intervalo propio del NPC (guardias usan 2000ms en vez de 3000ms para
    /// ser más decididos en combate; custom, no 1:1). Si 'intervalo' &lt;= 0 cae al default NpcAtacar.</summary>
    public static bool PuedeAtacarNpc(ref long timerAtaque, long intervalo) => Check(ref timerAtaque, intervalo > 0 ? intervalo : NpcAtacar);

    /// <summary>
    /// Cuántos ms faltan para que ESTE usuario pueda castear, con los mismos gates (y en el mismo
    /// orden) que mira Combat.LanzarHechizoEn. Es read-only: no consume ni mueve ningún timer.
    /// Sirve para diferir un casteo que llegó apenas antes de tiempo en vez de tirarlo a la basura
    /// — ver Combat.TickCastDiferido(). 0 = se puede ahora mismo.
    /// </summary>
    public static long FaltaParaCastear(User u)
    {
        long now = Now;
        long faltaArco = UsarArco - (now - u.TimerUsarArco);
        // Enclavamiento golpe→magia: si todavía no se consumió, habilita el casteo antes que el
        // propio intervalo de hechizo, así que de los dos manda el que primero deja pasar.
        long faltaGolpeMagia = u.TimerGolpeMagia <= u.TimerAtacar
            ? GolpeMagia - (now - u.TimerAtacar) : long.MaxValue;
        long faltaSpell = LanzarSpell - (now - u.TimerLanzarSpell);
        long falta = Math.Max(faltaArco, Math.Min(faltaGolpeMagia, faltaSpell));
        return falta > 0 ? falta : 0;
    }

    /// <summary>IntervaloPermiteMagiaGolpe 1:1: ¿pasó suficiente desde el último casteo para golpear?
    /// Si pasó, fija TimerMagiaGolpe y TimerAtacar (consume el cooldown de ataque). Guard: si ya se
    /// consumió (TimerMagiaGolpe>TimerLanzarSpell) devuelve false → el caller cae al chequeo de Atacar.</summary>
    public static bool PuedeMagiaGolpe(User u)
    {
        if (u.TimerMagiaGolpe > u.TimerLanzarSpell) return false;
        long now = Now;
        if (now - u.TimerLanzarSpell >= MagiaGolpe)
        {
            u.TimerMagiaGolpe = now;
            u.TimerAtacar = now;   // VB6: Counters.TimerPuedeAtacar
            return true;
        }
        return false;
    }

    /// <summary>IntervaloPermiteGolpeMagia 1:1: ¿pasó suficiente desde el último golpe para castear?
    /// Si pasó, fija TimerGolpeMagia y TimerLanzarSpell. Guard simétrico al de MagiaGolpe.</summary>
    public static bool PuedeGolpeMagia(User u)
    {
        if (u.TimerGolpeMagia > u.TimerAtacar) return false;
        long now = Now;
        if (now - u.TimerAtacar >= GolpeMagia)
        {
            u.TimerGolpeMagia = now;
            u.TimerLanzarSpell = now;
            return true;
        }
        return false;
    }
}
