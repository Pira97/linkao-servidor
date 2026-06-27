namespace ServidorCS.Network;

// Generado 1:1 desde Protocol.bas (VB6). Valores secuenciales salvo asignacion explicita (= N).
// NO reordenar: los valores DEBEN coincidir con el cliente Godot. Ver [[vb6_enum_packet_ids]].
public enum ServerPacketID : short
{
    LoggedSuccessful = 0,
    Logged = 1,
    RemoveDialogs = 2,
    RemoveCharDialog = 3,
    NavigateToggle = 4,
    MontateToggle = 5,
    Disconnect = 6,
    CommerceEnd = 7,
    BankEnd = 8,
    CommerceInit = 9,
    BankInit = 10,
    UpdateSta = 11,
    UpdateMana = 12,
    UpdateHP = 13,
    UpdateGold = 14,
    UpdateCreditos = 15,
    UpdateExp = 16,
    ChangeMap = 17,
    PosUpdate = 18,
    ChatOverHead = 19,
    ChatOverHeadLocale = 20,
    ConsoleMsg = 21,
    GuildChat = 22,
    ShowMessageBox = 23,
    UserIndexInServer = 24,
    UserCharIndexInServer = 25,
    CharacterCreate = 26,
    CharacterRemove = 27,
    CharacterMove = 28,
    ForceCharMove = 29,
    CharacterChange = 30,
    CharacterChangeSlot = 31,
    ObjectCreate = 32,
    ObjectDelete = 33,
    BlockPosition = 34,
    PlayMidi = 35,
    PlayWave = 36,
    guildList = 37,
    AreaChanged = 38,
    PauseToggle = 39,
    RainToggle = 40,
    CreateFX = 41,
    UpdateUserStats = 42,
    UpdateUserStatsForLevel = 43,
    WorkRequestTarget = 44,
    ChangeInventorySlot = 45,
    ChangeBankSlot = 46,
    ChangeSpellSlot = 47,
    atributes = 48,
    BlacksmithWeapons = 49,
    BlacksmithArmors = 50,
    BlacksmithHelmet = 51,
    BlacksmithShield = 52,
    CarpenterObjects = 53,
    SastreObjects = 54,
    AlquimiaObjects = 55,
    RestOK = 56,
    SendMsgBox = 57,
    Blind = 58,
    Dumb = 59,
    ChangeNPCInventorySlot = 60,
    UpdateHungerAndThirst = 61,
    MiniStats = 62,
    LevelUp = 63,
    SetInvisible = 64,
    MeditateToggle = 65,
    BlindNoMore = 66,
    DumbNoMore = 67,
    SendSkills = 68,
    TrainerCreatureList = 69,
    guildNews = 70,
    OfferDetails = 71,
    AlianceProposalsList = 72,
    PeaceProposalsList = 73,
    CharacterInfo = 74,
    GuildLeaderInfo = 75,
    GuildMemberInfo = 76,
    GuildDetails = 77,
    ParalizeOK = 78,
    ShowUserRequest = 79,
    TradeOK = 80,
    BankOK = 81,
    Pong = 82,
    UpdateTagAndStatus = 83,
    LocaleMsg = 84,
    ShowSOSForm = 85,
    UserNameList = 86,
    CorreoList = 87,
    UpdateStrenght = 88,
    UpdateDexterity = 89,
    Premios = 90,
    EfectoCharParticula = 91,
    AddPJ = 92,
    EfectoTerrenoParticula = 93,
    EfectoTerrenoFX = 94,
    CharStatus = 95,
    MensajeSigno = 96,
    MarcamosSkin = 97,
    MostrarUbicacion = 98,
    CargarSkin = 99,
    CharMsgStatus = 100,
    CharMsgStatusNPC = 101,
    AbrirFormularios = 102,
    ChangeInventorySlotUser = 103,
    AuraToChar = 104,
    UpdateSed = 105,
    UpdateHambre = 106,
    EjecutarAccion = 107,
    PartyInvitation = 108,
    PartyMemberList = 109,
    PartyMessage = 110,
    RunaCastProgress = 111,
    PartyMemberHP = 112,
    AmbientLight = 113,
    NpcParalysisProgress = 114,
    CreateArrowProjectile = 115,
    PartyMemberPos = 116,         // ASCII(nombre) + Integer(mapa) + Byte(x) + Byte(y): posición de un compañero de grupo (minimapa/mapamundi)
    LevelUpFx = 117,              // Integer(charIndex) + Byte(nivel): subida de nivel VISIBLE. LevelUp(63) va
                                  // solo al que sube y es un dato de interfaz (puntos de skill libres); este
                                  // se difunde al área para que los de alrededor vean el efecto. Requiere
                                  // ClientCaps bit1 (ver Connection.SoportaEfectosNuevos).
    FurorIgneoTimers = 150,
    TempleCooldown = 151,
    MinimapPuntos = 152,
    GMFlyingToggle = 153,
    UserCommerceInit = 154,
    UserCommerceEnd = 155,
    UserCommerceUpdate = 156,
    UserCommerceConfirm = 157,
    UserCommerceInitRequest = 158,
    AuctionList = 159,
    ShopPaymentURL = 160,
    ShopItemGranted = 161,
    EventoExpBonus = 162,
    CharTyping = 163,
    MacrosConfig = 164,
    MapNpcsList = 165,
    ShopCatalog = 166,
    DonationHistory = 167,
    DonorRanking = 168,
    // --- Sistema de reportes / tickets (NUEVO, no VB6) ---
    ReportSubmitted = 169, // Byte ok, Long reportId, ASCIIString message
    ReportList = 170,      // Byte count, [Long id, Byte cat, Byte status, ASCIIString reporter, ASCIIString subject, ASCIIString fecha, ASCIIString gm, Byte replies]
    ReportDetail = 171,    // cabecera + hilo de respuestas
    ReportNotify = 172,    // Byte kind (0 info,1 ok,2 borrado), ASCIIString message

