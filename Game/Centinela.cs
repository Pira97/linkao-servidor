using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Centinela anti-macro (modCentinela.bas) 1:1. Cuando está activado (GM /centinelaactivado), cada
/// minuto elige un usuario que esté trabajando y le aparece el NPC Centinela con una clave que debe
/// responder (/CENTINELA &lt;clave&gt;, packet CentinelReport) en 2 minutos. Si no responde → cárcel
/// (mapa prisión 13) + pena + desconexión. Pasar()/CallUserAttention() se llaman desde el game loop.
/// </summary>
public static class Centinela
{
    private const int NPC_TIERRA = 622, NPC_AGUA = 623;
    private const byte TIEMPO_INICIAL = 2;          // minutos para responder
    private const int PRISION_MAP = 13;             // mapa cárcel (igual que /carcel)
    private const byte FONT_CENTINELA = 14, FONT_SERVER = 8; // colores de consola

    private const int MINUTOS_RESET = 10;           // cada cuánto se re-habilita la revisión de todos (ResetCentinelaInfo)

    public static bool Activado { get; private set; }
    private static int _revisando;                  // userIndex bajo revisión (0 = ninguno)
    private static int _minutosDesdeReset;
    private static int _tiempoRestante;
    private static int _clave;
    private static long _spawnTime;
    private static NpcManager.NpcInstance _npc;

    private static readonly Random _rng = new();
    private static long Now => Environment.TickCount64 & 0x7FFFFFFF;

    /// <summary>Toggle del sistema (GM /centinelaactivado). Devuelve el nuevo estado.</summary>
    public static bool Toggle()
    {
        Activado = !Activado;
        if (!Activado) Reset();
        return Activado;
    }

    /// <summary>CallUserAttention: tras 5s, reintenta llamar la atención (sonido+FX+reenvía clave). 1/seg.</summary>
    public static void CallUserAttention()
    {
        if (!Activado || _revisando == 0 || _npc == null) return;
        if (Now - _spawnTime < 5000) return;
        var u = UserListManager.UserList[_revisando];
        if (u == null || !u.flags.UserLogged || u.flags.CentinelaOK) return;
        ServerPackets.PlayWave(u.Conn, 3 /*SND_WARP*/, (byte)_npc.X, (byte)_npc.Y);
        ServerPackets.CreateFX(u.Conn, _npc.CharIndex, 1 /*FXWARP*/, 0);
        SendClave(_revisando);
    }

    /// <summary>PasarMinutoCentinela: control del timer. Llamar 1 vez por minuto.</summary>
    public static void PasarMinuto()
    {
        if (!Activado) return;

        // ResetCentinelaInfo: cada MINUTOS_RESET se re-habilita la revisión de todos (salvo el actual).
        if (++_minutosDesdeReset >= MINUTOS_RESET)
        {
            _minutosDesdeReset = 0;
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var x = UserListManager.UserList[i];
                if (x != null && x.flags.UserLogged && i != _revisando) x.flags.CentinelaOK = false;
            }
        }

        if (_revisando == 0) { GoToNextWorkingChar(); return; }

        _tiempoRestante--;
        if (_tiempoRestante <= 0)
        {
            FinalCheck();
            GoToNextWorkingChar();
            return;
        }

