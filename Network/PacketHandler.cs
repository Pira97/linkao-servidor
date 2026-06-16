namespace ServidorCS.Network;

/// <summary>
/// Punto de entrada del parseo de packets entrantes. Equivale a
/// HandleIncomingData() de Protocol.bas (VB6).
///
/// Patrón 1:1 con el VB6:
///   1. Mientras haya datos, mirar (Peek) el id de packet.
///   2. Despachar al Handle correspondiente, que lee sus campos EN EL MISMO ORDEN.
///   3. Si falta data para completar el packet (NotEnoughData), restaurar
///      el buffer y esperar a que llegue más (no se descarta nada).
///
/// IMPORTANTE (x1): cada Handle DEBE consumir exactamente la misma cantidad de
/// bytes que el VB6. Si un handler lee de menos/de más, todo el stream posterior
/// se desincroniza y el cliente Godot recibe basura.
///
/// La LÓGICA de juego (mover, atacar, responder con stats…) necesita el modelo de
/// usuario (UserList) que todavía no está portado — candidato a traer con VBUC.
/// Por eso muchos handlers hoy hacen el parseo correcto + TODO de lógica.
/// </summary>
public static class PacketHandler
{
    /// <summary>Activá con true para ver TODOS los packets (conocidos y desconocidos) en consola.</summary>
    public static bool DebugPackets = false;

    public static void HandleIncomingData(Connection conn)
    {
        var incoming = conn.IncomingData;

        lock (incoming)
        {
            while (incoming.Length > 0)
            {
                // Snapshot para poder reintentar si el packet llegó partido,
                // y para diagnosticar packets desconocidos viendo los bytes crudos.
                byte[] backup = incoming.ToArray();
                byte packetId = incoming.PeekByte();

                try
                {
                    bool conocido = Dispatch(conn, (ClientPacketID)packetId);

                    if (conocido)
                    {
                        if (DebugPackets)
                        {
                            // Instrumentación: id, bytes disponibles antes, consumidos, restantes, usuario.
                            int consumidos = backup.Length - incoming.Length;
                            Console.WriteLine($"[PKT] id={packetId} ({NombrePacket(packetId)}) dispo={backup.Length} consumió={consumidos} resto={incoming.Length} user#{conn.UserIndex}");
                            if (consumidos <= 0)
                                Console.WriteLine($"   ⚠ UNDERREAD: el handler no consumió bytes (riesgo de loop/desync).");
                        }
                    }
                    else
                    {
                        // ---- PACKET DESCONOCIDO ----
                        // No conocemos su payload: consumir SOLO el id desalinea el resto del stream
                        // (fue la causa de bugs como el de las partículas). Política: log detallado
                        // (id, offset/bytes pendientes) + CIERRE CONTROLADO de la conexión.
                        int verBytes = Math.Min(backup.Length, 24);
                        string hex = BitConverter.ToString(backup, 0, verBytes).Replace("-", " ");
                        Console.WriteLine(
                            $"[PKT-DESCONOCIDO] id={packetId} ({NombrePacket(packetId)}) " +
                            $"user #{conn.UserIndex} xorKey={conn.IncomingXorKey} " +
                            $"bytesPendientes={backup.Length} → CIERRE CONTROLADO (evita corrupción del stream)");
                        Console.WriteLine($"   bytes crudos (post-XOR): {hex}");
                        incoming.Clear();
                        conn.Close();
                        break;
                    }
                }
                catch (NotEnoughDataException)
                {
                    // Faltan bytes: restaurar y esperar a que llegue el resto.
                    incoming.Clear();
                    incoming.AppendRaw(backup, backup.Length);
                    break;
                }
                catch (Exception ex)
                {
                    // Un handler lanzó otra cosa: loggear con contexto y descartar el id
                    // para no trabar el bucle. NO cerramos la conexión.
                    Console.WriteLine($"[PKT-ERROR] id={packetId} ({NombrePacket(packetId)}) user #{conn.UserIndex}: {ex.Message}");
                    incoming.Clear();
                    incoming.AppendRaw(backup, backup.Length);
                    if (incoming.Length > 0) incoming.ReadByte();
                    break;
                }
            }
        }
    }

    /// <summary>Devuelve el nombre del ClientPacketID, o "??" si el valor no está definido.</summary>
    private static string NombrePacket(byte id)
        => Enum.IsDefined(typeof(ClientPacketID), (ClientPacketID)id)
            ? ((ClientPacketID)id).ToString()
            : "?? sin nombre en enum";

    /// <summary>Devuelve false si el id no está mapeado todavía.</summary>
    private static bool Dispatch(Connection conn, ClientPacketID id)
    {
        switch (id)
        {
            case ClientPacketID.ConnectAccount:        HandleConnectAccount(conn);        return true;
            case ClientPacketID.CreateNewAccount:      HandleCreateNewAccount(conn);      return true;
            case ClientPacketID.ProcesosLogin:         HandleProcesosLogin(conn);         return true;
            case ClientPacketID.LoginExistingChar:     HandleLoginExistingChar(conn);     return true;
            case ClientPacketID.LoginNewChar:          HandleLoginNewChar(conn);          return true;
            case ClientPacketID.Walk:                  HandleWalk(conn);                  return true;
            case ClientPacketID.ChangeHeading:         HandleChangeHeading(conn);         return true;
            case ClientPacketID.Talk:                  HandleTalk(conn);                  return true;
            case ClientPacketID.RequestPositionUpdate: HandleRequestPositionUpdate(conn); return true;
            case ClientPacketID.RequestAtributes:      HandleRequestAtributes(conn);      return true;
            case ClientPacketID.RequestSkills:         HandleRequestSkills(conn);         return true;
            case ClientPacketID.RequestMiniStats:      HandleRequestMiniStats(conn);      return true;
            case ClientPacketID.Online:                HandleOnline(conn);                return true;
            case ClientPacketID.PickUp:                HandlePickUp(conn);                return true;
            case ClientPacketID.Drop:                  HandleDrop(conn);                  return true;
            case ClientPacketID.DropDestroy:           HandleDropDestroy(conn);           return true;
            case ClientPacketID.ModifySkills:          HandleModifySkills(conn);          return true;
            case ClientPacketID.EquipItem:             HandleEquipItem(conn);             return true;
            case ClientPacketID.UseItem:               HandleUseItem(conn);               return true;
            case ClientPacketID.attack:                HandleAttack(conn);                return true;
            case ClientPacketID.Resucitate:            HandleResucitate(conn);            return true;
            case ClientPacketID.CastSpell:             HandleCastSpell(conn);             return true;
            case ClientPacketID.MoveSpell:             HandleMoveSpell(conn);             return true;
            case ClientPacketID.SpellInfoRequest:     HandleSpellInfo(conn);             return true;
            case ClientPacketID.WorkLeftClick:         HandleWorkLeftClick(conn);         return true;
            case ClientPacketID.LeftClick:             HandleLeftClick(conn);             return true;
            case ClientPacketID.DoubleClick:           HandleDoubleClick(conn);           return true;
            case ClientPacketID.Meditate:              HandleMeditate(conn);              return true;
            case ClientPacketID.Quit:                  HandleQuit(conn);                  return true;
            // === Clanes (núcleo) ===
            case ClientPacketID.GuildFundate:          HandleGuildFundate(conn);          return true;
            case ClientPacketID.GuildFundation:        HandleGuildFundation(conn);        return true;
            case ClientPacketID.CloseGuild:            HandleCloseGuild(conn);            return true;
            case ClientPacketID.CreateNewGuild:        HandleCreateNewGuild(conn);        return true;
            case ClientPacketID.GuildLeave:            HandleGuildLeave(conn);            return true;
            case ClientPacketID.GuildRequestMembership: HandleGuildRequestMembership(conn); return true;
            case ClientPacketID.GuildAcceptNewMember:  HandleGuildAcceptNewMember(conn);  return true;
            case ClientPacketID.GuildRejectNewMember:  HandleGuildRejectNewMember(conn);  return true;
            case ClientPacketID.GuildKickMember:       HandleGuildKickMember(conn);       return true;
            case ClientPacketID.RequestGuildLeaderInfo: HandleRequestGuildLeaderInfo(conn); return true;
            case ClientPacketID.ShowGuildNews:         HandleShowGuildNews(conn);         return true;
            case ClientPacketID.GuildOnline:           HandleGuildOnline(conn);           return true;
            case ClientPacketID.GuildMemberInfo:       HandleGuildMemberInfo(conn);       return true;
            case ClientPacketID.GuildRequestDetails:   HandleGuildRequestDetails(conn);   return true;
            case ClientPacketID.GuildUpdateNews:       HandleGuildUpdateNews(conn);       return true;
            case ClientPacketID.GuildNewWebsite:       HandleGuildNewWebsite(conn);       return true;
            case ClientPacketID.ClanCodexUpdate:       HandleClanCodexUpdate(conn);       return true;
            case ClientPacketID.GuildRequestJoinerInfo: HandleGuildRequestJoinerInfo(conn); return true;
            case ClientPacketID.GuildOpenElections:    HandleGuildOpenElections(conn);    return true;
            case ClientPacketID.GuildVote:             HandleGuildVote(conn);             return true;
            case ClientPacketID.GuildDeclareWar:       HandleGuildDeclareWar(conn);       return true;
            case ClientPacketID.GuildOfferPeace:       HandleGuildOfferPeace(conn);       return true;
            case ClientPacketID.GuildOfferAlliance:    HandleGuildOfferAlliance(conn);    return true;
            case ClientPacketID.GuildAcceptPeace:      HandleGuildAcceptPeace(conn);      return true;
            case ClientPacketID.GuildRejectPeace:      HandleGuildRejectPeace(conn);      return true;
            case ClientPacketID.GuildAcceptAlliance:   HandleGuildAcceptAlliance(conn);   return true;
            case ClientPacketID.GuildRejectAlliance:   HandleGuildRejectAlliance(conn);   return true;
            case ClientPacketID.GuildPeacePropList:    HandleGuildPeacePropList(conn);    return true;
            case ClientPacketID.GuildAlliancePropList: HandleGuildAlliancePropList(conn); return true;
            case ClientPacketID.RegresarHogar:         HandleRegresarHogar(conn);         return true;
            case ClientPacketID.Rest:                  HandleRest(conn);                  return true;
            case ClientPacketID.Casamiento:            HandleCasamiento(conn);            return true;
            case ClientPacketID.divorciar:             HandleDivorciar(conn);             return true;
            case ClientPacketID.CraftBlacksmith:       HandleCraft(conn, Game.Crafting.CraftType.Blacksmith); return true;
            case ClientPacketID.CraftCarpenter:        HandleCraft(conn, Game.Crafting.CraftType.Carpenter);  return true;
            case ClientPacketID.CraftSastre:           HandleCraft(conn, Game.Crafting.CraftType.Sastre);     return true;
            case ClientPacketID.Craftalquimia:         HandleCraft(conn, Game.Crafting.CraftType.Alquimia);   return true;
            case ClientPacketID.CommerceStart:         HandleCommerceStart(conn);         return true;
            case ClientPacketID.CommerceEnd:           HandleCommerceEnd(conn);           return true;
            case ClientPacketID.CommerceBuy:           HandleCommerceBuy(conn);           return true;
            case ClientPacketID.CommerceSell:          HandleCommerceSell(conn);          return true;
            case ClientPacketID.BankStart:             HandleBankStart(conn);             return true;
            case ClientPacketID.BankEnd:               HandleBankEnd(conn);               return true;
            case ClientPacketID.BankDeposit:           HandleBankDeposit(conn);           return true;
            case ClientPacketID.BankExtractItem:       HandleBankExtractItem(conn);       return true;
            case ClientPacketID.BankDepositGold:       HandleBankDepositGold(conn);       return true;
            case ClientPacketID.BankExtractGold:       HandleBankExtractGold(conn);       return true;
            case ClientPacketID.PartyCreate:           HandlePartyCreate(conn);           return true;
            case ClientPacketID.PartyJoin:             HandlePartyJoin(conn);             return true;
            case ClientPacketID.PartyLeave:            HandlePartyLeave(conn);            return true;
            case ClientPacketID.PartyMessage:          HandlePartyMessage(conn);          return true;
            case ClientPacketID.PartyAccept:           HandlePartyAccept(conn);           return true;
            case ClientPacketID.PartyReject:           HandlePartyReject(conn);           return true;
            case ClientPacketID.PartyKick:             HandlePartyKick(conn);             return true;
            case ClientPacketID.PartyOnline:           HandlePartyOnline(conn);           return true;
            case ClientPacketID.GuildMessage:          HandleGuildMessage(conn);          return true;
            case ClientPacketID.Work:                  HandleWork(conn);                  return true;
            case ClientPacketID.UserCommerceStart:     HandleUserCommerceStart(conn);     return true;
            case ClientPacketID.UserCommerceOfferGold: HandleUserCommerceOfferGold(conn); return true;
            case ClientPacketID.UserCommerceOfferItem: HandleUserCommerceOfferItem(conn); return true;
            case ClientPacketID.UserCommerceConfirm:   HandleUserCommerceConfirm(conn);   return true;
            case ClientPacketID.UserCommerceCancel:    HandleUserCommerceCancel(conn);    return true;
            case ClientPacketID.UserCommerceReqUpdate: HandleUserCommerceReqUpdate(conn); return true;
            case ClientPacketID.Packets_Correo:        HandlePacketsCorreo(conn);         return true;
            case ClientPacketID.EnviarCorreo:          HandleEnviarCorreo(conn);          return true;
            case ClientPacketID.Ping:                  HandlePing(conn);                  return true;

            // Algunos con lógica/handler propio:
            case ClientPacketID.SwapObjects:           HandleSwapObjects(conn);           return true;
            case ClientPacketID.MoveBank:              HandleMoveBank(conn);              return true;
            case ClientPacketID.Train:                 HandleTrain(conn);                 return true;
            case ClientPacketID.AddAmigos:             HandleAddAmigos(conn);             return true;
            case ClientPacketID.DelAmigos:             HandleDelAmigos(conn);             return true;
            case ClientPacketID.MsgAmigos:             HandleMsgAmigos(conn);             return true;
            case ClientPacketID.OnAmigos:              HandleOnAmigos(conn);              return true;
            case ClientPacketID.RequestStats:          HandleRequestStats(conn);          return true;
            case ClientPacketID.UpTime:                HandleUpTime(conn);                return true;
            case ClientPacketID.ArenaJoin:             HandleArenaJoin(conn);             return true;
            case ClientPacketID.AuctionCreate:         HandleAuctionCreate(conn);         return true;
            case ClientPacketID.AuctionBid:            HandleAuctionBid(conn);            return true;
            case ClientPacketID.CentinelReport:        HandleCentinelReport(conn);        return true;
            case ClientPacketID.RequestShopData:       HandleRequestShopData(conn);       return true;
            case ClientPacketID.ShopBuyItem:           HandleShopBuyItem(conn);           return true;
            case ClientPacketID.ResuscitationSafeToggle: HandleResuscitationSafeToggle(conn);return true;
            case ClientPacketID.SeleccionarHogar:      HandleSeleccionarHogar(conn);      return true;
            case ClientPacketID.Enlist:                HandleEnlist(conn);                return true;
            case ClientPacketID.RetirarFaccion:        HandleRetirarFaccion(conn);        return true;
            case ClientPacketID.ChangeDescription:     HandleChangeDescription(conn);     return true;
            case ClientPacketID.Typing:                HandleTyping(conn);                return true;
            case ClientPacketID.SaveMacrosConfig:      HandleSaveMacros(conn);            return true;
            case ClientPacketID.RequestMacrosConfig:   HandleRequestMacros(conn);         return true;
            case ClientPacketID.Whisper:               HandleWhisper(conn);               return true;

            // --- Sistema de reportes / tickets (NUEVO, no VB6) ---
            case ClientPacketID.ReportCreate:          HandleReportCreate(conn);          return true;
            case ClientPacketID.ReportListRequest:     HandleReportListRequest(conn);     return true;
            case ClientPacketID.ReportDetailRequest:   HandleReportDetailRequest(conn);   return true;
            case ClientPacketID.ReportAction:          HandleReportAction(conn);          return true;

            // --- Editor de objetos en vivo para GMs (NUEVO, no VB6) ---
            case ClientPacketID.ObjEditorRequest:       HandleObjEditorRequest(conn);       return true;
            case ClientPacketID.ObjEditorDetailRequest: HandleObjEditorDetailRequest(conn); return true;
            case ClientPacketID.ObjEditorSave:          HandleObjEditorSave(conn);          return true;
            case ClientPacketID.ObjEditorReloadAll:     HandleObjEditorReloadAll(conn);     return true;
            case ClientPacketID.NpcCatalogRequest:      HandleNpcCatalogRequest(conn);      return true;
            case ClientPacketID.TorneoAction:           HandleTorneoAction(conn);           return true;
            case ClientPacketID.QueryMapNpcs:           HandleQueryMapNpcs(conn);           return true;

            // --- Resto de packets conocidos: consumir su payload exacto vía tabla PayloadSpec.
            //     Mantiene el stream alineado aunque la lógica de juego aún no esté portada.
            default:
                return ConsumePorTabla(conn, id);
        }
    }

