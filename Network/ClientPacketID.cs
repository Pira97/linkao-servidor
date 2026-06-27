namespace ServidorCS.Network;

// Generado 1:1 desde Protocol.bas (VB6). Valores secuenciales salvo asignacion explicita (= N).
// NO reordenar: los valores DEBEN coincidir con el cliente Godot. Ver [[vb6_enum_packet_ids]].
public enum ClientPacketID : short
{
    Walk = 0,
    LoginExistingChar = 1,
    LoginNewChar = 2,
    Talk = 3,
    Whisper = 4,
    RequestPositionUpdate = 5,
    attack = 6,
    PickUp = 7,
    CombatModeToggle = 8,
    ResuscitationSafeToggle = 9,
    RequestGuildLeaderInfo = 10,
    RequestAtributes = 11,
    RequestSkills = 12,
    RequestMiniStats = 13,
    CommerceEnd = 14,
    BankEnd = 15,
    Drop = 16,
    DropDestroy = 17,
    CastSpell = 18,
    LeftClick = 19,
    DoubleClick = 20,
    Work = 21,
    UseItem = 22,
    CraftBlacksmith = 23,
    CraftCarpenter = 24,
    Craftalquimia = 25,
    CraftSastre = 26,
    WorkLeftClick = 27,
    CreateNewGuild = 28,
    EquipItem = 29,
    EquiparSkin = 30,
    ComprarSkin = 31,
    GuardarSkinPermanente = 32,
    ChangeHeading = 33,
    ModifySkills = 34,
    Train = 35,
    CommerceBuy = 36,
    BankExtractItem = 37,
    CommerceSell = 38,
    BankDeposit = 39,
    MoveSpell = 40,
    MoveBank = 41,
    ClanCodexUpdate = 42,
    GuildAcceptPeace = 43,
    GuildRejectAlliance = 44,
    GuildRejectPeace = 45,
    GuildAcceptAlliance = 46,
    GuildOfferPeace = 47,
    GuildOfferAlliance = 48,
    GuildAllianceDetails = 49,
    GuildPeaceDetails = 50,
    GuildRequestJoinerInfo = 51,
    GuildAlliancePropList = 52,
    GuildPeacePropList = 53,
    GuildDeclareWar = 54,
    GuildNewWebsite = 55,
    GuildAcceptNewMember = 56,
    GuildRejectNewMember = 57,
    GuildKickMember = 58,
    GuildUpdateNews = 59,
    GuildMemberInfo = 60,
    GuildOpenElections = 61,
    GuildRequestMembership = 62,
    GuildRequestDetails = 63,
    Online = 64,
    Quit = 65,
    GuildLeave = 66,
    Rest = 67,
    ConnectAccount = 68,
    CreateNewAccount = 69,
    Meditate = 70,
    Resucitate = 71,
    RequestStats = 72,
    CommerceStart = 73,
    BankStart = 74,
    Enlist = 75,
    Information = 76,
    Reward = 77,
    UpTime = 78,
    GuildMessage = 79,
    CentinelReport = 80,
    GuildOnline = 81,
    GMRequest = 82,
    ChangeDescription = 83,
    GuildVote = 84,
    Gamble = 85,
    BankExtractGold = 86,
    BankDepositGold = 87,
    Denounce = 88,
    PidePremios = 89,
    RPremios = 90,
    GuildFundate = 91,
    GuildFundation = 92,
    Ping = 93,
    GMCommands = 94,
    InitCrafting = 95,
    ShowGuildNews = 96,
    SwapObjects = 97,
    Packets_Correo = 98,
    EnviarCorreo = 99,
    RetirarFaccion = 100,
    RegresarHogar = 101,
    ParticulaUsuario = 102,
    ProcesosLogin = 103,
    TransferGOLD = 104,
    SeleccionarHogar = 105,
    Casamiento = 106,
    divorciar = 107,
    HayEventos = 108,
    CloseGuild = 109,
    AddAmigos = 110,
    DelAmigos = 111,
    OnAmigos = 112,
    MsgAmigos = 113,
    AbrirForms = 114,
    DesconectarCuenta = 115,
    PartyCreate = 116,
    PartyJoin = 117,
    PartyLeave = 118,
    PartyKick = 119,
    PartyMessage = 120,
    PartyOnline = 121,
    PartyAccept = 122,
    PartyReject = 123,
    ArenaJoin = 124,
    UserCommerceStart = 125,
    UserCommerceOfferGold = 126,
    UserCommerceOfferItem = 127,
    UserCommerceConfirm = 128,
    UserCommerceCancel = 129,
    UserCommerceReqUpdate = 130,
    AuctionCreate = 131,
    AuctionBid = 132,
    Typing = 134,
    RequestMacrosConfig = 135,
    SaveMacrosConfig = 136,
    QueryMapNpcs = 137,
    ShopBuyItem = 138,
    RequestShopData = 139,
    // Cosméticos (cascos/escudos/monturas) comprados con créditos de MercadoPago, ver CreditItems.cs.
    RequestCreditItems = 188,
    BuyCreditItem = 189,
    // --- Sistema de reportes / tickets (NUEVO, no VB6) ---
    ReportCreate = 140,        // Byte category, ASCIIString subject, ASCIIString body
    ReportListRequest = 141,   // Byte filter
    ReportDetailRequest = 142, // Long reportId
    ReportAction = 143,        // Long reportId, Byte action, ASCIIString message