    // --- Editor de objetos en vivo para GMs (NUEVO, no VB6) ---
    ObjEditorList = 173,   // Integer count, count×[Integer objIndex, Byte type, Byte subtipo, Long grh, ASCIIString name]
    ObjEditorDetail = 174, // Integer objIndex, Byte count, count×[ASCIIString clave, ASCIIString valor]
    ObjEditorResult = 175, // Byte ok, Integer objIndex, ASCIIString message

    // --- Buscador de NPCs para el panel GM (NUEVO, no VB6) ---
    NpcCatalog = 176,      // Integer count, count×[Integer npcIndex, Byte npcType, ASCIIString name]

    // --- Torneos PvP con cola automática (NUEVO, no VB6) ---
    TorneoState = 177,     // Byte yourMode, Byte q1,q2,q3, Byte cd1,cd2,cd3, Byte active, Byte activeMode, Byte activeTeams, Byte yourStatus, ASCIIString info
    TorneoCountdown = 178, // Byte seconds (>0 muestra número grande en pantalla; 0 lo oculta)

    // --- Ciclo Día/Noche (NUEVO, no VB6) ---
    DayNightInfo = 179,    // Byte hora (0-23), Byte minuto (0-59), Byte inDungeon (1=neutro)

    // --- Battle Pass / Pase de Temporada (NUEVO, no VB6) ---
    BattlePassInfo = 180,   // estado completo: temporada + tabla de niveles + flags de reclamado
    BattlePassUpdate = 181, // Long puntos, Byte nivel, ASCIIString mensaje (push tras ganar puntos/acción)

    // --- Lista de amigos para el panel de la solapa Amigos (NUEVO, no VB6) ---
    AmigosList = 182,       // Byte count, count×[ASCIIString nombre, Byte online (0/1), Integer mapa]
    AmigoRequest = 183,     // ASCIIString nombre del solicitante ("" = no hay solicitud pendiente)

    // --- Chequeo de nombre libre en la pantalla Crear Personaje (NUEVO, no VB6) ---
    NombreCheckResult = 184, // Byte disponible (1=libre, 0=inválido/en uso), ASCIIString msg

    // --- Logros (NUEVO, no VB6): respuesta a /logros para pintar la ventana ---
    LogrosInfo = 185, // Byte count, count×[ASCIIString cat, ASCIIString desc, ASCIIString reward,
                      //   Long actual, Long objetivo, Byte completado, Byte repetible, Integer veces, Integer body]

    // --- Scrolls de EXP/Oro (NUEVO, no VB6): estado del buff para el indicador del HUD ---
    ScrollBuff = 186, // Byte tipo (1=exp, 2=oro), Byte mult, Long segundos restantes (0 = terminó)
    SeamlessCross = 187, // Mundo continuo: Integer nuevoMapa, Integer globalX, Integer globalY. Cruce de
                         // borde SIN teardown (a diferencia de ChangeMap): el cliente NO limpia char_list.