    /// <summary>
    /// Tabla de firmas de payload (lo que viene DESPUÉS del id) de cada ClientPacketID,
    /// extraída de los write_* del cliente (protocol_outgoing.gd). Letras:
    /// B=byte, I=integer(2), L=long(4), S=ascii string, U=unicode string, K=block_prefixed.
    /// Permite consumir cualquier packet conocido sin desalinear el stream.
    /// </summary>
    /// NOTA (hardening 2026-06-07): esta tabla SOLO debe contener packets SIN handler propio.
    /// Las ~29 entradas que antes duplicaban un `case` del dispatch eran inalcanzables (el switch
    /// resuelve antes que el default→ConsumePorTabla) y se eliminaron. Lo que queda son los packets
    /// que el cliente Godot envía pero cuya lógica NO está portada (gaps de gameplay, NO desync) o
    /// que NO son portables 1:1 porque el cliente manda un payload distinto al VB6.
    private static readonly Dictionary<ClientPacketID, string> PayloadSpec = new()
    {
        // No portables 1:1 (el cliente diverge del VB6) — alineados para no desincronizar:
        { ClientPacketID.TransferGOLD, "L" },        // cliente manda solo amount (sin destino)
        { ClientPacketID.InitCrafting, "B" },        // cliente manda craft_type (VB6 espera TotalItems/PorCiclo)
        { ClientPacketID.CombatModeToggle, "" },     // toggle visual del cliente (sin handler VB6)
        { ClientPacketID.QueryMapNpcs, "I" },        // consulta de debug
        { ClientPacketID.HayEventos, "" },
        // Sistemas secundarios sin lógica portada (gaps de gameplay):
        { ClientPacketID.Gamble, "S" },              // timbero
        { ClientPacketID.Denounce, "S" },            // denuncias
        { ClientPacketID.AbrirForms, "B" },
        { ClientPacketID.DesconectarCuenta, "S" },
        { ClientPacketID.ParticulaUsuario, "SI" },   // /panelgm partículas
        { ClientPacketID.Information, "" },           // diálogo de NPC de facción
        // Premios:
        { ClientPacketID.Reward, "" }, { ClientPacketID.PidePremios, "" }, { ClientPacketID.RPremios, "I" },
        // Skins (apariencia comprable):
        { ClientPacketID.EquiparSkin, "BB" }, { ClientPacketID.ComprarSkin, "B" }, { ClientPacketID.GuardarSkinPermanente, "BB" },
        // Clan: paneles de detalle de propuestas (display) sin lógica:
        { ClientPacketID.GuildAllianceDetails, "S" }, { ClientPacketID.GuildPeaceDetails, "S" },
    };

    /// <summary>Consume el payload de un packet conocido según PayloadSpec. GMCommands aparte.</summary>
    private static bool ConsumePorTabla(Connection conn, ClientPacketID id)
    {
        if (id == ClientPacketID.GMCommands) { ConsumeGMCommand(conn); return true; }
        if (!PayloadSpec.TryGetValue(id, out var spec)) return false; // realmente desconocido
        ConsumeSpec(conn, spec);
        return true;
    }

    /// <summary>Lee el id + los campos descritos por 'spec' (ej "SB", "II"). Mantiene alineado el stream.</summary>
    private static void ConsumeSpec(Connection conn, string spec)
    {
        var b = conn.IncomingData;
        // Verificar que haya al menos 1 byte para el id (los strings validan su propio largo).
        b.ReadByte(); // id
        foreach (char c in spec)
        {
            switch (c)
            {
                case 'B': b.ReadByte(); break;
                case 'I': b.ReadInteger(); break;
                case 'L': b.ReadLong(); break;
                case 'S': case 'K': b.ReadASCIIString(); break;
                case 'U': b.ReadUnicodeString(); break;
            }
        }
    }

    /// <summary>
    /// GMCommands (id 94): Byte(id) + Byte(subcmd) + payload variable según subcmd.
    /// Procesa comandos GM enviados por el cliente Godot.
    /// </summary>
    private static void ConsumeGMCommand(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        byte[] snap = b.ToArray();
        try
        {
            b.ReadByte();               // id (94)
            byte sub = b.ReadByte();    // subcomando

            var u = Game.UserListManager.UserList[conn.UserIndex];
            if (!u.flags.UserLogged) return;

            Console.WriteLine($"[GMCMD] Subcomando {sub} recibido de {u.Name}");

            // IDs exactos del enum eGMCommands del cliente (secuencial desde 1)
            // Payloads tomados de protocol_outgoing.gd
            string cmd = sub switch
            {
                1  => $"/gmsg {b.ReadASCIIString()}",               // GM_MESSAGE: S
                2  => GM_NoPayload(b, "/showname"),                  // SHOW_NAME: -
                3  => $"/ircerca {Q(b.ReadASCIIString())}",          // GO_NEARBY: S
                5  => GM_NoPayload(b, "/time"),                      // SERVER_TIME: -
                6  => $"/donde {Q(b.ReadASCIIString())}",            // WHERE: S
                7  => $"/criaturas {b.ReadInteger()}",               // CREATURES_IN_MAP: I
                8  => GM_WarpChar(b),                                // WARP_CHAR: SIBB
                9  => GM_NoPayload(b, "/soslist"),                   // SOS_SHOW_LIST: -
                10 => $"/sosremove {Q(b.ReadASCIIString())}",        // SOS_REMOVE: S
                11 => $"/ircerca {Q(b.ReadASCIIString())}",          // GO_TO_CHAR: S
                12 => GM_NoPayload(b, "/invisible"),                  // INVISIBLE: -
                13 => GM_NoPayload(b, "/panelgm"),                   // GM_PANEL: -
                14 => GM_NoPayload(b, "/listusu"),                   // REQUEST_USER_LIST: -
                15 => GM_NoPayload(b, "/trabajando"),                // WORKING: -
                16 => GM_NoPayload(b, "/ocultando"),                 // HIDING: -
                17 => GM_Jail(b),                                    // JAIL: SSB
                18 => GM_NoPayload(b, "/rmata"),                      // KILL_NPC: -
                19 => GM_WarnUser(b),                                // WARN_USER: SS
                20 => GM_EditChar(b),                                // EDIT_CHAR: SBSS
                21 => $"/charinfo {Q(b.ReadASCIIString())}",         // REQUEST_CHAR_INFO: S
                22 => $"/charinv {Q(b.ReadASCIIString())}",          // REQUEST_CHAR_INVENTORY: S
                23 => $"/charbank {Q(b.ReadASCIIString())}",         // REQUEST_CHAR_BANK: S
                24 => $"/charskills {Q(b.ReadASCIIString())}",       // REQUEST_CHAR_SKILLS: S
                25 => $"/revivir {Q(b.ReadASCIIString())}",          // REVIVE_CHAR: S
                26 => GM_NoPayload(b, "/onlinegm"),                   // ONLINE_GM: -
                27 => $"/onlinemap {b.ReadInteger()}",               // ONLINE_MAP: I
                28 => $"/echar {Q(b.ReadASCIIString())}",            // KICK: S
                29 => $"/ejecutar {Q(b.ReadASCIIString())}",         // EXECUTE: S
                30 => GM_BanChar(b),                                 // BAN_CHAR: SB
                31 => GM_NoPayload(b, "/seguir"),                    // NPC_FOLLOW: -
                32 => $"/sum {Q(b.ReadASCIIString())}",              // SUMMON_CHAR: S
                33 => GM_NoPayload(b, "/resetinv"),                  // RESET_NPC_INVENTORY: -
                34 => GM_NoPayload(b, "/limpiar"),                   // CLEAN_WORLD: -
                35 => $"/rmsg {b.ReadASCIIString()}",                 // SERVER_MESSAGE: S
                36 => $"/nick2ip {Q(b.ReadASCIIString())}",          // NICK_TO_IP: S
                37 => $"/ip2nick {b.ReadASCIIString()}",             // IP_TO_NICK: S
                38 => $"/onclan {Q(b.ReadASCIIString())}",           // GUILD_ONLINE_MEMBERS: S
                39 => GM_TeleportCreate(b),                          // TELEPORT_CREATE: IBB
                40 => GM_NoPayload(b, "/dt"),                        // TELEPORT_DESTROY: -
                41 => $"/lluvia {b.ReadByte()}",                     // RAIN_TOGGLE: B
                42 => $"/talkas {b.ReadASCIIString()}",               // TALK_AS_NPC: S
                43 => GM_NoPayload(b, "/massdest"),                  // DESTROY_ALL_ITEMS_IN_AREA: -
                44 => $"/noestupido {Q(b.ReadASCIIString())}",       // MAKE_DUMB_NO_MORE: S
                45 => $"/trigger {b.ReadInteger()}",                 // SET_TRIGGER: I
                46 => GM_NoPayload(b, "/asktrigger"),                // ASK_TRIGGER: -
                47 => GM_NoPayload(b, "/baniplist"),                 // BANNED_IP_LIST: -
                48 => GM_NoPayload(b, "/banipreload"),               // BANNED_IP_RELOAD: -
                49 => $"/miembrosclan {Q(b.ReadASCIIString())}",     // GUILD_MEMBER_LIST: S
                50 => $"/banclan {Q(b.ReadASCIIString())}",          // GUILD_BAN: S
                51 => GM_BanIP(b),                                   // BAN_IP: SS
                52 => $"/unbanip {b.ReadASCIIString()}",             // UNBAN_IP: S
                53 => $"/ci {b.ReadInteger()} {b.ReadInteger()}",    // CREATE_ITEM: II
                54 => GM_NoPayload(b, "/dest"),                      // DESTROY_ITEMS: -
                55 => GM_NoPayload(b, "/bloq"),                      // TILE_BLOCKED_TOGGLE: -
                56 => GM_NoPayload(b, "/mata"),                       // KILL_NPC_NO_RESPAWN: -
                57 => GM_NoPayload(b, "/masskill"),                  // KILL_ALL_NEARBY_NPCS: -
                58 => $"/lastip {Q(b.ReadASCIIString())}",           // LAST_IP: S
                59 => $"/smsg {b.ReadASCIIString()}",                  // SYSTEM_MESSAGE: S
                60 => $"/acc {b.ReadInteger()}",                     // CREATE_NPC: I
                61 => $"/racc {b.ReadInteger()}",                    // CREATE_NPC_WITH_RESPAWN: I
                62 => GM_NoPayload(b, "/nave"),                       // NAVIGATE_TOGGLE: -
                63 => GM_NoPayload(b, "/habilitar"),                 // SERVER_OPEN_TO_USERS_TOGGLE: -
                64 => GM_NoPayload(b, ""),                           // TURN_OFF_SERVER: - DESHABILITADO
                65 => $"/rajarclan {Q(b.ReadASCIIString())}",        // REMOVE_CHAR_FROM_GUILD: S
                66 => GM_AlterPassword(b),                           // ALTER_PASSWORD: SS
                67 => GM_NoPayload(b, "/centinelaactivado"),          // TOGGLE_CENTINEL_ACTIVATED: -
                68 => $"/msgclan {Q(b.ReadASCIIString())}",          // SHOW_GUILD_MESSAGES: S
                69 => GM_NoPayload(b, "/guardamapa"),                // SAVE_MAP: -
                78 => GM_NoPayload(b, "/grabar"),                    // SAVE_CHARS: -
                79 => GM_NoPayload(b, "/cleansos"),                  // CLEAN_SOS: -
                80 => GM_NoPayload(b, "/echartodospjs"),             // KICK_ALL_CHARS: -
                81 => GM_NoPayload(b, "/reloadnpcs"),                // RELOAD_NPCS: -
                82 => GM_NoPayload(b, "/reloadsini"),                // RELOAD_SERVER_INI: -
                83 => GM_NoPayload(b, "/reloadhechizos"),            // RELOAD_SPELLS: -
                84 => GM_NoPayload(b, "/reloadobj"),                 // RELOAD_OBJECTS: -
                70 => GM_MapInfoDirect(b, conn.UserIndex, 0),        // CHANGE_MAP_INFO_PK
                71 => GM_MapInfoDirect(b, conn.UserIndex, 1),        // CHANGE_MAP_INFO_BACKUP
                72 => GM_MapInfoDirect(b, conn.UserIndex, 2),        // CHANGE_MAP_INFO_RESTRICTED
                73 => GM_MapInfoDirect(b, conn.UserIndex, 3),        // CHANGE_MAP_INFO_NO_MAGIC
                74 => GM_MapInfoDirect(b, conn.UserIndex, 4),        // CHANGE_MAP_INFO_NO_INVI
                75 => GM_MapInfoDirect(b, conn.UserIndex, 5),        // CHANGE_MAP_INFO_NO_RESU
                76 => GM_MapInfoDirect(b, conn.UserIndex, 6),        // CHANGE_MAP_INFO_LAND
                77 => GM_MapInfoDirect(b, conn.UserIndex, 7),        // CHANGE_MAP_INFO_ZONE
                85 => GM_SetIniVar(b),                               // SET_INI_VAR: SSS
                86 => GM_DarPun(b),                                  // DAR_PUN: SI
                87 => GM_DarFaccion(b),                              // DAR_FACCION: SB
                89 => $"/donador {Q(b.ReadASCIIString())}",          // DONADOR: S
                94 => $"/summonbot {Q(b.ReadASCIIString())}",        // SUMMON_BOT: S
                _ => ConsumePayload(sub, b)
            };

            if (!string.IsNullOrEmpty(cmd))
                Game.Chat.HandleGMCommand(conn.UserIndex, cmd);
        }
        catch (NotEnoughDataException)
        {
            b.Clear(); b.AppendRaw(snap, snap.Length);
            throw;
        }
    }

