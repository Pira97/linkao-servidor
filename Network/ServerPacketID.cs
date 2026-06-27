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
}
