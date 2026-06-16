namespace ServidorCS.Game;

/// <summary>
/// Cooldowns / intervalos de acciones. Valores reales de Server.ini (LinkAO 1.4.5).
/// Porta modNuevoTimer.bas: cada IntervaloPermite* devuelve true si el intervalo ya pasó
/// (y actualiza el timer), false si está en cooldown. 1:1 con el comportamiento VB6.
/// </summary>
public static class Intervals
{
    // Valores en milisegundos (Server.ini).
    public const long Atacar = 1200;        // IntervaloUserPuedeAtacar
    public const long LanzarSpell = 500;    // IntervaloLanzaHechizo
    public const long Trabajar = 700;       // IntervaloUserPuedeTrabajar (default)
    public const long Usar = 125;           // IntervaloUserPuedeUsar
    public const long ClicsMouse = 200;     // IntervaloClicsMouse (anti-autoclicker)
    public const long MagiaGolpe = 400;     // IntervaloMagiaGolpe
    public const long GolpeMagia = 400;     // IntervaloGolpeMagia
    public const long GolpeUsar = 300;      // IntervaloGolpeUsar (pociones) — autopot ~3.3/seg
    public const long UsarArco = 1100;      // IntervaloFlechasCazadores
    public const long NpcAtacar = 3000;     // IntervaloPermiteAtacarNpc hardcodea 3000ms (ignora el .ini=2500)

    // Reloj de alta resolución (Stopwatch). Sub-milisegundo y monotónico, a diferencia de
    // Environment.TickCount64 que en Windows salta de a ~15.6ms (tick del scheduler).
    // Esto hace que los intervalos de golpe/magia sean precisos y reproducibles.
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static long Now => _clock.ElapsedMilliseconds;

    /// <summary>Devuelve true si pasó 'intervalo' desde 'lastTimer'. Si 'actualizar', lo pone en ahora.</summary>
    private static bool Check(ref long lastTimer, long intervalo, bool actualizar = true)
    {
        long now = Now;
        if (now - lastTimer >= intervalo) { if (actualizar) lastTimer = now; return true; }
        return false;
    }

    /// <summary>IntervaloPermiteAtacar 1:1: aplica ExtraTimer del arma (neg=más rápido) y Furor Ígneo
    /// (×0.5), con piso de 50ms. 'actualizar=false' = chequeo read-only (no consume el timer).</summary>
    public static bool PuedeAtacar(User u, bool actualizar = true)
    {
        long intervalo = Atacar;
        int w = u.Invent.WeaponEqpObjIndex;
        if (w > 0)
        {
            int extra = ObjData.Get(w).ExtraTimer;
            if (extra != 0) intervalo += extra;
        }
        if (u.flags.FurorIgneo) intervalo = (long)(intervalo * 0.5);
        if (intervalo < 50) intervalo = 50;
        return Check(ref u.TimerAtacar, intervalo, actualizar);
    }

    public static bool PuedeLanzarSpell(User u, bool actualizar = true) => Check(ref u.TimerLanzarSpell, LanzarSpell, actualizar);
    public static bool PuedeTrabajar(User u)   => Check(ref u.TimerTrabajar, Trabajar);
    public static bool PuedeUsar(User u)       => Check(ref u.TimerUsar, Usar);
    public static bool PuedeClicsMouse(User u) => Check(ref u.TimerClicsMouse, ClicsMouse);
    public static bool PuedeUsarArco(User u, bool actualizar = true) => Check(ref u.TimerUsarArco, UsarArco, actualizar);
    public static bool PuedeGolpeUsar(User u)  => Check(ref u.TimerGolpeUsar, GolpeUsar);

    /// <summary>IntervaloPermiteAtacarNpc 1:1: gate único por NPC (3000ms) compartido por el golpe
    /// físico y el casteo del NPC. Pasar el campo TimerAtaque del NPC por referencia.</summary>
    public static bool PuedeAtacarNpc(ref long timerAtaque) => Check(ref timerAtaque, NpcAtacar);

    /// <summary>Variante con intervalo propio del NPC (guardias usan 2000ms en vez de 3000ms para
    /// ser más decididos en combate; custom, no 1:1). Si 'intervalo' &lt;= 0 cae al default NpcAtacar.</summary>
    public static bool PuedeAtacarNpc(ref long timerAtaque, long intervalo) => Check(ref timerAtaque, intervalo > 0 ? intervalo : NpcAtacar);

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