    // Helpers GM - nombres descriptivos, payloads exactos de protocol_outgoing.gd
    /// <summary>Cita un argumento string: los nombres de PJ/clan pueden tener espacios y
    /// Chat.Tokenize trata "..." como un solo token.</summary>
    private static string Q(string s) => "\"" + s + "\"";
    private static string GM_NoPayload(ByteQueue b, string cmd) => cmd;
    private static string GM_WarpChar(ByteQueue b) { var n=b.ReadASCIIString(); var m=b.ReadInteger(); var x=b.ReadByte(); var y=b.ReadByte(); return $"/telep {Q(n)} {m} {x} {y}"; } // SIBB
    private static string GM_Jail(ByteQueue b) { var n=b.ReadASCIIString(); var r=b.ReadASCIIString(); var m=b.ReadByte(); return $"/carcel {Q(n)} {Q(r)} {m}"; } // SSB
    private static string GM_WarnUser(ByteQueue b) { var n=b.ReadASCIIString(); var r=b.ReadASCIIString(); return $"/advertencia {Q(n)} {r}"; } // SS
    private static string GM_EditChar(ByteQueue b) { var n=b.ReadASCIIString(); var op=b.ReadByte(); var a1=b.ReadASCIIString(); var a2=b.ReadASCIIString(); return $"/mod {Q(n)} {op} {a1} {a2}".TrimEnd(); } // SBSS
    private static string GM_BanChar(ByteQueue b) { var n=b.ReadASCIIString(); b.ReadByte(); return $"/ban {Q(n)}"; } // SB
    private static string GM_TeleportCreate(ByteQueue b) { var m=b.ReadInteger(); var x=b.ReadByte(); var y=b.ReadByte(); return $"/ct {m} {x} {y}"; } // IBB
    private static string GM_BanIP(ByteQueue b) { var ip=b.ReadASCIIString(); var r=b.ReadASCIIString(); return $"/banip {ip} {r}"; } // SS
    private static string GM_AlterPassword(ByteQueue b) { var n=b.ReadASCIIString(); var c=b.ReadASCIIString(); return $"/altpass {Q(n)} {Q(c)}"; } // SS
    private static string GM_SetIniVar(ByteQueue b) { var k=b.ReadASCIIString(); var s=b.ReadASCIIString(); var v=b.ReadASCIIString(); return $"/setinivar {k} {s} {v}"; } // SSS
    private static string GM_DarPun(ByteQueue b) { var n=b.ReadASCIIString(); var p=b.ReadInteger(); return $"/darpun {Q(n)} {p}"; } // SI
    private static string GM_DarFaccion(ByteQueue b) { var n=b.ReadASCIIString(); var f=b.ReadByte(); return $"/darfaccion {Q(n)} {f}"; } // SB

    private static string ConsumePayload(byte sub, ByteQueue b)
    {
        if (!GMPayload.TryGetValue(sub, out var spec)) return "";
        foreach (char c in spec)
        {
            switch (c)
            {
                case 'B': b.ReadByte(); break;
                case 'I': b.ReadInteger(); break;
                case 'L': b.ReadLong(); break;
                case 'S': b.ReadASCIIString(); break;
                case 'U': b.ReadUnicodeString(); break;
            }
        }
        Console.WriteLine($"[GMCMD] Subcomando {sub} payload consumido pero no implementado");
        return "";
    }

    private static string GM_MapInfoDirect(ByteQueue b, int userIndex, int tipo) { bool v = b.ReadBoolean(); Game.Chat.HandleChangeMapInfo(userIndex, tipo, v); return ""; }

    /// <summary>Fallback: firma de payload para subcomandos no mapeados en el switch.</summary>
    private static readonly Dictionary<byte, string> GMPayload = new()
    {
        {70,"B"},{71,"B"},{72,"B"},{73,"B"},{74,"B"},{75,"B"},{76,"B"},{77,"B"},
    };

    // ====================================================================
    //  Handlers — el id (ReadByte) SIEMPRE se consume primero, igual que VB6.
    // ====================================================================

    /// <summary>
    /// HandleQuit (Protocol.bas:6904) → ExecuteQuit: el botón "Cambiar Personaje" del menú
    /// escape vuelve al panel de selección SIN cerrar el socket. Guarda el char, lo saca del
    /// mundo y reenvía la lista de personajes de la cuenta (AddPj → el cliente emite
    /// account_logged y oculta el game_screen). No sale si está paralizado.
    /// </summary>
    private static void HandleQuit(Connection conn)
    {
        conn.IncomingData.ReadByte(); // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        if (u.flags.Paralizado == 1)
        {
            ServerPackets.ConsoleMsg(conn, "No puedes salir estando paralizado.", 1);
            return;
        }
        // VB6 además activa un timer de 10s en zona insegura (PK); acá salimos directo.
        string cuenta = Game.UserListManager.LogoutToCharList(conn.UserIndex);
        // El usuario YA está autenticado: reenviar la lista SIN re-validar contraseña (con password
        // vacío, HandleLoginAccount fallaba con "Contraseña incorrecta" y no volvía a la selección).
        if (!string.IsNullOrEmpty(cuenta))
            Game.AccountManager.EnviarListaPersonajes(conn, cuenta);
    }

    // ====================================================================
    //  Clanes (núcleo) — GuildManager
    // ====================================================================
    private static void HandleGuildFundation(Connection conn)
    {
        conn.IncomingData.ReadByte();
        byte clanType = conn.IncomingData.ReadByte();
        Game.UserListManager.UserList[conn.UserIndex].FundandoGuildAlineacion = clanType;
    }

    private static void HandleCreateNewGuild(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string desc = conn.IncomingData.ReadASCIIString();
        string name = conn.IncomingData.ReadASCIIString();
        string site = conn.IncomingData.ReadASCIIString();
        string codexRaw = conn.IncomingData.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        string[] codex = codexRaw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string alin = AlineacionToString(u.FundandoGuildAlineacion);
        if (!Game.GuildManager.CrearNuevoClan(u, desc, name, site, codex, alin, out string err))
            ServerPackets.ConsoleMsg(conn, err, 1);
    }

    private static void HandleGuildLeave(Connection conn)
    {
        conn.IncomingData.ReadByte();
        Game.GuildManager.SalirDeClan(Game.UserListManager.UserList[conn.UserIndex]);
    }

    private static void HandleGuildRequestMembership(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string guild = conn.IncomingData.ReadASCIIString();
        string app = conn.IncomingData.ReadASCIIString();
        Game.GuildManager.SolicitarIngreso(Game.UserListManager.UserList[conn.UserIndex], guild, app);
    }

    private static void HandleGuildAcceptNewMember(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string name = conn.IncomingData.ReadASCIIString();
        Game.GuildManager.AceptarAspirante(Game.UserListManager.UserList[conn.UserIndex], name);
    }

    private static void HandleGuildRejectNewMember(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string name = conn.IncomingData.ReadASCIIString();
        conn.IncomingData.ReadASCIIString(); // motivo (no usado en el núcleo)
        Game.GuildManager.RechazarAspirante(Game.UserListManager.UserList[conn.UserIndex], name);
    }

    private static void HandleGuildKickMember(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string name = conn.IncomingData.ReadASCIIString();
        Game.GuildManager.ExpulsarMiembro(Game.UserListManager.UserList[conn.UserIndex], name);
    }

    private static void HandleRequestGuildLeaderInfo(Connection conn)
    {
        conn.IncomingData.ReadByte();
        Game.GuildManager.EnviarLeaderInfo(Game.UserListManager.UserList[conn.UserIndex]);
    }

    private static Game.User GU(Connection conn) => Game.UserListManager.UserList[conn.UserIndex];

    private static void HandleShowGuildNews(Connection conn)
    { conn.IncomingData.ReadByte(); Game.GuildManager.EnviarNews(GU(conn)); }

    /// <summary>HandleCloseGuild (Protocol.bas:19537). Cable: solo Byte(id). El líder disuelve su clan.</summary>
    private static void HandleCloseGuild(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = GU(conn);
        if (!u.flags.UserLogged) return;
        if (u.flags.Muerto == 1) { ServerPackets.ConsoleMsg(conn, "¡Estás muerto!!", 3); return; }
        var md = Game.MapLoader.Get(u.Pos.Map);
        if (md != null && md.Info.Pk) { ServerPackets.ConsoleMsg(conn, "No puedes cerrar el clan en zona insegura!", 3); return; }
        if (!Game.GuildManager.CerrarClan(u, out string err)) ServerPackets.ConsoleMsg(conn, err, 3);
    }

    /// <summary>
    /// HandleGuildFundate (Protocol.bas:8290). Cable: solo Byte(id). Si el usuario cumple los
    /// requisitos (PuedeFundarUnClan) abre el formulario de fundación (AbrirFormularios 7); si no, error.
    /// </summary>
    private static void HandleGuildFundate(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = GU(conn);
        if (!u.flags.UserLogged) return;
        const byte FONT_GUILD = 5;
        // Validación de facción (1:1 VB6: debe tener una facción base válida).
        if (!(Game.Facciones.EsCiuda(u) || Game.Facciones.EsArmada(u) || Game.Facciones.EsRepu(u)
              || Game.Facciones.EsMili(u) || Game.Facciones.EsCaos(u) || Game.Facciones.EsRene(u)))
        {
            ServerPackets.ConsoleMsg(conn, "Hay un error en su facción, comuníquese con algún GameMaster", FONT_GUILD);
            return;
        }
        if (Game.GuildManager.PuedeFundarUnClan(u, out string error))
            ServerPackets.AbrirFormularios(conn, 7); // 7 = formulario de fundar clan
        else
            ServerPackets.ConsoleMsg(conn, error, FONT_GUILD);
    }

    private static void HandleGuildOnline(Connection conn)
    { conn.IncomingData.ReadByte(); Game.GuildManager.EnviarOnline(GU(conn)); }

    private static void HandleGuildMemberInfo(Connection conn)
    { conn.IncomingData.ReadByte(); conn.IncomingData.ReadASCIIString(); Game.GuildManager.EnviarMemberInfo(GU(conn)); }

    private static void HandleGuildRequestDetails(Connection conn)
    { conn.IncomingData.ReadByte(); string n = conn.IncomingData.ReadASCIIString(); Game.GuildManager.EnviarDetails(GU(conn), n); }