    // --- Rayo de casteo de hechizos (NUEVO, no VB6): arco eléctrico procedural caster→objetivo ---
    SpellBeam = 188, // Integer charOrigen, Integer charDestino (0=terreno), Integer xO, yO, xD, yD (tiles),
                     // Byte tipo (0 arcano, 1 daño, 2 parálisis, 3 soporte/cura, 4 veneno)
    QuestInfo = 189, // Misiones: Byte origen (0=log del jugador, 1=oferta de NPC), ASCIIString npcName,
                     // Byte count, count×[Integer id, ASCIIString nombre, ASCIIString historia,
                     // ASCIIString reward, Byte estado, Byte nivelMin, Byte repetible, ASCIIString extra,
                     // Byte numObj, numObj×[Byte tipo (0=matar,1=juntar), ASCIIString targetName,
                     // Integer actual, Integer requerido, Integer body, Long grh]]

    // --- Modo espía (NUEVO, no VB6). OJO: estos TRES solo se le mandan a clientes que
    //     declararon soporte con ClientPacketID.ClientCaps (ver Connection.SoportaExtras).
    //     El cliente Godot viejo NO los declara y por lo tanto no los recibe nunca: su
    //     dispatcher no sabe saltear paquetes desconocidos y se desincronizaría. ---
    EspiaVista = 190,         // Byte activo, Integer charIndex espiado, ASCIIString nombre → AL ESPÍA
    EspiaReportar = 191,      // Byte activo → AL ESPIADO: que empiece/deje de reportar su mouse Y
                              //   el estado de su interfaz (solapa y ventanas abiertas). Silencioso.
    EspiaMousePos = 192,      // Long xPx, Long yPx (mundo = tile global*32), Integer nx, Integer ny
                              //   (posición en PANTALLA normalizada 0..10000, para cuando el mouse
                              //   está sobre el HUD y no sobre el mapa), Byte botones (bit0 izq,
                              //   bit1 der, bit6 = está sobre el mundo, bit7 = click nuevo)
                              //   → AL ESPÍA: el mouse del espiado
    EspiaUi = 193,            // ASCIIString estado → AL ESPÍA: qué tiene abierto el espiado en su
                              //   interfaz. Formato "solapa|ventana1,ventana2|slotInv|slotHechizo"
                              //   (lo arma y lo interpreta el cliente, ver game.html::espiaEstadoUi).

    // --- Editor de hechizos en vivo para GMs (NUEVO, no VB6) — mismo patrón que ObjEditor* ---
    SpellEditorList = 194,   // Integer count, count×[Integer spellIndex, Integer tipo, ASCIIString name]
    SpellEditorDetail = 195, // Integer spellIndex, Byte count, count×[ASCIIString clave, ASCIIString valor]
    SpellEditorResult = 196, // Byte ok, Integer spellIndex, ASCIIString message

    // --- MEJORA-005 (NUEVO, no VB6): barra de maná de los compañeros de grupo, mismo
    //     criterio que PartyMemberHP (112) pero para maná. ---
    PartyMemberMana = 197, // Integer charIndex, Integer minMana, Integer maxMana

    // --- MEJORA-005 (NUEVO, no VB6): señal de grupo (ayuda/recorrido). Se difunde a TODOS
    //     los miembros (incluido quien la mandó, para que la vea marcada en su propio
    //     mapamundi). El cliente la hace vivir 5 minutos y la dibuja en el Mapamundi. ---
    PartySignal = 198, // Byte tipo (1=ayuda, 2=recorrido), ASCIIString remitente,
                       // Integer mapa, Byte x, Byte y

    // --- Guerra mundial de facciones (NUEVO, no VB6): posición de TODOS los bots de los tres
    //     ejércitos, para verlos en vivo en el Mapamundi del cliente web. Sólo se manda a GMs
    //     y sólo mientras la guerra está activa (ver GuerraFacciones.BroadcastPosiciones). ---
    GuerraBots = 199, // Integer count, count×[Integer mapa, Byte x, Byte y, Byte faccion]

    // --- Partículas premium de meditación (NUEVO, no VB6) ---
    PremiumParticlesCatalog = 200, // Byte count, count×[Integer itemId, Integer streamId, Long precioCreditos, ASCIIString nombre]
    PremiumParticlesOwned = 201,   // Byte count, count×Integer streamId ; luego Integer equipado (0 = ninguno)
    PremiumParticleGranted = 202,  // Integer streamId, ASCIIString nombre

