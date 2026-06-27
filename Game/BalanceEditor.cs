using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Editor en vivo de los intervalos de Golpe y Hechizo para GMs (NUEVO, no VB6), mismo patrón
/// que ObjEditor/SpellEditor: el GM abre la pestaña "Intervalos" del panel GM, el server le manda
/// los valores actuales (BalanceEditorDetail) y al guardar:
///  1) persiste los cambios en Balance.dat sección [INTERVALOS] EN DISCO preservando el resto
///     del archivo (comentarios, otras secciones), vía IniDocument (mismo mecanismo que .chr),
///  2) recarga BalanceData en memoria (BalanceData.Reload) → efecto INMEDIATO en combate
///     (Intervals.Atacar / Intervals.LanzarSpell leen de ahí, no de una constante),
///  3) avisa a los demás GMs online.
/// Balance.dat es la única fuente de verdad: el detalle se lee SIEMPRE de BalanceData (que a su
/// vez cachea el archivo), así el round-trip es consistente con lo que ya usa el combate.
/// </summary>
public static class BalanceEditor
{
    private const byte MIN_PRIV = AdminLoader.STATUS_SEMIDIOS; // 8: mismo piso que ObjEditor/SpellEditor

    // Único intervalo permitido: piso 50ms (igual que el clamp de BalanceData), techo generoso
    // para no permitir que un GM trabado deje el combate inutilizable por accidente.
    private const long MIN_MS = 50;
    private const long MAX_MS = 20000;

    // Tasas de EXP/ORO globales: porcentaje entero (100 = x1.0), clamp 10-1000 (x0.1 a x10).
    private const int MIN_TASA = 10;
    private const int MAX_TASA = 1000;

    // Claves que van a [EXP] de Balance.dat con nombre distinto en disco (clave de protocolo -> clave del .dat).
    private static readonly Dictionary<string, string> ClavesTasa = new()
    {
        { "TasaExpGlobal", "TasaGlobal" },
        { "TasaOroGlobal", "TasaGlobalOro" },
    };

    private static readonly string[] Claves = { "Atacar", "LanzarSpell", "TasaExpGlobal", "TasaOroGlobal" };

    private static User ValidarGM(Connection conn)
    {
        var u = UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return null;
        if (AdminLoader.GetFaccionStatus(u.Name) < MIN_PRIV)
        {
            ServerPackets.ConsoleMsg(conn, "No tenés privilegios para editar los intervalos de combate.", 6);
            Console.WriteLine($"[BalanceEditor] RECHAZADO: {u.Name} intentó editar intervalos sin privilegios.");
            return null;
        }
        return u;
    }

    // ============================================================
    //  Detalle: los valores actuales (siempre desde BalanceData, ya clampeados/con defaults)
    // ============================================================
    public static void SendDetail(Connection conn)
    {
        var u = ValidarGM(conn);
        if (u == null) return;

        var cfg = BalanceData.Intervalos;
        var fields = new List<(string, string)>
        {
            ("Atacar", cfg.Atacar.ToString()),
            ("LanzarSpell", cfg.LanzarSpell.ToString()),
            ("TasaExpGlobal", ((int)Math.Round(BalanceData.Exp.TasaGlobal * 100)).ToString()),
            ("TasaOroGlobal", ((int)Math.Round(BalanceData.Exp.TasaGlobalOro * 100)).ToString()),
        };
        ServerPackets.BalanceEditorDetail(conn, fields);
    }

    // ============================================================
    //  Guardado: persistir a disco + recargar en caliente + difundir
    // ============================================================
    public static void Save(int userIndex, List<(string Key, string Value)> cambios)
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;
        var conn = u.Conn;
        if (ValidarGM(conn) == null) return;

        if (cambios == null || cambios.Count == 0)
        {
            ServerPackets.BalanceEditorResult(conn, false, "No hay cambios para guardar.");
            return;
        }

        foreach (var (key, value) in cambios)
        {
            if (Array.IndexOf(Claves, key) < 0)
            {
                ServerPackets.BalanceEditorResult(conn, false, $"Clave inválida: \"{key}\".");
                return;
            }
            if (ClavesTasa.ContainsKey(key))
            {
                if (!int.TryParse(value, out int pct) || pct < MIN_TASA || pct > MAX_TASA)
                {
                    ServerPackets.BalanceEditorResult(conn, false,
                        $"\"{key}\" debe ser un número entre {MIN_TASA} y {MAX_TASA} (porcentaje, 100 = x1.0).");
                    return;
                }
                continue;
            }
            if (!long.TryParse(value, out long ms) || ms < MIN_MS || ms > MAX_MS)
            {
                ServerPackets.BalanceEditorResult(conn, false,
                    $"\"{key}\" debe ser un número entre {MIN_MS} y {MAX_MS} (milisegundos).");
                return;
            }
        }

        string file = BalanceData.FilePath;
        if (file == null)
        {
            ServerPackets.BalanceEditorResult(conn, false, "No se encontró Balance.dat en el servidor.");
            return;
        }

        try
        {
            var doc = new IniDocument(file);
            if (!doc.Loaded)
            {
                ServerPackets.BalanceEditorResult(conn, false, "No se pudo leer Balance.dat.");
                return;
            }
            foreach (var (key, value) in cambios)
                doc.Set(ClavesTasa.ContainsKey(key) ? "EXP" : "INTERVALOS", ClavesTasa.TryGetValue(key, out var datKey) ? datKey : key, value);
            doc.Save(file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BalanceEditor] ERROR guardando Balance.dat: {ex}");
            ServerPackets.BalanceEditorResult(conn, false, "Error al escribir Balance.dat: " + ex.Message);
            return;
        }

        // Recarga en caliente: memoria == disco.
        BalanceData.Reload();
        var cfg = BalanceData.Intervalos;

        var expCfg = BalanceData.Exp;
        Console.WriteLine($"[BalanceEditor] {u.Name} guardó config: Atacar={cfg.Atacar}ms, LanzarSpell={cfg.LanzarSpell}ms, TasaExpGlobal=x{expCfg.TasaGlobal}, TasaOroGlobal=x{expCfg.TasaGlobalOro}.");
        ServerPackets.BalanceEditorResult(conn, true,
            $"Guardado. Golpe: {cfg.Atacar}ms — Hechizo: {cfg.LanzarSpell}ms — EXP global: x{expCfg.TasaGlobal:0.##} — ORO global: x{expCfg.TasaGlobalOro:0.##}. Aplicado en vivo.");

        // Re-sincronizar el gate LOCAL del cliente en TODOS los online (no solo GMs): sin esto,
        // cualquier personaje ya conectado sigue con el valor viejo hardcodeado, manda el clic
        // demasiado pronto y el server lo rechaza en silencio (parece que "castea y no lanza").
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var otro = UserListManager.UserList[i];
            if (otro?.flags.UserLogged != true || otro.Conn == null) continue;
            ServerPackets.IntervalConfig(otro.Conn, cfg.Atacar, cfg.LanzarSpell);
            if (otro != u && AdminLoader.GetFaccionStatus(otro.Name) >= MIN_PRIV)
                ServerPackets.ConsoleMsg(otro.Conn,
                    $"[Intervalos] {u.Name} cambió el cooldown de golpe/hechizo (Golpe={cfg.Atacar}ms, Hechizo={cfg.LanzarSpell}ms).", 7);
        }
    }
}