    // --- Editor de objetos en vivo para GMs (NUEVO, no VB6) ---
    ObjEditorRequest = 144,       // (sin payload) pide el catálogo completo de objetos
    ObjEditorDetailRequest = 145, // Integer objIndex: pide todos los campos del objeto
    ObjEditorSave = 146,          // Integer objIndex, Byte count, count×[ASCIIString clave, ASCIIString valor]

    // --- Buscador de NPCs para el panel GM (NUEVO, no VB6) ---
    NpcCatalogRequest = 147,      // (sin payload) pide el catálogo de NPCs (índice + nombre)

    // --- Torneos PvP con cola automática (NUEVO, no VB6) ---
    TorneoAction = 148,           // Byte action (0=pedir estado, 1=entrar a cola, 2=salir), Byte mode (1/2/3)

    // --- Info de hechizo (panel Conjuros) ---
    SpellInfoRequest = 149,       // Byte slot: pide la descripción del hechizo en ese slot (la imprime en consola)

    // --- Recarga global del editor de objetos (NUEVO, no VB6) ---
    ObjEditorReloadAll = 150,     // (sin payload) relee TODO el obj.dat de disco en caliente (GM)

    // --- Bots de prueba invocables (NUEVO, no VB6) ---
    SpawnBot = 151,               // ASCIIString clase ("all" = uno de cada), Byte raza (0=default),
                                   // Byte faccion (0=ninguna), Byte nivel (1-50, 0/fuera de rango = 50),
                                   // Byte progresivo (0/1: arranca con equipo de ese nivel, no el set
                                   // sacro, y sube de nivel matando NPCs; sólo aplica a spawns normales)

    // --- Battle Pass / Pase de Temporada (NUEVO, no VB6) ---
    BattlePassRequest = 152,      // (sin payload) pide el estado completo del pase
    BattlePassClaim = 153,        // Byte nivel, Byte carril (0=gratis, 1=premium)
    BattlePassBuy = 154,          // Byte metodo (0=créditos donador, 1=MercadoPago)

    // --- Lista de amigos estructurada para el panel de la solapa Amigos (NUEVO, no VB6) ---
    RequestAmigosList = 155,      // (sin payload) pide la lista de amigos con estado online/mapa
    AmigoReject = 156,            // ASCIIString nombre: rechaza la solicitud de amistad recibida de ese jugador
    PartyInviteByName = 157,      // ASCIIString nombre: invita a ese jugador al grupo por nombre (panel de Amigos)

    // --- Chequeo de nombre libre en la pantalla Crear Personaje (NUEVO, no VB6) ---
    CheckNombrePj = 158,          // ASCIIString nombre: pregunta si el nombre está libre ANTES de crear (pre-login)

    // --- Sistema de Misiones (NUEVO, no VB6) ---
    QuestAccept = 159,            // Integer questId: acepta la misión (frente al NPC dador)
    QuestTurnIn = 160,            // Integer questId: entrega la misión completada (frente al dador)
    QuestAbandon = 161,           // Integer questId: abandona una misión activa
    QuestLogRequest = 162,        // (sin payload) pide el log de misiones activas (QuestInfo origen=0)

    // --- Capacidades del cliente (NUEVO, no VB6) ---
    // El cliente WEB lo manda apenas entra al mundo para decir "entiendo los paquetes
    // nuevos". El Godot viejo no lo manda, y por eso el server nunca le envía los paquetes
    // que él no sabría leer (su dispatcher no puede saltear ids desconocidos). Es el
    // mecanismo para extender el protocolo sin romper a nadie.
    ClientCaps = 163,             // Byte caps (bit0 = paquetes del modo espía, bit1 = efectos visuales nuevos: LevelUpFx)

    // --- Modo espía: el cliente ESPIADO reporta dónde tiene el mouse (NUEVO, no VB6) ---
    // Solo se manda mientras el server lo pidió con EspiaReportarMouse(1).
    EspiaMouse = 164,             // Long xPx, Long yPx (mundo = tile global*32), Integer nx,
                                  // Integer ny (pantalla normalizada 0..10000), Byte botones
    EspiaUi = 165,                // ASCIIString estado de la interfaz (solapa + ventanas abiertas +
                                  // slots seleccionados). Se manda solo cuando CAMBIA.

