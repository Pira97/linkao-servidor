namespace ServidorCS.Game;

/// <summary>
/// [[b4_usersbymap]] Índice mapa → usuarios logueados presentes en ese mapa. Reemplaza, en los
/// sitios migrados, el patrón "recorrer TODOS los usuarios del servidor y filtrar por Pos.Map"
/// por "recorrer directamente los usuarios de ESE mapa" — mismo patrón que ya usa
/// <see cref="NpcManager"/> con su diccionario `_byMap` para NPCs.
///
/// Mantenimiento: se actualiza desde los puntos donde `User.Pos.Map` cambia. La mayoría pasan por
/// <see cref="AreaVisibility.OnUserEnter"/>/<see cref="AreaVisibility.OnUserLeave"/> (login, logout,
/// warp entre mapas clásico) — enganchado ahí. Los dos casos que NO pasan por esos hooks
/// (cruce "seamless" de borde del mundo continuo en `Movement.WarpUser`, y el comando de GM
/// `/ira`, que asigna `Pos` directo) llaman a <see cref="Move"/> explícitamente en el mismo punto
/// donde mutan `Pos.Map`. Ver auditoría B4.3 para la lista completa de sitios verificados.
///
/// Todo esto corre siempre bajo <see cref="UserListManager.GameLock"/> (igual que `NpcManager._byMap`),
/// así que no hace falta ningún lock propio.
/// </summary>
public static class UsersByMapIndex
{
    private static readonly Dictionary<int, HashSet<int>> _byMap = new();
    private static readonly HashSet<int> _empty = new();

    /// <summary>Agrega userIndex al set del mapa. No-op si map&lt;=0 (WorldPos default / sin mundo).</summary>
    public static void Add(int map, int userIndex)
    {
        if (map <= 0) return;
        if (!_byMap.TryGetValue(map, out var set))
        {
            set = new HashSet<int>();
            _byMap[map] = set;
        }
        set.Add(userIndex);
    }

    /// <summary>Saca userIndex del set del mapa. No-op si map&lt;=0 o el mapa no tiene set todavía.</summary>
    public static void Remove(int map, int userIndex)
    {
        if (map <= 0) return;
        if (_byMap.TryGetValue(map, out var set)) set.Remove(userIndex);
    }

    /// <summary>Mueve userIndex de un mapa a otro (quita del viejo, agrega al nuevo). No-op si son iguales.</summary>
    public static void Move(int userIndex, int oldMap, int newMap)
    {
        if (oldMap == newMap) return;
        Remove(oldMap, userIndex);
        Add(newMap, userIndex);
    }

    /// <summary>
    /// Usuarios (índices) presentes en ese mapa ahora mismo. Nunca null; colección vacía si no hay
    /// nadie. Devuelve el <see cref="HashSet{T}"/> CONCRETO (no la interfaz) a propósito: iterar un
    /// HashSet a través de IReadOnlyCollection&lt;int&gt; fuerza boxear su enumerador (struct) en
    /// cada foreach, medido ~2x más lento que un array plano incluso con la MISMA cantidad de
    /// elementos — con el tipo concreto, el compilador usa el enumerador sin boxing y el foreach
    /// queda tan rápido como recorrer un array. NO mutar la colección devuelta (llamar
    /// Add/Remove/Move en su lugar).
    /// </summary>
    public static HashSet<int> Get(int map)
        => _byMap.TryGetValue(map, out var set) ? set : _empty;
}