    private static void HandleGuildUpdateNews(Connection conn)
    { conn.IncomingData.ReadByte(); string n = conn.IncomingData.ReadASCIIString(); Game.GuildManager.ActualizarNews(GU(conn), n); }

    private static void HandleGuildNewWebsite(Connection conn)
    { conn.IncomingData.ReadByte(); string u = conn.IncomingData.ReadASCIIString(); Game.GuildManager.ActualizarWebsite(GU(conn), u); }

    private static void HandleClanCodexUpdate(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string raw = conn.IncomingData.ReadASCIIString();
        Game.GuildManager.ActualizarCodex(GU(conn), raw.Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void HandleGuildRequestJoinerInfo(Connection conn)
    { conn.IncomingData.ReadByte(); string n = conn.IncomingData.ReadASCIIString(); Game.GuildManager.EnviarJoinerInfo(GU(conn), n); }

    private static void HandleGuildOpenElections(Connection conn)
    { conn.IncomingData.ReadByte(); Game.GuildManager.AbrirElecciones(GU(conn)); }

    private static void HandleGuildVote(Connection conn)
    { conn.IncomingData.ReadByte(); string n = conn.IncomingData.ReadASCIIString(); Game.GuildManager.Votar(GU(conn), n); }

    private static void HandleGuildDeclareWar(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); Game.GuildManager.DeclararGuerra(GU(conn), g); }

    private static void HandleGuildOfferPeace(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); conn.IncomingData.ReadASCIIString(); Game.GuildManager.OfrecerPaz(GU(conn), g); }

