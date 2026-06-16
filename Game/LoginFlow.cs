using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Flujo de entrada al mundo tras un login válido. Subset 1:1 de ConnectUser (TCP.bas):
/// envía la ráfaga de packets que el cliente espera para meter al personaje en el mapa.
///
/// Orden (como ConnectUser):
///   1. LoggedSuccessful
///   2. Logged (clase)
///   3. UserIndexInServer
///   4. ChangeMap (mapa + versión)
///   5. UserCharIndexInServer
///   6. CharacterCreate (el propio personaje en su posición)
///
/// Falta (se agrega al portar esos módulos): inventario (UpdateUserInv),
/// hechizos (UpdateUserHechizos), stats completos, NPCs/otros PJs del área, clima, etc.
/// </summary>
public static class LoginFlow
{
    public static void EnterWorld(Connection conn, User u)
    {
        // Asignar un CharIndex al personaje (en VB6 lo hace MakeUserChar).
        // Pool compartido con NPCs para que no colisionen.
        if (u.Char.CharIndex == 0)
            u.Char.CharIndex = CharIndexPool.Next();

        // VB6 TCP.bas:2212-2235: asignar Faccion.Status según nivel de privilegio GM
        u.FaccionStatus = AdminLoader.GetFaccionStatus(u.Name);

        // GMs/Dioses (FaccionStatus >= Consejero): vida/maná/energía/hambre/sed al máximo que permite
        // el protocolo (Integer de 16 bits = 32767). Prácticamente infinito para staff.
        if (u.FaccionStatus >= AdminLoader.STATUS_CONSEJERO)
        {
            const short STAT_GM = short.MaxValue; // 32767
            u.Stats.MaxHP  = STAT_GM; u.Stats.MinHP  = STAT_GM;
            u.Stats.MaxMAN = STAT_GM; u.Stats.MinMAN = STAT_GM;
            u.Stats.MaxSta = STAT_GM; u.Stats.MinSta = STAT_GM;
            u.Stats.MaxHam = STAT_GM; u.Stats.MinHam = STAT_GM;
            u.Stats.MaxAGU = STAT_GM; u.Stats.MinAGU = STAT_GM;
            u.flags.Hambre = 0; u.flags.Sed = 0;
        }

        // Personaje nivel 15+ que quedó adentro del Dungeon Newbie: se lo reubica en la
        // ciudad de su facción ANTES de mandarle el mundo (solo ajusta u.Pos, sin warp).
        Facciones.SalirDungeonNewbie(u, warpear: false);

        ServerPackets.LoggedSuccessful(conn);

        // 'Redundance' = nueva clave XOR de sesión (VB6: RandomNumber(15,250)).
        // Se manda en Logged; el cliente la adopta para cifrar lo que envía a partir de ahí,
        // así que el server debe usar ESE valor para desencriptar lo entrante.
        byte redundance = (byte)Random.Shared.Next(15, 251);
        u.Redundance = redundance;
        ServerPackets.Logged(conn, redundance);
        conn.IncomingXorKey = redundance;

        ServerPackets.UserIndexInServer(conn, (short)conn.UserIndex);

        // Versión del mapa: 0 por ahora (se lee del .map/.dat al portar mapas).
        ServerPackets.ChangeMap(conn, u.Pos.Map, 0);

        ServerPackets.UserCharIndexInServer(conn, u.Char.CharIndex);

        // El propio personaje.
        SendCharCreate(conn, u);

        // Stats del personaje (HP/maná/nivel/oro/atributos visibles en el HUD).
        ServerPackets.UpdateUserStats(conn, u);

        // Estados al loguear (TCP.bas:130-168): el cliente debe arrancar sin overlays pegados.
        // Si no está estúpido/ciego, se le manda DumbNoMore/BlindNoMore; si está paralizado, ParalizeOK.
        if (u.flags.Estupido == 0) ServerPackets.DumbNoMore(conn);
        if (u.flags.Ciego == 0) ServerPackets.BlindNoMore(conn);
        if (u.flags.Paralizado == 1 || u.flags.Inmovilizado == 1) ServerPackets.ParalizeOK(conn);

        // Inventario completo (equivale a UpdateUserInv con todos los slots).
        for (byte slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
        {
            var o = u.Invent.Object[slot];
            ServerPackets.ChangeInventorySlot(conn, slot, o.ObjIndex, o.Amount, o.Equipped);
        }

        // Solo GMs/Dioses (FaccionStatus 7=Consejero/RM, 8=SemiDios, 9=Dios, 10=Soporte) reciben
        // el hechizo "Te Violo" (índice 30 en Hechizos.dat). Se agrega al primer slot libre si no lo tienen.
        if (u.FaccionStatus >= 7)
        {
            const short TE_VIOLO = 30;
            bool yaLoTiene = false;
            for (byte s = 1; s <= Constants.MAXUSERHECHIZOS; s++)
                if (u.Stats.UserHechizos[s] == TE_VIOLO) { yaLoTiene = true; break; }
            if (!yaLoTiene)
                for (byte s = 1; s <= Constants.MAXUSERHECHIZOS; s++)
                    if (u.Stats.UserHechizos[s] == 0) { u.Stats.UserHechizos[s] = TE_VIOLO; break; }
        }

        // Hechizos (equivale a UpdateUserHechizos): se mandan TODOS los slots, incluidos los vacíos
        // (hIndex=0), para que el cliente LIMPIE los hechizos que pudieran haber quedado de un
        // personaje anterior de la misma sesión (el cliente no resetea user_hechizos al cambiar de PJ).
        for (byte slot = 1; slot <= Constants.MAXUSERHECHIZOS; slot++)
        {
            short hIndex = u.Stats.UserHechizos[slot];
            ServerPackets.ChangeSpellSlot(conn, slot, hIndex, hIndex > 0 ? SpellData.GetName(hIndex) : "");
        }

        // Skills del personaje (WriteSendSkills): puntos de cada habilidad.
        ServerPackets.SendSkills(conn, u);

        // NOTA: las partículas ambientales del mapa las carga el propio cliente desde sus .csm
        // (map_loader.gd → set_map_particle). El servidor NO debe enviarlas. Los teleport los renderiza
        // el cliente desde su .csm (AreaVisibility omite ObjType.Teleport).

        // --- Visibilidad por área (AOI): crea para el nuevo los jugadores/NPCs/objetos de su área y lo
        // hace visible a los presentes de su área. Reemplaza la difusión por mapa completo. ---
        AreaVisibility.OnUserEnter(conn.UserIndex);

        // Clima actual (lluvia/tormenta + luz ambiente) al entrar al mundo.
        Clima.EnviarClimaAUsuario(conn.UserIndex);

        // Ciclo Día/Noche actual (hora del mundo + flag de dungeon) al entrar al mundo.
        DayNightCycle.EnviarAUsuario(conn.UserIndex);

        // Aviso de evento de ruleta activo (montar dungeon / minería x2 / drop x2).
        Ruleta.NotificarEventoAlLogin(conn.UserIndex);

        // Saldo de créditos de donación (cuenta .cnt [cuenta] Creditos) → header de la tienda.
        if (!string.IsNullOrEmpty(u.Account))
        {
            string cnt = System.IO.Path.Combine(AccountManager.AccountPath, u.Account.ToUpperInvariant() + ".cnt");
            u.CreditoDonador = new IniFile(cnt).GetInt(u.Account.ToUpperInvariant(), "Creditos");
            ServerPackets.UpdateCreditos(conn, u.CreditoDonador);
        }

        // Aviso de condena de cárcel pendiente (TCP.bas:1301). El preso sigue confinado al reloguear.
        if (u.flags.Pena > 0)
            ServerPackets.ConsoleMsg(conn, $"Estás cumpliendo condena. Te quedan {u.flags.Pena} minutos.", 4);

        // Sonido de entrada al mundo (SND_ENTRADA=15): lo escucha el que entra Y los ya logueados
        // del mismo mapa (antes solo se mandaba al propio conn y los demás no lo oían).
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.PlayWave(o.Conn, Sounds.ENTRADA, (byte)u.Pos.X, (byte)u.Pos.Y);
        }

        // AFK: arrancar el contador de inactividad al ingresar.
        u.flags.LastActivityAt = Environment.TickCount64;
        u.flags.AfkParticula = false;

        // Battle Pass: carga el progreso del personaje (reset si cambió la temporada) y envía el estado.
        BattlePass.OnLogin(conn.UserIndex);

        Console.WriteLine($"[ServidorCS] {u.Name} entró al mundo en mapa {u.Pos.Map} ({u.Pos.X},{u.Pos.Y}) con {u.Invent.NroItems} items");
    }