    // --- Bóvedas premium (NUEVO, no VB6). Solo salen a clientes con SoportaBovedaPremium
    //     (bit2 de ClientCaps) — ver Connection.SoportaBovedaPremium. ---
    BankInitPremium = 203,       // Boolean desbloqueada
    ChangeBankSlotPremium = 204, // Byte vaultId (1|2), Byte slot, Integer objIndex, Integer amount, Long valor

    // --- Catálogo de clases de bot para el panel GM "Invocar bots" (NUEVO, no VB6) ---
    BotClasesCatalog = 205, // Byte count, count×[Byte clase, Byte razaDefault, ASCIIString nombre]

    // --- GmWatchHP/Mana (NUEVO, no VB6): vida/maná de CUALQUIER personaje visible, para que
    //     los GM (FaccionStatus>=STATUS_CONSEJERO) vean el número en vivo sobre cada jugador,
    //     no solo los de su propio grupo. Mismo formato que PartyMemberHP/Mana; se manda solo
    //     a conexiones GM que ven ese char (AreaVisibility.VeChar) — ver Game/GmWatch.cs. ---
    GmWatchHP = 206,   // Integer charIndex, Integer minHP, Integer maxHP
    GmWatchMana = 207, // Integer charIndex, Integer minMana, Integer maxMana

    // --- PetInfo (NUEVO, no VB6): estado completo de la mascota compañera persistente del
    //     propio jugador, para el panel de mascota del cliente (vida, nivel, exp, hechizos
    //     conocidos). Se manda solo al dueño (ver Combat.EnviarPetInfo), no es un broadcast. ---
    // Byte tipo (PetLeveling.PetTipo, 0 = sin mascota viva), Byte nivel, Integer exp,
    // Integer expSiguiente (0 = nivel máximo), Integer hpActual, Integer hpMax,
    // ASCIIString nombre, Byte cantHechizos, cantHechizos×Integer hechizoIndex.
    PetInfo = 208,

    // --- Mochila de la mascota (NUEVO, no VB6): 12 slots que el jugador carga y que la mascota
    //     puede llevar a la bóveda con 'regresar al hogar'. Byte cantSlots + N×[Integer objIndex,
    //     Integer amount, ASCIIString nombre, Integer grhIndex]. Solo al dueño. ---
    PetInv = 209,

    // --- ObjInfoUpdate (NUEVO, no VB6): el catálogo de objetos del cliente (nombre, tipo,
    //     daño, defensa) NO viaja en el paquete de inventario — el cliente web lo tiene
    //     horneado en assets/items.json. Sin esto, editar un objeto con /editobj cambia el
    //     combate al instante pero NADIE ve el número nuevo en el tooltip hasta rehornear el
    //     JSON y recargar la página. Se manda a todos los online al guardar (ObjEditor.Save).
    //     Mismo formato que ObjEditorDetail; el cliente ignora las claves que no conoce.
    //     Name/GrhIndex sólo se incluyen si el GM los tocó, porque el cliente tiene su propia
    //     capa de presentación (locale_obj_es.ind renombra objetos a propósito). ---
    ObjInfoUpdate = 210, // Integer objIndex, Byte count, count×[ASCIIString clave, ASCIIString valor]

    // --- Editor en vivo de intervalos de Golpe/Hechizo (panel GM, NUEVO no VB6) — mismo patrón que ObjEditor* ---
    BalanceEditorDetail = 211, // Byte count, count×[ASCIIString clave, ASCIIString valor]
    BalanceEditorResult = 212, // Byte ok, ASCIIString message

    // IntervalConfig (NUEVO, no VB6): valores actuales de Golpe/Hechizo para que el cliente
    // sincronice su gate LOCAL (evita spamear clics que el server rechazaría en silencio).
    // Se manda al loguear y a TODOS los online cuando un GM los cambia (BalanceEditor.Save).
    IntervalConfig = 213, // Integer atacarMs, Integer lanzarSpellMs

    // --- Editor de balance de daño (panel GM, pestaña "Daño") — respuesta al preview ---
    DamageEditorPreviewResult = 214, // Integer baseMagnitud, Integer levelScaling, Integer intBonus,
                                      //   Integer staffBonus, Long razaMultX1000, Integer resistencia,
                                      //   Integer reduccionAnillo, Integer final

    // Cosméticos (cascos/escudos/monturas) comprados con créditos de MercadoPago, ver CreditItems.cs.
    CreditItemsCatalog = 215,
    CreditItemGranted = 216,
}
