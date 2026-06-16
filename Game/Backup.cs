namespace ServidorCS.Game;

/// <summary>
/// Backup programado (modBackup.bas) 1:1: además del autosave que sobrescribe los .chr,
/// hace un SNAPSHOT con timestamp en Backups\Auto_&lt;yyyyMMdd_HHmm&gt;\Charfile\ (copia de todos
/// los .chr) y limpia los snapshots antiguos (mantiene los últimos MaxBackupsGuardados, def 10).
/// Recuperación ante desastre: el autosave puede guardar un estado corrupto; los snapshots
/// permiten volver atrás.
/// </summary>
public static class Backup
{
    /// <summary>BackupProgramado: graba los online y copia todos los .chr a un snapshot fechado.</summary>
    public static void Snapshot()
    {
        try
        {
            // 1) Asegurar que los .chr estén al día (GuardarUsuarios → SaveUser de cada online).
            CharSaver.SaveAllOnline();
            BattlePass.SaveAll(); // progreso del pase de temporada al día antes del snapshot

            // 2) Copiar todos los .chr a Backups\Auto_<timestamp>\Charfile\.
            string charDir = CharLoader.CharPath;
            if (!Directory.Exists(charDir)) return;

            string backupsRoot = BackupsRoot();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string dest = Path.Combine(backupsRoot, "Auto_" + stamp, "Charfile");
            Directory.CreateDirectory(dest);

            int copied = 0;
            foreach (var f in Directory.GetFiles(charDir, "*.chr"))
            {
                try { File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true); copied++; }
                catch { /* archivo en uso: omitir */ }
            }

            // 3) Copiar el progreso del pase de temporada (BattlePass\*.json) al snapshot.
            int bpCopied = 0;
            try
            {
                string bpDir = string.IsNullOrEmpty(DataPaths.Root) ? "BattlePass" : DataPaths.Sub("BattlePass");
                if (Directory.Exists(bpDir))
                {
                    string bpDest = Path.Combine(backupsRoot, "Auto_" + stamp, "BattlePass");
                    Directory.CreateDirectory(bpDest);
                    foreach (var f in Directory.GetFiles(bpDir, "*.json"))
                    {
                        try { File.Copy(f, Path.Combine(bpDest, Path.GetFileName(f)), overwrite: true); bpCopied++; }
                        catch { /* archivo en uso: omitir */ }
                    }
                }
            }
            catch { /* no romper el backup de personajes por el pase */ }

            CleanupOld(backupsRoot);
            Console.WriteLine($"[Backup] Snapshot Auto_{stamp} completado ({copied} personajes, {bpCopied} pases).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Backup] Error en Snapshot: {ex.Message}");
        }
    }

    /// <summary>LimpiarBackupsAntiguos: deja solo los últimos MaxBackupsGuardados snapshots Auto_*.</summary>
    private static void CleanupOld(string backupsRoot)
    {
        int max = MaxBackups();
        if (max <= 0) max = 10;

        var dirs = Directory.GetDirectories(backupsRoot, "Auto_*");
        if (dirs.Length <= max) return;

        // Ordenar por fecha de creación ascendente y borrar los más antiguos.
        Array.Sort(dirs, (a, b) => Directory.GetCreationTime(a).CompareTo(Directory.GetCreationTime(b)));
        for (int i = 0; i < dirs.Length - max; i++)
        {
            try { Directory.Delete(dirs[i], recursive: true); Console.WriteLine($"[Backup] Snapshot antiguo eliminado: {Path.GetFileName(dirs[i])}"); }
            catch { /* ignorar */ }
        }
    }

    private static string BackupsRoot()
    {
        string root = string.IsNullOrEmpty(DataPaths.Root)
            ? Path.Combine(AppContext.BaseDirectory, "Backups")
            : DataPaths.Sub("Backups").TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>MaxBackupsGuardados de Server.ini [INTERVALOS] (default 10).</summary>
    private static int MaxBackups()
    {
        try
        {
            string ini = (string.IsNullOrEmpty(DataPaths.Root)
                ? AppContext.BaseDirectory
                : DataPaths.Root) + "Server.ini";
            if (!File.Exists(ini)) return 10;
            var doc = new IniFile(ini);
            int v = doc.GetInt("INTERVALOS", "MaxBackupsGuardados");
            return v > 0 ? v : 10;
        }
        catch { return 10; }
    }
}