    private static void HandleGuildOfferAlliance(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); conn.IncomingData.ReadASCIIString(); Game.GuildManager.OfrecerAlianza(GU(conn), g); }

    private static void HandleGuildAcceptPeace(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); Game.GuildManager.AceptarPaz(GU(conn), g); }

    private static void HandleGuildRejectPeace(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); Game.GuildManager.RechazarPaz(GU(conn), g); }

    private static void HandleGuildAcceptAlliance(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); Game.GuildManager.AceptarAlianza(GU(conn), g); }

    private static void HandleGuildRejectAlliance(Connection conn)
    { conn.IncomingData.ReadByte(); string g = conn.IncomingData.ReadASCIIString(); Game.GuildManager.RechazarAlianza(GU(conn), g); }

    private static void HandleGuildPeacePropList(Connection conn)
    { conn.IncomingData.ReadByte(); Game.GuildManager.EnviarPropList(GU(conn), paz: true); }

    private static void HandleGuildAlliancePropList(Connection conn)
    { conn.IncomingData.ReadByte(); Game.GuildManager.EnviarPropList(GU(conn), paz: false); }

    /// <summary>
    /// HandleRegresarHogar (Protocol.bas:18275): /hogar. Si el jugador está MUERTO, lo
    /// teletransporta al cementerio/revividor de su ciudad de origen (Ciudades(Hogar).Dead_*).
    /// </summary>
    private static void HandleRegresarHogar(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = GU(conn);
        if (u.flags.Muerto != 1)
        {
            ServerPackets.ConsoleMsg(conn, "Solo puedes regresar a tu hogar estando muerto.", 1);
            return;
        }
        var c = Game.CityData.Get(u.Hogar);
        if (c.DeadMap == 0) c = Game.CityData.Get(2); // fallback Illiandor
        Game.Movement.WarpUser(conn.UserIndex, c.DeadMap, c.DeadX, c.DeadY);
        ServerPackets.ConsoleMsg(conn, "Regresaste a tu hogar.", 5);
    }

    private const byte NPCTYPE_REVIVIDOR = 1; // eNPCType.Revividor (sacerdote)

    /// <summary>HandleCasamiento (Protocol.bas:19152) → PuedeCasarse (Trabajo.bas:2959).</summary>
    private static void HandleCasamiento(Connection conn)
    {
        conn.IncomingData.ReadByte();
        string nombre = conn.IncomingData.ReadASCIIString().Replace('+', ' ');
        conn.IncomingData.ReadByte(); // Modo (no usado, igual que VB6)
        int ti = Game.UserListManager.NameIndex(nombre);
        var u = GU(conn);
        if (ti <= 0) { ServerPackets.ConsoleMsg(conn, "El usuario está offline.", 1); return; }
        PuedeCasarse(conn, u, ti);
    }

    /// <summary>
    /// PuedeCasarse: si la pareja ya te tiene como candidato, los casa (anuncio global). Si no,
    /// envía la propuesta (requiere un sacerdote/Revividor seleccionado a ≤5 tiles).
    /// </summary>
    private static void PuedeCasarse(Connection conn, Game.User u, int parejaIdx)
    {
        var pareja = Game.UserListManager.UserList[parejaIdx];
        if (u.flags.Muerto == 1) { ServerPackets.ConsoleMsg(conn, "Estás muerto.", 1); return; }
        if (pareja.flags.Muerto == 1) { ServerPackets.ConsoleMsg(conn, "El usuario está muerto.", 1); return; }
        if (parejaIdx == conn.UserIndex) { ServerPackets.ConsoleMsg(conn, "¡No puedes casarte contigo mismo!", 1); return; }
        if (pareja.Genero == u.Genero) { ServerPackets.ConsoleMsg(conn, "No puedes casarte con alguien de tu mismo género.", 1); return; }
        if (Dist(u, pareja) > 1) { ServerPackets.ConsoleMsg(conn, "El objetivo está muy lejos.", 1); return; }
        if (u.CasamientoCasado == 1) { ServerPackets.ConsoleMsg(conn, "Ya estás casado.", 1); return; }
        if (pareja.CasamientoCasado == 1) { ServerPackets.ConsoleMsg(conn, "¡El usuario ya está casado!", 1); return; }

        // ¿La pareja ya me propuso (me tiene como candidato)? → casarse.
        if (pareja.CasamientoCandidato == conn.UserIndex)
        {
            pareja.CasamientoCasado = 1; pareja.CasamientoPareja = u.Name;
            u.CasamientoCasado = 1; u.CasamientoPareja = pareja.Name;
            u.CasamientoCandidato = 0; pareja.CasamientoCandidato = 0;
            string anuncio = $"¡{u.Name} y {pareja.Name} se han casado!";
            for (int i = 1; i <= Game.UserListManager.LastUser; i++)
            {
                var o = Game.UserListManager.UserList[i];
                if (o.flags.UserLogged && o.Conn != null) ServerPackets.ConsoleMsg(o.Conn, anuncio, 5);
            }
            return;
        }

        // Si no, enviar propuesta: requiere sacerdote (Revividor) seleccionado a ≤5 tiles.
        var npc = u.TargetNpcCharIndex > 0 ? Game.NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex) : null;
        if (npc == null || npc.NpcType != NPCTYPE_REVIVIDOR)
        { ServerPackets.ConsoleMsg(conn, "Primero seleccioná al sacerdote (clic izquierdo sobre él).", 1); return; }
        if (Math.Abs(npc.X - u.Pos.X) + Math.Abs(npc.Y - u.Pos.Y) > 5)
        { ServerPackets.ConsoleMsg(conn, "Estás muy lejos del sacerdote.", 1); return; }

        u.CasamientoCandidato = parejaIdx;
        ServerPackets.ConsoleMsg(conn, $"Le mandaste una propuesta de casamiento a {pareja.Name}, esperá su respuesta...", 5);
        if (pareja.Conn != null)
            ServerPackets.ConsoleMsg(pareja.Conn, $"{u.Name} te ha propuesto casamiento. Para aceptar, seleccioná al sacerdote y proponéle casamiento a {u.Name}.", 5);
    }

    /// <summary>HandleDivorciar (Protocol.bas:19207): rompe el casamiento de ambos.</summary>
    private static void HandleDivorciar(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = GU(conn);
        if (u.flags.Muerto == 1) { ServerPackets.ConsoleMsg(conn, "Estás muerto.", 1); return; }
        if (u.CasamientoCasado == 0 && string.IsNullOrEmpty(u.CasamientoPareja))
        { ServerPackets.ConsoleMsg(conn, "No puedes divorciarte porque no estás casado.", 1); return; }

        string parejaNombre = u.CasamientoPareja;
        int pi = Game.UserListManager.NameIndex(parejaNombre);
        u.CasamientoCasado = 0; u.CasamientoPareja = ""; u.CasamientoCandidato = 0;
        ServerPackets.ConsoleMsg(conn, $"{parejaNombre} ya no es más tu pareja.", 1);
        if (pi > 0)
        {
            var p = Game.UserListManager.UserList[pi];
            p.CasamientoCasado = 0; p.CasamientoPareja = ""; p.CasamientoCandidato = 0;
            if (p.Conn != null) ServerPackets.ConsoleMsg(p.Conn, $"{u.Name} ha decidido divorciarse de ti.", 1);
        }
    }

    private static int Dist(Game.User a, Game.User b)
        => a.Pos.Map != b.Pos.Map ? 9999 : Math.Abs(a.Pos.X - b.Pos.X) + Math.Abs(a.Pos.Y - b.Pos.Y);

    private const short FOGATA = 63; // ObjIndex de la fogata (Declares.bas:407)

    /// <summary>
    /// HandleRest (Protocol.bas:20300): /descansar. Si hay una fogata cerca, alterna el flag
    /// Descansar (el GameTimer regenera HP/Sta más rápido al descansar). Sin fogata, solo permite
    /// levantarse si estaba descansando.
    /// </summary>
    private static void HandleRest(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = GU(conn);
        if (u.flags.Muerto == 1) return;

        if (HayFogataCerca(u))
        {
            ServerPackets.RestOK(conn);
            ServerPackets.ConsoleMsg(conn, u.flags.Descansar == 0
                ? "Te acomodas junto a la fogata y comienzas a descansar."
                : "Te levantas.", 1);
            u.flags.Descansar = (byte)(u.flags.Descansar == 0 ? 1 : 0);
        }
        else if (u.flags.Descansar != 0)
        {
            ServerPackets.RestOK(conn);
            ServerPackets.ConsoleMsg(conn, "Te levantas.", 1);
            u.flags.Descansar = 0;
        }
        else
        {
            ServerPackets.ConsoleMsg(conn, "No hay ninguna fogata junto a la cual descansar.", 1);
        }
    }

    /// <summary>HayOBJarea(FOGATA): hay una fogata en el área de visión del jugador (TCP.bas:764).</summary>
    private static bool HayFogataCerca(Game.User u)
    {
        var map = Game.MapLoader.Get(u.Pos.Map);
        if (map == null) return false;
        for (int y = u.Pos.Y - 8; y <= u.Pos.Y + 8; y++)
            for (int x = u.Pos.X - 8; x <= u.Pos.X + 8; x++)
            {
                if (x < 1 || x > 100 || y < 1 || y > 100) continue;
                if (map.FloorObj[x, y] == FOGATA) return true;
            }
        return false;
    }

    // ALINEACION_GUILD (modGuilds.bas:36): 1=Republicano, 2=Imperial, 3=Caótico, 4=Renegado.
    private static string AlineacionToString(byte a) => a switch
    {
        1 => "Republicano", 2 => "Imperial", 3 => "Caótico", 4 => "Renegado", _ => "Neutral",
    };

    /// <summary>
    /// VersionOK (Admin.bas:95) + mensaje "Cliente desactualizado" (Protocol.bas:1364…). Compara la
    /// versión enviada por el cliente contra ULTIMAVERSION (version.txt). Devuelve true si puede seguir;
    /// si no coincide, manda el ShowMessageBox de cliente desactualizado y devuelve false.
    /// </summary>
    private static bool RechazarSiVersionInvalida(Connection conn, short version)
    {
        if (ServerConfig.VersionOk(version)) return true;

        Console.WriteLine($"[ServidorCS] RECHAZO CONEXIÓN - versión incorrecta. Servidor: {ServerConfig.UltimaVersion}, Cliente: {version}");
        ServerPackets.ShowMessageBox(conn,
            "¡¡ CLIENTE DESACTUALIZADO !!\r\n\r\n" +
            $"Tu version: {version}\r\n" +
            $"Version requerida: {ServerConfig.UltimaVersion}\r\n\r\n" +
            "Ejecuta el LAUNCHER para actualizar el juego.");
        // VB6: FlushBuffer + CloseSocket tras el rechazo (el cliente debe volver al login).
        conn.FlushAndClose();
        return false;
    }

    /// <summary>
    /// Port 1:1 de HandleLoginAccount (Protocol.bas:17906). Es el PRIMER paso del login:
    /// el cliente conecta la cuenta y el server responde con la lista de personajes.
    /// Cable: Byte(id) + ASCIIString(Cuenta) + block_prefixed(Password cifrado)
    ///        + Integer(Version) + ASCIIString(Mac) + Long(HDserial).
    /// (block_prefixed = Int16 len + bytes; se lee igual que ASCIIString.)
    /// </summary>
    private static void HandleConnectAccount(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 14) throw new NotEnoughDataException();

        b.ReadByte();                          // id
        string cuenta   = b.ReadASCIIString();
        byte[] passBlk  = b.ReadBlockBytes();  // password cifrado (shift AO) — leer bytes crudos
        short version   = b.ReadInteger();
        string mac      = b.ReadASCIIString();
        int hdSerial    = b.ReadLong();
        string password = Game.Crypto.ShiftDecrypt(passBlk);

        Console.WriteLine($"[ServidorCS] ConnectAccount: cuenta='{cuenta}' ver={version}");

        // VALIDACIÓN DE VERSIÓN (VersionOK, Admin.bas:95) — después de leer todo el payload.
        if (!RechazarSiVersionInvalida(conn, version)) return;

        Game.AccountManager.HandleLoginAccount(conn, cuenta, password);
    }

    /// <summary>
    /// HandleLoginNewChar. Cable: Byte(id) + ASCIIString(cuenta) + blockprefixed(pass)
    /// + Integer(version) + ASCIIString(nombre) + Byte(raza) + Byte(genero) + Byte(clase)
    /// + Byte(hogar) + Integer(head) + ASCIIString(mac) + Long(hdserial).
    /// </summary>
    private static void HandleLoginNewChar(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 14) throw new NotEnoughDataException();
        b.ReadByte();
        string cuenta = b.ReadASCIIString();
        byte[] passBlk = b.ReadBlockBytes(); // pass cifrado (bytes crudos)
        short version = b.ReadInteger();
        string nombre = b.ReadASCIIString();
        byte raza = b.ReadByte();
        byte genero = b.ReadByte();
        byte clase = b.ReadByte();
        byte hogar = b.ReadByte();
        short head = b.ReadInteger();
        b.ReadASCIIString();              // mac
        b.ReadLong();                     // hdserial

        // VALIDACIÓN DE VERSIÓN (VersionOK, Admin.bas:95) — después de leer todo el payload.
        if (!RechazarSiVersionInvalida(conn, version)) return;

        // Validar cuenta antes de crear el personaje (no baneada, password correcta).
        string password = Game.Crypto.ShiftDecrypt(passBlk);
        if (!Game.AccountManager.ValidarCuenta(cuenta, password, out string err))
        {
            // VB6: FlushBuffer + CloseSocket en todo rechazo de login.
            ServerPackets.ShowMessageBox(conn, err);
            conn.FlushAndClose();
            return;
        }
        // Una sola sesión por cuenta (anti-clon/dupe), igual que el login normal.
        if (Game.UserListManager.CuentaConectada(cuenta) >= 1)
        {
            ServerPackets.ShowMessageBox(conn, "Ya hay un usuario conectado con esta cuenta.");
            conn.FlushAndClose();
            return;
        }

        Game.CharCreator.LoginNewChar(conn, cuenta, nombre, raza, genero, clase, hogar, head);
    }

    /// <summary>
    /// Port 1:1 de HandleLoginExistingChar (Protocol.bas:1288).
    /// Cable: Byte(id) + ASCIIString(Cuenta) + ASCIIString(Password) + Integer(Version)
    ///        + ASCIIString(UserName) + ASCIIString(MacAddress) + Long(HDserial)
    /// </summary>
    private static void HandleLoginExistingChar(Connection conn)
    {
        var b = conn.IncomingData;

        // El VB6 exige al menos 6 bytes antes de empezar.
        if (b.Length < 6) throw new NotEnoughDataException();

        b.ReadByte();                            // id
        string cuenta     = b.ReadASCIIString();
        byte[] passBlk    = b.ReadBlockBytes();  // password cifrado (bytes crudos)
        short version     = b.ReadInteger();
        string userName   = b.ReadASCIIString();
        string macAddress = b.ReadASCIIString();
        int hdSerial      = b.ReadLong();
        string password   = Game.Crypto.ShiftDecrypt(passBlk);

        Console.WriteLine($"[ServidorCS] LoginExistingChar: cuenta='{cuenta}' pj='{userName}' ver={version}");

        // VALIDACIÓN DE VERSIÓN (VersionOK, Admin.bas:95) — después de leer todo el payload.
        if (!RechazarSiVersionInvalida(conn, version)) return;

        // Validaciones básicas (orden 1:1 con el VB6, versión mínima).
        if (string.IsNullOrEmpty(userName))
        {
            // VB6: FlushBuffer + CloseSocket en todo rechazo de login.
            ServerPackets.ShowMessageBoxCode(conn, 64); // Nombre inválido
            conn.FlushAndClose();
            return;
        }

        // Validar la cuenta (password correcta, no baneada) antes de entrar al mundo.
        if (!Game.AccountManager.ValidarCuenta(cuenta, password, out string loginErr))
        {
            ServerPackets.ShowMessageBox(conn, loginErr);
            conn.FlushAndClose();
            return;
        }
        // El personaje debe pertenecer a la cuenta.
        if (!Game.AccountManager.CuentaTienePersonaje(cuenta, userName))
        {
            ServerPackets.ShowMessageBox(conn, "Ese personaje no pertenece a esta cuenta.");
            conn.FlushAndClose();
            return;
        }
        // Una sola sesión por cuenta (VB6 CuentaConectada=1 → msgbox 47). Anti-clon/dupe.
        if (Game.UserListManager.CuentaConectada(cuenta) >= 1)
        {
            ServerPackets.ShowMessageBox(conn, "Ya hay un usuario conectado con esta cuenta.");
            conn.FlushAndClose();
            return;
        }

        // Cargar el personaje desde el charfile (.chr).
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!Game.CharLoader.LoadCharacter(u, userName))
        {
            ServerPackets.ShowMessageBoxCode(conn, 38); // El personaje no existe
            conn.FlushAndClose();
            return;
        }

        u.Name = userName;
        u.Account = cuenta;
        u.id = conn.UserIndex; // asignar índice para que u.id sea válido en todos los handlers
        u.flags.UserLogged = true;

        // Flujo de entrada al mundo (subset del ConnectUser de VB6).
        Game.LoginFlow.EnterWorld(conn, u);
    }

    /// <summary>
    /// HandleWalk (Protocol.bas:2070). Cable: Byte(id) + Byte(Heading).
    /// Heading válido: 1..4 (N=1, E=2, S=3, O=4, ver [[ao_heading_order]]).
    /// </summary>
    private static void HandleWalk(Connection conn)
    {
        var b = conn.IncomingData;
        b.ReadByte();                  // id
        byte heading = b.ReadByte();

        if (heading < 1 || heading > 4) return;   // VB6: Exit Sub si inválido

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;

        // Sólo se mueve si no está paralizado/inmovilizado (1:1 con HandleWalk) ni congelado por
        // la cuenta regresiva de un combate de torneo.
        if (u.flags.Paralizado == 0 && u.flags.Inmovilizado == 0 && !u.flags.TorneoCongelado)
            Game.Movement.MoveUserChar(conn.UserIndex, heading);
    }

    /// <summary>
    /// HandleChangeHeading (Protocol.bas:4817). Cable: Byte(id) + Byte(Heading).
    /// Solo cambia el rumbo del personaje sin moverlo.
    /// </summary>
    private static void HandleChangeHeading(Connection conn)
    {
        var b = conn.IncomingData;
        b.ReadByte();                  // id
        byte heading = b.ReadByte();

        if (heading < 1 || heading > 4) return;

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged || u.Char.heading == heading) return;
        u.Char.heading = heading;
        // Difundir el nuevo rumbo al área para que los demás vean el giro en el lugar (CharacterChange).
        Game.Combat.DifundirApariencia(u);
    }

    /// <summary>
    /// HandleTalk (Protocol.bas:1767). Cable: Byte(id) + ASCIIString(chat) + Byte(TalkMode).
    /// Difunde el texto sobre el personaje (ChatOverHead) a los del mismo mapa.
    /// </summary>
    private static void HandleTalk(Connection conn)
    {
        Console.WriteLine($"[TALK] HandleTalk recibido! BufferLength={conn.IncomingData.Length}");

        var b = conn.IncomingData;
        if (b.Length < 4)
        {
            Console.WriteLine($"[TALK] ERROR: Buffer muy corto ({b.Length} < 4)");
            throw new NotEnoughDataException();
        }

        b.ReadByte();                  // id
        string chat = b.ReadASCIIString();
        byte talkMode = b.ReadByte();

        Console.WriteLine($"[TALK] Chat: '{chat}' | TalkMode: {talkMode}");

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged)
        {
            Console.WriteLine($"[TALK] Usuario no loggeado");
            return;
        }
        if (string.IsNullOrEmpty(chat))
        {
            Console.WriteLine($"[TALK] Chat vacío");
            return;
        }

        Console.WriteLine($"[TALK] Llamando TalkToMap...");
        Game.Chat.TalkToMap(conn.UserIndex, chat, talkMode == 0 ? (byte)1 : talkMode);
    }

    // ============================================================
    //  Sistema de reportes / tickets (NUEVO, no VB6)
    // ============================================================

    /// <summary>ReportCreate: Byte(cat) ASCIIString(subject) ASCIIString(body).</summary>
    private static void HandleReportCreate(Connection conn)
    {
        var b = conn.IncomingData;
        b.ReadByte(); // id
        byte cat = b.ReadByte();
        string subject = b.ReadASCIIString();
        string body = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        Game.ReportManager.Create(conn.UserIndex, cat, subject, body);
    }

    /// <summary>ReportListRequest: Byte(filter).</summary>
    private static void HandleReportListRequest(Connection conn)
    {
        var b = conn.IncomingData;
        b.ReadByte(); // id
        byte filter = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        Game.ReportManager.SendList(conn.UserIndex, filter);
    }

    /// <summary>ReportDetailRequest: Long(reportId).</summary>
    private static void HandleReportDetailRequest(Connection conn)
    {
        var b = conn.IncomingData;
        b.ReadByte(); // id
        int reportId = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        Game.ReportManager.SendDetail(conn.UserIndex, reportId);
    }

    /// <summary>ReportAction: Long(reportId) Byte(action) ASCIIString(message).</summary>
    private static void HandleReportAction(Connection conn)
    {
        var b = conn.IncomingData;
        b.ReadByte(); // id
        int reportId = b.ReadLong();
        byte action = b.ReadByte();
        string message = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        Game.ReportManager.DoAction(conn.UserIndex, reportId, action, message);
    }

    // ============================================================
    //  Editor de objetos en vivo para GMs (NUEVO, no VB6)
    // ============================================================

    /// <summary>ObjEditorRequest: solo Byte(id). Responde ObjEditorList (valida privilegios adentro).</summary>
    private static void HandleObjEditorRequest(Connection conn)
    {
        conn.IncomingData.ReadByte(); // id
        Game.ObjEditor.SendList(conn);
    }

    /// <summary>NpcCatalogRequest: solo Byte(id). Responde NpcCatalog (catálogo índice+nombre) si es GM.</summary>
    private static void HandleNpcCatalogRequest(Connection conn)
    {
        conn.IncomingData.ReadByte(); // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return;
        if (Game.AdminLoader.GetFaccionStatus(u.Name) < Game.AdminLoader.STATUS_CONSEJERO) return;
        ServerPackets.NpcCatalog(conn, Game.NpcData.All());
    }

    /// <summary>
    /// QueryMapNpcs: Byte(id)+Integer(map). Responde MapNpcsList con las criaturas que
    /// habitan ese mapa (definición de spawn del .csm, no NPCs vivos: así funciona para
    /// cualquier mapa aunque nadie lo haya visitado). El cliente lo usa en el mapa-mundi
    /// (minimap.gd) al hacer clic en un mapa para ver qué criaturas hay ahí.
    /// </summary>
    private static void HandleQueryMapNpcs(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException(); // id(1)+int(2)
        b.ReadByte(); // id
        int map = b.ReadInteger();

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return;

        var md = Game.MapLoader.Get(map);
        var entries = new List<ServerPackets.MapNpcEntry>();
        if (md != null)
        {
            // Agrupar por NpcIndex preservando el orden de aparición.
            var counts = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var mn in md.Npcs)
            {
                if (mn.NpcIndex <= 0) continue;
                if (!counts.ContainsKey(mn.NpcIndex)) { counts[mn.NpcIndex] = 0; order.Add(mn.NpcIndex); }
                counts[mn.NpcIndex]++;
            }
            foreach (int idx in order)
            {
                if (entries.Count >= 255) break; // count viaja en un Byte
                var info = Game.NpcData.Get(idx);
                if (string.IsNullOrEmpty(info.Name)) continue;
                entries.Add(new ServerPackets.MapNpcEntry
                {
                    Name = info.Name,
                    Count = (byte)Math.Min(counts[idx], 255),
                    Body = info.Body,
                    Head = info.Head,
                    Exp = info.GiveEXP,
                    Gold = info.GiveGLD,
                    MaxHP = info.MaxHP,
                    Drops = info.Drops,
                });
            }
        }
        ServerPackets.MapNpcsList(conn, map, entries);
    }

    /// <summary>ObjEditorDetailRequest: Integer(objIndex). Responde ObjEditorDetail.</summary>
    private static void HandleObjEditorDetailRequest(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException(); // id(1)+int(2)
        b.ReadByte(); // id
        int objIndex = b.ReadInteger();
        Game.ObjEditor.SendDetail(conn, objIndex);
    }

    /// <summary>ObjEditorSave: Integer(objIndex) Byte(count) count×[ASCIIString clave, ASCIIString valor].</summary>
    private static void HandleObjEditorSave(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException(); // id(1)+int(2)+byte(1)
        b.ReadByte(); // id
        int objIndex = b.ReadInteger();
        byte count = b.ReadByte();
        var cambios = new List<(string, string)>();
        for (int i = 0; i < count; i++)
        {
            string key = b.ReadASCIIString();
            string value = b.ReadASCIIString();
            cambios.Add((key, value));
        }
        Game.ObjEditor.Save(conn.UserIndex, objIndex, cambios);
    }

    /// <summary>ObjEditorReloadAll: solo Byte(id). Relee todo el obj.dat de disco (valida privilegios adentro).</summary>
    private static void HandleObjEditorReloadAll(Connection conn)
    {
        conn.IncomingData.ReadByte(); // id
        Game.ObjEditor.ReloadAll(conn);
    }

    /// <summary>HandleRequestPositionUpdate. Cable: solo Byte(id). Responde PosUpdate.</summary>
    private static void HandleRequestPositionUpdate(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged)
            ServerPackets.PosUpdate(conn, (byte)u.Pos.X, (byte)u.Pos.Y);
    }

    /// <summary>HandleRequestAtributes. Cable: solo Byte(id). Responde Attributes.</summary>
    private static void HandleRequestAtributes(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) ServerPackets.Attributes(conn, u);
    }

    /// <summary>HandleRequestSkills. Cable: solo Byte(id). Responde SendSkills con los puntos de cada skill.</summary>
    private static void HandleRequestSkills(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) ServerPackets.SendSkills(conn, u);
    }

    /// <summary>
    /// HandleModifySkills (Protocol.bas:4859). Cable: Byte(id) + NUMSKILLS bytes (puntos a sumar a cada
    /// skill). Anti-hack: si la suma supera SkillPts libres → cierre. Resta de SkillPts, suma a UserSkills
    /// (cap 100, devolviendo el excedente). Responde SendSkills + UpdateUserStats. 1:1 con VB6.
    /// </summary>
    private static void HandleModifySkills(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 1 + Game.Constants.NUMSKILLS) throw new NotEnoughDataException();
        b.ReadByte(); // id
        // Leer SIEMPRE los NUMSKILLS bytes (mantiene el stream alineado aunque no esté logueado).
        var points = new byte[Game.Constants.NUMSKILLS + 1];
        int count = 0;
        for (int i = 1; i <= Game.Constants.NUMSKILLS; i++) { points[i] = b.ReadByte(); count += points[i]; }

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;

        // Anti-hack VB6: no puede asignar más puntos que los libres → CloseSocket.
        if (count > u.Stats.SkillPts)
        {
            Console.WriteLine($"[ANTI-HACK] {u.Name} intentó asignar {count} pts (libres={u.Stats.SkillPts}) → cierre.");
            b.Clear(); conn.Close(); return;
        }

        for (int i = 1; i <= Game.Constants.NUMSKILLS; i++)
        {
            u.Stats.SkillPts -= points[i];
            int nv = u.Stats.UserSkills[i] + points[i];
            if (nv > 100) { u.Stats.SkillPts += (short)(nv - 100); nv = 100; }
            u.Stats.UserSkills[i] = (byte)nv;
        }
        ServerPackets.SendSkills(conn, u);
        ServerPackets.UpdateUserStats(conn, u);
    }

    /// <summary>
    /// HandleDropDestroy (Protocol.bas:2681). Cable: Byte(id) + Byte(slot) + Long(amount). Destruye
    /// 'amount' del slot (no cae al piso). Validaciones 1:1 en Inventory.DropDestroy.
    /// </summary>
    private static void HandleDropDestroy(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 6) throw new NotEnoughDataException(); // id(1)+slot(1)+long(4)
        b.ReadByte();               // id
        byte slot = b.ReadByte();
        int amount = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Inventory.DropDestroy(conn.UserIndex, slot, amount);
    }

    /// <summary>
    /// HandleSwapObjects (Protocol.bas:18019). Cable: Byte(id) + Byte(slot1) + Byte(slot2).
    /// Reordena dos slots del inventario (lógica 1:1 en Inventory.SwapObjects).
    /// </summary>
    private static void HandleSwapObjects(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException(); // id(1)+slot1(1)+slot2(1)
        b.ReadByte();              // id
        byte slot1 = b.ReadByte();
        byte slot2 = b.ReadByte();
        Game.Inventory.SwapObjects(conn.UserIndex, slot1, slot2);
    }

    /// <summary>
    /// HandleMoveBank (Protocol.bas:5254). Cable: Byte(id) + Boolean(dir) + Byte(slot).
    /// dir=true sube el item (slot-1), dir=false baja (slot+1). Lógica 1:1 en Bank.MoveBank.
    /// </summary>
    private static void HandleMoveBank(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException(); // id(1)+bool(1)+slot(1)
        b.ReadByte();                 // id
        bool dirUp = b.ReadBoolean();
        byte slot = b.ReadByte();
        Game.Bank.MoveBank(conn.UserIndex, dirUp, slot);
    }

    /// <summary>
    /// HandleTrain (Protocol.bas:4930). Cable: Byte(id) + Byte(petIndex). El entrenador
    /// seleccionado (TargetNPC) invoca la criatura petIndex; LocaleMsg 593 si llegó al tope.
    /// </summary>
    private static void HandleTrain(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException(); // id(1)+petIndex(1)
        b.ReadByte();                  // id
        byte petIndex = b.ReadByte();

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        if (u.TargetNpcCharIndex == 0) return;                         // flags.TargetNPC = 0 → Exit Sub
        var npc = Game.NpcManager.NpcByCharIndex(u.Pos.Map, u.TargetNpcCharIndex);
        if (npc == null || npc.NpcType != 3) return;                   // no es Entrenador → Exit Sub

        if (!Game.NpcManager.Train(u.Pos.Map, npc, petIndex))
            ServerPackets.LocaleMsg(conn, 593, "", 1);                 // tope de mascotas
    }

    /// <summary>HandleAddAmigo (Protocol.bas:19867). Cable: Byte(id) + ASCII(nombre) + Byte(caso).</summary>
    private static void HandleAddAmigos(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException(); // id + ascii(min 2) + caso
        b.ReadByte();                       // id
        string nombre = b.ReadASCIIString();
        byte caso = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Social.AddAmigo(conn.UserIndex, nombre, caso);
    }

    /// <summary>HandleDelAmigo (Protocol.bas:19955). Cable: Byte(id) + ASCII(nick).</summary>
    private static void HandleDelAmigos(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();                       // id
        string nick = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Social.DelAmigo(conn.UserIndex, nick);
    }

    /// <summary>HandleMsgAmigo (Protocol.bas:19777). Cable: Byte(id) + ASCII(mensaje).</summary>
    private static void HandleMsgAmigos(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();                       // id
        string mensaje = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Social.MsgAmigos(conn.UserIndex, mensaje);
    }

    /// <summary>HandleOnAmigo (Protocol.bas:19825). Cable: solo Byte(id).</summary>
    private static void HandleOnAmigos(Connection conn)
    {
        conn.IncomingData.ReadByte();       // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Social.OnAmigos(conn.UserIndex);
    }

    /// <summary>HandleResuscitationToggle (Protocol.bas:2393). Cable: solo Byte(id). Alterna SeguroResu (LocaleMsg 14/15).</summary>
    private static void HandleResuscitationSafeToggle(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        // VB6: muestra el estado ACTUAL antes de invertir (15=activado→se desactiva, 14=desactivado→se activa).
        ServerPackets.LocaleMsg(conn, u.flags.SeguroResu ? 15 : 14, "", 12, 1);
        u.flags.SeguroResu = !u.flags.SeguroResu;
    }

    /// <summary>
    /// HandleSeleccionarHogar (Protocol.bas:19063). Cable: Byte(id) + Byte(caso). caso0 pide confirmación
    /// (NPC Revividor ≤5), caso1 fija el hogar según el mapa actual. 1:1 VB6.
    /// </summary>
    private static void HandleSeleccionarHogar(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte caso = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        Game.Social.SeleccionarHogar(conn.UserIndex, caso);
    }

    /// <summary>HandleEnlist (Protocol.bas:7413). Cable: solo Byte(id).</summary>
    private static void HandleEnlist(Connection conn)
    {
        conn.IncomingData.ReadByte();       // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Facciones.Enlist(conn.UserIndex);
    }

    /// <summary>HandleRetirarFaccion (Protocol.bas:18197). Cable: solo Byte(id).</summary>
    private static void HandleRetirarFaccion(Connection conn)
    {
        conn.IncomingData.ReadByte();       // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Facciones.RetirarFaccion(conn.UserIndex);
    }

    /// <summary>
    /// HandleChangeDescription (Protocol.bas:7850). Cable: Byte(id) + ASCIIString(desc).
    /// Valida AsciiValidos; los Soporte conservan el sufijo "&lt;Soporte&gt;". 1:1 VB6.
    /// </summary>
    private static void HandleChangeDescription(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();                          // id
        string description = b.ReadASCIIString();

        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;

        if (u.flags.Muerto == 1)
        {
            ServerPackets.LocaleMsg(conn, 77);
            return;
        }
        if (!AsciiValidos(description))
        {
            ServerPackets.LocaleMsg(conn, 392, "", 1);
            return;
        }
        // Los soportes pueden cambiar su descripción pero siempre mantienen "<Soporte>".
        if (u.FaccionStatus == Game.AdminLoader.STATUS_SOPORTE)
        {
            string descSinSoporte = description.Trim()
                .Replace("<Soporte>", "", StringComparison.OrdinalIgnoreCase).Trim();
            u.desc = descSinSoporte.Length > 0 ? descSinSoporte + " <Soporte>" : "<Soporte>";
        }
        else
        {
            u.desc = description.Trim();
        }
        ServerPackets.LocaleMsg(conn, 111);
    }

    /// <summary>AsciiValidos (TCP.bas:210) 1:1: cada char (en minúscula) debe ser a-z (97-122), ÿ (255) o espacio (32).</summary>
    private static bool AsciiValidos(string cad)
    {
        if (cad == null) return true;
        foreach (byte car in Cp1252.GetBytes(cad.ToLowerInvariant()))
            if ((car < 97 || car > 122) && car != 255 && car != 32) return false;
        return true;
    }

    /// <summary>
    /// HandleCreateNewAccount (Protocol.bas:17840). Cable Godot: Byte(id) + ASCII(cuenta) + Block(pass)
    /// + Block(pin) + Int(version). Descifra pass/pin (bytes crudos) y crea la cuenta (1:1 SaveNewAccount).
    /// </summary>
    private static void HandleCreateNewAccount(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte();                       // id
        string cuenta = b.ReadASCIIString();
        byte[] passBlk = b.ReadBlockBytes(); // password (bytes crudos)
        byte[] pinBlk  = b.ReadBlockBytes(); // pin (bytes crudos)
        short version  = b.ReadInteger();   // version

        // VALIDACIÓN DE VERSIÓN (VersionOK, Admin.bas:95) — después de leer todo el payload (Protocol.bas:17861).
        if (!RechazarSiVersionInvalida(conn, version)) return;

        string password = Game.Crypto.ShiftDecrypt(passBlk);
        string pin      = Game.Crypto.ShiftDecrypt(pinBlk);
        Game.AccountManager.CreateNewAccount(conn, cuenta, password, pin);
    }

    /// <summary>
    /// HandleProcesosLogin. Cable Godot (write_procesos_login / write_borrar_personaje):
    ///   Byte(id) + Byte(step) + ASCII(cuenta) + Block(pass) + [ step==8: ASCII(charName) + Int(version)
    ///   | else: Block(pin) + Long(token) + Int(version) ].
    /// El layout depende del step → handler propio (un PayloadSpec fijo no sirve). Consume EXACTO para
    /// no desincronizar; la lógica (borrar PJ / login multi-fase) no está portada.
    /// </summary>
    private static void HandleProcesosLogin(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte();                       // id
        byte step = b.ReadByte();
        string cuenta = b.ReadASCIIString();
        byte[] passBlk = b.ReadBlockBytes(); // password cifrado (bytes crudos)
        if (step == 8)                      // BorrarPersonaje
        {
            string charName = b.ReadASCIIString();
            b.ReadInteger();                // version
            string password = Game.Crypto.ShiftDecrypt(passBlk);
            Game.AccountManager.BorrarPersonaje(conn, cuenta, password, charName);
        }
        else
        {
            b.ReadASCIIString();            // pin (block)
            b.ReadLong();                   // token
            b.ReadInteger();                // version
            Console.WriteLine($"[ProcesosLogin] step={step} cuenta='{cuenta}' — no implementado (stream OK).");
        }
    }

    /// <summary>HandleCentinelReport (modCentinela). Cable: Byte(id) + Int(clave). Valida la clave anti-macro.</summary>
    private static void HandleCentinelReport(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();
        short clave = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Centinela.CheckClave(conn.UserIndex, clave);
    }

    /// <summary>HandleRequestShopData (modMercadoPago). Cable: solo Byte(id). Envía catálogo/historial/ranking.</summary>
    private static void HandleRequestShopData(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.MercadoPago.RequestShopData(conn.UserIndex);
    }

    /// <summary>HandleShopBuyItem (modMercadoPago). Cable: Byte(id) + Int(itemId). Crea el link de pago.</summary>
    private static void HandleShopBuyItem(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();
        short itemId = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.MercadoPago.ShopBuyItem(conn.UserIndex, itemId);
    }

    /// <summary>HandleAuctionCreate (Protocol.bas:21774). Cable: Byte(id) + Int(objIndex) + Long(amount) + Long(buyout).</summary>
    private static void HandleAuctionCreate(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 11) throw new NotEnoughDataException(); // id(1)+int(2)+long(4)+long(4)
        b.ReadByte();
        short objIndex = b.ReadInteger();
        int amount = b.ReadLong();
        int buyout = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Subastas.Crear(conn.UserIndex, objIndex, amount, buyout);
    }

    /// <summary>HandleAuctionBid (Protocol.bas:21789). Cable: Byte(id) + Int(auctionId) + Long(bid).</summary>
    private static void HandleAuctionBid(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 7) throw new NotEnoughDataException(); // id(1)+int(2)+long(4)
        b.ReadByte();
        short subId = b.ReadInteger();
        int bid = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Subastas.Pujar(conn.UserIndex, subId, bid);
    }

    /// <summary>HandleTorneoAction: Byte(id)+Byte(action)+Byte(mode). action 0=estado,1=entrar cola,2=salir.</summary>
    private static void HandleTorneoAction(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException(); // id(1)+action(1)+mode(1)
        b.ReadByte();             // id
        byte action = b.ReadByte();
        byte mode = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return;
        switch (action)
        {
            case 1: Game.TorneoEvento.EntrarCola(conn.UserIndex, mode); break;
            case 2: Game.TorneoEvento.SalirCola(conn.UserIndex); break;
            default: Game.TorneoEvento.SolicitarEstado(conn.UserIndex); break;
        }
    }

    /// <summary>HandleArenaJoin (Protocol.bas:HandleArenaJoin). Cable: solo Byte(id). Inscribe en la cola de arena.</summary>
    private static void HandleArenaJoin(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.ArenaEvento.UnirseArena(conn.UserIndex);
    }

    /// <summary>HandleUpTime (Protocol.bas:7622). Cable: solo Byte(id). Informa el tiempo online del server.</summary>
    private static void HandleUpTime(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        long secs = (Environment.TickCount64 - GameServer.StartTick) / 1000;
        long s = secs % 60; secs /= 60;
        long m = secs % 60; secs /= 60;
        long h = secs % 24; long d = secs / 24;
        string txt = $"{d} {(d == 1 ? "día" : "días")}, {h} horas, {m} minutos, {s} segundos.";
        ServerPackets.ConsoleMsg(conn, "Server Online: " + txt, 3); // FONTTYPE_INFO
    }

    /// <summary>HandleRequestStats (Protocol.bas:7180). Cable: solo Byte(id). Responde SendUserStatsTxt (/est).</summary>
    private static void HandleRequestStats(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) SendUserStatsTxt(conn, u);
    }

    /// <summary>
    /// SendUserStatsTxt (Modulo_UsUaRiOs.bas:1358) 1:1: vuelca las estadísticas del personaje a la
    /// consola (FONTTYPE_INFO=3). Mismo texto y orden que el VB6.
    /// </summary>
    private static void SendUserStatsTxt(Connection conn, Game.User u)
    {
        const byte F = 3; // FONTTYPE_INFO
        var st = u.Stats;
        ServerPackets.ConsoleMsg(conn, "Estadisticas de: " + u.Name, F);
        ServerPackets.ConsoleMsg(conn, $"Nivel: {st.ELV}  EXP: {(long)st.Exp}/{st.ELU}", F);
        ServerPackets.ConsoleMsg(conn, $"Salud: {st.MinHP}/{st.MaxHP}  Mana: {st.MinMAN}/{st.MaxMAN}  Energia: {st.MinSta}/{st.MaxSta}", F);

        if (u.Invent.WeaponEqpObjIndex > 0)
        {
            var w = Game.ObjData.Get(u.Invent.WeaponEqpObjIndex);
            ServerPackets.ConsoleMsg(conn, $"Menor Golpe/Mayor Golpe: {st.MinHIT}/{st.MaxHIT} ({w.MinHIT}/{w.MaxHIT})", F);
        }
        else ServerPackets.ConsoleMsg(conn, $"Menor Golpe/Mayor Golpe: {st.MinHIT}/{st.MaxHIT}", F);

        if (u.Invent.ArmourEqpObjIndex > 0)
        {
            var ar = Game.ObjData.Get(u.Invent.ArmourEqpObjIndex);
            if (u.Invent.EscudoEqpObjIndex > 0)
            {
                var es = Game.ObjData.Get(u.Invent.EscudoEqpObjIndex);
                ServerPackets.ConsoleMsg(conn, $"(CUERPO) Min Def/Max Def: {ar.MinDef + es.MinDef}/{ar.MaxDef + es.MaxDef}", F);
            }
            else ServerPackets.ConsoleMsg(conn, $"(CUERPO) Min Def/Max Def: {ar.MinDef}/{ar.MaxDef}", F);
        }
        else ServerPackets.ConsoleMsg(conn, "(CUERPO) Min Def/Max Def: 0", F);

        if (u.Invent.CascoEqpObjIndex > 0)
        {
            var c = Game.ObjData.Get(u.Invent.CascoEqpObjIndex);
            ServerPackets.ConsoleMsg(conn, $"(CABEZA) Min Def/Max Def: {c.MinDef}/{c.MaxDef}", F);
        }
        else ServerPackets.ConsoleMsg(conn, "(CABEZA) Min Def/Max Def: 0", F);

        if (u.GuildIndex > 0)
        {
            var g = Game.GuildManager.GetByNumber(u.GuildIndex);
            if (g != null)
            {
                ServerPackets.ConsoleMsg(conn, "Clan: " + g.Name, F);
                if (string.Equals(g.Leader, u.Name, StringComparison.OrdinalIgnoreCase))
                    ServerPackets.ConsoleMsg(conn, "Status: Lider", F);
            }
        }

        var ts = DateTime.Now - u.LogOnTime;
        ServerPackets.ConsoleMsg(conn, $"Logeado hace: {ts.Hours}:{ts.Minutes}:{ts.Seconds}", F);
        ServerPackets.ConsoleMsg(conn, $"Oro: {st.GLD}  Posicion: {u.Pos.X},{u.Pos.Y} en mapa {u.Pos.Map}", F);
        ServerPackets.ConsoleMsg(conn, $"Dados: {st.UserAtributos[1]}, {st.UserAtributos[2]}, {st.UserAtributos[3]}, {st.UserAtributos[4]}, {st.UserAtributos[5]}", F);
    }

    /// <summary>HandleRequestMiniStats. Cable: solo Byte(id). Responde MiniStats.</summary>
    private static void HandleRequestMiniStats(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) ServerPackets.MiniStats(conn, u);
    }

    /// <summary>HandleOnline. Cable: solo Byte(id). Responde con cantidad de online.</summary>
    private static void HandleOnline(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        int n = Game.UserListManager.OnlineCount();
        ServerPackets.ConsoleMsg(conn, $"Usuarios online: {n}", 3); // FONTTYPE_INFO
    }

    /// <summary>HandlePickUp. Cable: solo Byte(id). Levanta el objeto del tile del PJ.</summary>
    private static void HandlePickUp(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Inventory.PickUp(conn.UserIndex);
    }

    /// <summary>HandleDrop. Cable: Byte(id) + Byte(Slot) + Long(Amount).</summary>
    private static void HandleDrop(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 6) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        int amount = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Inventory.Drop(conn.UserIndex, slot, amount);
    }

    /// <summary>HandleEquipItem. Cable: Byte(id) + Byte(Slot) + Byte(autopot) + Byte(token).</summary>
    private static void HandleEquipItem(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        b.ReadByte();                  // autopot (ignorado)
        b.ReadByte();                  // token (ignorado)
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Inventory.EquipItem(conn.UserIndex, slot);
    }

    /// <summary>HandleUseItem. Cable: Byte(id) + Byte(Slot) + Byte(autopot) + Byte(token).</summary>
    private static void HandleUseItem(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        byte esAutoPot = b.ReadByte(); // 1 = AutoPot
        byte token = b.ReadByte();     // 97 = token secreto del autopot
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;

        // AutoPot legítimo (token correcto): bypass de la detección de patrón y del intervalo
        // GolpeUsar de pociones; solo lo frena el rate-limit propio del autopot (10/seg).
        if (esAutoPot == 1 && token == 97)
        {
            if (!Game.AntiCheat.VerificarLimitePaquetes(conn.UserIndex, true)) return;
            if (slot >= 1 && slot <= Game.Constants.MAX_INVENTORY_SLOTS && u.Invent.Object[slot].ObjIndex > 0)
                Game.Inventory.UseItem(conn.UserIndex, slot, esAutoPot: true);
            return;
        }

        // Uso manual: rate-limit + detección de autoclicker (AntiAutoClicker.bas) antes de usar.
        if (slot >= 1 && slot <= Game.Constants.MAX_INVENTORY_SLOTS && u.Invent.Object[slot].ObjIndex == 0) return;
        if (!Game.AntiCheat.VerificarLimitePaquetes(conn.UserIndex, false)) return;
        if (!Game.AntiCheat.PuedeUsarItem(conn.UserIndex, false)) return;

        Game.Inventory.UseItem(conn.UserIndex, slot);
    }

    /// <summary>HandleAttack. Cable: solo Byte(id). Golpea el tile de enfrente.</summary>
    private static void HandleAttack(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged && u.flags.Muerto == 0 && !u.flags.TorneoCongelado) Game.Combat.UsuarioAtaca(conn.UserIndex);
    }

    /// <summary>HandleCastSpell. Cable: Byte(id) + Byte(slot). Selecciona el hechizo a lanzar.</summary>
    private static void HandleCastSpell(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged && !u.flags.TorneoCongelado) Game.Combat.CastSpell(conn.UserIndex, slot);
    }

    /// <summary>
    /// HandleMoveSpell. Cable Godot: Byte(id) + Byte(slotFrom) + Byte(slotTo).
    /// (El VB6 original mandaba Boolean dir + Byte slot; el cliente Godot manda los dos
    /// slots explícitos.) Intercambia los hechizos de ambos slots, sigue al hechizo
    /// seleccionado (SpellPendiente) y reenvía ChangeSpellSlot de los dos slots — equiv.
    /// a DesplazarHechizo (modHechizos.bas:2294).
    /// </summary>
    /// HandleSpellInfo. Cable Godot: Byte(id) + Byte(slot). Imprime en consola la info del
    /// hechizo en ese slot (nombre, descripción, maná, stamina, skill mín., palabras mágicas).
    private static void HandleSpellInfo(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        if (slot < 1 || slot > Game.Constants.MAXUSERHECHIZOS) return;

        short hIndex = u.Stats.UserHechizos[slot];
        if (hIndex <= 0) { ServerPackets.ConsoleMsg(conn, "Ese hueco no tiene ningún hechizo.", 4); return; }

        var sp = Game.SpellData.Get(hIndex);
        const byte FONT_INFO = 4, FONT_CITA = 2;

        ServerPackets.ConsoleMsg(conn, "%%%%%%%%%%%%%%%% INFORMACIÓN DEL HECHIZO %%%%%%%%%%%%%%%%", FONT_CITA);
        ServerPackets.ConsoleMsg(conn, "Nombre: " + (string.IsNullOrEmpty(sp.Nombre) ? "(desconocido)" : sp.Nombre), FONT_INFO);
        if (!string.IsNullOrEmpty(sp.Desc))
            ServerPackets.ConsoleMsg(conn, "Descripción: " + sp.Desc, FONT_INFO);
        if (!string.IsNullOrEmpty(sp.PalabrasMagicas))
            ServerPackets.ConsoleMsg(conn, "Palabras mágicas: " + sp.PalabrasMagicas, FONT_INFO);
        ServerPackets.ConsoleMsg(conn, $"Maná: {sp.ManaRequerido}   Stamina: {sp.StaRequerido}   Skill mín.: {sp.MinSkill}", FONT_INFO);
        ServerPackets.ConsoleMsg(conn, "%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%", FONT_CITA);
    }

    private static void HandleMoveSpell(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte from = b.ReadByte();
        byte to = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;

        const int MAXUSERHECHIZOS = Game.Constants.MAXUSERHECHIZOS;
        if (from < 1 || from > MAXUSERHECHIZOS) return;
        if (to < 1 || to > MAXUSERHECHIZOS) return;
        if (from == to) return;

        // Intercambio de los dos slots (DesplazarHechizo: TempHechizo swap).
        (u.Stats.UserHechizos[from], u.Stats.UserHechizos[to]) =
            (u.Stats.UserHechizos[to], u.Stats.UserHechizos[from]);

        // El hechizo seleccionado pendiente sigue al movimiento (flags.Hechizo en VB6).
        if (u.SpellPendiente == from) u.SpellPendiente = to;
        else if (u.SpellPendiente == to) u.SpellPendiente = from;

        // Reenvío ambos slots para que el cliente Godot quede sincronizado.
        short hFrom = u.Stats.UserHechizos[from];
        short hTo = u.Stats.UserHechizos[to];
        ServerPackets.ChangeSpellSlot(conn, from, hFrom, hFrom > 0 ? Game.SpellData.GetName(hFrom) : "(Vacio)");
        ServerPackets.ChangeSpellSlot(conn, to, hTo, hTo > 0 ? Game.SpellData.GetName(hTo) : "(Vacio)");
    }

    /// <summary>
    /// HandleWorkLeftClick. Cable: Byte(id) + Byte(X) + Byte(Y) + Byte(Skill).
    /// Si Skill = Magia(8), lanza el hechizo pendiente sobre (X,Y).
    /// </summary>
    private static void HandleWorkLeftClick(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte x = b.ReadByte();
        byte y = b.ReadByte();
        byte skill = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged) return;
        if (u.flags.Muerto == 1 || u.flags.Descansar != 0 || u.flags.Meditando) return; // VB6 gate
        const byte Robar = 14, Magia = 8, FundirMetal = 88, Domar = 12, ArmasArrojadizas = 5, Proyectiles = 6;

        // Combate a distancia: el propio AtaqueADistancia llama LookatTile (Commerce.LeftClick) tras
        // validar arma/munición. Para el resto, fijamos el target del tile acá (VB6: cada case llama LookatTile).
        if (skill == Proyectiles)      { Game.Combat.AtaqueADistancia(conn.UserIndex, x, y, arrojadiza: false); return; }
        if (skill == ArmasArrojadizas) { Game.Combat.AtaqueADistancia(conn.UserIndex, x, y, arrojadiza: true);  return; }

        Game.Commerce.LeftClick(conn.UserIndex, x, y);

        if (skill == Magia) Game.Combat.LanzarHechizoEn(conn.UserIndex, x, y);
        else if (skill == FundirMetal) Game.Work.FundirMetal(conn.UserIndex, x, y);
        else if (skill == Robar) Game.Work.DoRobarEnTile(conn.UserIndex, x, y);
        else if (skill == Domar) Game.Work.DoDomarEnTile(conn.UserIndex, x, y);
        // pesca/mineria/talar NO se procesan en WorkLeftClick en este server (VB6): el trabajo real va por
        // doble-click sobre el recurso con la herramienta equipada → Accion → DoTrabajar (1:1). Ver Work.cs.
    }

    /// <summary>HandleLeftClick. Cable: Byte(id) + Byte(X) + Byte(Y). Selecciona NPC objetivo.</summary>
    private static void HandleLeftClick(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte x = b.ReadByte();
        byte y = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Commerce.LeftClick(conn.UserIndex, x, y);
    }

    /// <summary>HandleCraft*. Cable: Byte(id) + Integer(itemIndex) + Integer(cantidad). Fabrica un item.</summary>
    private static void HandleCraft(Connection conn, Game.Crafting.CraftType tipo)
    {
        var b = conn.IncomingData;
        if (b.Length < 5) throw new NotEnoughDataException();
        b.ReadByte();
        short item = b.ReadInteger();
        short cant = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Crafting.Craft(conn.UserIndex, tipo, item, cant);
    }

    /// <summary>
    /// HandleMeditate. Cable: solo Byte(id). Togglea meditar (Protocol.bas:7007).
    /// Si empieza, marca tInicioMeditar; el GameTimer regenera maná tras 2000ms.
    /// </summary>
    private static void HandleMeditate(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged || u.flags.Muerto == 1) return;

        ServerPackets.MeditateToggle(conn);
        u.flags.Meditando = !u.flags.Meditando;
        // Partícula de meditación por nivel/facción (ParticleToLevel), difundida al área:
        //   inicio → time=-1 (loops infinitos), remove=false; fin → time=0, remove=true.
        int particula = Game.Facciones.ParticleToLevel(u);
        if (u.flags.Meditando)
        {
            u._tInicioMeditar = Environment.TickCount64;
            ServerPackets.ConsoleMsg(conn, "Comienzas a meditar.", 3);
            BroadcastEfectoCharParticula(u, (short)particula, -1f, false);
        }
        else
        {
            ServerPackets.ConsoleMsg(conn, "Dejas de meditar.", 3);
            BroadcastEfectoCharParticula(u, (short)particula, 0f, true);
        }
    }

    /// <summary>Difunde EfectoCharParticula a todos los del mapa del usuario (equiv. SendData ToPCArea).</summary>
    private static void BroadcastEfectoCharParticula(Game.User u, short particula, float time, bool remove)
    {
        for (int i = 1; i <= Game.UserListManager.LastUser; i++)
        {
            var o = Game.UserListManager.UserList[i];
            if (o?.flags.UserLogged == true && o.Conn != null && o.Pos.Map == u.Pos.Map)
                ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, particula, time, remove);
        }
    }

    /// <summary>
    /// HandleDoubleClick. Cable: Byte(id) + Byte(X) + Byte(Y). Despacha Accion() según el
    /// NPCType del NPC en ese tile (banco/comercio/revivir/etc). Equivale a Accion (Acciones.bas).
    /// </summary>
    private static void HandleDoubleClick(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte x = b.ReadByte();
        byte y = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged)
            Game.Accion.DoubleClick(conn.UserIndex, u.Pos.Map, x, y);  // FASE 1: Accion() completa
    }

    /// <summary>HandleCommerceStart. Cable: solo Byte(id). Abre comercio con el NPC objetivo.</summary>
    private static void HandleCommerceStart(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Commerce.CommerceStart(conn.UserIndex);
    }

    /// <summary>HandleCommerceEnd. Cable: solo Byte(id).</summary>
    private static void HandleCommerceEnd(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Commerce.CommerceEnd(conn.UserIndex);
    }

    /// <summary>HandleCommerceBuy. Cable: Byte(id) + Byte(Slot) + Integer(Amount).</summary>
    private static void HandleCommerceBuy(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        int amount = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Commerce.CommerceBuy(conn.UserIndex, slot, amount);
    }

    /// <summary>HandleCommerceSell. Cable: Byte(id) + Byte(Slot) + Integer(Amount).</summary>
    private static void HandleCommerceSell(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        int amount = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Commerce.CommerceSell(conn.UserIndex, slot, amount);
    }

    /// <summary>HandleBankStart. Cable: solo Byte(id). Abre la bóveda con el NPC objetivo.</summary>
    private static void HandleBankStart(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Bank.BankStart(conn.UserIndex);
    }

    /// <summary>HandleBankEnd. Cable: solo Byte(id).</summary>
    private static void HandleBankEnd(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Bank.BankEnd(conn.UserIndex);
    }

    /// <summary>HandleBankDeposit. Cable: Byte(id) + Byte(Slot) + Integer(Amount).</summary>
    private static void HandleBankDeposit(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        int amount = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Bank.Deposit(conn.UserIndex, slot, amount);
    }

    /// <summary>HandleBankExtractItem. Cable: Byte(id) + Byte(Slot) + Integer(Amount).</summary>
    private static void HandleBankExtractItem(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte();                  // id
        byte slot = b.ReadByte();
        int amount = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Bank.Extract(conn.UserIndex, slot, amount);
    }

    /// <summary>HandleBankDepositGold. Cable: Byte(id) + Long(amount).</summary>
    private static void HandleBankDepositGold(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 5) throw new NotEnoughDataException();
        b.ReadByte(); int amount = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Bank.DepositGold(conn.UserIndex, amount);
    }

    /// <summary>HandleBankExtractGold. Cable: Byte(id) + Long(amount).</summary>
    private static void HandleBankExtractGold(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 5) throw new NotEnoughDataException();
        b.ReadByte(); int amount = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Bank.ExtractGold(conn.UserIndex, amount);
    }

    /// <summary>HandlePartyCreate. Cable: solo Byte(id).</summary>
    private static void HandlePartyCreate(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Create(conn.UserIndex);
    }

    /// <summary>HandlePartyJoin. Cable: Byte(id) + Integer(CharIndex).</summary>
    private static void HandlePartyJoin(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte(); short ci = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Join(conn.UserIndex, ci);
    }

    /// <summary>HandlePartyLeave. Cable: solo Byte(id).</summary>
    private static void HandlePartyLeave(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Leave(conn.UserIndex);
    }

    /// <summary>HandlePartyMessage. Cable: Byte(id) + UnicodeString(msg).</summary>
    private static void HandlePartyMessage(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte(); string msg = b.ReadUnicodeString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Message(conn.UserIndex, msg);
    }

    /// <summary>HandlePartyAccept. Cable: solo Byte(id). Acepta la invitación pendiente.</summary>
    private static void HandlePartyAccept(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Accept(conn.UserIndex);
    }

    /// <summary>HandlePartyReject. Cable: solo Byte(id).</summary>
    private static void HandlePartyReject(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Reject(conn.UserIndex);
    }

    /// <summary>HandlePartyKick. Cable: Byte(id) + ASCIIString(nombre) (el cliente Godot manda ASCII).</summary>
    private static void HandlePartyKick(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte(); string name = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Kick(conn.UserIndex, name);
    }

    /// <summary>HandlePartyOnline. Cable: solo Byte(id).</summary>
    private static void HandlePartyOnline(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.Online(conn.UserIndex);
    }

    /// <summary>HandleGuildMessage. Cable: Byte(id) + ASCIIString(msg).</summary>
    private static void HandleGuildMessage(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte(); string msg = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.PartySystem.GuildMessage(conn.UserIndex, msg);
    }

    /// <summary>
    /// HandleWork. Cable: Byte(id) + Byte(Skill). Activa el modo trabajo:
    /// devuelve WorkRequestTarget para que el cliente pida el destino del clic.
    /// </summary>
    private static void HandleWork(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte(); byte skill = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged || u.flags.Muerto == 1) return;

        const byte Ocultarse = 11;
        // VB6 HandleWork: Ocultarse es instantáneo; el resto pide target con el cursor.
        if (skill == Ocultarse)
            Game.Work.DoOcultarse(conn.UserIndex);
        else
            ServerPackets.WorkRequestTarget(conn, skill);
    }

    /// <summary>HandleUserCommerceStart. Cable: solo Byte(id).</summary>
    private static void HandleUserCommerceStart(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.UserTrade.Start(conn.UserIndex);
    }

    /// <summary>HandleUserCommerceOfferGold. Cable: Byte(id) + Long(amount).</summary>
    private static void HandleUserCommerceOfferGold(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 5) throw new NotEnoughDataException();
        b.ReadByte(); int amount = b.ReadLong();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.UserTrade.OfferGold(conn.UserIndex, amount);
    }

    /// <summary>HandleUserCommerceOfferItem. Cable: Byte(id) + Byte(Slot) + Integer(Amount).</summary>
    private static void HandleUserCommerceOfferItem(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 4) throw new NotEnoughDataException();
        b.ReadByte(); byte slot = b.ReadByte(); int amount = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.UserTrade.OfferItem(conn.UserIndex, slot, amount);
    }

    /// <summary>HandleUserCommerceConfirm. Cable: solo Byte(id).</summary>
    private static void HandleUserCommerceConfirm(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.UserTrade.Confirm(conn.UserIndex);
    }

    /// <summary>HandleUserCommerceCancel. Cable: solo Byte(id).</summary>
    private static void HandleUserCommerceCancel(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.UserTrade.Cancel(conn.UserIndex);
    }

    /// <summary>HandleUserCommerceReqUpdate. Cable: solo Byte(id).</summary>
    private static void HandleUserCommerceReqUpdate(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.UserTrade.RequestUpdate(conn.UserIndex);
    }

    /// <summary>HandlePackets_Correo. Cable: Byte(id) + Byte(action) + Byte(slot).</summary>
    private static void HandlePacketsCorreo(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte(); byte action = b.ReadByte(); byte slot = b.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Mail.Packets(conn.UserIndex, action, slot);
    }

    /// <summary>HandleEnviarCorreo. Cable: Byte(id) + ASCIIString(dest) + ASCIIString(msg) + Int(obj) + Int(cant).</summary>
    private static void HandleEnviarCorreo(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 6) throw new NotEnoughDataException();
        b.ReadByte();
        string dest = b.ReadASCIIString();
        string msg = b.ReadASCIIString();
        short obj = b.ReadInteger();
        int cant = b.ReadInteger();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Mail.Enviar(conn.UserIndex, dest, msg, obj, cant);
    }

    /// <summary>HandleResucitate. Cable: solo Byte(id). Revive al jugador muerto.</summary>
    private static void HandleResucitate(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged) Game.Combat.Resucitar(conn.UserIndex);
    }

    // ---- Handlers de packets que el cliente manda al entrar/interactuar ----

    /// <summary>HandleTyping. Cable: Byte(id) + Byte(typing). El cliente avisa que está escribiendo.</summary>
    private static void HandleTyping(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 2) throw new NotEnoughDataException();
        b.ReadByte(); b.ReadByte();   // id + flag typing (sin lógica por ahora)
        // TODO: difundir CharTyping(163) a los del área para mostrar el indicador.
    }

    /// <summary>SaveMacrosConfig. Cable: Byte(id) + ASCIIString(blob). Persiste el blob en &lt;NOMBRE&gt;.mac.</summary>
    private static void HandleSaveMacros(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 3) throw new NotEnoughDataException();
        b.ReadByte();
        string blob = b.ReadASCIIString();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (u.flags.UserLogged && !string.IsNullOrEmpty(u.Name)) Game.Macros.Save(u.Name, blob);
    }

    /// <summary>RequestMacrosConfig. Cable: solo Byte(id). Devuelve el blob de macros guardado (MacrosConfig 164).</summary>
    private static void HandleRequestMacros(Connection conn)
    {
        conn.IncomingData.ReadByte();
        var u = Game.UserListManager.UserList[conn.UserIndex];
        if (!u.flags.UserLogged || string.IsNullOrEmpty(u.Name)) return;
        ServerPackets.MacrosConfig(conn, Game.Macros.Load(u.Name));
    }

    /// <summary>HandleWhisper. Cable: Byte(id) + ASCIIString(nombre) + ASCIIString(chat).</summary>
    /// <summary>
    /// HandleWhisper (Protocol.bas:1983): mensaje privado a un jugador por nombre. Va por consola
    /// a ambos (font 22) y, si el destino está en el área de visión, también ChatOverHead (modo 4).
    /// </summary>
    private static void HandleWhisper(Connection conn)
    {
        var b = conn.IncomingData;
        if (b.Length < 5) throw new NotEnoughDataException();
        b.ReadByte();
        string nombre = b.ReadASCIIString();
        string chat = b.ReadASCIIString();
        var u = GU(conn);

        int ti = Game.UserListManager.NameIndex(nombre);
        if (ti <= 0) { ServerPackets.ConsoleMsg(conn, "Usuario offline o inexistente.", 1); return; }
        if (ti == conn.UserIndex || string.IsNullOrEmpty(chat)) return;
        var t = Game.UserListManager.UserList[ti];

        string msg = $"[{u.Name}] {chat}";
        ServerPackets.ConsoleMsg(conn, msg, 22);
        if (t.Conn != null) ServerPackets.ConsoleMsg(t.Conn, msg, 22);

        if (t.Pos.Map == u.Pos.Map && Math.Abs(t.Pos.X - u.Pos.X) <= 8 && Math.Abs(t.Pos.Y - u.Pos.Y) <= 8)
        {
            ServerPackets.ChatOverHead(conn, chat, u.Char.CharIndex, 4);
            if (t.Conn != null) ServerPackets.ChatOverHead(t.Conn, chat, u.Char.CharIndex, 4);
        }
    }

    /// <summary>HandlePing. Cable: solo Byte(id). Responde Pong.</summary>
    private static void HandlePing(Connection conn)
    {
        conn.IncomingData.ReadByte();  // id
        ServerPackets.Pong(conn);      // WritePong: Byte(Pong)
    }
}