        var u = UserListManager.UserList[_revisando];
        if (u == null || !u.flags.UserLogged) { Reset(); return; }
        if (_npc != null && (Math.Abs(_npc.X - u.Pos.X) + Math.Abs(_npc.Y - u.Pos.Y)) > 5) WarpCentinela(_revisando);
        if (_npc != null)
        {
            ServerPackets.ChatOverHead(u.Conn, $"¡{u.Name}, tienes un minuto más para responder! Debes escribir /CENTINELA {_clave}.", _npc.CharIndex, 1);
            ServerPackets.ConsoleMsg(u.Conn, $"¡{u.Name}, tienes un minuto más para responder!", FONT_CENTINELA);
        }
    }

    /// <summary>CentinelaCheckClave (desde el packet CentinelReport): valida la clave del usuario.</summary>
    public static void CheckClave(int userIndex, int clave)
    {
        if (!Activado) return;
        if (clave == _clave && userIndex == _revisando)
        {
            var u = UserListManager.UserList[userIndex];
            u.flags.CentinelaOK = true;
            if (_npc != null)
                ServerPackets.ChatOverHead(u.Conn, $"¡Muchas gracias {u.Name}! Espero no haber sido una molestia.", _npc.CharIndex, 1);
            _revisando = 0;
            if (_npc != null) { NpcManager.RemoveNpc(_npc); _npc = null; }
        }
        else SendClave(userIndex);
    }

    /// <summary>El usuario bajo revisión se desconectó (CentinelaUserLogout).</summary>
    public static void OnUserLogout(int userIndex)
    {
        if (_revisando != userIndex) return;
        Console.WriteLine($"[Centinela] {UserListManager.UserList[userIndex]?.Name} se deslogueó al pedírsele la clave.");
        Reset();
    }

    // --- privado ---

    private static void GoToNextWorkingChar()
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u == null || !u.flags.UserLogged || !u.flags.Trabajando) continue;
            if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO) continue; // no a GMs
            if (u.flags.CentinelaOK) continue;

            _revisando = i;
            _tiempoRestante = TIEMPO_INICIAL;
            _clave = _rng.Next(1, 32001);
            _spawnTime = Now;
            WarpCentinela(i);
            if (_npc != null)
                ServerPackets.ChatOverHead(u.Conn, $"Saludos {u.Name}, soy el Centinela de estas tierras. Escribe /CENTINELA {_clave} en no más de dos minutos.", _npc.CharIndex, 1);
            return;
        }
        // Nadie trabajando: limpiar.
        if (_npc != null) { NpcManager.RemoveNpc(_npc); _npc = null; }
        _revisando = 0;
    }

    private static void WarpCentinela(int userIndex)
    {
        if (_npc != null) { NpcManager.RemoveNpc(_npc); _npc = null; }
        var u = UserListManager.UserList[userIndex];
        var md = MapLoader.Get(u.Pos.Map);
        bool agua = md != null && u.Pos.X >= 1 && u.Pos.X <= 100 && u.Pos.Y >= 1 && u.Pos.Y <= 100 && md.Water[u.Pos.X, u.Pos.Y];
        _npc = NpcManager.SpawnAt(u.Pos.Map, agua ? NPC_AGUA : NPC_TIERRA, (byte)u.Pos.X, (byte)u.Pos.Y);
        if (_npc != null) _npc.NoRespawn = true;
        else _revisando = 0; // no se pudo crear: esperar
    }

    private static void SendClave(int userIndex)
    {
        if (_npc == null || userIndex != _revisando) return;
        var u = UserListManager.UserList[userIndex];
        if (u?.Conn == null) return;
        if (!u.flags.CentinelaOK)
            ServerPackets.ChatOverHead(u.Conn, $"¡La clave que te he dicho es /CENTINELA {_clave}, escríbelo rápido!", _npc.CharIndex, 1);
    }

    private static void FinalCheck()
    {
        var u = UserListManager.UserList[_revisando];
        if (u != null && u.flags.UserLogged && !u.flags.CentinelaOK)
        {
            Console.WriteLine($"[Centinela] Encarceló a {u.Name} por uso de macro inasistido.");
            // Pena en el .chr.
            try
            {
                string chr = System.IO.Path.Combine(CharLoader.CharPath, u.Name.ToUpperInvariant() + ".chr");
                if (System.IO.File.Exists(chr))
                {
                    var doc = new IniDocument(chr);
                    int cant = new IniFile(chr).GetInt("PENAS", "Cant") + 1;
                    doc.Set("PENAS", "Cant", cant.ToString());
                    doc.Set("PENAS", "P" + cant, $"Centinela: Macro inasistido. {DateTime.Now:dd/MM/yyyy HH:mm}");
                    doc.Save(chr);
                }
            }
            catch { /* pena no crítica */ }

            // Avisar a admins, encarcelar (mapa prisión) y desconectar.
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var gm = UserListManager.UserList[i];
                if (gm != null && gm.flags.UserLogged && gm.Conn != null && gm.FaccionStatus >= AdminLoader.STATUS_DIOS)
                    ServerPackets.ConsoleMsg(gm.Conn, $"Servidor> El centinela ha encarcelado a {u.Name}", FONT_SERVER);
            }
            int idx = _revisando;
            _revisando = 0;
            Movement.WarpUser(idx, PRISION_MAP, 50, 50);
            if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "Has sido encarcelado por uso de macro inasistido.", 4);
        }
        Reset();
    }

    private static void Reset()
    {
        _clave = 0; _tiempoRestante = 0; _revisando = 0;
        if (_npc != null) { NpcManager.RemoveNpc(_npc); _npc = null; }
    }
}
