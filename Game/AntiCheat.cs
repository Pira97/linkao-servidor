using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Anti-autoclicker de uso de items (AntiAutoClicker.bas / modItemDelay) 1:1. Dos defensas
/// server-side sobre el uso manual de items:
///  - VerificarLimitePaquetes: rate-limit (máx 30/seg manual, 10/seg autopot) en ventana de 1s.
///  - PuedeUsarItem: cooldown mínimo (100ms) + detección de patrón (intervalos demasiado regulares
///    entre las últimas muestras = autoclicker). El autopot legítimo (token 97) hace bypass.
/// </summary>
public static class AntiCheat
{
    private const long ITEM_USE_DELAY_MS = 100;
    private const byte PATRON_SAMPLES = 10;
    private const long PATRON_TOLERANCIA_MS = 50;
    private const byte DETECCIONES_PARA_AVISO = 3;

    private static long Now => Environment.TickCount64 & 0x7FFFFFFF;

    /// <summary>VerificarLimitePaquetes: true si está dentro del límite de paquetes/seg.</summary>
    public static bool VerificarLimitePaquetes(int userIndex, bool esAutoPot)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null) return true;
        const int MAX_MANUAL = 30, MAX_AUTOPOT = 10, VENTANA_MS = 1000;
        int limite = esAutoPot ? MAX_AUTOPOT : MAX_MANUAL;
        var ac = u.AntiClick;
        long t = Now;

        if (t - ac.VentanaInicio > VENTANA_MS) { ac.VentanaInicio = t; ac.ContadorPaquetes = 0; }
        ac.ContadorPaquetes++;
        if (ac.ContadorPaquetes > limite)
        {
            ac.ContadorPaquetes = 0;
            ac.VentanaInicio = t;
            return false;
        }
        return true;
    }

    /// <summary>PuedeUsarItem: cooldown mínimo + detección de patrón. false = bloquear.</summary>
    public static bool PuedeUsarItem(int userIndex, bool esAutoPot)
    {
        if (esAutoPot) return true;
        var u = UserListManager.UserList[userIndex];
        if (u == null) return true;
        var ac = u.AntiClick;
        long t = Now;

        if (ac.UltimoUso > 0)
        {
            long transcurrido = t - ac.UltimoUso;
            if (transcurrido < 0) transcurrido += 0x80000000L; // wrap de GetTickCount&0x7FFFFFFF
            if (transcurrido < ITEM_USE_DELAY_MS) { ac.UltimoUso = t; return false; }
        }

        RegistrarUso(ac, t);

        if (DetectarPatron(ac))
        {
            ac.Detecciones++;
            if (ac.Detecciones > 5)
            {
                ResetearHistorial(ac);
                ac.UltimoUso = t;
                return false;
            }
            if (ac.Detecciones >= DETECCIONES_PARA_AVISO)
            {
                NotificarDeteccion(u);
                ac.Detecciones = 0;
            }
        }
        else if (ac.Detecciones > 0) ac.Detecciones--;

        ac.UltimoUso = t;
        return true;
    }

    public static void ResetearHistorial(int userIndex)
    {
        var u = UserListManager.UserList[userIndex];
        if (u != null) ResetearHistorial(u.AntiClick);
    }

    // --- privado ---

    private static void RegistrarUso(AntiClickState ac, long t)
    {
        ac.Indice++;
        if (ac.Indice > PATRON_SAMPLES) ac.Indice = 1;
        ac.Tiempos[ac.Indice] = t;
        if (ac.Muestras < PATRON_SAMPLES) ac.Muestras++;
    }

    /// <summary>DetectarPatronAutoClicker: true si los intervalos entre clics son demasiado regulares.</summary>
    private static bool DetectarPatron(AntiClickState ac)
    {
        if (ac.Muestras < 5 || ac.Muestras > PATRON_SAMPLES) return false;

        var intervalos = new List<long>();
        for (int i = 1; i <= ac.Muestras - 1; i++)
        {
            int idxActual = ac.Indice - i + 1;
            if (idxActual <= 0) idxActual += PATRON_SAMPLES;
            if (idxActual > PATRON_SAMPLES) idxActual -= PATRON_SAMPLES;
            int idxAnterior = idxActual - 1;
            if (idxAnterior <= 0) idxAnterior = PATRON_SAMPLES;
            if (idxActual < 1 || idxActual > PATRON_SAMPLES || idxAnterior < 1 || idxAnterior > PATRON_SAMPLES) break;

            long tA = ac.Tiempos[idxActual], tAnt = ac.Tiempos[idxAnterior];
            long iv = tA - tAnt;
            if (iv < 0) iv += 0x80000000L;
            if (iv > 0 && iv < 5000) intervalos.Add(iv);
        }
        if (intervalos.Count < 4) return false;

        double suma = 0; foreach (var v in intervalos) suma += v;
        double promedio = suma / intervalos.Count;
        if (promedio < 0 || promedio > 5000) return false;
        long promLong = (long)promedio;

        long desvMax = 0;
        foreach (var v in intervalos)
        {
            long dif = v >= promLong ? v - promLong : promLong - v;
            if (dif > desvMax) desvMax = dif;
        }
        return desvMax <= PATRON_TOLERANCIA_MS; // intervalos casi idénticos = autoclicker
    }

    private static void ResetearHistorial(AntiClickState ac)
    {
        ac.UltimoUso = 0; ac.Indice = 0; ac.Muestras = 0; ac.Detecciones = 0;
        ac.VentanaInicio = 0; ac.ContadorPaquetes = 0;
        Array.Clear(ac.Tiempos, 0, ac.Tiempos.Length);
    }

    /// <summary>NotificarDeteccion: avisa al usuario y a los GMs online de un posible autoclicker.</summary>
    private static void NotificarDeteccion(User u)
    {
        if (u.Conn != null)
            ServerPackets.ConsoleMsg(u.Conn, "Se ha detectado un patrón de uso automático de items. Detené el autoclicker.", 2);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var gm = UserListManager.UserList[i];
            if (gm != null && gm.flags.UserLogged && gm.Conn != null && gm.FaccionStatus >= AdminLoader.STATUS_CONSEJERO)
                ServerPackets.ConsoleMsg(gm.Conn, $"[ANTICHEAT] Posible autoclicker: {u.Name}.", 8);
        }
        Console.WriteLine($"[ANTICHEAT] Posible autoclicker detectado: {u.Name}");
    }
}
