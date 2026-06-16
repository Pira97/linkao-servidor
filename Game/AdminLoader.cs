namespace ServidorCS.Game;

/// <summary>
/// Carga los niveles de privilegio de GMs desde Server.ini.
/// VB6 equivalente: EsAdmin/EsDios/EsSemiDios/EsConsejero/EsRolesMaster/EsSoporte en FileIO.bas
/// Los GMs se listan bajo secciones [Admin], [Dios], [SemiDios], [Consejeros], [RolesMaster], [Soporte] en Server.ini.
/// </summary>
public static class AdminLoader
{
    // Faccion.Status según VB6 TCP.bas:2212-2235
    public const byte STATUS_USER      = 0;
    public const byte STATUS_CONSEJERO = 7;
    public const byte STATUS_RM        = 7;
    public const byte STATUS_SEMIDIOS  = 8;
    public const byte STATUS_DIOS      = 9;
    public const byte STATUS_ADMIN     = 9;
    public const byte STATUS_SOPORTE   = 10;

    private static readonly HashSet<string> _admins    = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _dioses    = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _semidioses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _consejeros = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _rm        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _soporte   = new(StringComparer.OrdinalIgnoreCase);

    public static void Load()
    {
        string path = DataPaths.Root + "Server.ini";
        if (!File.Exists(path)) { Console.WriteLine("[AdminLoader] Server.ini no encontrado."); return; }

        var ini = new IniFile(path);
        LoadSection(ini, "Admins",     _admins,    "Admins");
        LoadSection(ini, "Dioses",     _dioses,    "Dioses");
        LoadSection(ini, "SemiDioses", _semidioses,"SemiDioses");
        LoadSection(ini, "Consejeros", _consejeros,"Consejeros");
        LoadSection(ini, "RolesMasters",_rm,       "RolesMasters");
        LoadSection(ini, "Soporte",    _soporte,   "Soporte");

        Console.WriteLine($"[AdminLoader] GMs cargados: {_admins.Count} admins, {_dioses.Count} dioses, {_semidioses.Count} semidioses, {_consejeros.Count} consejeros, {_rm.Count} RM, {_soporte.Count} soporte.");
    }

    private static void LoadSection(IniFile ini, string section, HashSet<string> set, string countKey)
    {
        // En Server.ini, los contadores están en [INIT] y los nombres en [Dioses] como Dios1, Dios2...
        // El prefijo del nombre = section sin 's' final (ej: "Dioses" → "Dios", "Soporte" → "Soporte")
        int count = ini.GetInt("INIT", countKey);
        string prefix = section.TrimEnd('s'); // "Dioses"→"Dios", "SemiDioses"→"SemiDio", etc.
        // Usar prefijo explícito por sección
        prefix = section switch {
            "Dioses"      => "Dios",
            "SemiDioses"  => "SemiDios",
            "Consejeros"  => "Consejero",
            "RolesMasters"=> "RM",
            "Soporte"     => "Soporte",
            "Admins"      => "Admin",
            _             => section
        };
        for (int i = 1; i <= count; i++)
        {
            string name = ini.Get(section, prefix + i).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(name)) set.Add(name);
        }
    }

    /// <summary>Calcula el FaccionStatus del jugador al login (VB6 TCP.bas:2212-2235).</summary>
    public static byte GetFaccionStatus(string name)
    {
        string n = name.ToUpperInvariant();
        if (_admins.Contains(n))    return STATUS_ADMIN;
        if (_dioses.Contains(n))    return STATUS_DIOS;
        if (_semidioses.Contains(n))return STATUS_SEMIDIOS;
        if (_consejeros.Contains(n))return STATUS_CONSEJERO;
        if (_rm.Contains(n))        return STATUS_RM;
        if (_soporte.Contains(n))   return STATUS_SOPORTE;
        return STATUS_USER;
    }

    public static bool EsGM(string name) => GetFaccionStatus(name) >= STATUS_CONSEJERO;
}
