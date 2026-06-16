namespace ServidorCS.Game;

/// <summary>
/// Sistema FIJO de vida/maná por nivel (GameLogic.bas:1538-1882) + curva de exp (Niveles.dat).
/// GetVidaFijaPorNivel/GetManaFijaPorNivel devuelven el valor TOTAL absoluto para un nivel dado
/// (raza+clase), garantizando llegar exacto al objetivo de nivel 50. Lo usan CheckUserLevel
/// (subida natural) y /mod nivel (ajusta vida/maná al subir O bajar el nivel).
/// </summary>
public static class Leveling
{
    public const int STAT_MAXELV = 50;            // Declares.bas:706
    public const int STAT_MAXHP = 999;            // Declares.bas:707
    public const int STAT_MAXSTA = 999;           // Declares.bas:708
    public const int STAT_MAXMAN = 9999;          // Declares.bas:709
    public const int STAT_MAXHIT_UNDER36 = 99;    // Declares.bas:710
    public const int STAT_MAXHIT_OVER36 = 999;    // Declares.bas:711

    /// <summary>Aumento de stamina al subir un nivel, por clase (CheckUserLevel:730-799).
    /// Lo usan la subida natural y /mod nivel (para que la energía acompañe al nivel).</summary>
    public static int AumentoSta(byte clase) => clase switch
    {
        5  => 18, // Ladrón
        2  => 14, // Mago
        13 => 38, // Leñador
        14 => 40, // Minero
        11 => 35, // Pescador
        _  => 15, // resto (AumentoSTDef)
    };

    /// <summary>Aumento de HIT al ALCANZAR 'nivel', por clase (CheckUserLevel:730-799; el umbral
    /// se evalúa con el nivel recién alcanzado, igual que la subida natural).</summary>
    public static int AumentoHit(byte clase, int nivel) => clase switch
    {
        3 or 10 => nivel > 35 ? 2 : 3, // Guerrero/Cazador
        9       => nivel > 35 ? 1 : 3, // Paladín
        4       => nivel > 35 ? 1 : 3, // Asesino
        8       => nivel > 40 ? 2 : 3, // Gladiador
        18      => nivel > 40 ? 1 : 3, // Nigromante
        17      => nivel > 30 ? 2 : 3, // Mercenario
        5 or 2 or 11 => 1,             // Ladrón/Mago/Pescador
        13 or 14     => 2,             // Leñador/Minero
        _       => 2,                  // Clérigo/Druida/Bardo/etc.
    };

    // levelELU(1..49) — CargarELU (General.bas:1763) lee Dat/Niveles.dat [INIT] Nivel1..Nivel49.
    private static int[] _elu;

    /// <summary>Exp necesaria para pasar del nivel dado al siguiente. 0 si nivel >= 50.</summary>
    public static int ELU(int nivel)
    {
        if (nivel < 1 || nivel >= STAT_MAXELV) return 0;
        if (_elu == null) CargarELU();
        return _elu[nivel];
    }

    private static void CargarELU()
    {
        _elu = new int[STAT_MAXELV];
        string file = System.IO.Path.Combine(DataPaths.Sub("Dat"), "Niveles.dat");
        var ini = new IniFile(file);
        if (!ini.Loaded)
        {
            file = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Dat", "Niveles.dat");
            ini = new IniFile(file);
        }
        if (ini.Loaded)
        {
            for (int n = 1; n < STAT_MAXELV; n++)
                _elu[n] = ini.GetInt("INIT", "Nivel" + n);
        }
        else
        {
            // Fallback si falta Niveles.dat: curva aproximada (ELU crece 40% por nivel desde 300).
            System.Console.WriteLine("[Leveling] Niveles.dat no encontrado; usando curva de exp aproximada.");
            double e = 300;
            for (int n = 1; n < STAT_MAXELV; n++) { _elu[n] = (int)e; e *= 1.4; }
        }
    }

    // ===== Vida fija (GameLogic.bas:1538-1706) =====

    /// <summary>GetVidaInicialN1 (GameLogic.bas:1596): vida base por clase + mod de constitución por raza.</summary>
    public static int VidaInicial(byte raza, byte clase)
    {
        int vidaBase = clase switch
        {
            2 or 18 => 50,          // Mago/Nigromante
            6 or 7 or 1 or 4 => 55, // Bardo/Druida/Clerigo/Asesino
            5 => 60,                // ladron
            9 or 17 or 10 => 65,    // Paladin/Mercenario/Cazador
            3 or 8 => 70,           // Guerrero/Gladiador
            _ => 60,
        };
        int modConst = raza switch { 4 => -5, 2 => 0, 3 => 5, 1 => 10, 6 or 5 => 20, _ => 0 };
        return vidaBase + modConst;
    }

