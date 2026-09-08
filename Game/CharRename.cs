using System.IO;

namespace ServidorCS.Game;

/// <summary>
/// (NUEVO, no VB6) Cambio de nombre de un personaje, para la "Poción mágica de cambio de nombre"
/// (obj.dat SubTipo 23 → comando /nombre).
///
/// El nombre del personaje es la CLAVE de casi todo lo que se guarda a disco, así que renombrar es
/// mover todos sus archivos y arreglar las referencias que otros guardan a él:
///   · Charfile\NOMBRE.chr y NOMBRE.mac
///   · Logros\, BattlePass\ y Quests\ (un .json por personaje)
///   · [PJS] de la cuenta (la pantalla de selección lista por nombre)
///   · listas de amigos de TODOS los demás personajes ([AMIGOS] del .chr) y las solicitudes
///     de amistad pendientes (amigo_requests.dat)
///   · clanes: los archivos de guild guardan a la gente por nombre — miembros, aspirantes,
///     fundador/líder y los votos de una elección en curso (GuildManager.RenombrarMiembro).
///     Hasta el 16-sep-2026 esto no estaba y el cambio se BLOQUEABA si el personaje estaba en
///     un clan; había que salir, renombrarse y volver a entrar.
/// Quedan FUERA a propósito las subastas activas: el cambio se bloquea si hay una.
///
/// Al terminar se desconecta al jugador: hay estado en memoria de otros sistemas (party, comercio,
/// espectadores) que quedó apuntando al nombre viejo, y reconectar es la forma barata de limpiarlo.
/// </summary>
public static class CharRename
{
    /// <summary>
    /// Renombra al personaje del usuario ONLINE. Devuelve false con 'motivo' si no se puede.
    /// El usuario queda con el nombre nuevo en memoria y sus archivos ya movidos.
    /// </summary>
    public static bool Renombrar(User u, string nuevo, out string motivo)
    {
        motivo = null;
        nuevo = (nuevo ?? "").Trim();
        string viejo = u.Name;

        if (!u.PuedeRenombrar) { motivo = "No tienes ningún cambio de nombre disponible."; return false; }
        if (string.IsNullOrEmpty(nuevo)) { motivo = "Escribe el nombre nuevo: /nombre <nuevo nombre>."; return false; }
        if (string.Equals(nuevo, viejo, System.StringComparison.OrdinalIgnoreCase))
        { motivo = "Ese ya es tu nombre."; return false; }
        if (!CharCreator.NombreValido(nuevo)) { motivo = "Ese nombre no es válido."; return false; }
        if (CharLoader.PersonajeExiste(nuevo)) { motivo = "Ya existe un personaje con ese nombre."; return false; }
        // Las subastas guardan al vendedor y al postor por nombre y se persisten: renombrar en
        // medio de una dejaría al vendedor sin cobrar o al postor sin su ítem. El comentario de
        // arriba decía que este chequeo existía, pero no estaba escrito (16-sep-2026).
        if (Subastas.TieneSubastaActiva(viejo))
        { motivo = "Tienes una subasta en curso. Espera a que termine para cambiar de nombre."; return false; }

        // Dejar el .chr al día ANTES de moverlo: si no, el guardado siguiente escribiría el
        // archivo del nombre nuevo y el viejo quedaría con datos rancios dando vueltas.
        CharSaver.SaveUser(u);

        string dirChar = CharLoader.CharPath;
        string chrViejo = Path.Combine(dirChar, viejo.ToUpperInvariant() + ".chr");
        string chrNuevo = Path.Combine(dirChar, nuevo.ToUpperInvariant() + ".chr");
        try
        {
            if (File.Exists(chrViejo)) File.Move(chrViejo, chrNuevo, true);
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"[CharRename] No se pudo mover {chrViejo} → {chrNuevo}: {ex.Message}");
            motivo = "No se pudo cambiar el nombre (error al mover el personaje).";
            return false;
        }

        // A partir de acá el .chr ya es el nuevo: si algo falla, se sigue igual (los archivos
        // que faltan se regeneran solos) pero se deja registro.
        MoverSiExiste(Path.Combine(dirChar, viejo.ToUpperInvariant() + ".mac"),
                      Path.Combine(dirChar, nuevo.ToUpperInvariant() + ".mac"));
        Achievements.RenombrarProgreso(viejo, nuevo);
        BattlePass.RenombrarProgreso(viejo, nuevo);
        QuestSystem.RenombrarProgreso(viejo, nuevo);
        // Clanes: miembro de uno, aspirante de varios, y fundador/líder si lo es. Antes esto
        // no existía y el cambio se bloqueaba si estabas en un clan; ahora se arregla el dato.
        GuildManager.RenombrarMiembro(viejo, nuevo);

        if (!AccountManager.RenombrarEnCuenta(u.Account, viejo, nuevo))
            Console.WriteLine($"[CharRename] ADVERTENCIA: '{viejo}' no estaba en [PJS] de la cuenta '{u.Account}'.");

        u.Name = nuevo;
        u.PuedeRenombrar = false;
        CharSaver.SaveUser(u); // ya con el nombre nuevo (consume el vale en disco)

        Console.WriteLine($"[CharRename] '{viejo}' pasó a llamarse '{nuevo}' (cuenta {u.Account}).");
        return true;
    }

    private static void MoverSiExiste(string origen, string destino)
    {
        try { if (File.Exists(origen)) File.Move(origen, destino, true); }
        catch (System.Exception ex) { Console.WriteLine($"[CharRename] No se pudo mover {origen}: {ex.Message}"); }
    }

}
