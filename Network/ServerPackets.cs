namespace ServidorCS.Network;

/// <summary>
/// Constructores de packets de SALIDA (server → cliente), portados 1:1 desde las
/// rutinas Write* de Protocol.bas (VB6). Verificado leyendo cada Sub original.
///
/// ⚠️ x1: en TODOS estos senders el ID de packet se escribe con WriteByte (1 byte).
///    Los campos siguen EXACTAMENTE el orden y ancho del VB6. Un cambio de orden
///    o de ancho rompe el parseo en el cliente Godot.
///
/// Cada método encola en conn.OutgoingData; el flush lo hace Connection.FlushAsync.
/// </summary>
public static class ServerPackets
{
    // conn puede ser null durante el cierre (CloseUser desequipa montura/ítem mágico y eso
    // intenta notificar al propio usuario que ya se desconectó): en ese caso se descarta.
    // TODOS los paquetes S->C pasan por acá (119 llamadas), y de eso se aprovecha el modo
    // espía (ver Espia.cs), que necesita las dos mitades:
    //   • si un Dios está mirando a este usuario, se le encola la MISMA copia de bytes;
    //   • si este usuario ES un Dios espiando, sus propios paquetes se DESCARTAN (salvo los
    //     de texto) para que su HP/hambre/área no le pisen la pantalla del espiado.
    // Es seguro mandar el mismo ByteQueue dos veces porque EnqueueOutgoing hace ToArray().
    // Sin ningún espejo activo, Rutear() sale por un bool volátil: cero costo por paquete.
    private static void Send(Connection conn, ByteQueue p)
    {
        if (conn == null) return;
        bool silenciar = Game.Espia.Rutear(conn, p, out var espia);
        if (!silenciar) conn.EnqueueOutgoing(p);
        espia?.EnqueueOutgoing(p);
    }

