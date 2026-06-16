using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Chat + Comandos GM. Porta HandleTalk (difusión) y eGMCommands (86 comandos).
/// </summary>
public static class Chat
{
    /// <summary>Procesa comandos GM enviados vía GMCommands packet (ID 94).</summary>
    public static void HandleGMCommand(int userIndex, string command)
    {
        Console.WriteLine($"[GMCMD] Procesando: {command}");
        HandleCommand(userIndex, command);
    }

    public static void TalkToMap(int userIndex, string chat, byte talkMode)
    {
        Console.WriteLine($"[CHAT] TalkToMap llamado: user={userIndex}, chat='{chat}', mode={talkMode}");

        if (chat.StartsWith("/"))
        {
            Console.WriteLine($"[CHAT] Es COMANDO: '{chat}' - Llamando HandleCommand...");
            if (HandleCommand(userIndex, chat))
            {
                Console.WriteLine($"[CHAT] Comando consumido: {chat}");
                return;
            }
            Console.WriteLine($"[CHAT] HandleCommand retornó false - no consumió");
        }

        var speaker = UserListManager.UserList[userIndex];
        short charIndex = speaker.Char.CharIndex;
        int map = speaker.Pos.Map;

        // talkMode 10 = chat GLOBAL: va a la consola de TODOS los usuarios (tab Global),
        // no como globo sobre la cabeza. Cooldown anti-spam por usuario.
        if (talkMode == 10)
        {
            long now = Environment.TickCount64;
            if (_lastGlobalChat.TryGetValue(userIndex, out long last) && now - last < GLOBAL_COOLDOWN_MS)
            {
                ServerPackets.ConsoleMsg(speaker.Conn, "Debes esperar antes de volver a usar el chat global.", 8);
                return;
            }
            _lastGlobalChat[userIndex] = now;

            string globalMsg = $"{speaker.Name} (Global): {chat}";
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var t = UserListManager.UserList[i];
                if (t?.flags.UserLogged == true && t.Conn != null)
                    ServerPackets.ConsoleMsg(t.Conn, globalMsg, 23); // FONTTYPE_GLOBAL → tab Global
            }
            return;
        }