    /// <summary>
    /// Status para el color del nick: si es GM (FaccionStatus 7-10) usa ese; si no, la facción
    /// del jugador (Faccion.Status 1-6). El cliente colorea el nombre con este valor
    /// (GeneralUtils.get_nick_color). 0 = nick blanco. [[facciones_jugador]]
    /// </summary>
    public static byte NickStatus(User u) => u.FaccionStatus >= 7 ? u.FaccionStatus : u.Faccion.Status;

    /// <summary>Envía a targetConn un CharacterCreate que representa al personaje 'u'.</summary>
    public static void SendCharCreate(Connection targetConn, User u)
    {
        ServerPackets.CharacterCreate(targetConn,
            charIndex: u.Char.CharIndex,
            body: u.Char.body,
            head: u.Char.Head,
            heading: u.Char.heading,
            x: (byte)u.Pos.X,
            y: (byte)u.Pos.Y,
            weapon: u.Char.WeaponAnim,
            shield: u.Char.ShieldAnim,
            helmet: u.Char.CascoAnim,
            fx: 0,
            fxLoops: 0,
            name: u.Name,
            privileges: NickStatus(u),
            donador: u.Char.Donador,
            particulaFx: 0,
            armaAura: u.Char.Arma_Aura,
            bodyAura: u.Char.Body_Aura,
            escudoAura: u.Char.Escudo_Aura,
            headAura: u.Char.Head_Aura,
            otraAura: u.Char.Otra_Aura,
            anilloAura: u.Char.Anillo_Aura,
            isTopGold: false,
            weaponObjIndex: 0);
    }
}