    // --- Modo ESPECTADOR (NUEVO, no VB6): mirar el juego SIN entrar con un personaje ---
    // La conexión se autentica con una cuenta que tenga un personaje Dios y queda de puro
    // observador: nunca hace EnterWorld, así que para el mundo no existe (el AOI y todo el
    // resto saltean a los que no tienen UserLogged). Lo usa el panel de deploy.
    SpectateLogin = 166,          // ASCIIString cuenta, blockPrefixed pass, Integer version,
                                  //   ASCIIString objetivo ("" = solo quiero la lista de online)
    SpectateTarget = 167,         // ASCIIString nombre ("" = dejar de mirar): cambia de objetivo

    // --- Editor de hechizos en vivo para GMs (NUEVO, no VB6) — mismo patrón que ObjEditor* ---
    SpellEditorRequest = 168,       // (sin payload) pide el catálogo completo de hechizos
    SpellEditorDetailRequest = 169, // Integer spellIndex: pide todos los campos del hechizo
    SpellEditorSave = 170,          // Integer spellIndex, Byte count, count×[ASCIIString clave, ASCIIString valor]
                                     //   Si el índice no existe en Hechizos.dat todavía, LO CREA (a diferencia de ObjEditorSave).
    SpellEditorReloadAll = 171,     // (sin payload) relee TODO el Hechizos.dat de disco en caliente (GM)

    // --- MEJORA-005 (NUEVO, no VB6): señales de grupo (ayuda / recorrido) ---
    PartySignal = 172,              // Byte tipo (1=ayuda, 2=recorrido)

    // --- Partículas premium de meditación (NUEVO, no VB6): cosméticos comprados con
    //     CreditoDonador (el mismo saldo que ya carga MercadoPago con dinero real). ---
    RequestPremiumParticles = 173,  // (sin payload) pide catálogo + poseídas + equipada
    BuyPremiumParticle = 174,       // Integer itemId (id del catálogo, no streamId)
    EquipPremiumParticle = 175,     // Integer streamId (0 = desequipar, volver al de nivel/facción)

    // --- Bóvedas premium (NUEVO, no VB6): 2 solapas extra de bóveda, 80 slots c/u, por
    //     personaje, desbloqueadas comprándolas con CreditoDonador (Bank.ComprarBovedaPremium).
    //     Solo se mandan/reciben si el cliente declaró CAPS_BOVEDA_PREMIUM en ClientCaps. ---
    BankDepositPremium = 176,       // Byte vaultId (1|2), Byte slot (inventario), Integer amount
    BankExtractItemPremium = 177,   // Byte vaultId (1|2), Byte slot (bóveda premium), Integer amount
    BuyBovedaPremium = 178,         // (sin payload) compra el combo (ambas solapas premium)

    // --- Catálogo de clases de bot para el panel GM "Invocar bots" (NUEVO, no VB6) ---
    // Antes bots_ui.js tenía las 10 clases hardcodeadas; ahora Bots._clases sale de
    // Dat/BotClases.dat (editable sin recompilar), así que el panel las pide en vivo.
    BotClasesRequest = 179,         // (sin payload) pide el catálogo de clases de bot invocables

    // --- Mascota compañera: elegirla DESPUÉS de creado el personaje (NUEVO, no VB6) ---
    // Para los personajes que existían ANTES de que la mascota fuera parte de la creación: no
    // tienen [MASCOTA] en el .chr y quedaron sin nada. El server sólo la acepta si el personaje
    // todavía no tiene ninguna (PetTipo == 0) y el tipo corresponde a su clase.
    PetElegir = 180,                // Byte tipo (PetLeveling.PetTipo), ASCIIString nombre

    // --- Mochila de la mascota (NUEVO, no VB6): la mascota como MULA. Ver Game/PetInventory.cs ---
    PetInvGuardar = 181,            // Byte slotInventario, Long cantidad  (jugador → mascota)
    PetInvSacar = 182,              // Byte slotMochila, Long cantidad     (mascota → jugador)
    PetHogar = 183,                 // (sin payload) la mascota vuelve al hogar y deja todo en la bóveda
    PetInvRequest = 184,            // (sin payload) pide la mochila (al abrir el panel)

    // --- Editor en vivo de intervalos de Golpe/Hechizo (panel GM, NUEVO no VB6) — mismo patrón que ObjEditor* ---
    BalanceEditorRequest = 185,     // (sin payload) pide los valores actuales (Golpe/Hechizo)
    BalanceEditorSave = 186,        // Byte count, count×[ASCIIString clave, ASCIIString valor]

    // --- Editor de balance de daño (panel GM, pestaña "Daño") — combina SpellEditor+ObjEditor, mismo patrón ---
    DamageEditorPreviewRequest = 187, // Integer spellIndex, Integer staffObjIndex, Integer casterLevel,
                                       //   Integer casterINT, Byte isPvP, Integer targetResistencia
}
