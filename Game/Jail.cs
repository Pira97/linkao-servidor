using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Cárcel (Encarcelar/PurgarPenas, Admin.bas:177/152). Encarcelar warpea al preso a la ciudad Prisión
/// (cPrision=13) y le pone la condena en minutos (flags.Pena). Cada minuto PurgarPenas descuenta la
/// condena; al llegar a 0 lo libera warpeándolo a Libertad (cLibertad=14). La confinación física es la
/// geometría del mapa de prisión (cerrado). Además se bloquea la runa de teletransporte mientras esté preso.
/// </summary>
public static class Jail
{
    private const int CPrision = 13, CLibertad = 14;

    /// <summary>Encarcelar (Admin.bas:177): fija la condena y warpea a la prisión.</summary>
    public static void Encarcelar(int userIndex, int minutos, string gmName = "")
    {
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;
        u.flags.Pena = minutos;
        var pri = CityData.Get(CPrision);
        Movement.WarpUser(userIndex, pri.Map, pri.X, pri.Y);
        if (string.IsNullOrEmpty(gmName))
            ServerPackets.ConsoleMsg(u.Conn, $"Has sido encarcelado. Te quedan {minutos} minutos de condena.", 4);
        else
            ServerPackets.ConsoleMsg(u.Conn, $"Has sido encarcelado por {gmName}.", 4);
    }

    /// <summary>
    /// PurgarPenas (Admin.bas:152): llamar 1 vez por minuto. Descuenta la condena de cada preso online;
    /// al llegar a 0 lo libera (warp a Libertad).
    /// </summary>
    public static void PurgarPenas()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u?.flags.UserLogged != true || u.flags.Pena <= 0) continue;
            u.flags.Pena--;
            if (u.flags.Pena < 1)
            {
                u.flags.Pena = 0;
                var lib = CityData.Get(CLibertad);
                Movement.WarpUser(i, lib.Map, lib.X, lib.Y);
                if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has cumplido tu condena. Eres libre.", 3);
            }
        }
    }

    /// <summary>True si el usuario está preso (no puede escapar con runa/teletransporte).</summary>
    public static bool EstaPreso(User u) => u != null && u.flags.Pena > 0;
}
