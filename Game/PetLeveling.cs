namespace ServidorCS.Game;

/// <summary>
/// Progresión (1..50) de la mascota compañera persistente. Hermana de Leveling.cs (mismo tope de
/// nivel y misma curva de exp, Leveling.ELU) pero con una tabla de stats PROPIA y más floja: el
/// pedido es "daño menor, no tanto daño" respecto a un NPC salvaje del mismo nivel.
///
/// Nivel 1 = los stats de la versión "FAMILIAR" que ya existe en NPCs.dat (NPC126-133, débil,
/// pensada para el hechizo "Llamado al familiar"). Nivel 50 = un objetivo fijo bien por debajo de
/// un NPC salvaje equivalente (ej. Oso Pardo salvaje NPC538 nivel 28 ya tiene 920HP/100-135 dmg).
/// Interpolación lineal entre ambos extremos, mismo patrón de "vida fija por nivel" que Leveling.cs.
/// </summary>
public static class PetLeveling
{
    public enum PetTipo : byte
    {
        Ninguna = 0,
        Lobo = 1,
        OsoPardo = 2,
        ElementalAgua = 3,
        ElementalFuego = 4,
        ElementalTierra = 5,
        Ely = 6,
    }

    /// <summary>NpcIndex de NPCs.dat (versión "FAMILIAR") que se spawnea para cada tipo.</summary>
    public static int NpcIndexFor(PetTipo tipo) => tipo switch
    {
        PetTipo.Lobo => 133,
        PetTipo.OsoPardo => 131,
        PetTipo.ElementalAgua => 127,
        PetTipo.ElementalFuego => 128,
        PetTipo.ElementalTierra => 129,
        PetTipo.Ely => 132,
        _ => 0,
    };

    /// <summary>Clase(s) de jugador habilitadas para tener este tipo de mascota (eClass, CharCreator.cs:80).</summary>
    public static bool ClasePuedeTener(byte clase, PetTipo tipo) => tipo switch
    {
        PetTipo.ElementalAgua or PetTipo.ElementalFuego or PetTipo.ElementalTierra or PetTipo.Ely
            => clase == 2 || clase == 18, // Mago / Nigromante
        PetTipo.Lobo or PetTipo.OsoPardo => clase == 10, // Cazador
        _ => false,
    };

    /// <summary>HechizoIndex (Hechizos.dat) que invoca cada tipo — ver Combat.cs (dispatch de invocación).</summary>
    public static int HechizoInvocarFor(PetTipo tipo) => tipo switch
    {
        PetTipo.ElementalFuego => 26,
        PetTipo.ElementalAgua => 27,
        PetTipo.ElementalTierra => 28,
        PetTipo.Ely => 123,
        PetTipo.Lobo => 124,
        PetTipo.OsoPardo => 125,
        _ => 0,
    };

    /// <summary>
    /// Hechizos que la mascota lanza en combate a este nivel. null = no castea (Lobo/OsoPardo:
    /// puro cuerpo a cuerpo).
    ///
    /// ⚠️ Los hechizos NO salen de NPCs.dat: las versiones FAMILIAR (NPC126-133) **no tienen
    /// `LanzaSpells`**, ni siquiera los elementales. Todo lo que castea una mascota se decide acá.
    ///
    /// ⚠️ El daño de un hechizo lo fija Hechizos.dat y **no escala con el nivel de la mascota**:
    /// sólo cambia cuando aprende otro. Por eso los que hacen daño se dan recién a nivel 10, y
    /// antes va una versión floja — si no, una mascota de nivel 1 pega más con el hechizo que un
    /// bicho salvaje del mismo nivel, que es exactamente lo que la curva de stats evita.
    ///   · Ely: Proyectil Mágico (2) 1-9 → Descarga Eléctrica (93) desde 10.
    ///   · Elemental de fuego: Saeta Ígnea (6, 7-14 de daño) 1-9 → **Tormenta de fuego (15,
    ///     15-50)** desde 10.
    ///   · Elemental de agua: **Paralizar (41)** desde nivel 1 — es utilidad, no daño: no rompe la
    ///     curva y su gracia es paralizar y rematar a golpes (ver AtacarObjetivoMascota).
    /// </summary>
    public static short[] SpellsPorNivel(PetTipo tipo, byte nivel) => tipo switch
    {
        PetTipo.Ely => nivel >= 10 ? new short[] { 93 } : new short[] { 2 },
        PetTipo.ElementalFuego => nivel >= 10 ? new short[] { 15 } : new short[] { 6 },
        PetTipo.ElementalAgua => new short[] { 41 },
        _ => null,
    };

    /// <summary>Opciones de mascota disponibles para elegir en la creación de personaje, por clase.</summary>
    public static PetTipo[] OpcionesPara(byte clase) => clase switch
    {
        2 or 18 => new[] { PetTipo.ElementalFuego, PetTipo.ElementalAgua, PetTipo.ElementalTierra, PetTipo.Ely },
        10 => new[] { PetTipo.Lobo, PetTipo.OsoPardo },
        _ => System.Array.Empty<PetTipo>(),
    };

    // (hpBase, minHitBase, maxHitBase, poderAtaqueBase, poderEvasionBase) en nivel 1 == stats del
    // NPC "FAMILIAR" tal cual está en NPCs.dat. (hpObj, minHitObj, maxHitObj, poderAtaqueObj,
    // poderEvasionObj) en nivel 50 == techo propio de la mascota, deliberadamente bajo.
    private static (int hpB, int minB, int maxB, int paB, int peB, int hpO, int minO, int maxO, int paO, int peO) Curva(PetTipo tipo) => tipo switch
    {
        PetTipo.Lobo           => (20, 10, 20, 25, 15, 260, 22, 34, 90, 60),
        PetTipo.OsoPardo       => (20, 5, 30, 30, 5, 320, 26, 42, 100, 55),
        PetTipo.ElementalAgua  => (10, 7, 20, 25, 10, 280, 24, 36, 95, 65),
        PetTipo.ElementalFuego => (10, 5, 15, 20, 20, 260, 20, 32, 90, 70),
        PetTipo.ElementalTierra=> (10, 5, 15, 30, 5, 300, 22, 34, 100, 55),
        PetTipo.Ely            => (15, 5, 20, 15, 30, 240, 18, 30, 85, 80),
        _                      => (1, 1, 1, 1, 1, 1, 1, 1, 1, 1),
    };

    private const int NIVEL_MAX = 50;

    private static int Interp(int baseVal, int objVal, byte nivel)
    {
        byte n = nivel < 1 ? (byte)1 : (nivel > NIVEL_MAX ? (byte)NIVEL_MAX : nivel);
        return baseVal + (objVal - baseVal) * (n - 1) / (NIVEL_MAX - 1);
    }

    public static int VidaFijaPorNivel(PetTipo tipo, byte nivel)
    {
        var c = Curva(tipo);
        return Interp(c.hpB, c.hpO, nivel);
    }

    public static (int minHit, int maxHit) DanoPorNivel(PetTipo tipo, byte nivel)
    {
        var c = Curva(tipo);
        return (Interp(c.minB, c.minO, nivel), Interp(c.maxB, c.maxO, nivel));
    }

    public static (int poderAtaque, int poderEvasion) PoderPorNivel(PetTipo tipo, byte nivel)
    {
        var c = Curva(tipo);
        return (Interp(c.paB, c.paO, nivel), Interp(c.peB, c.peO, nivel));
    }
}