    /// <summary>WriteLoggedSuccessful: Byte(LoggedSuccessful). Primer packet del login OK.</summary>
    public static void LoggedSuccessful(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.LoggedSuccessful);
        Send(conn, p);
    }

    /// <summary>
    /// WriteLoggedMessage: Byte(Logged) + Byte(redundance).
    /// El 'redundance' (VB6: RandomNumber(15,250)) es la nueva clave XOR de sesión:
    /// el cliente la adopta para cifrar lo que envía a partir de aquí.
    /// </summary>
    public static void Logged(Connection conn, byte redundance)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.Logged);
        p.WriteByte(redundance);
        Send(conn, p);
    }

    /// <summary>
    /// WriteCharacterCreate (PrepareMessageCharacterCreate). Orden EXACTO del VB6:
    /// Byte(id) Int(CharIndex) Int(body) Int(Head) Byte(heading) Byte(X) Byte(Y)
    /// Int(weapon) Int(shield) Int(helmet) Int(FX) Int(FXLoops) ASCIIString(Name)
    /// Byte(Privileges) Byte(Donador) Byte(ParticulaFx) Byte(Arma_Aura) Byte(Body_Aura)
    /// Byte(Escudo_Aura) Byte(Head_Aura) Byte(Otra_Aura) Byte(Anillo_Aura)
    /// Boolean(IsTopGold) Int(weaponObjIndex).
    /// </summary>
    public static void CharacterCreate(Connection conn, short charIndex, short body, short head,
        byte heading, int x, int y, short weapon, short shield, short helmet, short fx, short fxLoops,
        string name, byte privileges, byte donador, byte particulaFx,
        byte armaAura, byte bodyAura, byte escudoAura, byte headAura, byte otraAura, byte anilloAura,
        bool isTopGold, short weaponObjIndex)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharacterCreate);
        p.WriteInteger(charIndex);
        p.WriteInteger(body);
        p.WriteInteger(head);
        p.WriteByte(heading);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        p.WriteInteger(weapon);
        p.WriteInteger(shield);
        p.WriteInteger(helmet);
        p.WriteInteger(fx);
        p.WriteInteger(fxLoops);
        p.WriteASCIIString(name);
        p.WriteByte(privileges);
        p.WriteByte(donador);
        p.WriteByte(particulaFx);
        p.WriteByte(armaAura);
        p.WriteByte(bodyAura);
        p.WriteByte(escudoAura);
        p.WriteByte(headAura);
        p.WriteByte(otraAura);
        p.WriteByte(anilloAura);
        p.WriteBoolean(isTopGold);
        p.WriteInteger(weaponObjIndex);
        Send(conn, p);
    }

    /// <summary>WriteUpdateTagAndStatus (PrepareMessageUpdateTagAndStatus). Orden VB6:
    /// Byte(id) Int(CharIndex) ASCIIString(Tag) Byte(Status) Byte(Donador).
    /// El Tag es "Nombre &lt;Clan&gt;" (o solo "Nombre"); el cliente extrae el clan.</summary>
    public static void UpdateTagAndStatus(Connection conn, short charIndex, string tag, byte status, byte donador)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateTagAndStatus);
        p.WriteInteger(charIndex);
        p.WriteASCIIString(tag);
        p.WriteByte(status);
        p.WriteByte(donador);
        Send(conn, p);
    }

    /// <summary>WriteUserIndexInServer: Byte(ID) + Integer(UserIndex).</summary>
    public static void UserIndexInServer(Connection conn, short userIndex)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UserIndexInServer);
        p.WriteInteger(userIndex);
        Send(conn, p);
    }

    /// <summary>WriteUserCharIndexInServer: Byte(ID) + Integer(CharIndex).</summary>
    public static void UserCharIndexInServer(Connection conn, short charIndex)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UserCharIndexInServer);
        p.WriteInteger(charIndex);
        Send(conn, p);
    }

    /// <summary>WriteChangeMap: Byte(ID) + Integer(Map) + Integer(MapVersion).</summary>
    public static void ChangeMap(Connection conn, short map, short mapVersion)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChangeMap);
        p.WriteInteger(map);
        p.WriteInteger(mapVersion);
        Send(conn, p);
    }

    /// <summary>
    /// SeamlessCross (mundo continuo): Byte(ID) + Integer(nuevoMapa) + Integer(globalX) + Integer(globalY).
    /// Cruce de borde SIN teardown: el cliente actualiza current_map y su posición pero NO limpia el
    /// char_list ni recarga desde cero (a diferencia de ChangeMap). La continuidad la sostiene el
    /// re-anclado de coords globales (4a). Solo se envía con el mundo continuo activo.
    /// </summary>
    public static void SeamlessCross(Connection conn, short map, int gx, int gy)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SeamlessCross);
        p.WriteInteger(map);
        p.WriteInteger((short)gx);
        p.WriteInteger((short)gy);
        Send(conn, p);
    }

    /// <summary>
    /// WriteConsoleMsg (PrepareMessageConsoleMsg de VB6).
    /// Formato EXACTO: Byte(ID) + ASCIIString(chat) + Byte(FontIndex).
    /// OJO: el texto va ANTES del font, y el font se escribe como Byte (1 byte)
    /// aunque el parámetro VB6 sea Integer.
    /// </summary>
    public static void ConsoleMsg(Connection conn, string chat, byte fontIndex)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ConsoleMsg);
        p.WriteASCIIString(chat);
        p.WriteByte(fontIndex);
        Send(conn, p);
    }

    /// <summary>
    /// WriteLocaleMsg (PrepareMessageLocaleMsg de VB6, Protocol.bas:20453).
    /// Formato EXACTO: Byte(ID) + Integer(id) + ASCIIString(chat) + Byte(Modo) + Byte(fuente).
    /// </summary>
    public static void LocaleMsg(Connection conn, int id, string chat = "", byte modo = 0, byte fuente = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.LocaleMsg);
        p.WriteInteger((short)id);
        p.WriteASCIIString(chat);
        p.WriteByte(modo);
        p.WriteByte(fuente);
        Send(conn, p);
    }

    /// <summary>
    /// WriteShowMessageBox (PrepareMessageShowMessageBox de VB6).
    /// Formato EXACTO del cable: Byte(ID) + ASCIIString(message) + Boolean(EsPregunta)
    /// + Byte(Accion). Ver memoria [[show_message_box_accion]].
    /// </summary>
    public static void ShowMessageBox(Connection conn, string message, bool esPregunta = false, byte accion = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ShowMessageBox);
        p.WriteASCIIString(message);
        p.WriteBoolean(esPregunta);
        p.WriteByte(accion);
        Send(conn, p);
    }

    /// <summary>
    /// Variante con código de mensaje predefinido del cliente. En VB6
    /// WriteShowMessageBox(UserIndex, 64) convierte el número a string "64";
    /// el cliente lo interpreta como mensaje predefinido (64=nombre inválido,
    /// 38=personaje no existe, 47=cuenta ya conectada, 83=error de lectura...).
    /// </summary>
    public static void ShowMessageBoxCode(Connection conn, int codigo)
        => ShowMessageBox(conn, codigo.ToString());

    /// <summary>Respuesta al chequeo de nombre libre de la pantalla Crear Personaje (NUEVO, no VB6).
    /// Cable: Byte(id) + Byte(disponible 1/0) + ASCIIString(msg).</summary>
    public static void NombreCheckResult(Connection conn, byte disponible, string msg)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.NombreCheckResult);
        p.WriteByte(disponible);
        p.WriteASCIIString(msg);
        Send(conn, p);
    }

    /// <summary>Datos resumidos de un personaje para la pantalla de selección (AddPj).</summary>
    public struct AccountChar
    {
        public string Name;
        public short Head, Body, Casco, Weapon, Shield, Mapa;
        public byte Nivel, Clase, Color;
        public bool GameMaster;
        public string LastSeen;
        // Auras de los objetos equipados (obj.dat "Aura"), para dibujarlas en la
        // tarjeta de selección de personaje. 0 = sin aura.
        public byte ArmaAura, BodyAura, EscudoAura, HeadAura, AnilloAura;
    }

    /// <summary>
    /// WriteAddPj: lista de personajes de la cuenta. Formato 1:1:
    /// Byte(id) + ASCIIString(cuenta) + Byte(N) + N×[ ASCIIString(Name) Int(Head) Int(Body)
    /// Int(Casco) Int(Weapon) Int(Shield) Byte(Nivel) Byte(Clase) Int(Mapa) Byte(Color)
    /// Boolean(GameMaster) ASCIIString(LastSeen)
    /// Byte(ArmaAura) Byte(BodyAura) Byte(EscudoAura) Byte(HeadAura) Byte(AnilloAura) ].
    /// </summary>
    public static void AddPj(Connection conn, string cuenta, AccountChar[] chars)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AddPJ);
        p.WriteASCIIString(cuenta);
        p.WriteByte((byte)chars.Length);
        foreach (var c in chars)
        {
            p.WriteASCIIString(c.Name);
            p.WriteInteger(c.Head);
            p.WriteInteger(c.Body);
            p.WriteInteger(c.Casco);
            p.WriteInteger(c.Weapon);
            p.WriteInteger(c.Shield);
            p.WriteByte(c.Nivel);
            p.WriteByte(c.Clase);
            p.WriteInteger(c.Mapa);
            p.WriteByte(c.Color);
            p.WriteBoolean(c.GameMaster);
            p.WriteASCIIString(c.LastSeen ?? "");
            p.WriteByte(c.ArmaAura);
            p.WriteByte(c.BodyAura);
            p.WriteByte(c.EscudoAura);
            p.WriteByte(c.HeadAura);
            p.WriteByte(c.AnilloAura);
        }
        Send(conn, p);
    }

    /// <summary>WriteMacrosConfig (id 164): Byte(id) + ASCIIString(blob). Devuelve el blob de macros del PJ.</summary>
    public static void MacrosConfig(Connection conn, string blob)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.MacrosConfig);
        p.WriteASCIIString(blob ?? "");
        Send(conn, p);
    }

    /// <summary>WriteAbrirFormularios: Byte(id) + Byte(Formulario). 1 = pantalla de cuenta.</summary>
    public static void AbrirFormularios(Connection conn, byte formulario)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AbrirFormularios);
        p.WriteByte(formulario);
        Send(conn, p);
    }

    // ============================ LISTAS DE CRAFTEO ============================
    // Formato (1:1 con el cliente protocol_incoming): Byte(id) + Integer(count) + por item los campos.

    /// <summary>Lista de items de herrería (armas/armaduras/cascos/escudos). Item: obj, LingH, LingP, LingO, SkHerreria, craftIdx(=obj).</summary>
    public static void BlacksmithList(Connection conn, ServerPacketID id, List<short> objs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)id);
        p.WriteInteger((short)objs.Count);
        foreach (var oi in objs)
        {
            var od = Game.ObjData.Get(oi);
            p.WriteInteger(oi);
            p.WriteInteger((short)od.LingH);
            p.WriteInteger((short)od.LingP);
            p.WriteInteger((short)od.LingO);
            p.WriteInteger((short)od.SkHerreria);
            p.WriteInteger(oi); // craft_idx
        }
        Send(conn, p);
    }

    /// <summary>Lista de carpintería. Item: obj, Madera, craftIdx(=obj).</summary>
    public static void CarpenterList(Connection conn, List<short> objs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CarpenterObjects);
        p.WriteInteger((short)objs.Count);
        foreach (var oi in objs)
        {
            var od = Game.ObjData.Get(oi);
            p.WriteInteger(oi);
            p.WriteInteger((short)od.Madera);
            p.WriteInteger(oi);
        }
        Send(conn, p);
    }

    /// <summary>Lista de sastrería. Item: obj, PielLobo, PielOsoPardo, PielOsoPolar, craftIdx(=obj).</summary>
    public static void SastreList(Connection conn, List<short> objs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SastreObjects);
        p.WriteInteger((short)objs.Count);
        foreach (var oi in objs)
        {
            var od = Game.ObjData.Get(oi);
            p.WriteInteger(oi);
            p.WriteInteger((short)od.PielLobo);
            p.WriteInteger((short)od.PielOso);
            p.WriteInteger((short)od.PielOsoPolar);
            p.WriteInteger(oi);
        }
        Send(conn, p);
    }

    /// <summary>Lista de alquimia. Item: obj, Raices, craftIdx(=obj).</summary>
    public static void AlquimiaList(Connection conn, List<short> objs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AlquimiaObjects);
        p.WriteInteger((short)objs.Count);
        foreach (var oi in objs)
        {
            var od = Game.ObjData.Get(oi);
            p.WriteInteger(oi);
            p.WriteInteger((short)od.Raices);
            p.WriteInteger(oi);
        }
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageObjectCreate: Byte(id) + Byte(X) + Byte(Y) + Integer(ObjIndex)
    /// + Integer(Amount). Crea un objeto en el suelo.
    /// </summary>
    public static void ObjectCreate(Connection conn, int x, int y, short objIndex, short amount)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ObjectCreate);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        p.WriteInteger(objIndex);
        p.WriteInteger(amount);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageCreateFX: Byte(id) + Int(CharIndex) + Int(FX) + Int(FXLoops).
    /// Muestra un efecto visual (hechizo, etc.) sobre un personaje.
    /// </summary>
    public static void CreateFX(Connection conn, short charIndex, short fx, short fxLoops)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CreateFX);
        p.WriteInteger(charIndex);
        p.WriteInteger(fx);
        p.WriteInteger(fxLoops);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageCreateArrowProjectile (Protocol.bas:17189): Byte(id) + Int(CharOrigen) +
    /// Int(CharDestino) + Int(XOrigen) + Int(YOrigen) + Int(XDestino) + Int(YDestino) + Int(GrhIndex).
    /// Dibuja la flecha/arma arrojadiza animada volando del origen al destino.
    /// </summary>
    public static void CreateArrowProjectile(Connection conn, short charOrigen, short charDestino,
        short xOrigen, short yOrigen, short xDestino, short yDestino, short grhIndex)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CreateArrowProjectile);
        p.WriteInteger(charOrigen);
        p.WriteInteger(charDestino);
        p.WriteInteger(xOrigen);
        p.WriteInteger(yOrigen);
        p.WriteInteger(xDestino);
        p.WriteInteger(yDestino);
        p.WriteInteger(grhIndex);
        Send(conn, p);
    }

    /// <summary>
    /// SpellBeam (NUEVO, no VB6): Byte(id) + Int(CharOrigen) + Int(CharDestino) + Int(XOrigen) +
    /// Int(YOrigen) + Int(XDestino) + Int(YDestino) + Byte(Tipo). El cliente dibuja un arco
    /// eléctrico procedural del lanzador al objetivo al castear. Tipo = paleta (ver ServerPacketID).
    /// </summary>
    public static void SpellBeam(Connection conn, short charOrigen, short charDestino,
        short xOrigen, short yOrigen, short xDestino, short yDestino, byte tipo)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SpellBeam);
        p.WriteInteger(charOrigen);
        p.WriteInteger(charDestino);
        p.WriteInteger(xOrigen);
        p.WriteInteger(yOrigen);
        p.WriteInteger(xDestino);
        p.WriteInteger(yDestino);
        p.WriteByte(tipo);
        Send(conn, p);
    }

    /// <summary>WriteUpdateExp: Byte(id) + Long(Exp). Refresca la experiencia en el HUD.</summary>
    public static void UpdateExp(Connection conn, int exp)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateExp);
        p.WriteLong(exp);
        Send(conn, p);
    }

    /// <summary>WriteUpdateGold: Byte(id) + Long(GLD).</summary>
    public static void UpdateGold(Connection conn, int gld)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateGold);
        p.WriteLong(gld);
        Send(conn, p);
    }

    /// <summary>ScrollBuff: Byte(tipo 1=exp/2=oro) + Byte(mult) + Long(segundos restantes; 0 = terminó).
    /// El cliente lo usa para el chip con cuenta regresiva del HUD.</summary>
    public static void ScrollBuff(Connection conn, byte tipo, byte mult, int segundos)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ScrollBuff);
        p.WriteByte(tipo);
        p.WriteByte(mult);
        p.WriteLong(segundos);
        Send(conn, p);
    }

    /// <summary>WriteLevelUp: Byte(id) + Integer(skillPoints). Avisa subida de nivel.</summary>
    public static void LevelUp(Connection conn, short skillPoints)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.LevelUp);
        p.WriteInteger(skillPoints);
        Send(conn, p);
    }

    /// <summary>WriteUpdateHP: Byte(id) + Integer(MinHP actual). Refresca la vida en el HUD.</summary>
    public static void UpdateHP(Connection conn, short minHP)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateHP);
        p.WriteInteger(minHP);
        Send(conn, p);
    }

    /// <summary>WriteUpdateSta: Byte(id) + Integer(MinSta actual).</summary>
    public static void UpdateSta(Connection conn, short minSta)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateSta);
        p.WriteInteger(minSta);
        Send(conn, p);
    }

    /// <summary>WriteUpdateHungerAndThirst: Byte(id) + Byte(MinAGU) + Byte(MinHam).</summary>
    public static void UpdateHungerAndThirst(Connection conn, Game.User u)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateHungerAndThirst);
        p.WriteByte((byte)u.Stats.MinAGU);
        p.WriteByte((byte)u.Stats.MinHam);
        Send(conn, p);
    }

    /// <summary>WriteUpdateMana: Byte(id) + Integer(MinMAN actual).</summary>
    public static void UpdateMana(Connection conn, short minMana)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateMana);
        p.WriteInteger(minMana);
        Send(conn, p);
    }

    /// <summary>
    /// Mismo paquete que UpdateUserStats pero armado desde un NpcInstance (bot espectado por el
    /// panel, ver Espia.cs): sin esto, el HUD de vida/maná del espectador quedaba con lo último
    /// que tenía SU PROPIO personaje (o en cero) en vez del máximo real del bot que está mirando.
    /// Los campos que un bot no tiene (stamina/oro/nivel/exp/atributos) van con un placeholder
    /// razonable — el hambre/sed se manda lleno para que esas barras no aparezcan en rojo.
    /// </summary>
    public static void UpdateUserStatsNpc(Connection conn, Game.NpcManager.NpcInstance n)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateUserStats);
        p.WriteInteger((short)n.MaxHP);
        p.WriteInteger((short)n.MinHP);
        p.WriteInteger((short)n.MaxMana);
        p.WriteInteger((short)n.MinMana);
        p.WriteInteger(0);   // MaxSta: los bots no tienen stamina
        p.WriteInteger(0);   // MinSta
        p.WriteLong(0);      // GLD
        p.WriteInteger(1);   // ELV: placeholder, los bots no tienen nivel propio
        p.WriteLong(0);      // ELU
        p.WriteLong(0);      // Exp
        p.WriteByte(0);      // Agilidad
        p.WriteByte(0);      // Fuerza
        p.WriteByte(100);    // MinHam
        p.WriteByte(100);    // MaxHam
        p.WriteByte(100);    // MinAGU
        p.WriteByte(100);    // MaxAGU
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageCharacterChange: Byte(id) + Int(CharIndex) + Int(body) + Int(Head)
    /// + Byte(heading) + Int(weapon) + Int(shield) + Int(helmet) + Int(FX) + Int(FXLoops)
    /// + Int(weaponObjIndex). Actualiza la apariencia de un personaje ya visible.
    /// </summary>
    public static void CharacterChange(Connection conn, short charIndex, short body, short head,
        byte heading, short weapon, short shield, short helmet, short fx, short fxLoops, short weaponObjIndex)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharacterChange);
        p.WriteInteger(charIndex);
        p.WriteInteger(body);
        p.WriteInteger(head);
        p.WriteByte(heading);
        p.WriteInteger(weapon);
        p.WriteInteger(shield);
        p.WriteInteger(helmet);
        p.WriteInteger(fx);
        p.WriteInteger(fxLoops);
        p.WriteInteger(weaponObjIndex);
        Send(conn, p);
    }

    /// <summary>PrepareMessagePlayWave: Byte(id) + Integer(wave) + Byte(X) + Byte(Y). Reproduce un sonido.</summary>
    public static void PlayWave(Connection conn, short wave, int x, int y)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PlayWave);
        p.WriteInteger(wave);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        Send(conn, p);
    }

    /// <summary>PrepareMessageObjectDelete: Byte(id) + Byte(X) + Byte(Y). Quita un objeto del suelo.</summary>
    public static void ObjectDelete(Connection conn, int x, int y)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ObjectDelete);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        Send(conn, p);
    }

    /// <summary>WriteCommerceInit: Byte(id) + Byte(compra). Abre la ventana de comercio.
    /// compra=1 el NPC compra al usuario (muestra inventario y botón Vender); 0 = solo vende.</summary>
    public static void CommerceInit(Connection conn, bool compra = true, bool esViajes = false, short moneda = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CommerceInit);
        p.WriteByte((byte)(compra ? 1 : 0));      // 1 = el NPC compra ítems al usuario
        p.WriteByte((byte)(esViajes ? 1 : 0));    // 1 = transportador → abrir form de Viajar
        p.WriteInteger(moneda);                    // 0 = oro; >0 = ObjIndex de la divisa propia del NPC
        Send(conn, p);
    }

    /// <summary>WriteCommerceEnd: Byte(id). Cierra la ventana de comercio.</summary>
    public static void CommerceEnd(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CommerceEnd);
        Send(conn, p);
    }

    /// <summary>
    /// WriteChangeNPCInventorySlot. Formato 1:1: Byte(id) + Byte(Slot) + Integer(Amount)
    /// + Single(Precio) + Integer(ObjIndex) + Byte(PuedeUsar) + Byte(Motivo).
    /// </summary>
    public static void ChangeNPCInventorySlot(Connection conn, byte slot, int amount, float precio, short objIndex,
        byte puedeUsar = 1, byte motivo = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChangeNPCInventorySlot);
        p.WriteByte(slot);
        p.WriteInteger((short)amount);
        p.WriteSingle(precio);
        p.WriteInteger(objIndex);
        p.WriteByte(objIndex > 0 ? puedeUsar : (byte)1);  // PuedeUsar (slot vacío: no marcar)
        p.WriteByte(motivo);                               // Motivo (0 = ninguno)
        Send(conn, p);
    }

    /// <summary>WriteBankInit: Byte(id) + Byte(goliath) + Long(oroBanco) + Byte(itemCount). Abre la bóveda.</summary>
    public static void BankInit(Connection conn, int oroBanco, byte itemCount, byte goliath = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BankInit);
        p.WriteByte(goliath);
        p.WriteLong(oroBanco);
        p.WriteByte(itemCount);
        Send(conn, p);
    }

    /// <summary>WriteBankEnd: Byte(id). Cierra la bóveda.</summary>
    public static void BankEnd(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BankEnd);
        Send(conn, p);
    }

    /// <summary>
    /// WriteChangeBankSlot. Formato 1:1: Byte(id) + Byte(Slot) + Integer(ObjIndex)
    /// + Integer(Amount) + Long(Valor).
    /// </summary>
    public static void ChangeBankSlot(Connection conn, byte slot, short objIndex, int amount, int valor)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChangeBankSlot);
        p.WriteByte(slot);
        p.WriteInteger(objIndex);
        p.WriteInteger((short)amount);
        p.WriteLong(valor);
        Send(conn, p);
    }

    /// <summary>
    /// BankInitPremium (203): Byte(id) + Boolean(desbloqueada). Estado de las 2 bóvedas
    /// premium al abrir la bóveda o justo tras comprarlas. Sale SOLO a clientes con
    /// SoportaBovedaPremium (bit2 de ClientCaps) — al resto no le llega nunca.
    /// </summary>
    public static void BankInitPremium(Connection conn, bool desbloqueada)
    {
        if (conn == null || !conn.SoportaBovedaPremium) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BankInitPremium);
        p.WriteBoolean(desbloqueada);
        Send(conn, p);
    }

    /// <summary>
    /// ChangeBankSlotPremium (204): Byte(id) + Byte(vaultId 1|2) + Byte(Slot) + Integer(ObjIndex)
    /// + Integer(Amount) + Long(Valor). Mismo formato que ChangeBankSlot + el byte de vaultId.
    /// Sale SOLO a clientes con SoportaBovedaPremium.
    /// </summary>
    public static void ChangeBankSlotPremium(Connection conn, byte vaultId, byte slot, short objIndex, int amount, int valor)
    {
        if (conn == null || !conn.SoportaBovedaPremium) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChangeBankSlotPremium);
        p.WriteByte(vaultId);
        p.WriteByte(slot);
        p.WriteInteger(objIndex);
        p.WriteInteger((short)amount);
        p.WriteLong(valor);
        Send(conn, p);
    }

    /// <summary>WriteWorkRequestTarget: Byte(id) + Byte(Skill). Pide al cliente seleccionar destino.</summary>
    public static void WorkRequestTarget(Connection conn, byte skill)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.WorkRequestTarget);
        p.WriteByte(skill);
        Send(conn, p);
    }

    /// <summary>
    /// WriteCorreoList: Byte(id) + Byte(count) + count×[ASCIIString(de) + ASCIIString(msg)
    /// + Byte(leido) + Long(cantidad) + Integer(objIndex)]. Lista de correos del jugador.
    /// </summary>
    public static void CorreoList(Connection conn, System.Collections.Generic.List<Game.Correo> correos)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CorreoList);
        p.WriteByte((byte)correos.Count);
        foreach (var c in correos)
        {
            p.WriteASCIIString(c.Emisor ?? "");
            p.WriteASCIIString(c.Mensaje ?? "");
            p.WriteByte(c.Leida ? (byte)1 : (byte)0);
            p.WriteLong(c.Cantidad);
            p.WriteInteger(c.ObjIndex);
        }
        Send(conn, p);
    }

    /// <summary>WriteEventoExpBonus: Byte(id) + Long(segundosRestantes). Anuncia evento de EXP x2.</summary>
    public static void EventoExpBonus(Connection conn, int segundos)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EventoExpBonus);
        p.WriteLong(segundos);
        Send(conn, p);
    }

    /// <summary>WriteUserCommerceInit: Byte(id) + ASCIIString(nombreOtro). Abre la ventana de trade.</summary>
    public static void UserCommerceInit(Connection conn, string otroNombre)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UserCommerceInit);
        p.WriteASCIIString(otroNombre);
        Send(conn, p);
    }

    /// <summary>WriteUserCommerceEnd: Byte(id). Cierra la ventana de trade.</summary>
    public static void UserCommerceEnd(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UserCommerceEnd);
        Send(conn, p);
    }

    /// <summary>
    /// WriteUserCommerceUpdate: Byte(id) + Byte(side: 0=mío/1=del otro) + Byte(slot 1-5)
    /// + Integer(objIndex) + Long(amount). Actualiza un slot de la oferta.
    /// </summary>
    public static void UserCommerceUpdate(Connection conn, byte side, byte slot, short objIndex, int amount)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UserCommerceUpdate);
        p.WriteByte(side);
        p.WriteByte(slot);
        p.WriteInteger(objIndex);
        p.WriteLong(amount);
        Send(conn, p);
    }

    /// <summary>
    /// SendGuildLeaderInfo (id=75): info del clan para el panel del líder.
    /// Byte(id) + ASCIIString(memberList) + Integer(count) + ASCIIString(name)
    /// + ASCIIString(founder) + ASCIIString(joinRequests) + ASCIIString(news).
    /// Las listas van separadas por '|'.
    /// </summary>
    public static void GuildLeaderInfo(Connection conn, string memberList, short count,
        string name, string founder, string joinRequests, string news)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GuildLeaderInfo);
        p.WriteASCIIString(memberList);
        p.WriteInteger(count);
        p.WriteASCIIString(name);
        p.WriteASCIIString(founder);
        p.WriteASCIIString(joinRequests);
        p.WriteASCIIString(news);
        Send(conn, p);
    }

    /// <summary>
    /// WriteCharMsgStatus (id=100): info de un usuario al hacer clic (LookatTile). El cliente
    /// arma el texto de consola (nombre con color por status, clase, raza, nivel, pareja, desc).
    /// </summary>
    public static void CharMsgStatus(Connection conn, short charIndex, byte status, int vidaPct,
        short st1, byte st2, byte clase, short nivel, byte raza, byte donador, byte rango,
        string pareja, string desc, int arenaPoints = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharMsgStatus);
        p.WriteInteger(charIndex);
        p.WriteByte(status);
        p.WriteLong(vidaPct);
        p.WriteInteger(st1);
        p.WriteByte(st2);
        p.WriteByte(clase);
        p.WriteInteger(nivel);
        p.WriteByte(raza);
        p.WriteByte(donador);
        p.WriteByte(rango);
        p.WriteASCIIString(pareja ?? "");
        p.WriteASCIIString(desc ?? "");
        p.WriteLong(arenaPoints);   // Puntos de Torneo (NUEVO, al final para no romper el orden previo)
        Send(conn, p);
    }

    /// <summary>WriteCharMsgStatusNPC (id=101): info de un NPC al hacer clic.</summary>
    public static void CharMsgStatusNPC(Connection conn, short localeId, byte status, byte puedeVerVida,
        int porcVida, byte st1, short nivel, byte maestro, byte owner)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharMsgStatusNPC);
        p.WriteInteger(localeId);
        p.WriteByte(status);
        p.WriteByte(puedeVerVida);
        p.WriteLong(porcVida);
        p.WriteByte(st1);
        p.WriteInteger(nivel);
        p.WriteByte(maestro);
        p.WriteByte(owner);
        Send(conn, p);
    }

    /// <summary>WriteRunaCastProgress (id=111): barra de casteo de la runa.
    /// Byte(id) + Integer(CharIndex) + Byte(castTime restante) + Byte(maxTime).</summary>
    public static void RunaCastProgress(Connection conn, short charIndex, byte castTime, byte maxTime)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.RunaCastProgress);
        p.WriteInteger(charIndex);
        p.WriteByte(castTime);
        p.WriteByte(maxTime);
        Send(conn, p);
    }

    /// <summary>WriteUpdateStrenght (id=88): Byte(id) + Byte(fuerza). Actualiza la fuerza visible.</summary>
    public static void UpdateStrenght(Connection conn, byte fuerza)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateStrenght);
        p.WriteByte(fuerza);
        Send(conn, p);
    }

    /// <summary>WriteUpdateDexterity (id=89): Byte(id) + Byte(agilidad). Actualiza la agilidad visible.</summary>
    public static void UpdateDexterity(Connection conn, byte agilidad)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateDexterity);
        p.WriteByte(agilidad);
        Send(conn, p);
    }

    /// <summary>WriteRestOK (id=56): confirma toggle de descanso. Solo el byte ID.</summary>
    public static void RestOK(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.RestOK);
        Send(conn, p);
    }

    /// <summary>GuildList (id=37): lista de clanes, nombres separados por '\0'.</summary>
    public static void GuildList(Connection conn, string nullSeparated)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.guildList);
        p.WriteASCIIString(nullSeparated);
        Send(conn, p);
    }

    /// <summary>GuildNews (id=70): ASCIIString(news) + ASCIIString(warList) + ASCIIString(allyList).</summary>
    public static void GuildNews(Connection conn, string news, string warList, string allyList)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.guildNews);
        p.WriteASCIIString(news);
        p.WriteASCIIString(warList);
        p.WriteASCIIString(allyList);
        Send(conn, p);
    }

    /// <summary>GuildMemberInfo (id=76): ASCIIString(allGuilds) + ASCIIString(members). Listas '|'.</summary>
    public static void GuildMemberInfo(Connection conn, string allGuilds, string members)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GuildMemberInfo);
        p.WriteASCIIString(allGuilds);
        p.WriteASCIIString(members);
        Send(conn, p);
    }

    /// <summary>GuildDetails (id=77): nombre/fundador/fecha/desc/web + miembros + alianza + contadores.</summary>
    public static void GuildDetails(Connection conn, string name, string founder, string date,
        string desc, string web, short memberCount, string alliance, short enemies, short allyProp, short peaceProp)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GuildDetails);
        p.WriteASCIIString(name);
        p.WriteASCIIString(founder);
        p.WriteASCIIString(date);
        p.WriteASCIIString(desc);
        p.WriteASCIIString(web);
        p.WriteInteger(memberCount);
        p.WriteASCIIString(alliance);
        p.WriteInteger(enemies);
        p.WriteInteger(allyProp);
        p.WriteInteger(peaceProp);
        Send(conn, p);
    }

    /// <summary>WritePartyInvitation (Protocol.bas:14283): Byte(id) + UnicodeString(inviter).</summary>
    public static void PartyInvitation(Connection conn, string inviterName)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartyInvitation);
        p.WriteUnicodeString(inviterName);
        Send(conn, p);
    }

    /// <summary>
    /// WritePartyMemberList (Protocol.bas:14300): Byte(id) + Byte(count) + [count>0: UnicodeString(leader)
    /// + count×UnicodeString(member)]. members es 1-based (índice 0 sin usar).
    /// </summary>
    public static void PartyMemberList(Connection conn, string[] members, byte memberCount, string leaderName)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartyMemberList);
        p.WriteByte(memberCount);
        if (memberCount > 0)
        {
            p.WriteUnicodeString(leaderName);
            for (int i = 1; i <= memberCount; i++) p.WriteUnicodeString(members[i] ?? "");
        }
        Send(conn, p);
    }

    /// <summary>
    /// WritePartyMessage (Protocol.bas:14328). El cliente Godot lo lee como ASCII (no Unicode):
    /// Byte(id) + ASCIIString(sender) + ASCIIString(msg).
    /// </summary>
    public static void PartyMessage(Connection conn, string senderName, string message)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartyMessage);
        p.WriteASCIIString(senderName);
        p.WriteASCIIString(message);
        Send(conn, p);
    }

    /// <summary>PartyMemberHP: Byte(id) + Int(charIndex) + Int(minHP) + Int(maxHP).</summary>
    public static void PartyMemberHP(Connection conn, short charIndex, short minHP, short maxHP)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartyMemberHP);
        p.WriteInteger(charIndex);
        p.WriteInteger(minHP);
        p.WriteInteger(maxHP);
        Send(conn, p);
    }

    /// <summary>MEJORA-005: PartyMemberMana, mismo formato que PartyMemberHP pero para maná.</summary>
    public static void PartyMemberMana(Connection conn, short charIndex, short minMana, short maxMana)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartyMemberMana);
        p.WriteInteger(charIndex);
        p.WriteInteger(minMana);
        p.WriteInteger(maxMana);
        Send(conn, p);
    }

    /// <summary>GmWatchHP: Byte(id) + Int(charIndex) + Int(minHP) + Int(maxHP). Ver GmWatch.cs.</summary>
    public static void GmWatchHP(Connection conn, short charIndex, short minHP, short maxHP)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GmWatchHP);
        p.WriteInteger(charIndex);
        p.WriteInteger(minHP);
        p.WriteInteger(maxHP);
        Send(conn, p);
    }

    /// <summary>GmWatchMana: mismo formato que GmWatchHP pero para maná. Ver GmWatch.cs.</summary>
    public static void GmWatchMana(Connection conn, short charIndex, short minMana, short maxMana)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GmWatchMana);
        p.WriteInteger(charIndex);
        p.WriteInteger(minMana);
        p.WriteInteger(maxMana);
        Send(conn, p);
    }

    /// <summary>PetInfo: estado de la mascota compañera para el panel del dueño — se manda esté o
    /// no invocada AHORA MISMO (para poder previsualizarla con los datos guardados), ver
    /// 'invocada' y 'muerta'. Ver ServerPacketID.PetInfo y Combat.EnviarPetInfo.</summary>
    public static void PetInfo(Connection conn, byte tipo, byte nivel, int exp, int expSiguiente,
        int hpActual, int hpMax, string nombre, short[] hechizosConocidos, bool invocada, bool muerta,
        int minHit = 0, int maxHit = 0, int spellMin = 0, int spellMax = 0, byte[] opcionesElegibles = null,
        int charIndex = 0)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PetInfo);
        p.WriteByte(tipo);
        p.WriteByte(nivel);
        p.WriteLong(exp);
        p.WriteLong(expSiguiente);
        p.WriteLong(hpActual);
        p.WriteLong(hpMax);
        p.WriteASCIIString(nombre ?? "");
        hechizosConocidos ??= Array.Empty<short>();
        p.WriteByte((byte)hechizosConocidos.Length);
        foreach (var h in hechizosConocidos) p.WriteLong(h);
        p.WriteByte((byte)(invocada ? 1 : 0));
        p.WriteByte((byte)(muerta ? 1 : 0));
        // Daño cuerpo a cuerpo de su nivel actual (PetLeveling.DanoPorNivel): el panel muestra
        // cuánto pega y cómo mejora al subir de nivel.
        p.WriteLong(minHit);
        p.WriteLong(maxHit);
        // Daño del HECHIZO que lanza hoy (Hechizos.dat MinHP/MaxHP del spell activo), 0 = no castea.
        // Va aparte del golpe porque una mascota casteadora (Ely) hace casi todo su daño con el
        // hechizo y el panel no puede llamar "daño por golpe" a lo único que muestra.
        p.WriteLong(spellMin);
        p.WriteLong(spellMax);
        // Tipos que este personaje PODRÍA elegir todavía (vacío = ya tiene mascota, o su clase no
        // lleva). Existe para los personajes creados antes de que la mascota fuera parte de la
        // creación: el panel les muestra el elegidor con estas opciones y el cliente no necesita
        // saber nada de clases ni repetir la tabla de PetLeveling.
        opcionesElegibles ??= Array.Empty<byte>();
        p.WriteByte((byte)opcionesElegibles.Length);
        foreach (var t in opcionesElegibles) p.WriteByte(t);
        // CharIndex de la instancia viva (0 = no invocada): con esto el cliente sabe CUÁL de los
        // personajes del mapa es su mascota y puede ofrecer "regresar al hogar" al hacerle clic.
        p.WriteInteger((short)charIndex);
        Send(conn, p);
    }

    /// <summary>
    /// PetInv: mochila de la mascota compañera, COMPLETA (los 12 slots, vacíos incluidos: así el
    /// cliente pinta la grilla sin tener que recordar el estado anterior). Se manda sólo al dueño.
    /// Cada slot viaja con nombre y GrhIndex resueltos del lado del server, igual que hace el
    /// comercio: el cliente no tiene el obj.dat completo.
    /// </summary>
    public static void PetInv(Connection conn, Game.MascotaInventario inv)
    {
        if (conn == null || inv == null) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PetInv);
        p.WriteByte((byte)Game.Constants.MAX_PETINVENTORY_SLOTS);
        for (int i = 1; i <= Game.Constants.MAX_PETINVENTORY_SLOTS; i++)
        {
            var o = inv.Object[i];
            p.WriteInteger(o.ObjIndex);
            p.WriteLong(o.ObjIndex > 0 ? o.Amount : 0);
            if (o.ObjIndex > 0)
            {
                var od = Game.ObjData.Get(o.ObjIndex);
                p.WriteASCIIString(od.Name ?? "");
                p.WriteInteger((short)od.GrhIndex);
            }
            else { p.WriteASCIIString(""); p.WriteInteger(0); }
        }
        Send(conn, p);
    }

    /// <summary>MEJORA-005: PartySignal — señal de ayuda difundida al grupo (tipo 1).</summary>
    public static void PartySignal(Connection conn, byte tipo, string remitente, int mapa, byte x, byte y)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartySignal);
        p.WriteByte(tipo);
        p.WriteASCIIString(remitente);
        p.WriteInteger((short)mapa);
        p.WriteByte(x);
        p.WriteByte(y);
        Send(conn, p);
    }

    /// <summary>
    /// MEJORA-005: PartyRoute — recorrido de grupo (tipo 2): dos puntos elegidos a mano en
    /// el Mapamundi por quien lo manda, CADA UNO con su propio mapa (no tienen que ser el
    /// mismo — sirve para marcar p.ej. la entrada de un dungeon en un mapa y el objetivo en
    /// otro). Mismo cable que PartySignal + un segundo (mapa,x,y) al final, así que el
    /// cliente lo sigue leyendo por [S.PARTY_SIGNAL] con tipo==2 ramificando el resto.
    /// </summary>
    public static void PartyRoute(Connection conn, string remitente, int mapa1, byte x1, byte y1, int mapa2, byte x2, byte y2)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartySignal);
        p.WriteByte(2);
        p.WriteASCIIString(remitente);
        p.WriteInteger((short)mapa1);
        p.WriteByte(x1);
        p.WriteByte(y1);
        p.WriteInteger((short)mapa2);
        p.WriteByte(x2);
        p.WriteByte(y2);
        Send(conn, p);
    }

    /// <summary>
    /// PartyMemberPos (116, NUEVO no VB6): Byte(id) + ASCIIString(nombre) + Integer(mapa) +
    /// Byte(x) + Byte(y). Posición de un compañero de grupo, para marcarlo en el minimapa
    /// (mismo mapa) y en el mapamundi (cualquier mapa).
    /// </summary>
    public static void PartyMemberPos(Connection conn, string memberName, short map, byte x, byte y)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PartyMemberPos);
        p.WriteASCIIString(memberName);
        p.WriteInteger(map);
        p.WriteByte(x);
        p.WriteByte(y);
        Send(conn, p);
    }

    /// <summary>
    /// GuerraBots (199, NUEVO no VB6): Integer(count) + count×[Integer(mapa), Byte(x), Byte(y),
    /// Byte(faccion)]. Foto de dónde está cada bot de la guerra de facciones, para dibujarlos en
    /// vivo en el Mapamundi. Va en UN solo paquete (150 bots ≈ 1 KB) en vez de uno por bot.
    /// </summary>
    public static void GuerraBots(Connection conn, IReadOnlyList<(int map, byte x, byte y, byte faccion)> bots)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GuerraBots);
        p.WriteInteger((short)bots.Count);
        foreach (var (map, x, y, faccion) in bots)
        {
            p.WriteInteger((short)map);
            p.WriteByte(x); p.WriteByte(y); p.WriteByte(faccion);
        }
        Send(conn, p);
    }

    /// <summary>WriteGuildChat: Byte(id) + ASCIIString(chat). Mensaje del clan.</summary>
    public static void GuildChat(Connection conn, string chat)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.GuildChat);
        p.WriteASCIIString(chat);
        Send(conn, p);
    }

    /// <summary>WriteMeditateToggle: Byte(id). El cliente togglea la animación de meditar.</summary>
    public static void MeditateToggle(Connection conn) => SendId(conn, ServerPacketID.MeditateToggle);

    /// <summary>Packets de estado (solo Byte id): el cliente reacciona al efecto.</summary>
    public static void ParalizeOK(Connection conn) => SendId(conn, ServerPacketID.ParalizeOK);
    public static void Blind(Connection conn) => SendId(conn, ServerPacketID.Blind);
    public static void BlindNoMore(Connection conn) => SendId(conn, ServerPacketID.BlindNoMore);
    public static void Dumb(Connection conn) => SendId(conn, ServerPacketID.Dumb);
    public static void DumbNoMore(Connection conn) => SendId(conn, ServerPacketID.DumbNoMore);

    private static void SendId(Connection conn, ServerPacketID id)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)id);
        Send(conn, p);
    }

    /// <summary>WritePong: Byte(Pong).</summary>
    public static void Pong(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.Pong);
        Send(conn, p);
    }

    /// <summary>
    /// WriteChangeInventorySlot. Formato EXACTO (1:1):
    /// Byte(id) + Byte(Slot) + Integer(ObjIndex) + Integer(Amount) + Boolean(Equipped)
    /// + Single(SalePrice) + Byte(PuedeUsar).
    /// NOTA: SalePrice y PuedeUsar dependen de ObjData (obj.dat, aún no portado).
    ///       Se envían 0 / 1 por ahora SIN alterar el formato de bytes.
    /// </summary>
    /// <summary>
    /// Sobrecarga 1:1 con el VB6 (Protocol.bas:15280 WriteChangeInventorySlot): lee el slot del
    /// inventario del usuario y calcula PuedeUsar acá (clase/raza/nivel/sexo/facción; GM todo).
    /// Usar SIEMPRE esta versión para slots del inventario propio: la cruda con default
    /// puedeUsar=1 hacía que los ítems no usables nunca se pintaran en rojo en el cliente.
    /// </summary>
    public static void ChangeInventorySlot(Connection conn, Game.User u, byte slot)
    {
        if (conn == null || slot < 1 || slot > Game.Constants.MAX_INVENTORY_SLOTS) return;
        var o = u.Invent.Object[slot];
        byte puedeUsar = 1;
        if (o.ObjIndex > 0)
            puedeUsar = Game.Inventory.PuedeUsarObjeto(u, Game.ObjData.Get(o.ObjIndex), out _) ? (byte)1 : (byte)0;
        ChangeInventorySlot(conn, slot, o.ObjIndex, o.Amount, o.Equipped, 0f, puedeUsar);
    }

    public static void ChangeInventorySlot(Connection conn, byte slot, short objIndex,
        int amount, bool equipped, float salePrice = 0f, byte puedeUsar = 1)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChangeInventorySlot);
        p.WriteByte(slot);
        p.WriteInteger(objIndex);
        p.WriteInteger((short)amount);   // Amount viaja como Integer (2 bytes) igual que VB6
        p.WriteBoolean(equipped);
        p.WriteSingle(salePrice);
        p.WriteByte(objIndex > 0 ? puedeUsar : (byte)0);
        Send(conn, p);
    }

    /// <summary>
    /// WriteUpdateUserStats. Formato EXACTO (1:1):
    /// Byte(id) + Int(MaxHP) + Int(MinHP) + Int(MaxMAN) + Int(MinMAN) + Int(MaxSta) + Int(MinSta)
    /// + Long(GLD) + Int(ELV) + Long(ELU) + Long(Exp) + Byte(Agilidad) + Byte(Fuerza)
    /// + Byte(MinHam) + Byte(MaxHam) + Byte(MinAGU) + Byte(MaxAGU).
    /// OJO: Exp se manda como Long (4 bytes) aunque internamente sea Double.
    /// </summary>
    public static void UpdateUserStats(Connection conn, Game.User u)
    {
        var s = u.Stats;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateUserStats);
        p.WriteInteger(s.MaxHP);
        p.WriteInteger(s.MinHP);
        p.WriteInteger(s.MaxMAN);
        p.WriteInteger(s.MinMAN);
        p.WriteInteger(s.MaxSta);
        p.WriteInteger(s.MinSta);
        p.WriteLong(s.GLD);
        p.WriteInteger(s.ELV);
        p.WriteLong(s.ELU);
        p.WriteLong((int)s.Exp);
        p.WriteByte(s.UserAtributos[2]); // Agilidad
        p.WriteByte(s.UserAtributos[1]); // Fuerza
        p.WriteByte((byte)s.MinHam);
        p.WriteByte((byte)s.MaxHam);
        p.WriteByte((byte)s.MinAGU);
        p.WriteByte((byte)s.MaxAGU);
        Send(conn, p);
    }

    /// <summary>
    /// WriteChangeSpellSlot. Formato 1:1: Byte(id) + Byte(Slot) + Integer(HechizoIndex)
    /// + ASCIIString(Nombre) + ASCIIString(""). El cliente usa Nombre para mostrar el slot.
    /// </summary>
    public static void ChangeSpellSlot(Connection conn, byte slot, short hechizoIndex, string nombre)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChangeSpellSlot);
        p.WriteByte(slot);
        p.WriteInteger(hechizoIndex);
        // VB6 WriteChangeSpellSlot (Protocol.bas:15394): UN solo ASCIIString (el nombre, "" si vacío).
        // NO mandar un segundo string: agrega 2 bytes [0,0] que el cliente lee como "Packet ID=0" basura.
        p.WriteASCIIString(hechizoIndex > 0 ? (nombre ?? "") : "");
        Send(conn, p);
    }

    /// <summary>
    /// EfectoCharParticula: Byte(id) + Integer(CharIndex) + Integer(Particle) + Single(Time) + Boolean(Remove).
    /// Pone (o quita, si remove) una partícula sobre un personaje. Usado por los hechizos (InfoHechizo).
    /// </summary>
    public static void EfectoCharParticula(Connection conn, short charIndex, short particle, float time, bool remove)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EfectoCharParticula);
        p.WriteInteger(charIndex);
        p.WriteInteger(particle);
        p.WriteSingle(time);
        p.WriteBoolean(remove);
        Send(conn, p);
    }

    /// <summary>
    /// CharTyping: Byte(id) + Integer(CharIndex) + Byte(Typing 0|1).
    /// Muestra/oculta la burbuja de "escribiendo" sobre un personaje. Equiv. PrepareMessageCharTyping (VB6).
    /// </summary>
    public static void CharTyping(Connection conn, short charIndex, byte typing)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharTyping);
        p.WriteInteger(charIndex);
        p.WriteByte(typing);
        Send(conn, p);
    }

    /// <summary>
    /// EfectoTerrenoParticula: Byte(id) + Integer(ParticulaFx) + Byte(X) + Byte(Y) + Long(Time).
    /// Partícula sobre un tile. Time=0 → quitar; Time=1 → infinita (partículas de mapa); otro → duración.
    /// </summary>
    public static void EfectoTerrenoParticula(Connection conn, short particula, int x, int y, int time)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EfectoTerrenoParticula);
        p.WriteInteger(particula);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        p.WriteLong(time);
        Send(conn, p);
    }

    /// <summary>
    /// EfectoTerrenoFX: Byte(id) + Integer(fx) + Byte(X) + Byte(Y) + Integer(Loops).
    /// FX anclado a un tile fijo del mapa (no sigue al personaje). Lo usa, p.ej., la cañita voladora:
    /// queda en la posición donde se lanzó. Loops controla cuántos ciclos de animación dura
    /// (0 = una animación completa; -1 = infinito).
    /// </summary>
    public static void EfectoTerrenoFX(Connection conn, short fx, int x, int y, int loops)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EfectoTerrenoFX);
        p.WriteInteger(fx);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        p.WriteInteger((short)loops);
        Send(conn, p);
    }

    /// <summary>
    /// SendSkills (WriteSendSkills): Byte(id) + NUMSKILLS bytes con UserSkills[1..NUMSKILLS].
    /// El cliente (handle_send_skills) llena GameData.current_user.user_skills y abre/actualiza la ventana.
    /// </summary>
    public static void SendSkills(Connection conn, Game.User u)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SendSkills);
        for (int i = 1; i <= Game.Constants.NUMSKILLS; i++)
            p.WriteByte(u.Stats.UserSkills[i]);
        Send(conn, p);
    }

    /// <summary>
    /// WriteAttributes (Protocol.bas:15437): Byte(id) + 5 bytes (Fuerza, Agilidad, Inteligencia,
    /// Carisma, Constitucion). El cliente los lee 1..NUMATRIBUTOS en orden de índice.
    /// </summary>
    public static void Attributes(Connection conn, Game.User u)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.atributes);
        for (int i = 1; i <= Game.Constants.NUMATRIBUTOS; i++)
            p.WriteByte(u.Stats.UserAtributos[i]);
        Send(conn, p);
    }

    /// <summary>
    /// WriteMiniStats (Protocol.bas:15989). Layout EXACTO (verificado contra handle_mini_stats del
    /// cliente): Long ciudadanos, Long renegados, Long usuariosMatados, Int npcsMuertos, Byte clase,
    /// Byte raza, Byte genero, Long muertesUsuario, Byte status, Long republicanos, Long caos,
    /// Long armada, Long milicia.
    /// </summary>
    public static void MiniStats(Connection conn, Game.User u)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.MiniStats);
        p.WriteLong(u.Faccion.CiudadanosMatados);
        p.WriteLong(u.Faccion.RenegadosMatados);
        p.WriteLong(u.Stats.UsuariosMatados);
        p.WriteInteger(u.Stats.NPCsMuertos);
        p.WriteByte(u.Clase);
        p.WriteByte(u.raza);
        p.WriteByte(u.Genero);
        p.WriteLong(u.flags.MuertesUsuario);
        p.WriteByte(u.Faccion.Status);
        p.WriteLong(u.Faccion.RepublicanosMatados);
        p.WriteLong(u.Faccion.CaosMatados);
        p.WriteLong(u.Faccion.ArmadaMatados);
        p.WriteLong(u.Faccion.MilicianosMatados);
        Send(conn, p);
    }

    /// <summary>FurorIgneoTimers: Byte(id) + Byte(duración seg) + Byte(cooldown seg). Hechizo 120 (guerrero).</summary>
    public static void FurorIgneoTimers(Connection conn, byte duracion, byte cooldown)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.FurorIgneoTimers);
        p.WriteByte(duracion);
        p.WriteByte(cooldown);
        Send(conn, p);
    }

    /// <summary>TempleCooldown: Byte(id) + Byte(cooldown seg). Hechizo 116 (guerrero).</summary>
    public static void TempleCooldown(Connection conn, byte cooldown)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.TempleCooldown);
        p.WriteByte(cooldown);
        Send(conn, p);
    }

    /// <summary>WritePosUpdate: Byte(id) + Byte(X) + Byte(Y). Reposiciona al propio cliente.</summary>
    public static void PosUpdate(Connection conn, int x, int y)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PosUpdate);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        Send(conn, p);
    }

    /// <summary>PrepareMessageForceCharMove (id=29): Byte(id) + Byte(Direccion). Empuja al cliente 1 tile.</summary>
    public static void ForceCharMove(Connection conn, byte direccion)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ForceCharMove);
        p.WriteByte(direccion);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageCharacterMove: Byte(id) + Integer(CharIndex) + Byte(X) + Byte(Y).
    /// Notifica a los demás que un personaje se movió a (X,Y).
    /// </summary>
    public static void CharacterMove(Connection conn, short charIndex, int x, int y)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharacterMove);
        p.WriteInteger(charIndex);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        Send(conn, p);
    }

    /// <summary>
    /// LevelUpFx (117): Integer(charIndex) + Byte(nivel). "Este personaje ACABA de subir de
    /// nivel" → el cliente le dibuja el efecto encima. Se difunde a todo el que lo vea (no
    /// solo al que sube, a diferencia de LevelUp(63), que es el contador de puntos de skill).
    /// Sale únicamente a clientes que declararon el bit1 de ClientCaps.
    /// </summary>
    public static void LevelUpFx(Connection conn, short charIndex, int nivel)
    {
        if (conn == null || !conn.SoportaEfectosNuevos) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.LevelUpFx);
        p.WriteInteger(charIndex);
        p.WriteByte((byte)Math.Clamp(nivel, 0, 255));
        Send(conn, p);
    }

    // ======================= MODO ESPÍA (paquetes nuevos) =======================
    // Los tres salen SOLO si el cliente declaró que los entiende (ClientCaps). Al Godot
    // viejo no le llegan nunca: no sabe saltear ids desconocidos y se desincronizaría.

    /// <summary>
    /// EspiaVista (190) → AL ESPÍA: Byte(activo) + Integer(charIndex espiado) + ASCIIString(nombre).
    /// Le avisa al cliente del Dios que está en modo espectador. Con eso el cliente sabe que
    /// el personaje que le dijeron que es "suyo" en realidad es de otro: bloquea el input
    /// (si no, la predicción local le movería el muñeco del espiado) y camina los pasos que
    /// llegan en vez de descartarlos como eco propio.
    /// </summary>
    public static void EspiaVista(Connection conn, bool activo, short charIndex, string nombre)
    {
        if (conn == null || !conn.SoportaEspia) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EspiaVista);
        p.WriteBoolean(activo);
        p.WriteInteger(charIndex);
        p.WriteASCIIString(nombre ?? "");
        Send(conn, p);
    }

    /// <summary>
    /// EspiaReportar (191) → AL ESPIADO: Byte(activo). Le pide al cliente que empiece (o
    /// deje) de reportar lo que NO viaja en el protocolo normal: dónde tiene el mouse y qué
    /// tiene abierto en la interfaz. No muestra NADA en pantalla.
    /// Se manda con Espia.SinEspejo para que la copia del espejo no se la lleve el espía
    /// (si no, el Dios empezaría a reportar su propia pantalla al vacío).
    /// </summary>
    public static void EspiaReportar(Connection conn, bool activo)
    {
        if (conn == null || !conn.SoportaEspia) return;
        // Se manda SIEMPRE, aunque ya estuviera reportando: el cliente usa este paquete para
        // olvidar el último estado de interfaz que reportó y mandarlo entero de nuevo. Sin
        // eso, un espía que engancha a alguien que YA estaba siendo reportado no ve qué
        // tiene abierto hasta que el otro toque algo. Es un paquete de 2 bytes.
        conn.ReportaAlEspia = activo;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EspiaReportar);
        p.WriteBoolean(activo);
        Game.Espia.SinEspejo(() => Send(conn, p));
    }

    /// <summary>
    /// EspiaUi (193) → AL ESPÍA: ASCIIString(estado). Qué tiene abierto el espiado en su
    /// interfaz (solapa del panel lateral, ventanas, slots seleccionados). El server no
    /// interpreta el contenido: lo arma el cliente del espiado y lo aplica el del Dios.
    /// </summary>
    public static void EspiaUi(Connection conn, string estado)
    {
        if (conn == null || !conn.SoportaEspia) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EspiaUi);
        p.WriteASCIIString(estado ?? "");
        Send(conn, p);
    }

    /// <summary>
    /// EspiaMousePos (192) → AL ESPÍA: Long(xPx) + Long(yPx) + Integer(nx) + Integer(ny) + Byte(botones).
    /// El mouse del espiado tal cual lo mandó su cliente, en DOS sistemas de coordenadas:
    ///   • xPx/yPx = píxeles de MUNDO (tile global * 32) — valen cuando está sobre el mapa,
    ///     y caen en el mismo tile aunque las dos pantallas tengan distinto tamaño.
    ///   • nx/ny = posición en PANTALLA normalizada a 0..10000 — es la que vale cuando el
    ///     mouse está sobre el HUD (inventario, hechizos, consola), donde no hay ningún
    ///     tile debajo. El HUD está maquetado en porcentajes, así que normalizado cae en
    ///     el mismo botón en las dos pantallas.
    /// botones: bit0 izquierdo, bit1 derecho, bit6 = está sobre el mundo, bit7 = recién clickeó.
    /// </summary>
    public static void EspiaMousePos(Connection conn, int xPx, int yPx, short nx, short ny, byte botones)
    {
        if (conn == null || !conn.SoportaEspia) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.EspiaMousePos);
        p.WriteLong(xPx);
        p.WriteLong(yPx);
        p.WriteInteger(nx);
        p.WriteInteger(ny);
        p.WriteByte(botones);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageCharStatus (id=95): Byte(id) + Integer(CharIndex) + Byte(status).
    /// El cliente guarda 'status' como privileges del char → determina el color del nombre.
    /// VB6 lo manda con Faccion.Status al cambiar de facción (ModFacciones).
    /// </summary>
    public static void CharStatus(Connection conn, short charIndex, byte status)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharStatus);
        p.WriteInteger(charIndex);
        p.WriteByte(status);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageCharacterRemove: Byte(id) + Integer(CharIndex) + Boolean(Desvanecido).
    /// Quita un personaje de la vista de quien lo recibe.
    /// </summary>
    public static void CharacterRemove(Connection conn, short charIndex, bool desvanecido = false)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CharacterRemove);
        p.WriteInteger(charIndex);
        p.WriteBoolean(desvanecido);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageChatOverHead: Byte(id) + ASCIIString(chat) + Integer(CharIndex)
    /// + Byte(ModeChat). Muestra el texto flotando sobre el personaje.
    /// </summary>
    public static void ChatOverHead(Connection conn, string chat, short charIndex, byte modeChat)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChatOverHead);
        p.WriteASCIIString(chat);
        p.WriteInteger(charIndex);
        p.WriteByte(modeChat);
        Send(conn, p);
    }

    /// <summary>
    /// PrepareMessageChatOverHeadLocale: Byte(id) + Integer(CharIndex) + Long(id) + Byte(modo).
    /// El cliente lo usa para daño flotante de combate: modo 2 = rojo ("Te golpean por X"),
    /// modo 3 = azul ("Golpeás por X"). id = monto de daño (0 = falla). También loguea en consola.
    /// </summary>
    public static void ChatOverHeadLocale(Connection conn, short charIndex, int id, byte modo)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ChatOverHeadLocale);
        p.WriteInteger(charIndex);
        p.WriteLong(id);
        p.WriteByte(modo);
        Send(conn, p);
    }

    /// <summary>
    /// WriteBlockPosition: Byte(id) + Byte(X) + Byte(Y) + Boolean(Blocked).
    /// Actualiza el estado de bloqueo de un tile (puerta abierta/cerrada, etc).
    /// Portado 1:1 desde Protocol.bas:14857 (WriteBlockPosition).
    /// </summary>
    public static void BlockPosition(Connection conn, int x, int y, bool blocked)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BlockPosition);
        p.WriteInteger((short)x);
        p.WriteInteger((short)y);
        p.WriteBoolean(blocked);
        Send(conn, p);
    }

    // ID 64 — SET_INVISIBLE: Byte(ID) + Integer(charIndex) + Boolean(invisible)
    public static void SetInvisible(Connection conn, short charIndex, bool invisible)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SetInvisible);
        p.WriteInteger(charIndex);
        p.WriteBoolean(invisible);
        Send(conn, p);
    }

    // ID 85 — SHOW_SOS_FORM: Byte(ID) + ASCIIString(lista SOS separada por |)
    public static void ShowSOSForm(Connection conn, string sosList)
    {
        var p = new ByteQueue();
        p.WriteByte(85); // SHOW_SOS_FORM
        p.WriteASCIIString(sosList);
        Send(conn, p);
    }

    // ID 86 — USER_NAME_LIST: Byte(ID) + ASCIIString(names separados por |)
    public static void UserNameList(Connection conn, string names)
    {
        var p = new ByteQueue();
        p.WriteByte(86); // USER_NAME_LIST
        p.WriteASCIIString(names);
        Send(conn, p);
    }

    // ID 4 — NAVIGATE_TOGGLE: solo el byte ID
    public static void NavigateToggle(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte(4); // NAVIGATE_TOGGLE
        Send(conn, p);
    }

    // ID 5 — MONTATE_TOGGLE: solo el byte ID (el cliente alterna montando + velocidad de scroll)
    public static void MontateToggle(Connection conn)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.MontateToggle);
        Send(conn, p);
    }

    /// <summary>
    /// WriteAuraToChar (104): Byte(id) + Int(charIndex) + Byte(aura) + Byte(slot). slot: 1=arma,
    /// 2=body, 3=escudo, 4=cabeza, 5=otra, 6=anillo. aura=0 quita el aura de ese slot.
    /// </summary>
    public static void AuraToChar(Connection conn, short charIndex, byte aura, byte slot)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AuraToChar);
        p.WriteInteger(charIndex);
        p.WriteByte(aura);
        p.WriteByte(slot);
        Send(conn, p);
    }

    /// <summary>NpcParalysisProgress (114): Byte(id) + Integer(charIndex) + Byte(segundos) + Byte(tipo).
    /// El cliente dibuja la barra de parálisis bajo el personaje (NPC o usuario) y el efecto
    /// sobre el body: tipo 1 = parálisis (petrificado), tipo 2 = inmovilización (enredaderas).</summary>
    public static void NpcParalysisProgress(Connection conn, short charIndex, byte seconds, byte tipo = 1)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.NpcParalysisProgress);
        p.WriteInteger(charIndex);
        p.WriteByte(seconds);
        p.WriteByte(tipo);
        Send(conn, p);
    }

    // ID 40 — RAIN_TOGGLE: Byte(ID) + Byte(tipo)
    public static void RainToggle(Connection conn, byte tipo)
    {
        var p = new ByteQueue();
        p.WriteByte(40); // RAIN_TOGGLE
        p.WriteByte(tipo);
        Send(conn, p);
    }

    /// <summary>UpdateCreditos (15): Byte(id) + Long(creditos). Saldo de créditos de donación.</summary>
    public static void UpdateCreditos(Connection conn, int creditos)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.UpdateCreditos);
        p.WriteLong(creditos);
        Send(conn, p);
    }

    public struct ShopItem { public int Id; public int PrecioARS; public int Creditos; public string Nombre; }

    /// <summary>WriteShopCatalog (166): Byte(id) + Byte(count) + [Int(id)+Long(precio)+Long(creditos)+ASCII(nombre)].</summary>
    public static void ShopCatalog(Connection conn, System.Collections.Generic.List<ShopItem> items)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ShopCatalog);
        p.WriteByte((byte)items.Count);
        foreach (var it in items)
        {
            p.WriteInteger((short)it.Id);
            p.WriteLong(it.PrecioARS);
            p.WriteLong(it.Creditos);
            p.WriteASCIIString(it.Nombre ?? "");
        }
        Send(conn, p);
    }

    public struct DonationEntry { public string Fecha; public int Creditos; public byte Estado; }

    /// <summary>WriteDonationHistory (167): Byte(id) + Byte(count) + [ASCII(fecha)+Long(creditos)+Byte(estado)].</summary>
    public static void DonationHistory(Connection conn, System.Collections.Generic.List<DonationEntry> entries)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.DonationHistory);
        p.WriteByte((byte)entries.Count);
        foreach (var e in entries)
        {
            p.WriteASCIIString(e.Fecha ?? "");
            p.WriteLong(e.Creditos);
            p.WriteByte(e.Estado);
        }
        Send(conn, p);
    }

    public struct DonorEntry { public string Nombre; public int Total; }

    /// <summary>WriteDonorRanking (168): Byte(id) + Byte(count) + [ASCII(nombre)+Long(total)].</summary>
    public static void DonorRanking(Connection conn, System.Collections.Generic.List<DonorEntry> entries)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.DonorRanking);
        p.WriteByte((byte)entries.Count);
        foreach (var e in entries)
        {
            p.WriteASCIIString(e.Nombre ?? "");
            p.WriteLong(e.Total);
        }
        Send(conn, p);
    }

    /// <summary>
    /// WriteAmigosList (182, NUEVO no VB6): Byte(id) + Byte(count) + por amigo:
    /// ASCII(nombre) + Byte(online 0/1) + Integer(mapa). Alimenta el panel de la solapa Amigos.
    /// </summary>
    public static void AmigosList(Connection conn, System.Collections.Generic.List<(string Nombre, bool Online, int Mapa)> amigos)
    {
        if (conn == null) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AmigosList);
        p.WriteByte((byte)amigos.Count);
        foreach (var a in amigos)
        {
            p.WriteASCIIString(a.Nombre ?? "");
            p.WriteByte((byte)(a.Online ? 1 : 0));
            p.WriteInteger((short)a.Mapa);
        }
        Send(conn, p);
    }

    /// <summary>
    /// WriteAmigoRequest (183, NUEVO no VB6): Byte(id) + ASCII(nombre del solicitante).
    /// nombre="" limpia la solicitud pendiente en el panel del cliente.
    /// </summary>
    public static void AmigoRequest(Connection conn, string nombre)
    {
        if (conn == null) return;
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AmigoRequest);
        p.WriteASCIIString(nombre ?? "");
        Send(conn, p);
    }

    /// <summary>WriteShopPaymentURL (160): Byte(id) + ASCII(url). El cliente abre la URL de pago.</summary>
    public static void ShopPaymentURL(Connection conn, string url)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ShopPaymentURL);
        p.WriteASCIIString(url ?? "");
        Send(conn, p);
    }

    /// <summary>WriteShopItemGranted (161): Byte(id) + Int(itemId) + ASCII(nombre).</summary>
    public static void ShopItemGranted(Connection conn, int itemId, string nombre)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ShopItemGranted);
        p.WriteInteger((short)itemId);
        p.WriteASCIIString(nombre ?? "");
        Send(conn, p);
    }

    public struct PremiumParticleItem { public int Id; public int StreamId; public int PrecioCreditos; public string Nombre; }

    /// <summary>WritePremiumParticlesCatalog (200): Byte(id) + Byte(count) + [Int(itemId)+Int(streamId)+Long(precioCreditos)+ASCII(nombre)].</summary>
    public static void PremiumParticlesCatalog(Connection conn, System.Collections.Generic.List<PremiumParticleItem> items)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PremiumParticlesCatalog);
        p.WriteByte((byte)items.Count);
        foreach (var it in items)
        {
            p.WriteInteger((short)it.Id);
            p.WriteInteger((short)it.StreamId);
            p.WriteLong(it.PrecioCreditos);
            p.WriteASCIIString(it.Nombre ?? "");
        }
        Send(conn, p);
    }

    /// <summary>WritePremiumParticlesOwned (201): Byte(id) + Byte(count) + count×Int(streamId) + Int(equipado, 0=ninguno).</summary>
    public static void PremiumParticlesOwned(Connection conn, System.Collections.Generic.IEnumerable<int> owned, int equipado)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PremiumParticlesOwned);
        var list = new System.Collections.Generic.List<int>(owned);
        p.WriteByte((byte)list.Count);
        foreach (var streamId in list) p.WriteInteger((short)streamId);
        p.WriteInteger((short)equipado);
        Send(conn, p);
    }

    /// <summary>WritePremiumParticleGranted (202): Byte(id) + Int(streamId) + ASCII(nombre).</summary>
    public static void PremiumParticleGranted(Connection conn, int streamId, string nombre)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.PremiumParticleGranted);
        p.WriteInteger((short)streamId);
        p.WriteASCIIString(nombre ?? "");
        Send(conn, p);
    }

    public struct CreditShopItem { public int Id; public int ObjIndex; public int PrecioCreditos; public int VisualId; public int AuraId; public string Nombre; }

    /// <summary>WriteCreditItemsCatalog (215): Byte(id) + Byte(count) + [Int(itemId)+Int(objIndex)+Long(precioCreditos)+Int(visualId)+Int(auraId)+ASCII(nombre)].
    /// VisualId = Anim (cascos/escudos) o NumRopaje (monturas) — lo usa el cliente para
    /// previsualizar el personaje completo con el cosmético puesto (credit_items_ui.js).
    /// AuraId (0 = sin partícula) = id de assets/particles.json — mismo catálogo que
    /// PremiumParticles, para que si un cosmético trae efecto/partícula propia se vea
    /// también en la previsualización.</summary>
    public static void CreditItemsCatalog(Connection conn, System.Collections.Generic.List<CreditShopItem> items)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CreditItemsCatalog);
        p.WriteByte((byte)items.Count);
        foreach (var it in items)
        {
            p.WriteInteger((short)it.Id);
            p.WriteInteger((short)it.ObjIndex);
            p.WriteLong(it.PrecioCreditos);
            p.WriteInteger((short)it.VisualId);
            p.WriteInteger((short)it.AuraId);
            p.WriteASCIIString(it.Nombre ?? "");
        }
        Send(conn, p);
    }

    /// <summary>WriteCreditItemGranted (216): Byte(id) + Int(objIndex) + ASCII(nombre).</summary>
    public static void CreditItemGranted(Connection conn, int objIndex, string nombre)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.CreditItemGranted);
        p.WriteInteger((short)objIndex);
        p.WriteASCIIString(nombre ?? "");
        Send(conn, p);
    }

    public struct AuctionEntry
    {
        public int Id; public short ObjIndex; public int Amount; public string Seller;
        public long CurrentBid; public string LastBidder; public long Buyout; public long RemainingSecs;
    }

    /// <summary>
    /// WriteAuctionList (Protocol.bas:21800). Byte(id) + Int(count) + por subasta:
    /// Int(id) + Int(objIndex) + Long(amount) + ASCII(seller) + Long(currentBid) + ASCII(lastBidder)
    /// + Long(buyout) + Long(remainingSecs). Layout verificado contra handle_auction_list del cliente.
    /// </summary>
    public static void AuctionList(Connection conn, System.Collections.Generic.List<AuctionEntry> list)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AuctionList);
        p.WriteInteger((short)list.Count);
        foreach (var a in list)
        {
            p.WriteInteger((short)a.Id);
            p.WriteInteger(a.ObjIndex);
            p.WriteLong(a.Amount);
            p.WriteASCIIString(a.Seller ?? "");
            p.WriteLong((int)a.CurrentBid);
            p.WriteASCIIString(a.LastBidder ?? "");
            p.WriteLong((int)a.Buyout);
            p.WriteLong((int)a.RemainingSecs);
        }
        Send(conn, p);
    }

    /// <summary>WriteAmbientLight (Protocol.bas:21491): Byte(id) + Byte(LightLevel 0-255, 100=normal).</summary>
    public static void AmbientLight(Connection conn, byte lightLevel)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.AmbientLight);
        p.WriteByte(lightLevel);
        Send(conn, p);
    }

    /// <summary>DayNightInfo (NUEVO): Byte(id) + Byte(hora 0-23) + Byte(minuto 0-59) + Byte(inDungeon).</summary>
    public static void DayNightInfo(Connection conn, byte hora, byte minuto, byte inDungeon)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.DayNightInfo);
        p.WriteByte(hora);
        p.WriteByte(minuto);
        p.WriteByte(inDungeon);
        Send(conn, p);
    }

    // ============================================================
    //  Sistema de reportes / tickets (NUEVO, no VB6)
    // ============================================================

    /// <summary>ReportSubmitted: Byte(ok) Long(reportId) ASCIIString(message). Ack al crear ticket.</summary>
    public static void ReportSubmitted(Connection conn, bool ok, int reportId, string message)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ReportSubmitted);
        p.WriteByte((byte)(ok ? 1 : 0));
        p.WriteLong(reportId);
        p.WriteASCIIString(message ?? "");
        Send(conn, p);
    }

    /// <summary>
    /// ReportList: Byte(count) + por reporte:
    /// Long(id) Byte(cat) Byte(status) ASCIIString(reporter) ASCIIString(subject)
    /// ASCIIString(fecha) ASCIIString(gm) Byte(replies).
    /// </summary>
    public static void ReportList(Connection conn, IReadOnlyList<Game.ReportManager.Report> reports)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ReportList);
        byte n = (byte)System.Math.Min(reports.Count, 200);
        p.WriteByte(n);
        for (int i = 0; i < n; i++)
        {
            var r = reports[i];
            p.WriteLong(r.Id);
            p.WriteByte(r.Category);
            p.WriteByte(r.Status);
            p.WriteASCIIString(r.Reporter ?? "");
            p.WriteASCIIString(r.Subject ?? "");
            p.WriteASCIIString(r.CreatedAt ?? "");
            p.WriteASCIIString(r.AssignedGm ?? "");
            p.WriteByte((byte)System.Math.Min(r.Replies?.Count ?? 0, 255));
        }
        Send(conn, p);
    }

    /// <summary>
    /// ReportDetail: Long(id) Byte(cat) Byte(status) ASCIIString(reporter) ASCIIString(subject)
    /// ASCIIString(body) ASCIIString(fecha) ASCIIString(gm) Integer(map) Byte(x) Byte(y)
    /// Byte(replyCount) + por respuesta: ASCIIString(author) ASCIIString(fecha) ASCIIString(text) Byte(isGm).
    /// </summary>
    public static void ReportDetail(Connection conn, Game.ReportManager.Report r)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ReportDetail);
        p.WriteLong(r.Id);
        p.WriteByte(r.Category);
        p.WriteByte(r.Status);
        p.WriteASCIIString(r.Reporter ?? "");
        p.WriteASCIIString(r.Subject ?? "");
        p.WriteASCIIString(r.Body ?? "");
        p.WriteASCIIString(r.CreatedAt ?? "");
        p.WriteASCIIString(r.AssignedGm ?? "");
        p.WriteInteger((short)r.Map);
        p.WriteByte(r.X);
        p.WriteByte(r.Y);
        byte rc = (byte)System.Math.Min(r.Replies?.Count ?? 0, 255);
        p.WriteByte(rc);
        for (int i = 0; i < rc; i++)
        {
            var rep = r.Replies[i];
            p.WriteASCIIString(rep.Author ?? "");
            p.WriteASCIIString(rep.Date ?? "");
            p.WriteASCIIString(rep.Text ?? "");
            p.WriteByte((byte)(rep.IsGm ? 1 : 0));
        }
        Send(conn, p);
    }

    /// <summary>ReportNotify: Byte(kind: 0 info, 1 ok, 2 borrado) ASCIIString(message).</summary>
    public static void ReportNotify(Connection conn, byte kind, string message)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ReportNotify);
        p.WriteByte(kind);
        p.WriteASCIIString(message ?? "");
        Send(conn, p);
    }

    // ============================================================
    //  Battle Pass / Pase de Temporada (NUEVO, no VB6)
    // ============================================================
    /// <summary>
    /// BattlePassInfo: estado completo del pase para pintar la UI.
    ///   Long seasonId, ASCIIString nombre, Byte nivelesMax, Long puntosPorNivel,
    ///   Long puntos, Byte nivel, Byte premium, Long precioCredito, Long precioMP,
    ///   Byte count, count×[Byte nivel, ASCIIString freeDesc, ASCIIString premDesc,
    ///                       Byte reclamadoFree, Byte reclamadoPrem]
    /// </summary>
    public static void BattlePassInfo(Connection conn, Game.BattlePass.Season season,
        Game.BattlePass.Progress prog, List<(int level, string free, string prem)> descs, string comoGanar,
        List<(string desc, int actual, int objetivo, bool completada, int puntos)> misiones)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BattlePassInfo);
        p.WriteLong(season.Id);
        p.WriteASCIIString(season.Nombre ?? "");
        p.WriteByte((byte)System.Math.Min(season.NivelesMax, 255));
        p.WriteLong(season.PuntosPorNivel);
        p.WriteLong(prog.Puntos);
        p.WriteByte((byte)System.Math.Min(prog.Nivel, 255));
        p.WriteByte((byte)(prog.Premium ? 1 : 0));
        p.WriteLong(season.PrecioCredito);
        p.WriteLong(season.PrecioMercadoPago);
        p.WriteASCIIString(comoGanar ?? "");

        byte n = (byte)System.Math.Min(descs.Count, 255);
        p.WriteByte(n);
        for (int i = 0; i < n; i++)
        {
            var d = descs[i];
            p.WriteByte((byte)System.Math.Min(d.level, 255));
            p.WriteASCIIString(d.free ?? "");
            p.WriteASCIIString(d.prem ?? "");
            p.WriteByte((byte)(prog.ReclamadosFree.Contains(d.level) ? 1 : 0));
            p.WriteByte((byte)(prog.ReclamadosPrem.Contains(d.level) ? 1 : 0));
        }

        // Misiones: Byte count, count×[ASCIIString desc, Integer actual, Integer objetivo, Byte completada, Integer puntos]
        byte nm = (byte)System.Math.Min(misiones.Count, 255);
        p.WriteByte(nm);
        for (int i = 0; i < nm; i++)
        {
            var m = misiones[i];
            p.WriteASCIIString(m.desc ?? "");
            p.WriteInteger((short)System.Math.Min(m.actual, short.MaxValue));
            p.WriteInteger((short)System.Math.Min(m.objetivo, short.MaxValue));
            p.WriteByte((byte)(m.completada ? 1 : 0));
            p.WriteInteger((short)System.Math.Min(m.puntos, short.MaxValue));
        }
        Send(conn, p);
    }

    /// <summary>BattlePassUpdate: Long puntos, Byte nivel, ASCIIString mensaje.</summary>
    public static void BattlePassUpdate(Connection conn, int puntos, byte nivel, string mensaje)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BattlePassUpdate);
        p.WriteLong(puntos);
        p.WriteByte(nivel);
        p.WriteASCIIString(mensaje ?? "");
        Send(conn, p);
    }

    /// <summary>
    /// LogrosInfo (185): listado de logros con progreso para pintar la ventana de Logros.
    /// Byte count, count×[ASCIIString cat, ASCIIString desc, ASCIIString reward,
    /// Long actual, Long objetivo, Byte completado, Byte repetible, Integer veces, Integer body, Long grh].
    /// body = Body del NPC objetivo (sprite del NPC); grh = GrhIndex de un objeto representativo
    /// (mineral / recompensa) para el ícono. 0 = sin sprite.
    /// </summary>
    public static void LogrosInfo(Connection conn,
        List<(string cat, string desc, string reward, long actual, long objetivo, bool completado, bool repetible, int veces, int body, int grh)> logros)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.LogrosInfo);
        byte n = (byte)System.Math.Min(logros.Count, 255);
        p.WriteByte(n);
        for (int i = 0; i < n; i++)
        {
            var l = logros[i];
            p.WriteASCIIString(l.cat ?? "");
            p.WriteASCIIString(l.desc ?? "");
            p.WriteASCIIString(l.reward ?? "");
            p.WriteLong((int)System.Math.Min(l.actual, int.MaxValue));
            p.WriteLong((int)System.Math.Min(l.objetivo, int.MaxValue));
            p.WriteByte((byte)(l.completado ? 1 : 0));
            p.WriteByte((byte)(l.repetible ? 1 : 0));
            p.WriteInteger((short)System.Math.Min(l.veces, short.MaxValue));
            p.WriteInteger((short)System.Math.Max(0, System.Math.Min(l.body, short.MaxValue)));
            p.WriteLong(System.Math.Max(0, l.grh));
        }
        Send(conn, p);
    }

    /// <summary>Un objetivo de misión para QuestInfo. tipo: 0=matar (body = sprite del NPC),
    /// 1=juntar (grh = GrhIndex del objeto). DestMap/X/Y = zona de ESTE objetivo (0 = sin zona);
    /// EntMap/X/Y = entrada al dungeon de esa zona si no figura en el mapamundi.</summary>
    public readonly record struct QuestObjetivo(byte Tipo, string TargetName, int Actual, int Requerido, int Body, int Grh,
        int DestMap, byte DestX, byte DestY, int EntMap, byte EntX, byte EntY);

    /// <summary>Una misión para QuestInfo. estado: 0=disponible, 1=en curso, 2=lista para entregar,
    /// 3=completada, 4=nivel insuficiente, 5=bloqueada por requisito, 6=en cooldown.
    /// GiverMap/X/Y = posición del NPC dador (0 = sin posición conocida);
    /// DestMap/X/Y = zona objetivo (flecha del minimapa / marcador del mapamundi, 0 = sin destino).</summary>
    public readonly record struct QuestEntry(int Id, string Nombre, string Historia, string Reward,
        byte Estado, byte NivelMin, bool Repetible, string Extra,
        int GiverMap, byte GiverX, byte GiverY, int DestMap, byte DestX, byte DestY,
        int EntMap, byte EntX, byte EntY, List<QuestObjetivo> Objetivos);

    /// <summary>Un NPC dador para los marcadores del mapamundi (catálogo fijo de [NPCSPAWNS]).
    /// NpcIndex = índice de NPCs.dat (el cliente marca "!" sobre la cabeza de esos NPCs).
    /// EntMap/X/Y = entrada al dungeon si el dador está en un mapa fuera del mapamundi.</summary>
    public readonly record struct QuestGiver(int NpcIndex, string Nombre, int Map, byte X, byte Y, bool Disponible,
        int EntMap, byte EntX, byte EntY);

    /// <summary>
    /// QuestInfo (189): misiones de un NPC dador (origen=1), log del jugador (origen=0 abre la
    /// ventana, origen=2 silencioso: solo actualiza marcadores). Ver layout en ServerPacketID.
    /// </summary>
    public static void QuestInfo(Connection conn, byte origen, string npcName, List<QuestEntry> quests, List<QuestGiver> givers)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.QuestInfo);
        p.WriteByte(origen);
        p.WriteASCIIString(npcName ?? "");
        byte n = (byte)System.Math.Min(quests.Count, 255);
        p.WriteByte(n);
        for (int i = 0; i < n; i++)
        {
            var q = quests[i];
            p.WriteInteger((short)q.Id);
            p.WriteASCIIString(q.Nombre ?? "");
            p.WriteASCIIString(q.Historia ?? "");
            p.WriteASCIIString(q.Reward ?? "");
            p.WriteByte(q.Estado);
            p.WriteByte(q.NivelMin);
            p.WriteByte((byte)(q.Repetible ? 1 : 0));
            p.WriteASCIIString(q.Extra ?? "");
            p.WriteInteger((short)q.GiverMap);
            p.WriteByte(q.GiverX);
            p.WriteByte(q.GiverY);
            p.WriteInteger((short)q.DestMap);
            p.WriteByte(q.DestX);
            p.WriteByte(q.DestY);
            p.WriteInteger((short)q.EntMap);
            p.WriteByte(q.EntX);
            p.WriteByte(q.EntY);
            byte no = (byte)System.Math.Min(q.Objetivos.Count, 255);
            p.WriteByte(no);
            for (int o = 0; o < no; o++)
            {
                var ob = q.Objetivos[o];
                p.WriteByte(ob.Tipo);
                p.WriteASCIIString(ob.TargetName ?? "");
                p.WriteInteger((short)System.Math.Min(ob.Actual, short.MaxValue));
                p.WriteInteger((short)System.Math.Min(ob.Requerido, short.MaxValue));
                p.WriteInteger((short)System.Math.Max(0, System.Math.Min(ob.Body, short.MaxValue)));
                p.WriteLong(System.Math.Max(0, ob.Grh));
                p.WriteInteger((short)ob.DestMap);
                p.WriteByte(ob.DestX);
                p.WriteByte(ob.DestY);
                p.WriteInteger((short)ob.EntMap);
                p.WriteByte(ob.EntX);
                p.WriteByte(ob.EntY);
            }
        }
        // Catálogo de dadores ([NPCSPAWNS]) para el mapamundi (0 entradas cuando origen=1).
        byte ng = (byte)System.Math.Min(givers.Count, 255);
        p.WriteByte(ng);
        for (int g = 0; g < ng; g++)
        {
            var gv = givers[g];
            p.WriteInteger((short)gv.NpcIndex);
            p.WriteASCIIString(gv.Nombre ?? "");
            p.WriteInteger((short)gv.Map);
            p.WriteByte(gv.X);
            p.WriteByte(gv.Y);
            p.WriteByte((byte)(gv.Disponible ? 1 : 0));
            p.WriteInteger((short)gv.EntMap);
            p.WriteByte(gv.EntX);
            p.WriteByte(gv.EntY);
        }
        Send(conn, p);
    }

    // ============================================================
    //  Editor de objetos en vivo para GMs (NUEVO, no VB6)
    // ============================================================

    /// <summary>
    /// ObjEditorList: Integer(count) + count×[Integer(objIndex) Byte(type) Byte(subtipo)
    /// Long(grhIndex) ASCIIString(name)]. Catálogo resumido para la lista del editor.
    /// </summary>
    public static void ObjEditorList(Connection conn, List<(int Index, byte Type, byte SubTipo, int Grh, string Name)> objs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ObjEditorList);
        p.WriteInteger((short)objs.Count);
        foreach (var o in objs)
        {
            p.WriteInteger((short)o.Index);
            p.WriteByte(o.Type);
            p.WriteByte(o.SubTipo);
            p.WriteLong(o.Grh);
            p.WriteASCIIString(o.Name ?? "");
        }
        Send(conn, p);
    }

    /// <summary>Entrada de MapNpcsList: una criatura distinta que habita el mapa.</summary>
    public sealed class MapNpcEntry
    {
        public string Name = "";
        public byte Count;
        public short Body, Head;
        public int Exp, Gold, MaxHP;
        public (short objIndex, int amount, double prob)[] Drops;
    }

    /// <summary>
    /// MapNpcsList (165): criaturas que habitan un mapa, para el mapa-mundi del cliente.
    /// Formato (debe coincidir con handle_map_npcs_list en protocol_incoming.gd):
    /// Integer map_id, Byte count, y por NPC:
    /// Integer body, Integer head, Long exp, Long gold, Long max_hp,
    /// Byte ndrops, [Integer obj_idx, Long amount, Single prob] x ndrops,
    /// UnicodeString name, Byte count.
    /// </summary>
    public static void MapNpcsList(Connection conn, int map, List<MapNpcEntry> npcs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.MapNpcsList);
        p.WriteInteger((short)map);
        p.WriteByte((byte)npcs.Count);
        foreach (var n in npcs)
        {
            p.WriteInteger(n.Body);
            p.WriteInteger(n.Head);
            p.WriteLong(n.Exp);
            p.WriteLong(n.Gold);
            p.WriteLong(n.MaxHP);
            var drops = n.Drops ?? System.Array.Empty<(short objIndex, int amount, double prob)>();
            byte nd = (byte)System.Math.Min(drops.Length, 255);
            p.WriteByte(nd);
            for (int i = 0; i < nd; i++)
            {
                p.WriteInteger(drops[i].objIndex);
                p.WriteLong(drops[i].amount);
                p.WriteSingle((float)drops[i].prob);
            }
            p.WriteUnicodeString(n.Name ?? "");
            p.WriteByte(n.Count);
        }
        Send(conn, p);
    }

    /// <summary>
    /// NpcCatalog: Integer(count) + count×[Integer(npcIndex) Byte(npcType) Byte(hostil)
    /// Integer(body) Integer(head) ASCIIString(name)]. Catálogo resumido de NPCs para el
    /// buscador de "Crear NPC" del panel GM (body/head: vista previa del cuerpo en el cliente).
    /// </summary>
    public static void NpcCatalog(Connection conn, List<(int Index, string Name, byte NpcType, bool Hostil, short Body, short Head)> npcs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.NpcCatalog);
        p.WriteInteger((short)npcs.Count);
        foreach (var n in npcs)
        {
            p.WriteInteger((short)n.Index);
            p.WriteByte(n.NpcType);
            p.WriteByte((byte)(n.Hostil ? 1 : 0));
            p.WriteInteger(n.Body);
            p.WriteInteger(n.Head);
            p.WriteASCIIString(n.Name ?? "");
        }
        Send(conn, p);
    }

    /// <summary>
    /// BotClasesCatalog: Byte(count) + count×[Byte(clase) Byte(razaDefault) ASCIIString(nombre)].
    /// Catálogo de clases de bot invocables (Bots.Clases) para el panel GM de invocar bots — así
    /// la lista del cliente queda sincronizada con Dat/BotClases.dat sin tocar el JS.
    /// </summary>
    public static void BotClasesCatalog(Connection conn, IEnumerable<Game.Bots.BotClase> clases)
    {
        var list = new List<Game.Bots.BotClase>();
        foreach (var c in clases) list.Add(c);

        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BotClasesCatalog);
        p.WriteByte((byte)list.Count);
        foreach (var c in list)
        {
            p.WriteByte(c.Clase);
            p.WriteByte(c.RazaDefault);
            p.WriteASCIIString(c.Nombre ?? "");
        }
        Send(conn, p);
    }

    /// <summary>TorneoState: estado del torneo para refrescar la ventana del cliente (NUEVO, no VB6).</summary>
    public static void TorneoState(Connection conn, byte yourMode, byte q1, byte q2, byte q3,
        byte cd1, byte cd2, byte cd3, byte active, byte activeMode, byte activeTeams, byte yourStatus, string info)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.TorneoState);
        p.WriteByte(yourMode);
        p.WriteByte(q1); p.WriteByte(q2); p.WriteByte(q3);
        p.WriteByte(cd1); p.WriteByte(cd2); p.WriteByte(cd3);
        p.WriteByte(active);
        p.WriteByte(activeMode);
        p.WriteByte(activeTeams);
        p.WriteByte(yourStatus);
        p.WriteASCIIString(info ?? "");
        Send(conn, p);
    }

    /// <summary>TorneoCountdown: número grande de cuenta regresiva en pantalla (0 = ocultar).</summary>
    public static void TorneoCountdown(Connection conn, byte seconds)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.TorneoCountdown);
        p.WriteByte(seconds);
        Send(conn, p);
    }

    /// <summary>
    /// ObjEditorDetail: Integer(objIndex) + Byte(count) + count×[ASCIIString(clave) ASCIIString(valor)].
    /// Todos los campos del objeto tal cual están en obj.dat.
    /// </summary>
    public static void ObjEditorDetail(Connection conn, int objIndex, List<(string Key, string Value)> fields)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ObjEditorDetail);
        p.WriteInteger((short)objIndex);
        byte count = (byte)System.Math.Min(fields.Count, 255);
        p.WriteByte(count);
        for (int i = 0; i < count; i++)
        {
            p.WriteASCIIString(fields[i].Key ?? "");
            p.WriteASCIIString(fields[i].Value ?? "");
        }
        Send(conn, p);
    }

    /// <summary>
    /// ObjInfoUpdate: Integer(objIndex) + Byte(count) + count×[ASCIIString(clave) ASCIIString(valor)].
    /// Stats ya resueltos de un objeto que acaba de cambiar en caliente, para que el catálogo
    /// del cliente (assets/items.json, horneado) se actualice sin recargar la página.
    /// </summary>
    public static void ObjInfoUpdate(Connection conn, int objIndex, List<(string Key, string Value)> fields)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ObjInfoUpdate);
        p.WriteInteger((short)objIndex);
        byte count = (byte)System.Math.Min(fields.Count, 255);
        p.WriteByte(count);
        for (int i = 0; i < count; i++)
        {
            p.WriteASCIIString(fields[i].Key ?? "");
            p.WriteASCIIString(fields[i].Value ?? "");
        }
        Send(conn, p);
    }

    /// <summary>ObjEditorResult: Byte(ok) Integer(objIndex) ASCIIString(message).</summary>
    public static void ObjEditorResult(Connection conn, bool ok, int objIndex, string message)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.ObjEditorResult);
        p.WriteByte((byte)(ok ? 1 : 0));
        p.WriteInteger((short)objIndex);
        p.WriteASCIIString(message ?? "");
        Send(conn, p);
    }

    /// <summary>BalanceEditorDetail: Byte(count) + count×[ASCIIString(clave) ASCIIString(valor)]. Valores actuales de Golpe/Hechizo.</summary>
    public static void BalanceEditorDetail(Connection conn, List<(string Key, string Value)> fields)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BalanceEditorDetail);
        byte count = (byte)System.Math.Min(fields.Count, 255);
        p.WriteByte(count);
        for (int i = 0; i < count; i++)
        {
            p.WriteASCIIString(fields[i].Key ?? "");
            p.WriteASCIIString(fields[i].Value ?? "");
        }
        Send(conn, p);
    }

    /// <summary>BalanceEditorResult: Byte(ok) ASCIIString(message).</summary>
    public static void BalanceEditorResult(Connection conn, bool ok, string message)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.BalanceEditorResult);
        p.WriteByte((byte)(ok ? 1 : 0));
        p.WriteASCIIString(message ?? "");
        Send(conn, p);
    }

    /// <summary>DamageEditorPreviewResult: desglose de daño calculado por Combat.PreviewSpellDamage.</summary>
    public static void DamageEditorPreviewResult(Connection conn, Game.Combat.DamageBreakdown b)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.DamageEditorPreviewResult);
        p.WriteInteger((short)b.BaseMagnitud);
        p.WriteInteger((short)b.LevelScaling);
        p.WriteInteger((short)b.IntBonus);
        p.WriteInteger((short)b.StaffBonus);
        p.WriteLong((int)(b.RazaMult * 1000));
        p.WriteInteger((short)b.Resistencia);
        p.WriteInteger((short)b.ReduccionAnillo);
        p.WriteInteger((short)b.Final);
        Send(conn, p);
    }

    /// <summary>IntervalConfig: Integer(atacarMs) Integer(lanzarSpellMs). Sincroniza el gate local del cliente.</summary>
    public static void IntervalConfig(Connection conn, long atacarMs, long lanzarSpellMs)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.IntervalConfig);
        p.WriteInteger((short)atacarMs);
        p.WriteInteger((short)lanzarSpellMs);
        Send(conn, p);
    }

    // ============================================================
    //  Editor de hechizos en vivo para GMs (NUEVO, no VB6) — mismo patrón que ObjEditor*
    // ============================================================

    /// <summary>
    /// SpellEditorList: Integer(count) + count×[Integer(spellIndex) Integer(tipo) ASCIIString(name)].
    /// Catálogo resumido para la lista del editor.
    /// </summary>
    public static void SpellEditorList(Connection conn, List<(int Index, int Tipo, string Name)> spells)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SpellEditorList);
        p.WriteInteger((short)spells.Count);
        foreach (var s in spells)
        {
            p.WriteInteger((short)s.Index);
            p.WriteInteger((short)s.Tipo);
            p.WriteASCIIString(s.Name ?? "");
        }
        Send(conn, p);
    }

    /// <summary>
    /// SpellEditorDetail: Integer(spellIndex) + Byte(count) + count×[ASCIIString(clave) ASCIIString(valor)].
    /// Todos los campos del hechizo tal cual están en Hechizos.dat (vacío si el índice no existe todavía).
    /// </summary>
    public static void SpellEditorDetail(Connection conn, int spellIndex, List<(string Key, string Value)> fields)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SpellEditorDetail);
        p.WriteInteger((short)spellIndex);
        byte count = (byte)System.Math.Min(fields.Count, 255);
        p.WriteByte(count);
        for (int i = 0; i < count; i++)
        {
            p.WriteASCIIString(fields[i].Key ?? "");
            p.WriteASCIIString(fields[i].Value ?? "");
        }
        Send(conn, p);
    }

    /// <summary>SpellEditorResult: Byte(ok) Integer(spellIndex) ASCIIString(message).</summary>
    public static void SpellEditorResult(Connection conn, bool ok, int spellIndex, string message)
    {
        var p = new ByteQueue();
        p.WriteByte((byte)ServerPacketID.SpellEditorResult);
        p.WriteByte((byte)(ok ? 1 : 0));
        p.WriteInteger((short)spellIndex);
        p.WriteASCIIString(message ?? "");
        Send(conn, p);
    }
}