        // talkMode 11 = chat FACCIONARIO: solo lo ven los usuarios de la MISMA facción.
        // Las facciones enemigas no reciben el mensaje. Requiere estar enlistado.
        if (talkMode == 11)
        {
            // Bandos: Ciudadano+Armada (Imperial), Republicano+Milicia (República), Caos.
            // Los renegados no tienen facción.
            int grupo = FaccionGrupo(speaker.Faccion.Status);
            if (grupo == 0)
            {
                ServerPackets.ConsoleMsg(speaker.Conn, "No perteneces a ninguna facción.", 8);
                return;
            }
            const byte fac_font = 61; // FONTTYPE_FACCION (cliente) — color propio del chat faccionario
            string facMsg = $"{speaker.Name} (Facción): {chat}";
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var t = UserListManager.UserList[i];
                if (t?.flags.UserLogged == true && t.Conn != null && FaccionGrupo(t.Faccion.Status) == grupo)
                    ServerPackets.ConsoleMsg(t.Conn, facMsg, fac_font);
            }
            return;
        }

        Console.WriteLine($"[CHAT] Difundiendo chat normal: {chat}");

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var other = UserListManager.UserList[i];
            if (!other.flags.UserLogged || other.Conn == null) continue;
            if (other.Pos.Map != map) continue;
            ServerPackets.ChatOverHead(other.Conn, chat, charIndex, talkMode);
        }
    }

    // Cooldown del chat global (talkMode 10).
    private static readonly System.Collections.Generic.Dictionary<int, long> _lastGlobalChat = new();
    private const long GLOBAL_COOLDOWN_MS = 8000;

    // Bando del chat faccionario: 1=Imperial (Ciudadano+Armada), 2=República (Republicano+Milicia),
    // 3=Caos, 0=sin facción (Renegado). Los enemigos no comparten bando, así que no se ven.
    private static int FaccionGrupo(byte status) => status switch
    {
        Facciones.CIUDADANO or Facciones.ARMADA   => 1,
        Facciones.REPUBLICANO or Facciones.MILICIA => 2,
        Facciones.CAOS                             => 3,
        _                                          => 0,
    };

    private static bool HandleCommand(int userIndex, string chat)
    {
        var u = UserListManager.UserList[userIndex];
        u.id = userIndex; // asegurar que u.id siempre sea válido
        var parts = Tokenize(chat);
        if (parts.Length == 0) return false;
        string cmd = parts[0].ToLowerInvariant();

        Console.WriteLine($"[COMANDO] Usuario {u.Name} escribió: {cmd}");

        // Alias para comandos
        switch (cmd)
        {
            // === UTILIDADES ===
            case "/spawn": return Cmd_Spawn(u, parts);
            case "/pos": case "/donde": return Cmd_Donde(u, parts);
            case "/resucitar": return Cmd_Resucitar(userIndex, u);
            case "/curar": return Cmd_Curar(u);
            case "/mana": return Cmd_Mana(u);
            case "/sta": case "/stamina": return Cmd_Stamina(u);
            case "/save": return Cmd_Save(u);
            case "/eventoexp": return Cmd_EventoExp(parts);
            case "/invasion": return Cmd_Invasion(u, parts);
            case "/centinelaactivado": return Cmd_CentinelaToggle(u);
            case "/todosvstodos": case "/tvt": case "/pvpmapa": return Cmd_TodosVsTodos(u);
            case "/torneoforce": case "/torneostart": return Cmd_TorneoForce(userIndex);
            case "/torneocancel": case "/torneocancelar": return Cmd_TorneoCancel(userIndex);
            case "/torneoestado": case "/torneo": return Cmd_TorneoEstado(userIndex);
            case "/eventocaceria": case "/eventocaceriaon": case "/iniciarcaceria": return Cmd_CaceriaIniciar(u);
            case "/eventocaceriaoff": case "/eventocaceriades": case "/finalizarcaceria": return Cmd_CaceriaFinalizar(u);
            case "/estadocaceria": case "/vercaceria": return Cmd_CaceriaEstado(u);
            case "/barrido": case "/eventobarrido": case "/barridoon": return Cmd_BarridoIniciar(u);
            case "/barridooff": case "/eventobarridooff": return Cmd_BarridoFinalizar(u);
            case "/movimiento": return Cmd_BarridoMovimiento(u, parts);

            // === STATS ===
            case "/stats": return Cmd_Stats(u, parts);
            case "/exp": return Cmd_Exp(u, parts);
            case "/level": case "/lvl": return Cmd_Level(u, parts);
            case "/gld": case "/oro": return Cmd_Oro(u, parts);
            case "/banco": return Cmd_Banco(u, parts);
            case "/habilidad": case "/skill": return Cmd_Skill(u, parts);
            case "/atributo": case "/attr": return Cmd_Atributo(u, parts);

            // === INFORMACIÓN ===
            case "/info": return Cmd_Info(u, parts);
            case "/inv": case "/inventario": return Cmd_Inventario(u);
            case "/bov": case "/banco-inv": return Cmd_BancoInv(u);
            case "/hechizos": case "/spells": return Cmd_Hechizos(u);
            case "/skills": return Cmd_Skills(u);
            case "/listusu": return Cmd_ListaUsuarios(u);
            case "/online": return Cmd_Online();
            case "/onlinegm": return Cmd_OnlineGM();
            case "/onlinemap": return Cmd_OnlineMap(u);
            case "/nene": case "/criaturas": return Cmd_Criaturas(u, parts);

            // === BLOQUEOS ===
            case "/bloq": case "/bloqueo": return Cmd_Bloq(u, parts);
            case "/trigger": return Cmd_Trigger(u, parts);

            // === HERRAMIENTAS ===
            case "/hora": return Cmd_Hora();
            case "/gmsg": case "/gmessage": return Cmd_GMMsg(u, parts);
            case "/smsg": case "/smessage": return Cmd_SMsg(parts);
            case "/rmsg": case "/rmessage": return Cmd_RMsg(parts);

            // === TELEPORT ===
            case "/telep": case "/tp": return Cmd_Telep(u, parts);
            case "/ira": case "/goto": return Cmd_IRA(u, parts);
            case "/ircerca": case "/near": return Cmd_IrCerca(u, parts);
            case "/sum": case "/summon": return Cmd_Summon(u, parts);

            // === MODULOS ===
            case "/invisible": case "/invi": return Cmd_Invisible(u);
            case "/trabajando": return Cmd_Trabajando(u);
            case "/ocultando": return Cmd_Ocultando(u);
            case "/nave": case "/navigate": return Cmd_Navigate(u);
            case "/gmvuelo": case "/volar": return Cmd_Volar(u);

            // === ADMINISTRACIÓN ===
            case "/ban": return Cmd_Ban(u, parts);
            case "/banip": case "/ban-ip": return Cmd_BanIP(parts);
            case "/unbanip": case "/unban-ip": return Cmd_UnbanIP(parts);
            case "/baniplist": return Cmd_BanIPList();
            case "/banipreload": return Cmd_BanIPReload();
            case "/echar": case "/kick": return Cmd_Kick(u, parts);
            case "/carcel": case "/jail": return Cmd_Jail(u, parts);
            case "/advertencia": case "/warn": return Cmd_Warn(u, parts);
            case "/mod": case "/editchar": return Cmd_Mod(u, parts);
            case "/apass": case "/alter-pass": return Cmd_APass(parts);

            // === NPC ===
            case "/rmata": case "/kill-npc": return Cmd_RMata(u);
            case "/mata": case "/kill-all": return Cmd_Mata(u);
            case "/masskill": case "/kill-area": return Cmd_MassKill(u);
            case "/acc": case "/create-npc": return Cmd_CreateNPC(u, parts);
            case "/racc": case "/respawn-npc": return Cmd_RespawnNPC(u, parts);
            case "/seguir": case "/follow": return Cmd_Seguir(u, parts);
            case "/talkas": case "/habla-como": return Cmd_TalkAs(u, parts);
            case "/reloadnpcs": return Cmd_ReloadNPCs();

            // === ITEMS ===
            case "/ci": case "/create-item": return Cmd_CI(u, parts);
            case "/dest": case "/destroy": return Cmd_Dest(u, parts);
            case "/massdest": case "/destroy-area": return Cmd_MassDest(u);
            case "/resetinv": return Cmd_ResetInv(u);
            case "/limpiar": case "/clean": return Cmd_Limpiar(u);

            // === MAPA ===
            case "/guardamapa": case "/save-map": return Cmd_GuardaMapa(u);
            case "/modmapinfo": case "/map-info": return Cmd_ModMapInfo(u, parts);
            case "/ct": case "/create-teleport": return Cmd_CT(u, parts);
            case "/dt": case "/destroy-teleport": return Cmd_DT(u, parts);
            case "/lluvia": case "/rain": return Cmd_Lluvia(parts);

            // === SERVIDOR ===
            case "/apagar": case "/shutdown": return Cmd_Apagar();
            case "/habilitar": case "/enable": return Cmd_Habilitar();
            case "/grabar": case "/save-all": return Cmd_Grabar();
            case "/reloadobj": case "/reload-objects": return Cmd_ReloadObj();
            case "/reloadhechizos": case "/reload-spells": return Cmd_ReloadSpells();
            case "/reloadsini": case "/reload-ini": return Cmd_ReloadIni();
            case "/setinivar": return Cmd_SetIniVar(parts);

            // === OTROS ===
            case "/showname": return Cmd_ShowName(u);
            case "/rem": case "/comment": return true;
            case "/panelgm": return Cmd_PanelGM(u);
            case "/show": return Cmd_Show(u, parts);
            case "/sosdone": return Cmd_SOSDone(u);
            case "/soslist": return Cmd_SosList(u);
            case "/sosremove": return Cmd_SosRemove(u, parts);
            case "/cleansos": return Cmd_CleanSos(u);
            case "/revivir": case "/revive": return Cmd_Revivir(u, parts);
            case "/ejecutar": return Cmd_Ejecutar(u, parts);
            case "/invocacion": return Cmd_Summon(u, parts);
            case "/charinfo": return Cmd_CharInfo(u, parts);
            case "/charinv": return Cmd_CharInv(u, parts);
            case "/charbank": return Cmd_CharBank(u, parts);
            case "/charskills": return Cmd_CharSkills(u, parts);
            case "/resetskills": case "/reset-skills": return Cmd_ResetSkills(u, parts);
            case "/setskills": case "/subirskills": return Cmd_SetSkills(u, parts);
            case "/setskill": case "/subirskill": return Cmd_SetSkill(u, parts);
            case "/msgclan": return Cmd_ShowGuildMessages(u, parts);
            case "/altpass": return Cmd_AlterPassword(u, parts);
            case "/donador": return Cmd_Donador(u, parts);
            case "/summonbot": return Send(u, "SummonBot: (no implementado)");
            case "/time": return Send(u, $"Hora servidor: {DateTime.Now:HH:mm}");
            case "/asktrigger": return Cmd_AskTrigger(u);
            case "/echartodospjs": case "/kick-all": return Cmd_KickAll();
            case "/rajarclan": case "/remove-guild": return Cmd_RajarClan(u, parts);
            case "/banclan": case "/ban-guild": return Cmd_BanClan(u, parts);
            case "/miembrosclan": return Cmd_MiembrosClan(u, parts);
            case "/onclan": case "/guild-online": return Cmd_OnClan(u, parts);
            case "/nick2ip": return Cmd_Nick2IP(u, parts);
            case "/ip2nick": return Cmd_IP2Nick(u, parts);
            case "/lastip": return Cmd_LastIP(u, parts);
            case "/noestupido": case "/smart": return Cmd_NoEstupido(u, parts);
            case "/darfaccion": case "/faction": return Cmd_DarFaccion(u, parts);
            case "/darpun": case "/points": return Cmd_DarPun(parts);
            case "/cuentaregresiva": case "/countdown": return Cmd_CuentaRegresiva(parts);
            case "/eventoro": case "/event-gold": return Cmd_EventoOro(parts);

            default: return false;
        }
    }

    // ===== IMPLEMENTACIONES =====

    /// <summary>Split por espacios respetando comillas dobles ("Juan Perez" = un token).
    /// El panel GM cita los nombres porque pueden contener espacios.</summary>
    private static string[] Tokenize(string chat)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in chat)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list.ToArray();
    }

    /// <summary>Resto de la línea desde parts[from]: los nombres de PJ/clan pueden tener espacios.</summary>
    private static string ArgResto(string[] parts, int from = 1) => string.Join(' ', parts.Skip(from));

    /// <summary>Nombre con espacios seguido de <paramref name="drop"/> argumentos fijos al final.</summary>
    private static string ArgNombre(string[] parts, int from, int drop) =>
        string.Join(' ', parts.Skip(from).Take(parts.Length - from - drop));

    private static bool Cmd_Spawn(User u, string[] parts)
    {
        int npcIdx = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 500;
        WorldPos front = u.Pos;
        Movement.HeadtoPos(u.Char.heading, ref front);
        NpcManager.SpawnAt(u.Pos.Map, npcIdx, (byte)front.X, (byte)front.Y);
        ServerPackets.ConsoleMsg(u.Conn, $"NPC {npcIdx} spawneado en ({front.X},{front.Y}).", 1);
        return true;
    }

    private static bool Cmd_Donde(User u, string[] parts)
    {
        // VB6 HandleWhere: con nombre muestra la posición del target (botón "Dónde" del panel GM)
        if (parts.Length > 1)
        {
            string nombre = ArgResto(parts);
            var t = BuscarOnline(nombre);
            if (t == null) return Send(u, $"'{nombre}' no está online.");
            return Send(u, $"{t.Name}: Mapa {t.Pos.Map} ({t.Pos.X},{t.Pos.Y})");
        }
        return Send(u, $"Mapa {u.Pos.Map} ({u.Pos.X},{u.Pos.Y}) heading {u.Char.heading}");
    }
    private static bool Cmd_Resucitar(int idx, User u) { Combat.Resucitar(idx); return Send(u, "¡Resucitado!"); }
    private static bool Cmd_Curar(User u) { u.Stats.MinHP = u.Stats.MaxHP; ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP); return Send(u, "¡Curado!"); }
    private static bool Cmd_Mana(User u) { u.Stats.MinMAN = u.Stats.MaxMAN; ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN); return Send(u, "¡Maná lleno!"); }
    private static bool Cmd_Stamina(User u) { u.Stats.MinSta = u.Stats.MaxSta; return Send(u, "¡Stamina llena!"); }
    private static bool Cmd_Save(User u) { CharSaver.SaveUser(u); return Send(u, "✓ Personaje guardado."); }
    /// <summary>/centinelaactivado — GM (Dios): activa/desactiva el centinela anti-macro.</summary>
    private static bool Cmd_CentinelaToggle(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses pueden activar el Centinela.");
        bool act = Centinela.Toggle();
        return Send(u, $"Centinela anti-macro: {(act ? "ACTIVADO" : "DESACTIVADO")}.");
    }

    /// <summary>/todosvstodos (alias /tvt, /pvpmapa) — GM (≥SemiDios): activa/desactiva el modo
    /// "todos contra todos" en el mapa actual. Con el modo activo, cualquier usuario vivo puede
    /// atacar a cualquier otro: se ignoran zona segura, pareja, clan, party, facciones aliadas y
    /// la protección de dioses/GMs (melee, distancia y hechizos — todos pasan por PuedeAtacar).
    /// Además, cualquier usuario que muera en el mapa resucita automáticamente con vida completa
    /// (deathmatch: la reanimación la dispara Combat.UserDie al final).</summary>
    private static bool Cmd_TodosVsTodos(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) return Send(u, "No tienes privilegios para usar este comando.");
        int mapa = u.Pos.Map;
        bool activado;
        if (Combat.MapasTodosVsTodos.Contains(mapa)) { Combat.MapasTodosVsTodos.Remove(mapa); activado = false; }
        else { Combat.MapasTodosVsTodos.Add(mapa); activado = true; }

        string msg = activado
            ? "¡TODOS CONTRA TODOS activado en este mapa! Cualquier usuario puede atacar a cualquier otro (incluidos GMs y dioses). Los caídos resucitan automáticamente para seguir peleando."
            : "Todos contra todos DESACTIVADO en este mapa: vuelven las reglas normales de combate.";
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == mapa)
                ServerPackets.ConsoleMsg(o.Conn, msg, 2); // font 2 = rojo + pestaña Combate
        }
        return Send(u, $"Todos contra todos en el mapa {mapa}: {(activado ? "ACTIVADO" : "DESACTIVADO")}.");
    }

    /// <summary>/torneoforce (/torneostart) — GM (≥SemiDios): arranca ya la cola con más equipos.</summary>
    private static bool Cmd_TorneoForce(int userIndex) { TorneoEvento.ForzarInicio(userIndex); return true; }

    /// <summary>/torneocancel — GM (≥SemiDios): cancela el torneo en curso.</summary>
    private static bool Cmd_TorneoCancel(int userIndex) { TorneoEvento.Cancelar(userIndex); return true; }

    /// <summary>/torneo (/torneoestado) — cualquiera: muestra el estado de colas/torneo.</summary>
    private static bool Cmd_TorneoEstado(int userIndex) { TorneoEvento.Estado(userIndex); return true; }

    /// <summary>/INVASION &lt;mapa&gt; &lt;cantidad&gt; — GM (≥SemiDios): dispara la invasión de cofres.</summary>
    private static bool Cmd_Invasion(User u, string[] parts)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) return Send(u, "No tienes privilegios para usar este comando.");
        if (parts.Length < 3 || !int.TryParse(parts[1], out int mapa) || !int.TryParse(parts[2], out int cant))
            return Send(u, "Uso correcto: /INVASION MAPA CANTIDAD");
        CofresEvento.IniciarInvasion(mapa, cant);
        return true;
    }

    /// <summary>/EVENTOCACERIA — GM (Dios): inicia la Cacería por Facción.</summary>
    private static bool Cmd_CaceriaIniciar(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses pueden activar el Evento de Cacería por Facción.");
        CaceriaEvento.Iniciar(u.Name);
        return Send(u, "Evento de Cacería por Facción iniciado correctamente.");
    }

    /// <summary>/EVENTOCACERIAOFF — GM (Dios): finaliza la Cacería y reparte premios.</summary>
    private static bool Cmd_CaceriaFinalizar(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses pueden finalizar el Evento de Cacería por Facción.");
        if (!CaceriaEvento.EventoActivo) return Send(u, "El Evento de Cacería por Facción no está activo.");
        CaceriaEvento.Finalizar(u.Name);
        return Send(u, "Evento de Cacería por Facción finalizado correctamente.");
    }

    /// <summary>/ESTADOCACERIA — GM (Dios/Admin): muestra el estado del evento.</summary>
    private static bool Cmd_CaceriaEstado(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses y administradores pueden ver el estado del evento.");
        return Send(u, CaceriaEvento.Estado());
    }

    /// <summary>/BARRIDO — GM (Dios): inicia el evento "El Barrido" (criatura cuerpo 702 en mapa 238).</summary>
    private static bool Cmd_BarridoIniciar(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses pueden activar el evento El Barrido.");
        if (BarridoEvento.EventoActivo) return Send(u, "El evento El Barrido ya está activo.");
        BarridoEvento.Iniciar(u.Name);
        return Send(u, "Evento El Barrido iniciado.");
    }

    /// <summary>/BARRIDOOFF — GM (Dios): finaliza el evento El Barrido.</summary>
    private static bool Cmd_BarridoFinalizar(User u)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses pueden finalizar el evento El Barrido.");
        if (!BarridoEvento.EventoActivo) return Send(u, "El evento El Barrido no está activo.");
        BarridoEvento.Finalizar();
        return Send(u, "Evento El Barrido finalizado.");
    }

    /// <summary>/MOVIMIENTO N — GM (Dios): cambia el patrón de movimiento de El Barrido (1=barrido ><, 2=X diagonal).</summary>
    private static bool Cmd_BarridoMovimiento(User u, string[] parts)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_DIOS) return Send(u, "Solo los dioses pueden cambiar el movimiento del evento.");
        if (!BarridoEvento.EventoActivo) return Send(u, "El evento El Barrido no está activo. Activalo con /barrido.");
        if (parts.Length < 2 || !int.TryParse(parts[1], out int modo)) return Send(u, "Uso: /MOVIMIENTO 1 (barrido) | 2 (X diagonal)");
        BarridoEvento.SetMovMode(modo);
        return Send(u, modo == BarridoEvento.MOV_X ? "Movimiento del Barrido: X diagonal." : "Movimiento del Barrido: barrido horizontal.");
    }

    private static bool Cmd_EventoExp(string[] parts)
    {
        int mult = parts.Length > 1 && int.TryParse(parts[1], out var mv) ? mv : 2;
        int segs = parts.Length > 2 && int.TryParse(parts[2], out var sv) ? sv : 300;
        Events.ActivarEventoExp(mult, segs);
        return true;
    }

    private static bool Cmd_Stats(User u, string[] parts)
    {
        if (parts.Length < 4 || !short.TryParse(parts[1], out short hp) ||
            !short.TryParse(parts[2], out short mana) || !short.TryParse(parts[3], out short sta))
            return Send(u, "Uso: /stats <hp> <mana> <sta>");
        u.Stats.MinHP = Math.Min(hp, u.Stats.MaxHP);
        u.Stats.MinMAN = Math.Min(mana, u.Stats.MaxMAN);
        u.Stats.MinSta = Math.Min(sta, u.Stats.MaxSta);
        ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
        ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
        return Send(u, "✓ Stats actualizados.");
    }

    private static bool Cmd_Exp(User u, string[] parts)
    {
        if (parts.Length < 2 || !long.TryParse(parts[1], out long exp))
            return Send(u, "Uso: /exp <cantidad>");
        u.Stats.Exp += exp;
        return Send(u, $"+{exp} exp → Total: {u.Stats.Exp}");
    }

    private static bool Cmd_Level(User u, string[] parts)
    {
        if (parts.Length < 2 || !byte.TryParse(parts[1], out byte lvl) || lvl < 1 || lvl > 60)
            return Send(u, "Uso: /level <1-60>");
        u.Stats.ELV = lvl;
        return Send(u, $"Nivel → {lvl}");
    }

    private static bool Cmd_Oro(User u, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int gld))
            return Send(u, "Uso: /gld <cantidad>");
        u.Stats.GLD += gld;
        return Send(u, $"+{gld} oro → Total: {u.Stats.GLD}");
    }

    private static bool Cmd_Banco(User u, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int banco))
            return Send(u, "Uso: /banco <cantidad>");
        u.Stats.Banco += banco;
        return Send(u, $"+{banco} banco → Total: {u.Stats.Banco}");
    }

    private static bool Cmd_Skill(User u, string[] parts)
    {
        if (parts.Length < 3 || !byte.TryParse(parts[1], out byte skillId) || !byte.TryParse(parts[2], out byte level))
            return Send(u, "Uso: /skill <id> <nivel>");
        if (skillId < 1 || skillId > Constants.NUMSKILLS) return Send(u, "Skill inválido.");
        u.Stats.UserSkills[skillId] = level;
        return Send(u, $"Skill {skillId} → {level}");
    }

    private static bool Cmd_Atributo(User u, string[] parts)
    {
        if (parts.Length < 3 || !byte.TryParse(parts[1], out byte attrId) || !byte.TryParse(parts[2], out byte valor))
            return Send(u, "Uso: /atributo <id> <valor>");
        if (attrId < 1 || attrId > Constants.NUMATRIBUTOS) return Send(u, "Atributo inválido.");
        u.Stats.UserAtributos[attrId] = valor;
        return Send(u, $"Atributo {attrId} → {valor}");
    }

    private static bool Cmd_Info(User u, string[] parts)
    {
        string targetName = parts.Length > 1 ? ArgResto(parts) : u.Name;
        var target = UserListManager.UserList.FirstOrDefault(x => x != null && x.Name == targetName);
        if (target == null) return Send(u, $"Usuario '{targetName}' no encontrado.");
        return Send(u, $"{target.Name}: Lvl {target.Stats.ELV} | HP {target.Stats.MinHP}/{target.Stats.MaxHP} | Mana {target.Stats.MinMAN}/{target.Stats.MaxMAN} | Oro {target.Stats.GLD}");
    }

    private static bool Cmd_Inventario(User u)
    {
        var items = string.Join(", ", u.Invent.Object.Where(x => x.ObjIndex > 0).Select(x => $"OBJ{x.ObjIndex}x{x.Amount}"));
        return Send(u, $"Inv: {(string.IsNullOrEmpty(items) ? "(vacío)" : items)}");
    }

    private static bool Cmd_BancoInv(User u)
    {
        var items = string.Join(", ", u.BancoInvent.Object.Where(x => x.ObjIndex > 0).Select(x => $"OBJ{x.ObjIndex}x{x.Amount}"));
        return Send(u, $"Banco: {(string.IsNullOrEmpty(items) ? "(vacío)" : items)}");
    }

    private static bool Cmd_Hechizos(User u)
    {
        var spells = string.Join(", ", u.Stats.UserHechizos.Where(x => x > 0).Select((x, i) => $"H{x}"));
        return Send(u, $"Hechizos: {(string.IsNullOrEmpty(spells) ? "(ninguno)" : spells)}");
    }

    private static bool Cmd_Skills(User u)
    {
        var skills = string.Join(", ", u.Stats.UserSkills.Select((v, i) => v > 0 ? $"S{i}:{v}" : null).Where(x => x != null));
        return Send(u, $"Skills: {(string.IsNullOrEmpty(skills) ? "(ninguno)" : skills)}");
    }

    private static bool Cmd_Online() => Send(null, $"Online: {UserListManager.LastUser} usuarios");

    private static bool Cmd_ListaUsuarios(User u)
    {
        // VB6 HandleRequestUserList: recopila nombres de todos online y envía WriteUserNameList
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && !string.IsNullOrEmpty(t.Name))
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(t.Name);
            }
        }
        ServerPackets.UserNameList(u.Conn, sb.ToString());
        return true;
    }

    private static bool Cmd_OnlineGM()
    {
        var gms = new System.Text.StringBuilder("GMs online: ");
        bool any = false;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && t.Clase >= 20)
            { gms.Append(t.Name + " "); any = true; }
        }
        if (!any) gms.Append("ninguno");
        Console.WriteLine(gms);
        return true;
    }

    private static bool Cmd_OnlineMap(User u) => Send(u, $"En este mapa: {UserListManager.UserList.Count(x => x != null && x.Pos.Map == u.Pos.Map)} usuarios");

    private static bool Cmd_Criaturas(User u, string[] parts = null)
    {
        // VB6 HandleCreaturesInMap: cuenta NPCs vivos en el mapa y los muestra
        int mapId = parts?.Length > 1 && int.TryParse(parts[1], out var m) ? m : u.Pos.Map;
        var npcs = NpcManager.GetMapNpcs(mapId);
        int vivos = npcs.Count(n => !n.Dead);
        int muertos = npcs.Count(n => n.Dead);
        return Send(u, $"Mapa {mapId}: {vivos} NPCs vivos, {muertos} esperando respawn. Total instancias: {npcs.Count}");
    }

    private static bool Cmd_Bloq(User u, string[] parts)
    {
        if (parts.Length < 3 || !byte.TryParse(parts[1], out byte x) || !byte.TryParse(parts[2], out byte y))
            return Send(u, "Uso: /bloq <x> <y>");
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return Send(u, "Mapa no encontrado.");
        map.Blocked[x, y] = !map.Blocked[x, y];
        return Send(u, $"Tile ({x},{y}) → {(map.Blocked[x, y] ? "BLOQUEADO" : "DESBLOQUEADO")}");
    }

    private static bool Cmd_Trigger(User u, string[] parts)
    {
        // VB6 HandleSetTrigger: set MapData.Trigger en tile actual
        if (parts.Length < 2 || !byte.TryParse(parts[1], out byte trigger))
            return Send(u, "Uso: /trigger <tipo> (0=nada, 1=bajotecho, 2=trigger2, 3=posInvalida, 4=zonaSegura, 5=antiPiquete, 6=zonaPerlea)");
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return Send(u, "Mapa no encontrado.");
        map.Trigger[u.Pos.X, u.Pos.Y] = trigger;
        return Send(u, $"Trigger {trigger} seteado en ({u.Pos.X},{u.Pos.Y}) mapa {u.Pos.Map}");
    }

    private static bool Cmd_Hora()
    {
        return false;  // Comando sin parámetros
    }

    private static bool Cmd_GMMsg(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /gmsg <mensaje>");
        string msg = string.Join(" ", parts.Skip(1));
        foreach (var other in UserListManager.UserList.Where(x => x?.Conn != null && x.flags.UserLogged))
            ServerPackets.ConsoleMsg(other.Conn, $"[GM {u.Name}]: {msg}", 2);
        return true;
    }

    private static bool Cmd_SMsg(string[] parts)
    {
        if (parts.Length < 2) return false;
        string msg = string.Join(" ", parts.Skip(1));
        foreach (var user in UserListManager.UserList.Where(x => x?.Conn != null && x.flags.UserLogged))
            ServerPackets.ConsoleMsg(user.Conn, msg, 2);
        return true;
    }

    private static bool Cmd_RMsg(string[] parts)
    {
        if (parts.Length < 2) return false;
        string msg = string.Join(" ", parts.Skip(1));
        foreach (var user in UserListManager.UserList.Where(x => x?.Conn != null && x.flags.UserLogged))
            ServerPackets.ConsoleMsg(user.Conn, msg, 4);
        return true;
    }

    private static bool Cmd_Telep(User u, string[] parts)
    {
        // Formato desde panel GM: /telep nombre mapa x y (el nombre puede tener espacios)
        // Formato manual: /telep mapa x y
        if (parts.Length >= 5 && short.TryParse(parts[^3], out short map2) &&
            byte.TryParse(parts[^2], out byte x2) && byte.TryParse(parts[^1], out byte y2))
        {
            string targetName = ArgNombre(parts, 1, 3);
            int targetIdx;
            // VB6: "YO" se resuelve al propio usuario (el GM se teletransporta a sí mismo).
            if (string.Equals(targetName, "YO", StringComparison.OrdinalIgnoreCase))
                targetIdx = u.id;
            else
            {
                targetIdx = -1;
                for (int i = 1; i <= UserListManager.LastUser; i++)
                {
                    var t = UserListManager.UserList[i];
                    if (t != null && t.flags.UserLogged && string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    { targetIdx = i; break; }
                }
                if (targetIdx < 0) return Send(u, $"Usuario '{targetName}' no encontrado.");
            }
            Movement.WarpUser(targetIdx, map2, (short)x2, (short)y2);
            return Send(u, $"✓ {targetName} → Mapa {map2} ({x2},{y2})");
        }
        // Teleportar al GM mismo
        if (parts.Length >= 4 && short.TryParse(parts[1], out short map) &&
            byte.TryParse(parts[2], out byte x) && byte.TryParse(parts[3], out byte y))
        {
            Movement.WarpUser(u.id, map, (short)x, (short)y);
            return Send(u, $"→ Mapa {map} ({x},{y})");
        }
        return Send(u, "Uso: /telep [usuario] <mapa> <x> <y>");
    }

    private static bool Cmd_IRA(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /ira <usuario>");
        string nombre = ArgResto(parts);
        var target = BuscarOnline(nombre);
        if (target == null) return Send(u, $"Usuario '{nombre}' no encontrado.");
        u.Pos = target.Pos;
        return Send(u, $"→ Con {target.Name} en mapa {target.Pos.Map}");
    }

    private static bool Cmd_IrCerca(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /ircerca <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                short nx = (short)Math.Max(1, t.Pos.X - 3);
                short ny = (short)Math.Max(1, t.Pos.Y - 3);
                Movement.WarpUser(u.id, t.Pos.Map, nx, ny);
                return Send(u, $"→ Cerca de {t.Name}");
            }
        }
        return Send(u, $"Usuario '{nombre}' no encontrado.");
    }

    private static bool Cmd_Summon(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /sum <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                Movement.WarpUser(i, u.Pos.Map, u.Pos.X, u.Pos.Y);
                ServerPackets.ConsoleMsg(t.Conn, "¡Has sido invocado!", 1);
                return Send(u, $"✓ {t.Name} invocado.");
            }
        }
        return Send(u, $"Usuario '{nombre}' no encontrado.");
    }

    private static bool Cmd_Invisible(User u)
    {
        // VB6 HandleInvisible: toggle Meditando + WriteMeditateToggle al cliente
        u.flags.Meditando = !u.flags.Meditando;
        ServerPackets.MeditateToggle(u.Conn);
        return Send(u, u.flags.Meditando ? "¡Invisible!" : "¡Visible!");
    }

    private static bool Cmd_Trabajando(User u) { u.flags.Trabajando = !u.flags.Trabajando; return Send(u, u.flags.Trabajando ? "Trabajando..." : "Parado."); }
    private static bool Cmd_Ocultando(User u) { u.flags.Meditando = !u.flags.Meditando; return Send(u, u.flags.Meditando ? "Oculto activado." : "Oculto desactivado."); }
    private static bool Cmd_Navigate(User u)
    {
        u.flags.Navegando = !u.flags.Navegando;
        ServerPackets.NavigateToggle(u.Conn);
        return Send(u, u.flags.Navegando ? "Navegación activada." : "Navegación desactivada.");
    }
    private static bool Cmd_Volar(User u) { return Send(u, "Vuelo: (no implementado aún)"); }

    private static bool Cmd_Ban(User u, string[] parts)
    {
        // /ban usuario 0=ban 1=unban
        if (parts.Length < 2) return Send(u, "Uso: /ban <usuario> [0=ban|1=unban]");
        // El flag 0/1 es opcional y va al final; todo lo demás es el nombre (puede tener espacios)
        bool hasFlag = parts.Length > 2 && (parts[^1] == "0" || parts[^1] == "1");
        string nombre = hasFlag ? ArgNombre(parts, 1, 1) : ArgResto(parts);
        bool unban = hasFlag && parts[^1] == "1";

        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (!System.IO.File.Exists(chrPath)) return Send(u, $"Personaje '{nombre}' no existe.");

        var ini = new IniDocument(chrPath);
        if (unban)
        {
            ini.Set("CUENTA", "Banned", "0");
            ini.Save(chrPath);
            return Send(u, $"✓ {nombre} desbaneado.");
        }
        else
        {
            ini.Set("CUENTA", "Banned", "1");
            ini.Save(chrPath);
            // Desconectar si está online
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var t = UserListManager.UserList[i];
                if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
                { t.Conn?.Close(); break; }
            }
            return Send(u, $"✓ {nombre} baneado.");
        }
    }

    // AppContext.BaseDirectory funciona también en single-file (Assembly.Location devuelve "" ahí).
    private static readonly string BannedIPFile = System.IO.Path.Combine(
        AppContext.BaseDirectory, "BannedIPs.txt");

    private static bool Cmd_BanIP(string[] parts)
    {
        // VB6 HandleBanIP: agrega IP a archivo BannedIP y desconecta si está online
        if (parts.Length < 2) return Send(null, "Uso: /banip <ip> [motivo]");
        string ip = parts[1];
        System.IO.File.AppendAllText(BannedIPFile, ip + "\n");
        // Desconectar online con esa IP
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t?.flags.UserLogged == true && t.Conn?.RemoteEndPoint?.ToString().StartsWith(ip) == true)
                t.Conn.Close();
        }
        Console.WriteLine($"[GM] IP baneada: {ip}"); return true;
    }

    private static bool Cmd_UnbanIP(string[] parts)
    {
        if (parts.Length < 2) return false;
        string ip = parts[1];
        if (System.IO.File.Exists(BannedIPFile))
        {
            var lines = System.IO.File.ReadAllLines(BannedIPFile).Where(l => l.Trim() != ip).ToArray();
            System.IO.File.WriteAllLines(BannedIPFile, lines);
        }
        Console.WriteLine($"[GM] IP desbaneada: {ip}"); return true;
    }

    private static bool Cmd_BanIPList()
    {
        if (!System.IO.File.Exists(BannedIPFile)) { Console.WriteLine("[GM] No hay IPs baneadas."); return true; }
        var lines = System.IO.File.ReadAllLines(BannedIPFile);
        Console.WriteLine($"[GM] IPs baneadas ({lines.Length}): " + string.Join(", ", lines));
        return true;
    }

    private static bool Cmd_BanIPReload() { Console.WriteLine("[GM] /banipreload: recargado."); return true; }

    private static bool Cmd_Kick(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /echar <usuario>");
        string nombre = ArgResto(parts);
        var target = BuscarOnline(nombre);
        if (target == null) return Send(u, $"Usuario '{nombre}' no encontrado.");
        target.Conn?.Close();
        return Send(u, $"✓ {target.Name} expulsado.");
    }

    private static bool Cmd_Jail(User u, string[] parts)
    {
        // /carcel usuario motivo minutos  → teleporta a mapa prisión (13) y escribe pena
        if (parts.Length < 4 || !byte.TryParse(parts[3], out byte mins))
            return Send(u, "Uso: /carcel <usuario> <motivo> <minutos>");
        string nombre = parts[1];
        string motivo = parts[2];

        // Escribir pena en .chr
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (System.IO.File.Exists(chrPath))
        {
            var ini = new IniDocument(chrPath);
            int cant = new IniFile(chrPath).GetInt("PENAS", "Cant") + 1;
            ini.Set("PENAS", "Cant", cant.ToString());
            ini.Set("PENAS", $"P{cant}",$"{u.Name}: CARCEL por: {motivo} {DateTime.Now:dd/MM/yyyy HH:mm}");
            ini.Save(chrPath);
        }

        // Encarcelar si está online: fija la condena (flags.Pena) y warpea a la ciudad Prisión real
        // (CityData cPrision=13). PurgarPenas lo libera al cumplir. (Antes warpeaba a "mapa 13" —
        // el índice de ciudad, NO el mapa— y no seteaba la pena → nunca se liberaba.)
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                Jail.Encarcelar(i, mins, u.Name);
                ServerPackets.ConsoleMsg(t.Conn, $"Motivo: {motivo}", 4);
                break;
            }
        }
        return Send(u, $"✓ {nombre} encarcelado {mins} min. Motivo: {motivo}");
    }

    private static bool Cmd_Warn(User u, string[] parts)
    {
        if (parts.Length < 3) return Send(u, "Uso: /advertencia <usuario> <motivo>");
        string nombre = parts[1];
        string motivo = string.Join(" ", parts.Skip(2));

        // Escribir advertencia en .chr
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (System.IO.File.Exists(chrPath))
        {
            var ini = new IniDocument(chrPath);
            int cant = new IniFile(chrPath).GetInt("PENAS", "Cant") + 1;
            ini.Set("PENAS", "Cant", cant.ToString());
            ini.Set("PENAS", $"P{cant}",$"{u.Name}: ADVERTENCIA por: {motivo} {DateTime.Now:dd/MM/yyyy HH:mm}");
            ini.Save(chrPath);
        }

        // Notificar si está online
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            { ServerPackets.ConsoleMsg(t.Conn, $"⚠ Advertencia de GM: {motivo}", 4); break; }
        }
        return Send(u, $"✓ Has advertido a {nombre}.");
    }

    private static bool Cmd_Mod(User u, string[] parts)
    {
        // /mod usuario opcion arg1 [arg2]
        // opcion: 1=oro 2=exp 3=cuerpo 4=cabeza 5=ciudMatados 6=crimMatados 7=nivel 8=clase 9=skills 10=puntosSkill 11=sexo 12=raza 13=agregaroro
        if (parts.Length < 4) return Send(u, "Uso: /mod <usuario> <opcion> <valor> [valor2]");

        // El nombre puede tener espacios: la opción es el primer token numérico después del
        // comando (los nombres de PJ no llevan dígitos), dejando al menos un valor detrás.
        int optIdx = -1;
        byte opcion = 0;
        for (int i = 2; i <= parts.Length - 2; i++)
            if (byte.TryParse(parts[i], out opcion)) { optIdx = i; break; }
        if (optIdx == -1) return Send(u, "Opción inválida.");

        string nombre = string.Join(' ', parts, 1, optIdx - 1);
        string arg1   = parts.Length > optIdx + 1 ? parts[optIdx + 1] : "";
        string arg2   = parts.Length > optIdx + 2 ? parts[optIdx + 2] : "";

        // VB6: "YO" se resuelve al propio GM.
        if (string.Equals(nombre, "YO", StringComparison.OrdinalIgnoreCase)) nombre = u.Name;

        // Buscar usuario online
        int tIdx = -1;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            { tIdx = i; break; }
        }

        User target = tIdx > 0 ? UserListManager.UserList[tIdx] : null;
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");

        if (target == null && !System.IO.File.Exists(chrPath))
            return Send(u, $"Usuario '{nombre}' no encontrado.");

        switch (opcion)
        {
            case 1: // ORO
                if (!int.TryParse(arg1, out int oro)) return Send(u, "Valor inválido.");
                if (target != null) { target.Stats.GLD = oro; ServerPackets.UpdateGold(target.Conn, oro); }
                else { var d = new IniDocument(chrPath); d.Set("STATS", "GLD", oro.ToString()); d.Save(chrPath); }
                return Send(u, $"✓ Oro de {nombre} → {oro}");

            case 2: // EXP
                if (!int.TryParse(arg1, out int exp)) return Send(u, "Valor inválido.");
                if (target != null) { target.Stats.Exp += exp; ServerPackets.UpdateExp(target.Conn, (int)target.Stats.Exp); }
                else { var ini2 = new IniFile(chrPath); var d2 = new IniDocument(chrPath); d2.Set("STATS", "EXP", (ini2.GetInt("STATS","EXP")+exp).ToString()); d2.Save(chrPath); }
                return Send(u, $"✓ EXP +{exp} a {nombre}");

            case 3: // CUERPO
                if (!short.TryParse(arg1, out short body)) return Send(u, "Valor inválido.");
                if (target != null) { target.Char.body = body; Combat.DifundirApariencia(target); }
                else { var d3 = new IniDocument(chrPath); d3.Set("INIT", "Body", arg1); d3.Save(chrPath); }
                return Send(u, $"✓ Cuerpo de {nombre} → {body}");

            case 4: // CABEZA
                if (!short.TryParse(arg1, out short head)) return Send(u, "Valor inválido.");
                if (target != null) { target.Char.Head = head; Combat.DifundirApariencia(target); }
                else { var d4 = new IniDocument(chrPath); d4.Set("INIT", "Head", arg1); d4.Save(chrPath); }
                return Send(u, $"✓ Cabeza de {nombre} → {head}");

            case 7: // NIVEL — además ajusta vida/maná (valor FIJO) y energía/HIT (aumentos por nivel), sube o baja
            {
                if (!short.TryParse(arg1, out short lvl) || lvl < 1 || lvl > Leveling.STAT_MAXELV)
                    return Send(u, $"Nivel inválido (1-{Leveling.STAT_MAXELV}).");
                int hp7, man7, sta7, skp7;
                if (target != null)
                {
                    hp7  = Math.Min(Leveling.VidaFijaPorNivel(target.raza, target.Clase, lvl), Leveling.STAT_MAXHP);
                    man7 = Math.Min(Leveling.ManaFijaPorNivel(target.raza, target.Clase, lvl), Leveling.STAT_MAXMAN);
                    // Energía y HIT no tienen valor fijo por nivel (la base de nivel 1 es por dados):
                    // se aplican los MISMOS aumentos por nivel de la subida natural, en ambas direcciones.
                    (sta7, int maxHit7, int minHit7) = AjustarStaHitPorNivel(
                        target.Clase, target.Stats.ELV, lvl, target.Stats.MaxSta, target.Stats.MaxHIT, target.Stats.MinHIT);
                    // Skills libres: ±5 por nivel de diferencia, igual que la subida natural; nunca < 0.
                    skp7 = Math.Max(0, target.Stats.SkillPts + 5 * (lvl - target.Stats.ELV));
                    target.Stats.SkillPts = (short)skp7;
                    target.Stats.ELV = (byte)lvl;
                    target.Stats.ELU = Leveling.ELU(lvl);
                    target.Stats.Exp = 0; // exp limpia: evita re-subir al instante si se bajó el nivel
                    target.Stats.MaxHP = (short)hp7;  target.Stats.MinHP  = target.Stats.MaxHP;
                    target.Stats.MaxMAN = (short)man7; target.Stats.MinMAN = target.Stats.MaxMAN;
                    target.Stats.MaxSta = (short)sta7; target.Stats.MinSta = target.Stats.MaxSta;
                    target.Stats.MaxHIT = (short)maxHit7; target.Stats.MinHIT = (short)minHit7;
                    ServerPackets.UpdateUserStats(target.Conn, target);
                    ServerPackets.UpdateSta(target.Conn, target.Stats.MinSta);
                    ServerPackets.LevelUp(target.Conn, target.Stats.SkillPts); // refresca skills libres en el cliente
                    ServerPackets.ConsoleMsg(target.Conn, $"Un GM ha establecido tu nivel en {lvl}.", 1);
                    // Igual que la subida natural: si quedó nivel 15+ dentro del Dungeon Newbie,
                    // se lo expulsa a la ciudad de su facción (y se actualiza su Hogar).
                    Facciones.SalirDungeonNewbie(target, warpear: true);
                }
                else
                {
                    var ini7 = new IniFile(chrPath);
                    byte raza7  = (byte)ini7.GetInt("INIT", "Raza");
                    byte clase7 = (byte)ini7.GetInt("INIT", "Clase");
                    int elv7    = Math.Max(1, ini7.GetInt("STATS", "ELV"));
                    hp7  = Math.Min(Leveling.VidaFijaPorNivel(raza7, clase7, lvl), Leveling.STAT_MAXHP);
                    man7 = Math.Min(Leveling.ManaFijaPorNivel(raza7, clase7, lvl), Leveling.STAT_MAXMAN);
                    (sta7, int maxHit7, int minHit7) = AjustarStaHitPorNivel(
                        clase7 == 0 ? (byte)1 : (byte)clase7, elv7, lvl,
                        ini7.GetInt("STATS", "MaxSTA"), ini7.GetInt("STATS", "MaxHIT"), ini7.GetInt("STATS", "MinHIT"));
                    skp7 = Math.Max(0, ini7.GetInt("STATS", "SkillPtsLibres") + 5 * (lvl - elv7));
                    var d7 = new IniDocument(chrPath);
                    d7.Set("STATS", "ELV", lvl.ToString());
                    d7.Set("STATS", "ELU", Leveling.ELU(lvl).ToString());
                    d7.Set("STATS", "EXP", "0");
                    d7.Set("STATS", "MaxHP", hp7.ToString());  d7.Set("STATS", "MinHP", hp7.ToString());
                    d7.Set("STATS", "MaxMAN", man7.ToString()); d7.Set("STATS", "MinMAN", man7.ToString());
                    d7.Set("STATS", "MaxSTA", sta7.ToString()); d7.Set("STATS", "MinSTA", sta7.ToString());
                    d7.Set("STATS", "MaxHIT", maxHit7.ToString()); d7.Set("STATS", "MinHIT", minHit7.ToString());
                    d7.Set("STATS", "SkillPtsLibres", skp7.ToString());
                    d7.Save(chrPath);
                }
                return Send(u, $"✓ Nivel de {nombre} → {lvl} (Vida {hp7}, Maná {man7}, Energía {sta7}, Skills libres {skp7})");
            }

            case 13: // AGREGAR ORO (banco)
                if (!int.TryParse(arg1, out int addOro)) return Send(u, "Valor inválido.");
                if (target != null) { target.Stats.Banco = Math.Max(0, target.Stats.Banco + addOro); }
                else { var ini13 = new IniFile(chrPath); var d13 = new IniDocument(chrPath); d13.Set("STATS", "BANCO", Math.Max(0, ini13.GetInt("STATS","BANCO")+addOro).ToString()); d13.Save(chrPath); }
                return Send(u, $"✓ Banco +{addOro} a {nombre}");

            case 8: // CLASE — arg1 = nombre de la clase (ej "ASESINO")
            {
                int clase = ClasePorNombre(arg1);
                if (clase == 0) return Send(u, $"Clase desconocida: {arg1}");
                if (target != null) target.Clase = (byte)clase;
                else { var dc = new IniDocument(chrPath); dc.Set("INIT", "Clase", clase.ToString()); dc.Save(chrPath); }
                return Send(u, $"✓ Clase de {nombre} → {arg1.ToUpper()} ({clase})");
            }

            case 11: // SEXO — HOMBRE=1, MUJER=2
            {
                byte sexo = string.Equals(arg1, "HOMBRE", StringComparison.OrdinalIgnoreCase) ? (byte)1
                          : string.Equals(arg1, "MUJER", StringComparison.OrdinalIgnoreCase) ? (byte)2 : (byte)0;
                if (sexo == 0) return Send(u, "Sexo inválido (HOMBRE/MUJER).");
                if (target != null) target.Genero = sexo;
                else { var ds = new IniDocument(chrPath); ds.Set("INIT", "Genero", sexo.ToString()); ds.Save(chrPath); }
                return Send(u, $"✓ Sexo de {nombre} → {arg1.ToUpper()}");
            }

            case 12: // RAZA — HUMANO=1, ELFO=2, DROW=3, GNOMO=4, ENANO=5, ORCO=6
            {
                int raza = RazaPorNombre(arg1);
                if (raza == 0) return Send(u, $"Raza desconocida: {arg1}");
                if (target != null) target.raza = (byte)raza;
                else { var dr = new IniDocument(chrPath); dr.Set("INIT", "Raza", raza.ToString()); dr.Save(chrPath); }
                return Send(u, $"✓ Raza de {nombre} → {arg1.ToUpper()} ({raza})");
            }

            default:
                return Send(u, $"Opción {opcion} no implementada aún.");
        }
    }

    /// <summary>
    /// Ajusta energía y HIT al cambiar de nivel con /mod nivel: aplica los MISMOS aumentos por
    /// nivel de la subida natural (Leveling.AumentoSta/AumentoHit), nivel a nivel, en ambas
    /// direcciones. La base de nivel 1 (dados del PJ) se conserva.
    /// </summary>
    private static (int sta, int maxHit, int minHit) AjustarStaHitPorNivel(
        byte clase, int nivelActual, int nivelNuevo, int sta, int maxHit, int minHit)
    {
        if (nivelNuevo > nivelActual)
        {
            for (int n = nivelActual + 1; n <= nivelNuevo; n++)
            {
                sta += Leveling.AumentoSta(clase);
                int tope = n < 36 ? Leveling.STAT_MAXHIT_UNDER36 : Leveling.STAT_MAXHIT_OVER36;
                maxHit = Math.Min(maxHit + Leveling.AumentoHit(clase, n), tope);
                minHit = Math.Min(minHit + Leveling.AumentoHit(clase, n), tope);
            }
        }
        else if (nivelNuevo < nivelActual)
        {
            for (int n = nivelActual; n > nivelNuevo; n--)
            {
                sta -= Leveling.AumentoSta(clase);
                maxHit -= Leveling.AumentoHit(clase, n);
                minHit -= Leveling.AumentoHit(clase, n);
            }
        }
        sta = Math.Clamp(sta, 40, Leveling.STAT_MAXSTA);   // 40 = piso de la STA inicial (20*2)
        maxHit = Math.Clamp(maxHit, 2, Leveling.STAT_MAXHIT_OVER36);
        minHit = Math.Clamp(minHit, 1, Leveling.STAT_MAXHIT_OVER36);
        return (sta, maxHit, minHit);
    }

    // eClass (Declares.bas:149): nombre → índice. 0 = desconocida.
    private static int ClasePorNombre(string s) => s?.ToUpperInvariant() switch
    {
        "CLERIGO" => 1, "MAGO" => 2, "GUERRERO" => 3, "ASESINO" => 4, "LADRON" => 5,
        "BARDO" => 6, "DRUIDA" => 7, "GLADIADOR" => 8, "PALADIN" => 9, "CAZADOR" => 10,
        "PESCADOR" => 11, "HERRERO" => 12, "LEÑADOR" => 13, "LENADOR" => 13, "MINERO" => 14,
        "CARPINTERO" => 15, "SASTRE" => 16, "MERCENARIO" => 17, "NIGROMANTE" => 18,
        _ => 0
    };

    // eRaza (Declares.bas): nombre → índice. 0 = desconocida.
    private static int RazaPorNombre(string s) => s?.ToUpperInvariant() switch
    {
        "HUMANO" => 1, "ELFO" => 2, "DROW" => 3, "ELFOOSCURO" => 3,
        "GNOMO" => 4, "ENANO" => 5, "ORCO" => 6,
        _ => 0
    };
    private static bool Cmd_APass(string[] parts) { return false; }

    private static bool Cmd_RMata(User u)
    {
        var npc = NpcManager.NpcAt(u.Pos.Map, u.Pos.X, u.Pos.Y);
        if (npc == null) return Send(u, "No hay NPC aquí.");
        KillNpc(u, npc, respawn: true);
        return Send(u, $"✓ {npc.Name} eliminado.");
    }

    private static bool Cmd_Mata(User u)
    {
        var npc = NpcManager.NpcAt(u.Pos.Map, u.Pos.X, u.Pos.Y);
        if (npc == null) return Send(u, "No hay NPC aquí.");
        KillNpc(u, npc, respawn: false);
        return Send(u, $"✓ {npc.Name} eliminado sin respawn.");
    }

    private static bool Cmd_MassKill(User u)
    {
        var npcs = NpcManager.GetMapNpcs(u.Pos.Map)
            .Where(n => !n.Dead && Math.Abs(n.X - u.Pos.X) <= 8 && Math.Abs(n.Y - u.Pos.Y) <= 6)
            .ToList();
        foreach (var n in npcs) KillNpc(u, n, respawn: true);
        return Send(u, $"✓ {npcs.Count} NPCs eliminados.");
    }

    private static void KillNpc(User u, NpcManager.NpcInstance npc, bool respawn)
    {
        npc.Dead = true;
        npc.RespawnAt = respawn ? (Environment.TickCount64 / 1000.0 + NpcManager.RespawnSeconds) : double.MaxValue;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == npc.Map)
                ServerPackets.CharacterRemove(o.Conn, npc.CharIndex);
        }
    }

    private static bool Cmd_CreateNPC(User u, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int npcIdx))
            return Send(u, "Uso: /acc <npcIndex>");
        NpcManager.SpawnAt(u.Pos.Map, npcIdx, (byte)u.Pos.X, (byte)u.Pos.Y);
        return Send(u, $"✓ NPC {npcIdx} creado.");
    }

    private static bool Cmd_RespawnNPC(User u, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int npcIdx))
            return Send(u, "Uso: /racc <npcIndex>");
        WorldPos front = u.Pos;
        Movement.HeadtoPos(u.Char.heading, ref front);
        NpcManager.SpawnAt(u.Pos.Map, npcIdx, (byte)front.X, (byte)front.Y);
        return Send(u, $"✓ NPC {npcIdx} spawneado con respawn en ({front.X},{front.Y}).");
    }

    private static bool Cmd_Seguir(User u, string[] parts)
    {
        // VB6 HandleNpcFollow: no existe handler → el NPC sigue al GM via IA
        // TODO: implementar cuando se porte el sistema de IA de NPCs con flag Follow
        return Send(u, "Seguir NPC: (requiere AI follow - TODO)");
    }
    private static bool Cmd_TalkAs(User u, string[] parts)
    {
        // VB6: Busca TargetNPC, usa su CharIndex para enviar ChatOverHead al mapa
        if (parts.Length < 2) return Send(u, "Uso: /talkas <texto>");
        string texto = string.Join(" ", parts.Skip(1));
        // Broadcast como NPC usando CharIndex del GM (sin TargetNPC tracking aún)
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.ChatOverHead(o.Conn, texto, u.Char.CharIndex, 1);
        }
        return true;
    }
    private static bool Cmd_ReloadNPCs() { return false; }

    private static bool Cmd_CI(User u, string[] parts)
    {
        Console.WriteLine($"[CI] Cmd_CI llamado: parts.Length={parts.Length}");
        if (parts.Length < 2 || !int.TryParse(parts[1], out int objIdx))
        {
            Console.WriteLine("[CI] Error: sin parámetros");
            return Send(u, "Uso: /ci <objIndex> [cantidad]");
        }
        int amt = parts.Length > 2 && int.TryParse(parts[2], out var a) ? a : 1;
        Console.WriteLine($"[CI] objIdx={objIdx}, amt={amt}");

        var od = ObjData.Get(objIdx);
        Console.WriteLine($"[CI] ObjData.Type={od.Type}");
        if (od.Type == 0) return Send(u, $"Objeto {objIdx} no existe.");

        if (!Inventory.AddItemToInventory(u, (short)objIdx, amt))
        {
            Console.WriteLine("[CI] Inventario lleno");
            return Send(u, "Inventario lleno.");
        }

        Console.WriteLine($"[CI] Item agregado correctamente");
        return Send(u, $"✓ Objeto {objIdx} ({od.Name}) x{amt} agregado al inventario");
    }

    // Tipos de objeto que NO se eliminan con comandos GM (objetos del mapa)
    private static readonly HashSet<ObjType> TiposEstructurales = new()
    {
        ObjType.Puertas, ObjType.Carteles, ObjType.Arboles, ObjType.Teleport,
        ObjType.Muebles, ObjType.Yacimiento, ObjType.Yunque, ObjType.Fragua,
        ObjType.Pozos, ObjType.Puestos
    };

    private static bool EsObjEliminable(int objIdx, int amount)
    {
        if (objIdx <= 0 || amount <= 0) return false; // sin obj o sin cantidad = objeto del mapa
        var od = ObjData.Get(objIdx);
        return !TiposEstructurales.Contains(od.Type);
    }

    private static void BorrarObjEnTile(MapData map, int mapId, int x, int y)
    {
        map.FloorObj[x, y] = 0; map.FloorAmount[x, y] = 0;
        AreaVisibility.ObjectRemoved(mapId, x, y);
    }

    private static bool Cmd_Dest(User u, string[] parts)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return Send(u, "Mapa no encontrado.");
        int x = u.Pos.X, y = u.Pos.Y;
        if (!EsObjEliminable(map.FloorObj[x, y], map.FloorAmount[x, y]))
            return Send(u, "No hay objetos eliminables en este tile.");
        BorrarObjEnTile(map, u.Pos.Map, x, y);
        return Send(u, "✓ Objeto eliminado.");
    }

    private static bool Cmd_MassDest(User u)
    {
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return Send(u, "Mapa no encontrado.");
        int count = 0;
        for (int x = Math.Max(1, u.Pos.X - 5); x <= Math.Min(100, u.Pos.X + 5); x++)
        for (int y = Math.Max(1, u.Pos.Y - 5); y <= Math.Min(100, u.Pos.Y + 5); y++)
        {
            if (!EsObjEliminable(map.FloorObj[x, y], map.FloorAmount[x, y])) continue;
            BorrarObjEnTile(map, u.Pos.Map, x, y); count++;
        }
        return Send(u, $"✓ {count} objetos eliminados en área.");
    }

    private static bool Cmd_ResetInv(User u) { return Send(u, "Reset inventario NPC: (no implementado aún)"); }

    private static bool Cmd_Limpiar(User u)
    {
        // 1:1 VB6 HandleCleanWorld: solo elimina objetos tirados (Amount > 0) que no sean estructurales
        int count = 0;
        for (int mapId = 1; mapId <= 300; mapId++)
        {
            var map = MapLoader.Get(mapId);
            if (map == null) continue;
            for (int x = 1; x <= 100; x++)
            for (int y = 1; y <= 100; y++)
            {
                if (!EsObjEliminable(map.FloorObj[x, y], map.FloorAmount[x, y])) continue;
                BorrarObjEnTile(map, mapId, x, y); count++;
            }
        }
        return Send(u, $"✓ Limpieza ejecutada. {count} objetos eliminados. Objetos de mapa (puertas, carteles, decoración) permanecen intactos.");
    }

    private static bool Cmd_GuardaMapa(User u)
    {
        // VB6: GrabarMapa - guarda el mapa actual en WorldBackUp/
        // En C# aún no tenemos serialización de mapas, pero sí podemos guardar los personajes
        int n = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged) { CharSaver.SaveUser(t); n++; }
        }
        Console.WriteLine($"[GM] /guardamapa: mapa {u.Pos.Map} guardado + {n} personajes guardados.");
        return Send(u, $"✓ Mapa {u.Pos.Map} guardado.");
    }
    private static bool Cmd_ModMapInfo(User u, string[] parts)
    {
        // VB6 HandleChangeMapInfo*: 8 subopciones, cada una modifica MapInfo y escribe en .dat
        // Cliente envía sub-IDs 70-77 con un byte Boolean
        return Send(u, "ModMapInfo: (sub-comandos 70-77 mapeados por PacketHandler)");
    }
    private static bool Cmd_CT(User u, string[] parts)
    {
        // VB6 HandleTeleportCreate 1:1
        if (parts.Length < 4 || !short.TryParse(parts[1], out short destMap) ||
            !byte.TryParse(parts[2], out byte destX) || !byte.TryParse(parts[3], out byte destY))
            return false; // VB6 Exit Sub silencioso si payload inválido

        var srcMap = MapLoader.Get(u.Pos.Map);
        if (srcMap == null) return false;

        int tx = u.Pos.X;
        int ty = u.Pos.Y - 1; // VB6: .Pos.Y - 1
        if (tx < 1 || tx > 100 || ty < 1 || ty > 100) return false;

        // VB6: Exit Sub silencioso si ya hay objeto o exit
        if (srcMap.FloorObj[tx, ty] != 0) return false;
        if (srcMap.Exits[tx, ty].HasValue && srcMap.Exits[tx, ty].Value.DestMap > 0) return false;

        // VB6: MakeObj → pone objeto + broadcast ObjectCreate al área
        srcMap.FloorObj[tx, ty] = 378;
        srcMap.FloorAmount[tx, ty] = 1;
        srcMap.Exits[tx, ty] = new TileExit { DestMap = destMap, DestX = destX, DestY = destY };

        // VB6: SendToAreaByPos → broadcast a todos en el mapa
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.ObjectCreate(o.Conn, (byte)tx, (byte)ty, 378, 1);
        }
        Console.WriteLine($"[CT] Teleport creado en ({tx},{ty}) → Mapa {destMap} ({destX},{destY})");
        return true;
    }

    private static bool Cmd_DT(User u, string[] parts)
    {
        // VB6 HandleTeleportDestroy: usa flags.TargetMap/X/Y (último tile clickeado)
        // TODO: implementar cursor tracking. Por ahora usa posición actual del GM.
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return Send(u, "Mapa no encontrado.");

        int x = u.Pos.X, y = u.Pos.Y;
        var od = ObjData.Get(map.FloorObj[x, y]);
        if (od.Type != ObjType.Teleport || !map.Exits[x, y].HasValue)
            return Send(u, "No hay teleport en esta posición.");

        // Borrar objeto y exit
        map.FloorObj[x, y] = 0; map.FloorAmount[x, y] = 0;
        map.Exits[x, y] = null;

        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.ObjectDelete(o.Conn, (byte)x, (byte)y);
        }
        return Send(u, "✓ Teleport eliminado.");
    }
    private static bool Cmd_Lluvia(string[] parts)
    {
        byte tipo = parts.Length > 1 && byte.TryParse(parts[1], out var t) ? t : (byte)0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null)
                ServerPackets.RainToggle(o.Conn, tipo);
        }
        return true;
    }

    private static bool Cmd_Ejecutar(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /ejecutar <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            { Combat.UserDie(i); return Send(u, $"✓ {t.Name} ejecutado."); }
        }
        return Send(u, $"Usuario '{nombre}' no encontrado.");
    }

    private static bool Cmd_CharInfo(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /info <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                return Send(u, $"[{t.Name}] Niv:{t.Stats.ELV} HP:{t.Stats.MinHP}/{t.Stats.MaxHP} Mana:{t.Stats.MinMAN}/{t.Stats.MaxMAN} Oro:{t.Stats.GLD} Exp:{t.Stats.Exp} Mapa:{t.Pos.Map}({t.Pos.X},{t.Pos.Y})");
            }
        }
        // Offline: leer del .chr
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (!System.IO.File.Exists(chrPath)) return Send(u, $"'{nombre}' no encontrado.");
        var ini = new IniFile(chrPath);
        return Send(u, $"[{nombre}] (offline) Niv:{ini.GetInt("STATS","ELV")} Oro:{ini.GetInt("STATS","GLD")} Exp:{ini.GetInt("STATS","EXP")}");
    }

    private static bool Cmd_CharInv(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /inv <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder($"Inv [{t.Name}]: ");
                for (int s = 1; s <= 20; s++)
                {
                    var item = t.Invent.Object[s];
                    if (item.ObjIndex > 0) sb.Append($"#{s}:{ObjData.Get(item.ObjIndex).Name}x{item.Amount} ");
                }
                return Send(u, sb.ToString());
            }
        }
        return Send(u, $"'{nombre}' no está online.");
    }

    private static bool Cmd_CharBank(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /bov <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder($"Banco [{t.Name}] oro:{t.Stats.Banco}: ");
                for (int s = 1; s <= 30; s++)
                {
                    var item = t.BancoInvent.Object[s];
                    if (item.ObjIndex > 0) sb.Append($"#{s}:{ObjData.Get(item.ObjIndex).Name}x{item.Amount} ");
                }
                return Send(u, sb.ToString());
            }
        }
        return Send(u, $"'{nombre}' no está online.");
    }

    private static bool Cmd_CharSkills(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /skills <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder($"Skills [{t.Name}]: ");
                for (int s = 1; s < t.Stats.UserSkills.Length; s++)
                    if (t.Stats.UserSkills[s] > 0) sb.Append($"SK{s}:{t.Stats.UserSkills[s]} ");
                return Send(u, sb.ToString());
            }
        }
        return Send(u, $"'{nombre}' no está online.");
    }

    /// <summary>Busca un usuario online por nombre (case-insensitive). Null si no está.</summary>
    private static User BuscarOnline(string nombre)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    // /resetskills <usuario> — GM (≥SemiDios): pone TODOS los skills del personaje en 0 y guarda el .chr.
    private static bool Cmd_ResetSkills(User u, string[] parts)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) return Send(u, "No tienes privilegios para usar este comando.");
        if (parts.Length < 2) return Send(u, "Uso: /resetskills <usuario>");
        string nombre = ArgResto(parts);
        var t = BuscarOnline(nombre);
        if (t == null) return Send(u, $"'{nombre}' no está online.");
        for (int s = 1; s < t.Stats.UserSkills.Length; s++) t.Stats.UserSkills[s] = 0;
        CharSaver.SaveUser(t);
        if (t.Conn != null) ServerPackets.ConsoleMsg(t.Conn, "Un Game Master ha reseteado todas tus habilidades a 0.", 28);
        Console.WriteLine($"[GM] {u.Name} RESETSKILLS a {t.Name}");
        return Send(u, $"Todas las skills de {t.Name} fueron reseteadas a 0.");
    }

    // /setskills <usuario> <valor 0-100> — GM (≥SemiDios): pone TODOS los skills del personaje en <valor>.
    private static bool Cmd_SetSkills(User u, string[] parts)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) return Send(u, "No tienes privilegios para usar este comando.");
        // El valor va al final; todo lo demás es el nombre (puede tener espacios)
        if (parts.Length < 3 || !int.TryParse(parts[^1], out int valor) || valor < 0 || valor > Skills.MAXSKILLPOINTS)
            return Send(u, $"Uso: /setskills <usuario> <valor 0-{Skills.MAXSKILLPOINTS}>");
        string nombre = ArgNombre(parts, 1, 1);
        var t = BuscarOnline(nombre);
        if (t == null) return Send(u, $"'{nombre}' no está online.");
        for (int s = 1; s < t.Stats.UserSkills.Length; s++) t.Stats.UserSkills[s] = (byte)valor;
        CharSaver.SaveUser(t);
        if (t.Conn != null) ServerPackets.ConsoleMsg(t.Conn, $"Un Game Master ha puesto todas tus habilidades en {valor}.", 28);
        Console.WriteLine($"[GM] {u.Name} SETSKILLS {valor} a {t.Name}");
        return Send(u, $"Todas las skills de {t.Name} fueron puestas en {valor}.");
    }

    // /setskill <usuario> <skill> <valor 0-100> — GM (≥SemiDios): pone UN skill puntual en <valor>.
    // <skill> puede ser el número (1..27) o el nombre (ej: equitacion, navegacion, magia).
    private static bool Cmd_SetSkill(User u, string[] parts)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) return Send(u, "No tienes privilegios para usar este comando.");
        // Formato: <usuario...> <skill> <valor>. El valor y el skill son los dos últimos tokens.
        if (parts.Length < 4 || !int.TryParse(parts[^1], out int valor) || valor < 0 || valor > Skills.MAXSKILLPOINTS)
            return Send(u, $"Uso: /setskill <usuario> <skill nº o nombre> <valor 0-{Skills.MAXSKILLPOINTS}>. Skills: {Skills.ListaSkills()}");
        int skill = Skills.ResolverSkill(parts[^2]);
        if (skill == 0) return Send(u, $"Skill '{parts[^2]}' no reconocido. Skills: {Skills.ListaSkills()}");
        string nombre = ArgNombre(parts, 1, 2);   // todo menos los dos últimos tokens (skill + valor)
        var t = BuscarOnline(nombre);
        if (t == null) return Send(u, $"'{nombre}' no está online.");
        t.Stats.UserSkills[skill] = (byte)valor;
        CharSaver.SaveUser(t);
        if (t.Conn != null)
        {
            ServerPackets.ConsoleMsg(t.Conn, $"Un Game Master ha puesto tu habilidad {Skills.NombreDe(skill)} en {valor}.", 28);
            ServerPackets.UpdateUserStats(t.Conn, t);
        }
        Console.WriteLine($"[GM] {u.Name} SETSKILL {Skills.NombreDe(skill)}={valor} a {t.Name}");
        return Send(u, $"Skill {Skills.NombreDe(skill)} de {t.Name} puesto en {valor}.");
    }

    private static bool Cmd_Apagar() { Environment.Exit(0); return true; }
    private static bool Cmd_Habilitar()
    {
        GameState.ServerSoloGMs = !GameState.ServerSoloGMs;
        Console.WriteLine($"[GM] Servidor {(GameState.ServerSoloGMs ? "CERRADO a usuarios" : "ABIERTO a usuarios")}");
        return true;
    }
    private static bool Cmd_Grabar()
    {
        int n = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged) { CharSaver.SaveUser(t); n++; }
        }
        Console.WriteLine($"[GM] /grabar: {n} personajes guardados.");
        return true;
    }
    private static bool Cmd_ReloadObj()  { ObjData.Reload();   Console.WriteLine("[GM] Objetos recargados.");  return true; }
    private static bool Cmd_ReloadSpells() { SpellData.Reload(); Console.WriteLine("[GM] Hechizos recargados."); return true; }
    private static bool Cmd_ReloadIni()  { Console.WriteLine("[GM] /reloadsini: recarga de Server.ini no implementada aún."); return true; }
    private static bool Cmd_SetIniVar(string[] parts) { Console.WriteLine("[GM] /setinivar (no implementado)"); return true; }

    private static bool Cmd_ShowName(User u)
    {
        u.showName = !u.showName;
        return Send(u, u.showName ? "Nombre visible." : "Nombre oculto.");
    }
    private static bool Cmd_PanelGM(User u)
    {
        // VB6 HandleGMPanel: envía AbrirFormularios(8) al cliente para abrir el panel GM
        // Luego el cliente pide /listusu → servidor envía WriteUserNameList
        ServerPackets.AbrirFormularios(u.Conn, 8);
        return true;
    }
    private static bool Cmd_Show(User u, string[] parts) { return Send(u, "Show SOS: (no implementado)"); }
    private static bool Cmd_SOSDone(User u) { return Send(u, "SOS resuelto: (no implementado)"); }
    private static bool Cmd_Revivir(User u, string[] parts)
    {
        // Sin argumento → revive al GM mismo
        if (parts.Length < 2) { Combat.Resucitar(u.id); return Send(u, "¡Resucitado!"); }
        // Con argumento → revive a otro jugador
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            { Combat.Resucitar(i); return Send(u, $"✓ {t.Name} resucitado."); }
        }
        return Send(u, $"Usuario '{nombre}' no encontrado.");
    }
    private static bool Cmd_KickAll()
    {
        // VB6 EcharPjsNoPrivilegiados: solo echa a Users (Clase < 20)
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && t.Clase < 20)
                t.Conn?.Close();
        }
        return true;
    }
    private static bool Cmd_RajarClan(User u, string[] parts)
    {
        // VB6: quita al usuario de su clan y escribe en .chr GuildIndex=0
        if (parts.Length < 2) return Send(u, "Uso: /rajarclan <usuario>");
        string nombre = ArgResto(parts);
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (System.IO.File.Exists(chrPath))
        { var d = new IniDocument(chrPath); d.Set("GUILD", "GUILDINDEX", "0"); d.Save(chrPath); }
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            { t.GuildIndex = 0; break; }
        }
        return Send(u, $"✓ {nombre} removido de su clan.");
    }

    // /banclan <clan> (HandleGuildBan): banea a TODOS los miembros del clan.
    private static bool Cmd_BanClan(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /banclan <clan>");
        string guildName = ArgResto(parts).Replace("\\", "").Replace("/", "");
        var g = GuildManager.GetByName(guildName);
        if (g == null) return Send(u, "No existe el clan: " + guildName);

        // Aviso global y log.
        BroadcastConsole($"{u.Name} baneó al clan {guildName.ToUpperInvariant()}", 6);
        Console.WriteLine($"[GM] {u.Name} BANCLAN a {guildName.ToUpperInvariant()}");

        foreach (var member in g.Members.ToArray())
        {
            string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), member + ".chr");
            if (System.IO.File.Exists(chrPath))
            {
                var d = new IniDocument(chrPath);
                d.Set("CUENTA", "Banned", "1");
                int cant = new IniFile(chrPath).GetInt("PENAS", "Cant") + 1;
                d.Set("PENAS", "Cant", cant.ToString());
                d.Set("PENAS", $"P{cant}", $"{u.Name.ToLowerInvariant()}: BAN AL CLAN: {guildName} {DateTime.Now:dd/MM/yyyy HH:mm}");
                d.Save(chrPath);
            }
            // Desconectar si está online.
            for (int i = 1; i <= UserListManager.LastUser; i++)
            {
                var t = UserListManager.UserList[i];
                if (t != null && t.flags.UserLogged && string.Equals(t.Name, member, StringComparison.OrdinalIgnoreCase))
                { t.Conn?.Close(); break; }
            }
            BroadcastConsole($"   {member}<{guildName}> ha sido expulsado del servidor.", 6);
        }
        return true;
    }

    // /miembrosclan <clan> (HandleGuildMemberList): lista todos los miembros del clan a la consola del GM.
    private static bool Cmd_MiembrosClan(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /miembrosclan <clan>");
        string guildName = ArgResto(parts).Replace("\\", "").Replace("/", "");
        var g = GuildManager.GetByName(guildName);
        if (g == null) return Send(u, "No existe el clan: " + guildName);
        foreach (var member in g.Members)
            Send(u, $"{member}<{guildName}>");
        return true;
    }

    // /onclan <clan> (HandleGuildOnlineMembers): lista los miembros del clan que están online.
    private static bool Cmd_OnClan(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /onclan <clan>");
        string guildName = ArgResto(parts).Replace("+", " ");
        var g = GuildManager.GetByName(guildName);
        if (g == null) return Send(u, "No existe el clan: " + guildName);
        var online = new List<string>();
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && t.GuildIndex == g.Number) online.Add(t.Name);
        }
        return Send(u, $"Clan {guildName.ToUpperInvariant()}: {string.Join(",", online)}");
    }

    private static void BroadcastConsole(string msg, byte fuente)
    {
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t?.flags.UserLogged == true && t.Conn != null)
                ServerPackets.ConsoleMsg(t.Conn, msg, fuente);
        }
    }

    private static bool Cmd_Nick2IP(User u, string[] parts)
    {
        // VB6 HandleNickToIP: muestra IP actual del jugador online
        if (parts.Length < 2) return Send(u, "Uso: /nick2ip <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return Send(u, $"{nombre} → IP: {t.Conn?.RemoteEndPoint}");
        }
        return Send(u, $"'{nombre}' no está online.");
    }

    private static bool Cmd_IP2Nick(User u, string[] parts)
    {
        // VB6 HandleIPToNick: busca todos los usuarios con esa IP online
        if (parts.Length < 2) return Send(u, "Uso: /ip2nick <ip>");
        string ip = parts[1];
        var sb = new System.Text.StringBuilder($"Nicks en {ip}: ");
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && t.Conn?.RemoteEndPoint?.ToString().StartsWith(ip) == true)
                sb.Append(t.Name + " ");
        }
        return Send(u, sb.ToString());
    }

    private static bool Cmd_LastIP(User u, string[] parts)
    {
        // VB6 HandleLastIP: muestra última IP del usuario (online o desde log)
        if (parts.Length < 2) return Send(u, "Uso: /lastip <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return Send(u, $"Última IP de {nombre}: {t.Conn?.RemoteEndPoint}");
        }
        // TODO: leer del archivo de log de IPs cuando se implemente
        return Send(u, $"'{nombre}' no está online. Log de IPs: TODO");
    }

    // /noestupido <usuario> (HandleMakeDumbNoMore): le saca el efecto de estupidez/ceguera al objetivo
    // online y le manda el packet DumbNoMore (73) para que el cliente restaure la pantalla.
    private static bool Cmd_NoEstupido(User u, string[] parts)
    {
        if (parts.Length < 2) return Send(u, "Uso: /noestupido <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                t.flags.Estupido = 0;
                t.flags.EstupidezExpira = 0;
                t.flags.CegueraExpira = 0;
                if (t.Conn != null) ServerPackets.DumbNoMore(t.Conn);
                return Send(u, $"✓ {nombre} ya no está estúpido/ciego.");
            }
        }
        return Send(u, "Usuario offline."); // WriteLocaleMsg(75)
    }

    /// <summary>/darfaccion (alias /faction) &lt;nombre&gt; &lt;facción&gt; — GM (≥SemiDios): asigna la
    /// facción de un jugador. Facción = el Status de [FACCIONES] (la clave que realmente lee el
    /// CharLoader): 1=Renegado, 2=Ciudadano, 3=Republicano, 4=Caos, 5=Armada, 6=Milicia.
    /// Si el jugador está online, actualiza su estado en memoria y refresca el color de nick al
    /// instante (sin reloguear); si está offline, escribe directo en el .chr.</summary>
    private static bool Cmd_DarFaccion(User u, string[] parts)
    {
        if (u.FaccionStatus < AdminLoader.STATUS_SEMIDIOS) return Send(u, "No tienes privilegios para usar este comando.");
        if (parts.Length < 3 || !byte.TryParse(parts[^1], out byte faccion) || faccion < 1 || faccion > 6)
            return Send(u, "Uso: /darfaccion <nombre> <facción>  (1=Renegado 2=Ciudadano 3=Republicano 4=Caos 5=Armada 6=Milicia)");
        string nombre = ArgNombre(parts, 1, 1);

        // Si está online: actualizar en memoria + refrescar nick a todos en el mapa (sin reloguear).
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                t.Faccion.Status = faccion;
                short ropa = Facciones.DarArmaduraFaccion(t); // arma rango 1 + armadura faccionaria
                Facciones.BroadcastCharStatus(t);
                if (t.Conn != null)
                {
                    ServerPackets.ConsoleMsg(t.Conn, $"Un administrador ha cambiado tu facción a {NombreFaccion(faccion)}.", 1);
                    if (ropa > 0) ServerPackets.ConsoleMsg(t.Conn, "Recibiste la armadura de tu facción.", 1);
                }
                Console.WriteLine($"[GM] {u.Name} /darfaccion: {t.Name} → {NombreFaccion(faccion)} ({faccion}){(ropa > 0 ? $" + armadura {ropa}" : "")}");
                return Send(u, $"Facción de {t.Name} cambiada a {NombreFaccion(faccion)}.{(ropa > 0 ? " Se le entregó la armadura faccionaria." : "")}");
            }
        }

        // Offline: escribir directo la clave correcta (Status) en el .chr.
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (!System.IO.File.Exists(chrPath)) return Send(u, $"Usuario '{nombre}' no encontrado.");
        var d = new IniDocument(chrPath); d.Set("FACCIONES", "Status", faccion.ToString()); d.Save(chrPath);
        Console.WriteLine($"[GM] {u.Name} /darfaccion (offline): {nombre} → {NombreFaccion(faccion)} ({faccion})");
        return Send(u, $"Facción de {nombre} (offline) cambiada a {NombreFaccion(faccion)}. Tomará efecto al reloguear.");
    }

    private static string NombreFaccion(byte f) => f switch
    {
        Facciones.RENEGADO => "Renegado", Facciones.CIUDADANO => "Ciudadano", Facciones.REPUBLICANO => "Republicano",
        Facciones.CAOS => "Caos", Facciones.ARMADA => "Armada", Facciones.MILICIA => "Milicia", _ => $"#{f}",
    };

    private static bool Cmd_DarPun(string[] parts)
    {
        // VB6: asigna puntos de facción al jugador
        if (parts.Length < 3 || !int.TryParse(parts[^1], out int pts)) { Console.WriteLine("[GM] /darpun: TODO - requiere sistema de facciones"); return true; }
        string nombre = ArgNombre(parts, 1, 1);
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        if (System.IO.File.Exists(chrPath))
        { var ini = new IniFile(chrPath); var d = new IniDocument(chrPath); d.Set("FACCIONES", "PuntosFacteado", (ini.GetInt("FACCIONES","PuntosFacteado")+pts).ToString()); d.Save(chrPath); }
        Console.WriteLine($"[GM] /darpun: {nombre} +{pts} puntos"); return true;
    }

    private static bool Cmd_Donador(User u, string[] parts)
    {
        // VB6 HandleDonador: toggle Donador flag del usuario online
        if (parts.Length < 2) return Send(u, "Uso: /donador <usuario>");
        string nombre = ArgResto(parts);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t != null && t.flags.UserLogged && string.Equals(t.Name, nombre, StringComparison.OrdinalIgnoreCase))
            {
                t.Char.Donador = t.Char.Donador == 0 ? (byte)1 : (byte)0;
                return Send(u, $"✓ {nombre} donador: {(t.Char.Donador == 1 ? "activado" : "desactivado")}");
            }
        }
        return Send(u, $"'{nombre}' no está online.");
    }

    private static bool Cmd_CuentaRegresiva(string[] parts)
    {
        // VB6 HandleCuentaRegresiva: broadcast de cuenta regresiva a todos
        if (parts.Length < 2 || !int.TryParse(parts[1], out int secs)) { Console.WriteLine("[GM] /cuentaregresiva: uso /cuentaregresiva <segundos>"); return true; }
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var t = UserListManager.UserList[i];
            if (t?.flags.UserLogged == true && t.Conn != null)
                ServerPackets.ConsoleMsg(t.Conn, $"Cuenta regresiva: {secs} segundos.", 4);
        }
        return true;
    }

    private static bool Cmd_EventoOro(string[] parts) { Console.WriteLine("[GM] /eventoro: TODO - requiere sistema de eventos de oro"); return true; }

    private static bool Cmd_AskTrigger(User u)
    {
        // VB6 HandleAskTrigger: lee trigger en tile actual y lo muestra al GM
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return Send(u, "Mapa no encontrado.");
        byte trig = map.Trigger[u.Pos.X, u.Pos.Y];
        return Send(u, $"Trigger {trig} en mapa {u.Pos.Map} ({u.Pos.X},{u.Pos.Y})");
    }

    private static bool Cmd_AlterPassword(User u, string[] parts)
    {
        // VB6 HandleAlterPassword: copia hash de contraseña de un .chr a otro
        if (parts.Length < 3) return Send(u, "Uso: /altpass <usuario> <copiar_de>");
        string nombre = parts[1], copiarDe = parts[2];
        string chrPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), nombre + ".chr");
        string srcPath = System.IO.Path.Combine(DataPaths.Sub("Charfile"), copiarDe + ".chr");
        if (!System.IO.File.Exists(chrPath)) return Send(u, $"'{nombre}' no existe.");
        if (!System.IO.File.Exists(srcPath)) return Send(u, $"'{copiarDe}' no existe.");
        var src = new IniFile(srcPath);
        string pass = src.Get("CUENTA", "Password");
        if (string.IsNullOrEmpty(pass)) return Send(u, "No se pudo leer la contraseña fuente.");
        var dst = new IniDocument(chrPath);
        dst.Set("CUENTA", "Password", pass);
        dst.Save(chrPath);
        return Send(u, $"✓ Contraseña de '{nombre}' copiada de '{copiarDe}'.");
    }

    private static bool Cmd_ShowGuildMessages(User u, string[] parts)
    {
        // VB6 HandleShowGuildMessages: muestra mensajes del clan (requiere sistema de clanes)
        if (parts.Length < 2) return Send(u, "Uso: /showcmsg <clan>");
        // TODO: requiere sistema de clanes completo
        return Send(u, $"Mensajes del clan '{parts[1]}': TODO - requiere sistema de clanes.");
    }

    // Lista SOS en memoria (nombre|mensaje)
    private static readonly List<string> _sosList = new();

    private static bool Cmd_SosList(User u)
    {
        // VB6 HandleSOSShowList: envía WriteShowSOSForm con lista SOS
        string lista = string.Join("|", _sosList);
        ServerPackets.ShowSOSForm(u.Conn, lista);
        return true;
    }

    private static bool Cmd_SosRemove(User u, string[] parts)
    {
        // VB6 HandleSOSRemove: elimina el SOS del usuario indicado
        if (parts.Length < 2) return Send(u, "Uso: /sosdone <usuario>");
        string nombre = ArgResto(parts);
        _sosList.RemoveAll(s => s.StartsWith(nombre + "|", StringComparison.OrdinalIgnoreCase));
        return Send(u, $"✓ SOS de '{nombre}' removido.");
    }

    private static bool Cmd_CleanSos(User u)
    {
        // VB6 HandleCleanSOS: vacía toda la lista de SOS
        _sosList.Clear();
        return Send(u, "✓ Lista SOS limpiada.");
    }

    /// <summary>Agrega un SOS a la lista (llamado cuando un jugador pide ayuda).</summary>
    public static void AddSOS(string nombre, string mensaje)
    {
        _sosList.RemoveAll(s => s.StartsWith(nombre + "|", StringComparison.OrdinalIgnoreCase));
        _sosList.Add($"{nombre}|{mensaje}");
    }

    // Implementar MapInfo sub-comandos (70-77) — se manejan desde PacketHandler directamente
    public static void HandleChangeMapInfo(int userIndex, int infoType, bool value)
    {
        var u = UserListManager.UserList[userIndex];
        if (u == null) return;
        var map = MapLoader.Get(u.Pos.Map);
        if (map == null) return;
        string campo;
        switch (infoType)
        {
            case 0: map.Info.Pk = value;          campo = "Pk"; break;
            case 1: map.Info.Backup = value;       campo = "backup"; break;
            case 2: map.Info.Restricted = value;   campo = "Restringido"; break;
            case 3: map.Info.NoMagia = value;      campo = "MagiaSinEfecto"; break;
            case 4: map.Info.NoInvi = value;       campo = "InviSinEfecto"; break;
            case 5: map.Info.NoResu = value;       campo = "ResuSinEfecto"; break;
            case 6: map.Info.Land = value;         campo = "Terreno"; break;
            default: return;
        }
        ServerPackets.ConsoleMsg(u.Conn, $"Mapa {u.Pos.Map} {campo}: {(value ? "1" : "0")}", 1);
    }

    // === HELPERS ===
    private static bool Send(User u, string msg)
    {
        if (u?.Conn != null) ServerPackets.ConsoleMsg(u.Conn, msg, 1);
        return true;
    }
}
