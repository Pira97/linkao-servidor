using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Subida de habilidades — SubirSkill (Modulo_UsUaRiOs.bas:1701) portado 1:1 desde VB6.
/// </summary>
public static class Skills
{
    public const int MAXSKILLPOINTS = 100;   // Declares.bas:464

    // Nombre de cada skill por su id (eSkill, Declares.bas:504). Índice = id (1..27).
    private static readonly string[] SkillNombre =
    {
        "",                       // 0 (sin uso)
        "Tácticas de combate",    // 1 Tacticas
        "Combate con armas",      // 2 armas
        "Lucha libre",            // 3 Wrestling
        "Apuñalar",               // 4 Apuñalar
        "Armas arrojadizas",      // 5 ArmasArrojadizas
        "Proyectiles",            // 6 Proyectiles
        "Defensa con escudos",    // 7 Defensa
        "Magia",                  // 8 magia
        "Resistencia mágica",     // 9 Resistencia
        "Meditar",                // 10 Meditar
        "Ocultarse",              // 11 Ocultarse
        "Domar animales",         // 12 domar
        "Suerte",                 // 13 Suerte
        "Robar",                  // 14 robar
        "Comerciar",              // 15 comerciar
        "Supervivencia",          // 16 Supervivencia
        "Liderazgo",              // 17 Liderazgo
        "Pesca",                  // 18 pesca
        "Minería",                // 19 mineria
        "Talar",                  // 20 talar
        "Botánica",               // 21 botanica
        "Herrería",               // 22 Herreria
        "Carpintería",            // 23 Carpinteria
        "Alquimia",               // 24 alquimia
        "Sastrería",              // 25 Sastreria
        "Navegación",             // 26 Navegacion
        "Equitación",             // 27 Equitacion
    };
    public static int SkillCount => SkillNombre.Length - 1;   // 27 skills (ids 1..27)
    public static string NombreDe(int skill) => (skill >= 1 && skill < SkillNombre.Length) ? SkillNombre[skill] : "habilidad";

    /// <summary>Lista "id - Nombre" de todos los skills, para mostrar en ayuda de comandos GM.</summary>
    public static string ListaSkills() =>
        string.Join(", ", Enumerable.Range(1, SkillCount).Select(i => $"{i}={SkillNombre[i]}"));

    /// <summary>
    /// Resuelve un skill por número (1..27) o por nombre (parcial, sin distinguir acentos/mayúsculas).
    /// Devuelve el id (1..27) o 0 si no se encuentra.
    /// </summary>
    public static int ResolverSkill(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return 0;
        texto = texto.Trim();
        if (int.TryParse(texto, out int id) && id >= 1 && id <= SkillCount) return id;
        string norm = Normalizar(texto);
        // Coincidencia exacta primero, luego "empieza con", luego "contiene".
        for (int pasada = 0; pasada < 3; pasada++)
            for (int i = 1; i < SkillNombre.Length; i++)
            {
                string n = Normalizar(SkillNombre[i]);
                if (pasada == 0 && n == norm) return i;
                if (pasada == 1 && n.StartsWith(norm)) return i;
                if (pasada == 2 && n.Contains(norm)) return i;
            }
        return 0;
    }

    private static string Normalizar(string s)
    {
        s = s.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c switch { 'á' => 'a', 'é' => 'e', 'í' => 'i', 'ó' => 'o', 'ú' => 'u', _ => c });
        return sb.ToString();
    }

    // Tope de puntos de skill según nivel del personaje — LevelSkill (General.bas:2281-2330)
    // portado 1:1. Índice = nivel (1..50); nivel 40+ ya permite 100.
    private static readonly int[] LevelSkillCap =
    {
        0,                                       // 0 (sin uso)
        3, 5, 7, 10, 13, 15, 17, 20, 23, 25,     // niveles 1-10
        27, 30, 33, 35, 37, 40, 43, 45, 47, 50,  // niveles 11-20
        53, 55, 57, 60, 63, 65, 67, 70, 73, 75,  // niveles 21-30
        77, 80, 83, 85, 87, 90, 93, 95, 97, 100, // niveles 31-40
        100, 100, 100, 100, 100, 100, 100, 100, 100, 100, // niveles 41-50
    };

    /// <summary>Máximo de puntos de skill permitido para un nivel dado (Modulo_UsUaRiOs.bas:1712-1720).</summary>
    public static int MaxSkillPorNivel(int nivel)
    {
        if (nivel < 1) nivel = 1;
        if (nivel >= LevelSkillCap.Length) nivel = LevelSkillCap.Length - 1;
        return LevelSkillCap[nivel];
    }

    // Probabilidad (%) de que una acción entrenadora sume +1. Se lee de Server.ini
    // PorcentajeSkills (cacheado al primer uso); default 100 = sistema de skills rápido.
    private static int? _porcentajeSkill;
    public static int PorcentajeSkill => _porcentajeSkill ??= Math.Clamp(ServerConfig.ReadInt("PorcentajeSkills", 100), 1, 100);
    private static readonly System.Random _rng = new();

    /// <summary>
    /// SubirSkill — sistema de skills rápido con tope por nivel (LevelSkill del VB6,
    /// Modulo_UsUaRiOs.bas:1712-1720): el skill no puede superar el máximo que permite
    /// el nivel del personaje, y va subiendo progresivamente al levear. A diferencia del
    /// VB6 entrena incluso con hambre/sed (decisión de diseño). Con probabilidad
    /// PorcentajeSkill (default 100 = siempre) sube +1, otorga 10 de experiencia
    /// y chequea nivel.
    /// </summary>
    public static void SubirSkill(int userIndex, int skill)
    {
        var u = UserListManager.UserList[userIndex];
        if (u.Stats.UserSkills[skill] >= MAXSKILLPOINTS) return;       // ya está al máximo
        if (u.Stats.UserSkills[skill] >= MaxSkillPorNivel(u.Stats.ELV)) return; // tope por nivel
        if (_rng.Next(1, 101) > PorcentajeSkill) return;              // probabilidad de subir

        u.Stats.UserSkills[skill]++;
        if (u.Conn != null)
        {
            string nombre = (skill >= 0 && skill < SkillNombre.Length) ? SkillNombre[skill] : "habilidad";
            // Font 28 = naranja brillante (fonttypes.ind) para que la subida resalte en la consola.
            ServerPackets.ConsoleMsg(u.Conn, $"¡Has subido {nombre} a {u.Stats.UserSkills[skill]} puntos! (+1)", 28);
            ServerPackets.ConsoleMsg(u.Conn, "¡Has ganado 10 puntos de experiencia!", 1);
        }
        u.Stats.Exp += 10;
        Combat.CheckUserLevel(u);
        if (u.Conn != null) ServerPackets.UpdateUserStats(u.Conn, u);
    }
}