    /// <summary>GetVidaObjetivoN50 (GameLogic.bas:1538): vida total al nivel 50 por clase y raza.</summary>
    public static int VidaObjetivoN50(byte raza, byte clase)
    {
        if (clase < 1 || clase > 18 || raza < 1 || raza > 6) return 400;
        // Índice de raza: 1=Humano, 2=Elfo, 3=Drow, 4=Gnomo, 5=Enano, 6=Orco.
        int[] fila = clase switch
        {
            2  => new[] { 361, 341, 351, 331, 386, 375 }, // Mago
            18 => new[] { 400, 380, 390, 370, 425, 414 }, // Nigromante
            6  => new[] { 404, 385, 394, 375, 430, 419 }, // Bardo
            7  => new[] { 402, 383, 392, 373, 428, 417 }, // Druida
            1  => new[] { 410, 390, 400, 380, 435, 424 }, // Clerigo
            9  => new[] { 490, 471, 481, 461, 516, 505 }, // Paladin
            17 => new[] { 497, 477, 487, 467, 522, 511 }, // Mercenario
            8  => new[] { 551, 531, 541, 521, 576, 565 }, // Gladiador
            3  => new[] { 520, 500, 510, 490, 545, 534 }, // Guerrero
            5  => new[] { 471, 451, 461, 441, 496, 486 }, // Ladron
            4  => new[] { 450, 430, 440, 420, 475, 464 }, // Asesino
            10 => new[] { 495, 476, 486, 466, 521, 510 }, // Cazador
            _  => null,
        };
        return fila == null ? 400 : fila[raza - 1];
    }

    /// <summary>
    /// GetVidaFijaPorNivel (GameLogic.bas:1639): HP total absoluto para un nivel. Reparte la
    /// diferencia inicial→objetivo en los 49 niveles (parte entera + resto en los primeros niveles).
    /// </summary>
    public static int VidaFijaPorNivel(byte raza, byte clase, int nivel)
    {
        if (nivel < 1) nivel = 1;
        if (nivel > 50) nivel = 50;
        if (raza < 1 || raza > 6 || clase < 1 || clase > 18) return 100;

        int inicial = VidaInicial(raza, clase);
        if (nivel == 1) return inicial;

        int diferencia = VidaObjetivoN50(raza, clase) - inicial;
        const int nivelesARepartir = 49; // del nivel 2 al 50
        int entero = (int)System.Math.Floor(diferencia / (double)nivelesARepartir);
        int resto = diferencia - entero * nivelesARepartir;

        int total = inicial;
        for (int i = 2; i <= nivel; i++)
            total += (resto > 0 && (i - 1) <= resto) ? entero + 1 : entero;
        return total;
    }

    // ===== Maná fijo (GameLogic.bas:1715-1882) =====

    /// <summary>GetManaInicialN1 (GameLogic.bas:1763): 0 si la clase no usa maná; si no, base + mod int por raza.</summary>
    public static int ManaInicial(byte raza, byte clase)
    {
        int manaBase = clase switch
        {
            2 => 30,           // Mago
            18 => 25,          // Nigromante
            7 or 6 or 1 => 20, // Druida/Bardo/Clerigo
            4 => 15,           // Asesino
            9 => 10,           // Paladin
            _ => -1,           // clase sin maná
        };
        if (manaBase < 0) return 0;
        int modInt = raza switch { 4 => 12, 2 => 9, 3 => 6, 1 => 3, 6 or 5 => -15, _ => 0 };
        return System.Math.Max(0, manaBase + modInt);
    }

    /// <summary>GetManaObjetivoN50 (GameLogic.bas:1715): maná total al nivel 50; 0 = clase sin maná.</summary>
    public static int ManaObjetivoN50(byte raza, byte clase)
    {
        if (clase < 1 || clase > 18 || raza < 1 || raza > 6) return 0;
        // Índice de raza: 1=Humano, 2=Elfo, 3=Drow, 4=Gnomo, 5=Enano, 6=Orco.
        int[] fila = clase switch
        {
            2  => new[] { 3136, 3455, 3302, 3625, 2077, 2236 }, // Mago
            18 => new[] { 2600, 2949, 2783, 3136, 1441, 1615 }, // Nigromante
            7  => new[] { 1993, 2208, 2105, 2323, 1280, 1387 }, // Druida
            6  => new[] { 1930, 2131, 2035, 2238, 1264, 1364 }, // Bardo
            1  => new[] { 1900, 2080, 1994, 2177, 1300, 1390 }, // Clerigo
            4  => new[] { 1055, 1134, 1096, 1176, 792, 832 },   // Asesino
            9  => new[] { 954, 1026, 992, 1065, 714, 750 },     // Paladin
            _  => null,
        };
        return fila == null ? 0 : fila[raza - 1];
    }

    /// <summary>GetManaFijaPorNivel (GameLogic.bas:1809): maná total absoluto para un nivel (0 = clase sin maná).</summary>
    public static int ManaFijaPorNivel(byte raza, byte clase, int nivel)
    {
        if (nivel < 1) nivel = 1;
        if (nivel > 50) nivel = 50;
        if (raza < 1 || raza > 6 || clase < 1 || clase > 18) return 0;

        int inicial = ManaInicial(raza, clase);
        int objetivo = ManaObjetivoN50(raza, clase);
        if (objetivo == 0) return 0;
        if (nivel == 1) return inicial;

        int diferencia = objetivo - inicial;
        const int nivelesARepartir = 49;
        int entero = (int)System.Math.Floor(diferencia / (double)nivelesARepartir);
        int resto = diferencia - entero * nivelesARepartir;

        int total = inicial;
        for (int i = 2; i <= nivel; i++)
            total += (resto > 0 && (i - 1) <= resto) ? entero + 1 : entero;
        return total;
    }
}
