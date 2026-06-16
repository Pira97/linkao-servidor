# Estado de Migración VB6 → C# (ServidorCS)

Tabla maestra de los ClientPacketID (lo que el cliente Godot envía). Metodología 1:1:
Cliente Godot → handler VB6 (Protocol.bas) → implementación C#.

---

# ===== FIX SISTEMA DE EQUIPAR (2026-06-07) =====

Reportado: no funcionaban auras de items, monturas ni barcas (y dudas con casco/escudo/armadura).
Análisis del VB6 (EquiparInvItem/DoEquita/DoNavega) y del cliente Godot. Corregido:
- **Auras** (obj.dat "Aura", packet **AuraToChar 104**): armas/armadura/escudo/casco/anillo ahora setean
  el aura (Char.*_Aura) y la difunden al equipar/desequipar. ObjData.Aura agregado (antes no se parseaba).
  Persistidas en .chr [INIT] *Aura y enviadas en el CharacterCreate del login. El cliente ya soportaba
  AuraToChar y las auras en CharacterCreate; el server no las mandaba.
- **Monturas** (`DoEquita`): antes era lambda vacía (no hacía nada). Ahora cambia el body al de la
  montura (NumRopaje), pone Montando, oculta arma, sonido 133, **MontateToggle**; al desmontar restaura
  apariencia a pie. Valida no-navegando.
- **Barcas** (`DoNavega`, ObjType.Barcos): no estaba en el switch (no equipable). Ahora cambia el body
  al del barco (NumRopaje/87), pone Navegando, sonido 133, **NavigateToggle**; al bajar restaura.
- **Casco/Escudo/Armadura/Arma**: ya enviaban anim por CharacterChange (alineado con el cliente, sin
  auras — el cliente tampoco las lee ahí); ahora además llevan su aura por AuraToChar.
- **Bug de persistencia corregido**: si el user deslogueaba montado/navegando, CharSaver persistía el
  body del caballo/barca. Ahora CloseUser restaura la apariencia a pie antes de guardar
  (Inventory.RestaurarAparienciaAPie, reutilizada por desmontar/desembarcar).
- Compila 0 errores, exe publicado, server bootea OK.
- ⚠️ Versión núcleo: DoEquita/DoNavega NO portan skin permanente, validación de skill
  (Equitacion/Navegacion) ni de costa/dungeon avanzada. El equipar/visual funciona; esos refinamientos
  quedan pendientes si se requieren.

# ===== FASE DE HARDENING / CIERRE (2026-06-07) =====

**VEREDICTO: LISTO PARA BETA CERRADA.** Compila 0 errores/0 warnings, exe self-contained publicado,
arranca limpio, estable bajo stress sintético. Quedan riesgos acotados (abajo), ninguno bloqueante
para una beta supervisada.

## 1. Auditoría de equivalencia
- **Cobertura de packets: 138/138** que el cliente Godot envía → 100% (137 con case/PayloadSpec +
  GMCommands por vía propia). Verificado por cruce automático protocol_outgoing.gd ↔ dispatch C#.
- **Bytes**: cada handler lee el payload exacto; PayloadSpec consume exacto el resto. Sin under/overread.
- **Desyncs: 0.** Packet desconocido → CIERRE CONTROLADO (no corrompe el stream) — validado en stress
  (id 243 basura → cierre limpio, server vivo).
- **Timings**: IA NPC 380ms (= TIMER_AI), autosave 300s, backup 1800s, clima/eventos/centinela por
  tick de 1s, GameTimer (hambre/sed/regen/veneno) con timestamps absolutos. 1:1 con los intervalos VB6.

## 2. PayloadSpec — limpieza
- Se eliminaron **29 entradas MUERTAS** (tenían `case` en el dispatch → inalcanzables): Casamiento,
  ClanCodexUpdate, CreateNewGuild, todos los Guild* con handler (Accept/Reject/Offer/Vote/Kick/etc.),
  GuildOnline, RequestGuildLeaderInfo, ShowGuildNews, RegresarHogar, divorciar, GuildFundation.
- Quedan **20 entradas consume-only reales** (packets que el cliente envía sin lógica portada o no
  portables 1:1). NINGÚN packet del cliente quedó sin cubrir (re-verificado: 0 sin cubrir).
- Riesgo: nulo (cambio puramente de redundancia; comportamiento idéntico).

## 3. Stress test (sintético, sin cliente Godot)
- **AntiDos**: 20 conexiones desde 1 IP → 5 aceptadas, **15 rechazadas** (cap por IP funciona).
- **Spam/unknown packet**: 200 bytes basura → CIERRE CONTROLADO, sin crash ni corrupción de stream.
- **Churn**: 100 connect/close → conexión nueva sigue entrando (AntiDos.Liberar NO filtra el contador);
  sin leak de slots/handles/RAM.
- **Idle**: ~0% CPU (loop con await Task.Delay, sin busy-wait), RAM ~36 MB estable, 10 threads.
- **Carga de gameplay AUTENTICADO** (harness propio `LoadBot/`): mini-cliente C# que hace el flujo
  real (CreateNewAccount + LoginNewChar con password cifrada shift + XOR de sesión) y mueve bots.
  Resultado: **5/5 bots entraron al mundo y se movieron 25s sin desync ni kick** (la alineación
  XOR/protocolo aguantó tráfico sostenido), server estable, CPU ~0%. RAM 32→71 MB al cargar el mapa
  + NPCs en caché (no leak). **AntiDos limita a 5 conexiones/IP** → para >5 concurrentes se necesitan
  varias IPs (clientes reales) o subir el cap temporalmente. La capa autenticada quedó validada.

## 4. Seguridad
- **Anti-flood conexiones**: AntiDos (5/IP) ✅. **Rate-limit de packets**: AntiCheat.VerificarLimitePaquetes
  (30/seg manual, 10/seg autopot) ✅. **Anti-autoclicker** (patrón de intervalos) ✅. **Centinela**
  anti-macro ✅. **Packet desconocido** → cierre controlado ✅.
- **Edge cases networking**: bloque prefijado se lee con largo validado (NotEnoughData re-encola);
  password como BYTES crudos (no CP1252 lossy). ✅

## 5. Persistencia (sin pérdida de datos)
- CharSaver abre el `.chr` EXISTENTE con `IniDocument` → **preserva secciones que no escribe** (GUILD,
  PENAS, etc.). Round-trip verificado: INIT/STATS/ATRIBUTOS/SKILLS/HECHIZOS/Inventory/BancoInventory+oro/
  CORREO/AMIGOS/FACCIONES/CASAMIENTO/FLAGS. Cuentas (.cnt), clanes (guildsinfo+members+sol), créditos.
- **Backups**: snapshot fechado en Backups/Auto_<ts>/ cada 30 min + limpieza (MaxBackupsGuardados) =
  rollback manual disponible.

## 6. RIESGOS REALES RESTANTES (para beta)
1. **Login duplicado** ✅ RESUELTO (2026-06-07): se portó la regla VB6 de **una sesión por cuenta**
   (CuentaConectada=1 → rechazo "Ya hay un usuario conectado con esta cuenta"). Aplicado en
   HandleLoginExistingChar y HandleLoginNewChar antes de entrar al mundo. Cierra el vector de dupe/clon.
   (Nota: depende de que CloseUser libere el slot al desconectar — lo hace; un drop sin OnClose dejaría
   el slot ocupado hasta el cleanup, edge raro.)
2. **MercadoPago** (MEDIO, operativo): cobro/polling requiere AccessToken real + testing vs API en vivo
   (maneja dinero; concurrencia HTTP↔game-loop a revisar al activar). Display funciona sin token.
3. **Carga de gameplay** (BAJO, antes MEDIO): la capa autenticada se validó con el harness `LoadBot`
   (login+movimiento sostenido, sin desync). Falta solo una corrida de ALTA concurrencia (>5) desde
   varias IPs o con el cap de AntiDos subido, y probar combate/party masivos — idealmente con el
   cliente Godot real o ampliando LoadBot (combate/ataque).
4. **P3.4 difusión por área** (BAJO): diferida; hoy se difunde por mapa completo (más tráfico, sin
   desync). Optimización, no bug.
5. **Relaciones de clan en memoria** (BAJO): guerra/paz/alianza no todas persisten; se pierden al reiniciar.
6. **Concurrencia**: el game loop es mono-hilo; AntiDos y MercadoPago usan locks/Task de fondo. Sin
   problemas observados, pero MercadoPago al activarse toca UserList desde Task → revisar.

## SISTEMAS NO PORTABLES 1:1 (el cliente Godot diverge del VB6)
- **TransferGOLD** (cliente manda solo amount, sin destino), **InitCrafting** (manda craft_type, no
  TotalItems/PorCiclo), **CombatModeToggle** (toggle visual del cliente, sin handler VB6),
  **QueryMapNpcs** (debug). Quedan consume-only alineados (sin desync).
## GAPS DE GAMEPLAY (consume-only, sin lógica; NO desync)
- Skins (EquiparSkin/ComprarSkin/GuardarSkinPermanente), Premios (Reward/PidePremios/RPremios),
  Gamble, Denounce, AbrirForms, DesconectarCuenta, ParticulaUsuario, Information (NPC facción),
  GuildAllianceDetails/GuildPeaceDetails, RequestMacrosConfig, HayEventos.

---

Leyenda: ✅ completo · ⚠️ parcial/stub · ❌ sin handler (cae en default)

---

## NÚCLEO JUGABLE (alta prioridad — uso diario)

| Packet | Handler VB6 | Estado C# | Notas |
|---|---|---|---|
| Walk | HandleWalk | ✅ | cancela trabajo/medita/descanso |
| ChangeHeading | HandleChangeHeading | ⚠️ | actualiza heading; falta broadcast CharacterChange |
| Talk | HandleTalk | ✅ | difusión + comandos GM |
| Whisper | HandleWhisper | ⚠️ | lee datos pero NO envía el susurro |
| attack | HandleAttack→UsuarioAtaca | ✅ | PVP+NPC+apuñalar+revela oculto+swing |
| PickUp | HandlePickUp | ✅ | |
| Drop | HandleDrop | ✅ | |
| DropDestroy | HandleDropDestroy | ❌ | destruir item del inventario |
| UseItem | HandleUseItem | ✅ | comida/bebida/pociones/oro |
| EquipItem | HandleEquipItem | ✅ | arma/armadura/escudo/casco/anillo/montura |
| CastSpell | HandleCastSpell | ✅ | autolanzar + GM sin maná + WorkRequestTarget |
| WorkLeftClick | HandleWorkLeftClick | ⚠️ | magia/pesca/minar/talar/robar OK; falta proyectiles/domar |
| Work | HandleWork | ✅ | ocultarse directo, resto pide target |
| LeftClick | HandleLeftClick | ✅ | selección comercio |
| DoubleClick | HandleDoubleClick→Accion | ⚠️ | FASE1 puertas/correo/pozos + herramientas; falta facciones/subastas |
| Meditate | HandleMeditate | ✅ | |
| Resucitate | HandleResucitate | ✅ | no recarga inventario |
| Rest | HandleRest | ❌ | descansar (regen acelerada) |
| RequestPositionUpdate | HandleRequestPositionUpdate | ✅ | |

## STATS / INFO

| Packet | Handler VB6 | Estado C# | Notas |
|---|---|---|---|
| RequestAtributes | HandleRequestAtributes | ✅ | Attributes: 5 bytes (Fuerza..Constitucion) |
| RequestSkills | HandleRequestSkills | ✅ | SendSkills: NUMSKILLS bytes |
| RequestMiniStats | HandleRequestMiniStats | ✅ | MiniStats: layout verificado vs cliente |
| RequestStats | HandleRequestStats | ✅ | SendUserStatsTxt (/est) volcado a consola 1:1 |
| Online | HandleOnline | ✅ | |
| ModifySkills | HandleModifySkills | ✅ | subir puntos de skill (anti-hack) |
| Train | HandleTrain | ✅ | entrenador de mascotas (NpcManager.Train) |

## COMERCIO / BANCO / TRADE

| Packet | Handler VB6 | Estado C# | Notas |
|---|---|---|---|
| CommerceStart/End/Buy/Sell | HandleCommerce* | ✅ | comercio NPC |
| BankStart/End/Deposit/Extract | HandleBank* | ✅ | items |
| BankDepositGold/ExtractGold | HandleBank*Gold | ✅ | oro |
| UserCommerce* (6 packets) | HandleUserCommerce* | ✅ | trade jugador-jugador |
| MoveBank | HandleMoveBank | ❌ | reordenar banco |

## TRABAJO / CRAFTEO

| Packet | Handler VB6 | Estado C# | Notas |
|---|---|---|---|
| CraftBlacksmith/Carpenter/Sastre/Alquimia | HandleCraft* | ✅ | |
| InitCrafting | HandleInitCrafting | ❌ | abrir ventana crafteo |

## CORREO / AMIGOS / PARTY

| Packet | Handler VB6 | Estado C# | Notas |
|---|---|---|---|
| Packets_Correo / EnviarCorreo | HandlePacketsCorreo/Enviar | ✅ | |
| PartyCreate/Join/Leave/Message | HandleParty* | ✅ | |
| PartyKick/Online/Accept/Reject | HandleParty* | ❌ | gestión avanzada party |
| AddAmigos/DelAmigos/OnAmigos/MsgAmigos | HandleAmigo* | ❌ | sistema de amigos |

## MAGIA / ESTADO

| Sistema | Estado C# | Notas |
|---|---|---|
| Cura/Daño/Paraliza/Inmoviliza/Ceguera/Remover | ✅ | |
| Veneno/Incineración (daño por tick) | ✅ | GameTimer |
| Invisibilidad mágica / curar veneno | ✅ | |

## MUERTE / RESURRECCIÓN

| Sistema | Estado C# | Notas |
|---|---|---|
| UserDie (tirar items en PK, desequipar, fantasma) | ✅ | guard doble-muerte + ItemSeCae |
| Mapa PK (battle_mode del .csm) | ✅ | battle_mode==0 → PK |
| Resucitar (sin recargar inventario) | ✅ | desde OrigChar + equipo |

## GM (94 = GMCommands) — todos mapeados, ver gm_commands_mapped.md

---

## SISTEMAS COMPLETOS PENDIENTES (sin handler / ❌)

- **Clanes/Guild** (~35 packets): crear, paz/alianza/guerra, miembros, elecciones, codex, web, noticias
- **Facciones**: Enlist, RetirarFaccion, RecompensaArmada/Caos
- **Subastas**: AuctionCreate/Bid
- **Casamiento/Divorcio**: Casamiento, divorciar
- **Hogar**: RegresarHogar, SeleccionarHogar
- **Skins**: EquiparSkin, ComprarSkin, GuardarSkinPermanente
- **TransferGOLD**, **Gamble**, **Denounce**, **GMRequest/CentinelReport**
- **Premios**: PidePremios, RPremios
- **Domar mascotas** (WorkLeftClick skill=domar)
- **Arena**: ArenaJoin
- **Macros**: RequestMacrosConfig/SaveMacrosConfig (persistencia)
- **Typing** (notificación "está escribiendo")
- **QueryMapNpcs**, **ShopBuyItem/RequestShopData** (tienda especial)

## PRÓXIMO ORDEN SUGERIDO (por impacto en gameplay)
1. Rest (descansar) — núcleo, simple
2. RequestStats/Atributes/Skills — ventana de personaje (muy usado)
3. ModifySkills — progresión
4. DropDestroy — tirar/destruir
5. Domar mascotas, Train
6. Hogar (RegresarHogar/SeleccionarHogar)
7. Sistema de Amigos
8. Clanes (bloque grande)
9. Facciones, Subastas, Casamiento (FASE 3)

---

# AUDITORÍA DE PROTOCOLO — PRIORIDAD 1 (2026-06-06)

Objetivo: CERO fuentes de desync antes de auditar gameplay. Foco de validación = "el stream
queda alineado byte a byte con lo que envía el cliente Godot", NO "compila".

Verificación de alineación: `write_block_prefixed` del cliente = `write_integer(len)` + bytes,
IDÉNTICO a `write_ascii_string` → se consume con `ReadASCIIString` (Int16 + N bytes). VB6 y x64
son little-endian → ByteQueue 1:1.

## DESYNC ACTIVOS — RESUELTOS

| Packet | Payload que envía el cliente | C# ANTES (consumía) | Bytes sobrantes | Impacto | Estado |
|---|---|---|---|---|---|
| ModifySkills | id + 27 bytes (NUMSKILLS) | PayloadSpec "B" = 1 byte | **26** | Asignar puntos desincronizaba toda la sesión | **RESUELTO**: HandleModifySkills lee 27 + aplica 1:1 (anti-hack, cap 100) |
| DropDestroy | id + byte(slot) + long(amount) = 6 | nada (descartaba solo id) | **5** | Tirar/destruir desincronizaba | **RESUELTO**: HandleDropDestroy + Inventory.DropDestroy 1:1 |
| CreateNewAccount | id + ascii + block + block + int | nada (descartaba solo id) | variable | Alta cuenta desincronizaba | **RESUELTO**: handler consume exacto (alta no portada, sin desync) |
| ProcesosLogin | id + byte(step) + ascii + block [+variante step8] | nada | variable | Login multi-fase / borrar PJ desincronizaba | **RESUELTO**: handler lee ambas variantes por step, consume exacto |

## POLÍTICA DE PACKET DESCONOCIDO — CAMBIADA
- ANTES: descartaba SOLO el byte del id → desalineaba el resto del stream (causa raíz de bugs
  tipo "partículas/portales desaparecen").
- AHORA: log detallado (id, nombre, bytesPendientes, hex) + **CIERRE CONTROLADO** (`incoming.Clear()`
  + `conn.Close()`). Mejor cortar una sesión que corromperla en silencio.

## INSTRUMENTACIÓN (temporal, toggle `PacketHandler.DebugPackets`)
Por cada packet: id, nombre, bytes disponibles antes, consumidos, restantes, user. Avisa UNDERREAD
(consumió 0). Packet desconocido y errores SIEMPRE se loguean.

## COBERTURA TOTAL DE PACKETS DEL CLIENTE
Se verificó cada ClientPacketID que el cliente Godot REALMENTE envía: todos están (a) con handler
propio o (b) en PayloadSpec con tamaño correcto. Único no cubierto: `GMRequest (82)` — el cliente
NO lo envía (solo está en el enum). ⇒ **CERO packets en estado DESYNC / UNKNOWN PAYLOAD / BYTES
SOBRANTES.** Condición para pasar a PRIORIDAD 2 (combate) cumplida.

### Estado por grupo (resumen)
- **OK (handler real):** login (ConnectAccount/LoginExistingChar/LoginNewChar), movimiento (Walk,
  ChangeHeading, RequestPositionUpdate), Talk, combate (attack, CastSpell, MoveSpell, WorkLeftClick,
  LeftClick, DoubleClick), inventario (PickUp, Drop, **DropDestroy**, EquipItem, UseItem), Meditate,
  Resucitate, Quit, **ModifySkills**, Rest/RegresarHogar/divorciar/Casamiento, Craft x4, Commerce x4,
  Bank x6, Party (Create/Join/Leave/Message), todos los Guild* del núcleo, UserCommerce x6, Correo x2,
  Ping, Typing, SaveMacros, Whisper, GMCommands (subcomandos), **CreateNewAccount/ProcesosLogin** (consumo exacto).
- **CONSUME-ONLY (alineado, SIN lógica — gaps de gameplay, NO desync):** SwapObjects, MoveBank,
  ChangeDescription, Train, AddAmigos/DelAmigos/MsgAmigos/OnAmigos, SeleccionarHogar, Enlist,
  RetirarFaccion, CombatModeToggle, Gamble, Denounce, Reward/PidePremios/RPremios, CentinelReport,
  AuctionCreate/AuctionBid, ShopBuyItem/RequestShopData, EquiparSkin/ComprarSkin/GuardarSkinPermanente,
  PartyAccept/PartyReject/PartyKick/PartyOnline, ParticulaUsuario, QueryMapNpcs, ArenaJoin, HayEventos,
  AbrirForms, TransferGOLD, InitCrafting, Information, UpTime, RequestStats, RequestMacrosConfig,
  DesconectarCuenta, ResuscitationSafeToggle, GuildFundate/CloseGuild/GuildAllianceDetails/GuildPeaceDetails.
- **DESYNC:** ninguno.
- **NO IMPLEMENTADO (lógica) pero sin desync:** alta de cuenta, borrar PJ, los CONSUME-ONLY de arriba.

---

# AUDITORÍA PRIORIDAD 3 (MOVIMIENTO) y PRIORIDAD 4 (IA NPC) — 2026-06-06

## PRIORIDAD 3 — MOVIMIENTO

| Tarea | Estado | Detalle |
|---|---|---|
| P3.1 Triggers de mapa | ✅ | eTrigger/eTrigger6 (MapLoader.cs). ZONAPELEA en PuedeAtacar (TriggerZonaPelea 1:1) + no-drop al morir en arena. ZONASEGURA bloquea robar (DoRobarEnTile) e invocación de criaturas. POSINVALIDA ya estaba en LegalPosNPC. |
| P3.2 Agua/navegación | ✅ | MapLoader lee L1 (Graphic1, column-major) y L2 → precalcula MapData.Water (HayAgua 1:1). Movement usa PuedeAtravesarAgua (Navegando\|\|Vuela): navegando solo pisa agua, a pie solo tierra. Flag Vuela agregado a UserFlags. |
| P3.3 Empuje de caspers | ✅ | Al pisar el tile de un muerto se le envía PosUpdate (Modulo_UsUaRiOs.bas:1175). El "push" de vivos del VB6 modded es código muerto (MoveToLegalPos ya rebota al vivo) → no se porta. |
| P3.4 Difusión por área | ⏸️ DIFERIDO | Helper Areas.InPCArea (bloques de 9, ±2 áreas) creado, pero NO aplicado: el char create/remove es por mapa completo (no hay AreaChanged/MakeUserChar al cruzar bloques). Filtrar solo el movimiento dejaría posiciones stale en jugadores lejanos (desync). Requiere portar antes el subsistema completo de áreas. |

## PRIORIDAD 4 — IA NPC

| Tarea | Estado | Detalle |
|---|---|---|
| P4.5 NPC melee a usuarios | ✅ | NpcAtacaUsuario reescrito 1:1: NpcImpacto (PoderAtaque vs evasión+escudo, rechazo con escudo) + Npcdaño (parte del cuerpo 1-6: cabeza→casco, resto→armadura+escudo; +def barco/montura; rompe meditación con fórmula VB6; mata). Sonido/FX de sangre. No ataca a ocultos/invisibles. |
| P4.6 IA completa (tipos/spawn/respawn/órdenes mascotas/guardias) | ⏳ PENDIENTE | Guardias y mascotas básicas ya existen (NpcManager). Falta: tipos de IA completos, spawn/respawn por mapa, órdenes de mascotas, CheckPets (mascotas defienden al amo automáticamente). |

## PRIORIDAD 5 — SKILLS/PROGRESIÓN

| Tarea | Estado | Detalle |
|---|---|---|
| SwapObjects | ✅ | Inventory.SwapObjects 1:1: intercambia 2 slots + reasigna todos los *EqpSlot (anillo/armadura/barco/casco/escudo/munición/nudillos/arma/montura/mágico). |
| ChangeDescription | ✅ | PacketHandler.HandleChangeDescription 1:1: AsciiValidos (a-z/ÿ/espacio vía CP1252), Soporte conserva "<Soporte>", LocaleMsg 77/392/111. |
| MoveBank | ✅ | Bank.MoveBank 1:1: dir=true sube (slot-1), false baja (slot+1); refresca toda la bóveda. |
| Train (entrenador) | ✅ | NpcManager.Train 1:1: NpcType=3 invoca Criaturas[petIndex] (NroCriaturas+CI1..N) cerca suyo con FX warp; tope MAXMASCOTASENTRENADOR=7 (LocaleMsg 593); criatura NoRespawn + MaestroNpc, descuenta al morir (QuitarMascotaNpc en MatarNpc). |
| Amigos (Add/Del/On/Msg) | ✅ | Social.cs reescrito 1:1 con AmigoSlot{Nombre,index}, flags.CantidadAmigos/CheckAmigos, QuienAmigo: Add caso1 solicitud / caso2 confirma (/FACCEPT); Del con compactación mutua; On lista online/offline (mapa por número, no nombre); Msg a online (FONTTYPE_INFOBOLD4=24). Helpers BuscarSlotAmigoVacio/Name/NameSlot, NoTieneEspacioAmigos, IntentarAgregarAmigo, ObtenerIndexLibre. Persistencia .chr [AMIGOS]+CantidadAmigos. |
| Enlist | ✅ | Facciones.Enlist 1:1: NPC facciones(5) + distancia≤4 (LocaleMsg 22/8); por Status del NPC (1=Armada,2=Milicia,4=Caos) → Enlistar*. |
| RetirarFaccion | ✅ | Facciones.RetirarFaccion 1:1: no muerto/newbie(ELV≤14, LocaleMsg 425)/renegado/clan; Milicia→Repu, Caos→Rene, Armada→Imp con hogar por mapa, resto→Renegado+Rinkel; ExpulsarFaccion + CharStatus. ParticleToLevel de meditación omitido (no portado). |

## PRIORIDAD 4.6 — IA NPC COMPLETA

| Tarea | Estado | Detalle |
|---|---|---|
| CheckPets | ✅ | NpcManager.CheckPets (SistemaCombate.bas:754) 1:1: al atacar el NPC al usuario (NpcAtacaUsuario), sus mascotas sin objetivo lo atacan (CheckElementales=False excluye fuego/tierra 93/94). TickMascota prioriza MascotaTargetNpc; al morir el objetivo vuelve a IA libre (= TipoAI.SigueAmo del VB6). |
| Tipos de IA | ✅ | Cubiertos los tipos ACTIVOS de este build: NpcMaloAtacaUsersBuenos(0)=persigue, ESTATICO(1)=solo adyacente, NPCDEFENSA(4)/SigueAmo(8)/NpcAtacaNpc(9)=mascotas, NpcPathfinding(10)=BFS StepToward. MueveAlAzar(3) está DESACTIVADO en el VB6 modded (AI_NPC.bas:1818) → no-hostiles quietos = 1:1. |
| Spawn/respawn por mapa | ✅ | GetMapNpcs spawnea los NPCs del .csm la primera vez que se visita el mapa; TickRespawns revive tras RespawnSeconds (criaturas de entrenador con NoRespawn no reviven). |
| Órdenes de mascotas | ✅ (build sin packets) | El cliente Godot NO envía packets de orden de mascota; /SEGUIR(NPCFollow) es GM-only. Comportamiento de jugador = defender amo (CheckPets), seguir amo y dejar de atacar al morir el target (TickMascota). |

## PRIORIDAD 6 — VENTANAS DE PERSONAJE

| Tarea | Estado | Detalle |
|---|---|---|
| RequestAtributes | ✅ | `ServerPackets.Attributes` (packet 48): Byte(id)+5 bytes (Fuerza,Agilidad,Inteligencia,Carisma,Constitucion), índices 1..NUMATRIBUTOS. |
| RequestSkills | ✅ | `ServerPackets.SendSkills` (ya existía): NUMSKILLS bytes; verificado 1:1 vs WriteSendSkills. |
| RequestMiniStats | ✅ | `ServerPackets.MiniStats` (packet 62): layout verificado contra handle_mini_stats del cliente (Long×3, Int npcs, 3 Byte clase/raza/genero, Long muertes, Byte status, Long×4 facciones). Agregado flags.MuertesUsuario (.chr [FLAGS] Murio). |
| RequestStats (/est) | ✅ | `SendUserStatsTxt` 1:1: nivel/exp, salud/mana/energia, golpe (+arma), def cuerpo/cabeza, clan+lider, "Logeado hace", oro/posición, dados. FONTTYPE_INFO=3. |

## PRIORIDAD 7 — TOGGLES / HOGAR / GUILD ENTRY

| Tarea | Estado | Detalle |
|---|---|---|
| ResuscitationSafeToggle | ✅ | Alterna flags.SeguroResu (campo nuevo) + LocaleMsg 14/15. |
| SeleccionarHogar | ✅ | `Social.SeleccionarHogar` 1:1: caso0 valida Revividor ≤5 + ShowMessageBox(accion 5); caso1 fija Hogar por mapa/facción (tabla Equidad eCiudad). |
| GuildFundate | ✅ | `HandleGuildFundate`: valida facción + `GuildManager.PuedeFundarUnClan` (nivel40/lider90/4 gemas) → AbrirFormularios(7). |
| ShowGuildNews | ✅ | Ya estaba wireado (GuildManager.EnviarNews). |
| CombatModeToggle | ✅ consume-only correcto | El VB6 no tiene handler (es toggle visual del cliente). |
| TransferGOLD | ⚠️ consume-only correcto | El cliente Godot manda SOLO `amount` (sin nombre destino) → la lógica VB6 (transferir a otro PJ) NO es portable sin tocar el cliente. |
| InitCrafting | ⚠️ consume-only correcto | El cliente manda `craft_type` (1 byte); la semántica VB6 (TotalItems/PorCiclo) difiere → no portable 1:1. |

## PRIORIDAD 8 — PARTY COMPLETO (tParty) ✅

`PartySystem.cs` reescrito 1:1 con el modelo `tParty` (Parties[1..100] = LeaderIndex/Members[1..5]/MemberCount; User.PartyId = índice de grupo). Handlers:
- **PartyJoin** (líder invita por CharIndex) → restricciones de facción 1:1, máx 5, PartyInvitation al objetivo.
- **PartyAccept/PartyReject** (flujo de invitación): el grupo se crea al aceptar la 1ª invitación.
- **PartyLeave/PartyKick** (líder, por nombre ASCII): compactación + disolución (≤1) o traspaso de liderazgo.
- **PartyMessage** (cliente manda Unicode; server reenvía PartyMessage en ASCII a cada miembro).
- **PartyOnline**: reenvía PartyMemberList + PartyMemberHP de todos.
- **Cleanup** en CloseUser (desconexión): saca del grupo, disuelve/traspasa.
- Packets nuevos en ServerPackets: PartyInvitation(Uni), PartyMemberList(count+Uni), PartyMessage(ASCII×2), PartyMemberHP(3×Int). HP en vivo se refresca por PartyOnline + al unirse (push por combate = mejora futura menor).

## YA ESTABAN HECHOS (el doc estaba desactualizado)

- **Domar mascotas**: `Work.DoDomarEnTile` wireado en WorkLeftClick (skill 12). ✅
- **Estupidez / RemoverEstupidez**: aplicadas en LanzarHechizoEn + tick de expiración + DumbNoMore. ✅
- **Hechizo de área**: `sp.HechizoDeArea` aplicado (daño/estados al área). ✅
- **Warp de mascotas**: `sp.Warp` en hechizos de invocación → `NpcManager.WarpFarthestPet` (acerca la mascota más lejana). ✅ (agregado esta sesión)
- **ShowGuildNews / 27 handlers de clan**: wireados. ✅

## PRIORIDAD 9 — CUENTAS + SEGURIDAD ✅

`Crypto.cs` (nuevo): ShiftDecrypt (= SDesencriptar, revierte el shift-cipher del cliente leyendo el bloque como BYTES CRUDOS vía `ByteQueue.ReadBlockBytes`, NO ASCIIString que sería lossy en 0x81/0x8D/0x8F/0x90/0x9D), Sha256Hex (SHA-256 sobre bytes CP1252, hex minúsculas = CSHA256), PasswordValida.
- **Validación de login REAL**: ConnectAccount/LoginExistingChar/LoginNewChar descifran el password y validan SHA256(pass+salt)==hash del .cnt + Ban + (login existente) pertenencia del PJ a la cuenta. Antes aceptaba cualquier password (TODO).
- **Alta de cuenta** (CreateNewAccount): CheckDataNewAccount 1:1 (cuenta ≤20/LegalCharacter, pass ≤30, pin numérico ≤4), crea .cnt con Password/Salt/UserCodigo + [PJS] vacío, responde AbrirFormularios(1)+ShowMessageBox(32).
- **Borrar personaje** (ProcesosLogin step 8): valida password, exige pertenencia, borra el .chr, compacta [PJS], reenvía AddPj.
- Verificado: SHA256("abc")=vector estándar; shift roundtrip OK en 16 casos.
- ⚠️ EFECTO: ahora se exige la contraseña REAL de cada cuenta (las .cnt ya tienen el hash VB6). Cuentas de testing necesitan su password real.

## PRIORIDAD 10 — INFRAESTRUCTURA (backups + AntiDos) ✅

- **Backups** (`Backup.cs`, modBackup.bas): snapshot con timestamp en `Backups\Auto_<yyyyMMdd_HHmm>\Charfile\` (copia todos los .chr tras SaveAllOnline) + limpieza de antiguos (mantiene MaxBackupsGuardados de Server.ini, def 10). Programado cada 30 min en el FlushLoop (separado del autosave de 5 min que sobrescribe los .chr).
- **AntiDos** (`AntiDos.cs`, clsAntiDos.cls): máx 5 conexiones simultáneas por IP. PuedeConectar reserva cupo en accept; Liberar lo devuelve en OnClose. Connection.RemoteIp = IP sin puerto.
- **UpTime** (HandleUpTime): informa el tiempo online del server (días/horas/min/seg) por consola. GameServer.StartTick = tInicioServer.
- **ModLimpieza**: NO portado a propósito — en el VB6 no tiene callers (AgregarItemLimpieza/LimpiarItemsViejos nunca se invocan), los pisos no se auto-limpian. Portarlo activo divergiría del comportamiento real.

## PRIORIDAD 11 — CLIMA (modClima.bas) ✅

`Clima.cs` 1:1 (845 líneas portadas). `Tick()` 1/seg desde FlushLoop:
- Cambio automático cada IntervaloClima (Server.ini [CLIMA], def 2400s) con prob 50/35/15 (despe/lluvia/tormenta=3).
- Lluvia/tormenta: AmbientLight=40 (oscuro), RainToggle, sonido de lluvia (191) en loop cada 5s, truenos, mensajes de consola.
- Rayos cada 300s (y al iniciar el clima): flash de luz (100→40 tras 2s), partícula 48 + daño (50/100 HP) a jugadores a cielo abierto (no dungeon, no BAJOTECHO, no trigger≥20); UserDie si mata.
- Dungeons: siempre despejado + luz normal. `EsDungeon` = mapa 37 o `MapInfo.Zona=="DUNGEON"`.
- `EnviarClimaAUsuario` en login (LoginFlow.EnterWorld) y warp (Movement.WarpUser, con sonido al salir de dungeon a la lluvia).
- Soporte: se agregó `MapInfo.Zona` (tMapDat.zone offset 86, 16 bytes CP1252) en MapLoader, y `ServerPackets.AmbientLight` (packet 113). RainToggle/EfectoTerrenoParticula/PlayWave/CreateFX/UpdateHP ya existían. Cliente: handle_rain_toggle + handle_ambient_light (verificado).

## PRIORIDAD 12 — EVENTOS: RULETA (modRuletaEventos.bas) ✅

`Ruleta.cs` 1:1. Tick() 1/seg: cada 20s (test) sortea 1 de 3 eventos que dura 1h y afecta a todos:
- MONTAR_DUNGEON (montar en dungeon; expuesto vía EventoMontarEnDungeonActivo, sin hook de bloqueo aún en C#),
- MINERIA_X2 (DoMineria duplica minerales),
- DROP_X2 (Combat.TirarDrops duplica la probabilidad).
Anuncio global al iniciar/terminar; NotificarEventoAlLogin en EnterWorld.

## PRIORIDAD 13 — EVENTO: INVASIÓN DE COFRES (modEventoInvasionCofres.bas) ✅

`CofresEvento.cs` 1:1. GM `/invasion <mapa> <cantidad>` (≥SemiDios) spawnea cofres (NPC 619,
ESTATICO + NoRespawn) en posiciones legales aleatorias. Al matar un cofre (hook en Combat.MatarNpc
por NpcIndex==619): 4.000.000 oro (cap MAXORO 90M) + 30% item legendario (402/481/1606, cae al
piso si no entra). Anuncios globales; finaliza al saquear todos.

## PRIORIDAD 14 — EVENTO: INFRAMUNDO (modEventoInframundo.bas) ✅

`InframundoEvento.cs` 1:1. Automático: cada 60s (test) con jugadores online invaden 4 Hechiceros
Elementales (Fuego512/Agua516/Aire517/Tierra518) en 4 ciudades aleatorias distintas (Ullathorpe/
Nix/Banderbill/Rinkel/Illiandor) con narrativa secuencial (5s + 180s entre apariciones, o inmediato
al matar el actual). Verificar() 1/seg; OnHechiceroMuere desde Combat.MatarNpc. Victoria al caer los 4.
Hechiceros NoRespawn, spawn en tile legal cerca del centro de la ciudad. (Discord omitido: no portado.)

## PRIORIDAD 15 — EVENTO: CACERÍA POR FACCIÓN (modEventoCaceriaFaccion.bas) ✅

`CaceriaEvento.cs` 1:1. GM (Dios) `/eventocaceria` inicia, `/eventocaceriaoff` finaliza, `/estadocaceria`
muestra estado (alias /iniciarcaceria, /finalizarcaceria, /vercaceria). Mientras activo, `SumarKill`
(hook en Facciones.ContarMuerte) cuenta kills PvP por facción del atacante (excluye newbies; Desnudo/
zona-pelea omitidos = igual que ContarMuerte). Al finalizar: DeterminarFaccionGanadora (más kills, 0 si
empate) + reparte Gran Saco de Créditos (item 1605) a los online de la facción ganadora. Discord omitido.

## PRIORIDAD 16 — EVENTO: ARENAS 1v1 (modArenas.bas) ✅

`ArenaEvento.cs` 1:1. Packet ArenaJoin → UnirseArena (nivel 40-50, no muerto, no en cola/arena).
Cola simple; al haber 2, ocupa 1 de 3 arenas (mapa 859, X1/Y1-X2/Y2) con cuenta regresiva 10s
(Procesar 1/seg), teletransporta+cura ambos, mejor de 3. Muerte en arena → OnUserDeath (hook en
Combat.UserDie) → FinalizarArena (revive+cura, siguiente round o fin con 10 Puntos de Arena +
warp a origen). CheckArenaDisconnect (hook en UserList.CloseUser) da victoria por abandono.
Fuerza MapInfo(859).Pk=true; el PvP de arena ya andaba por trigger ZONAPELEA (sin drop al morir).

## PRIORIDAD 17 — SUBASTAS (mdlSubastas.bas) ✅

`Subastas.cs` 1:1. Packets: AuctionCreate(Int objIndex+Long amount+Long buyout), AuctionBid(Int id+
Long bid), AuctionList (server→cliente, layout verificado vs handle_auction_list). Crear: busca slot
por objIndex, item a escrow (precio inicial 0 — el cliente no lo envía). Pujar: cobra oro, devuelve al
superado por correo (`Mail.DeliverSystem`, ORO=12), buyout finaliza ya. Finalizar (CheckExpirations
1/seg o buyout): item al ganador + oro al vendedor por correo; sin pujas → item de vuelta. Persistido
en Dat/Subastas.txt (escrow no se pierde al reiniciar). NPC subastador (Accion NT_Subastador) → SendList.

## PRIORIDAD 18 — ANTI-CHEAT: AntiAutoClicker (AntiAutoClicker.bas) ✅

`AntiCheat.cs` 1:1 (server-side, NO toca el cliente — el cliente ya manda los 2 bytes autopot+token
en UseItem/EquipItem). En HandleUseItem: si autopot(1)+token(97) → bypass (autopot legítimo); si no,
VerificarLimitePaquetes (30/seg manual, 10/seg autopot, ventana 1s) + PuedeUsarItem (cooldown 100ms +
DetectarPatron: intervalos casi idénticos entre los últimos clics = autoclicker; ≥3 detecciones avisa
al user+GMs, >5 bloquea y resetea). Estado AntiClickState por usuario (UserModel).

## PRIORIDAD 19 — MERCADOPAGO / DONACIONES (modMercadoPago.bas) ✅ (gateado por token)

`MercadoPago.cs` 1:1. Display SIEMPRE activo: RequestShopData → ShopCatalog(166)+DonationHistory(167)+
DonorRanking(168); catálogo de Server.ini [MercadoPago] Item1..32 (Nombre|PrecioARS|Creditos). Saldo
de créditos al login (UpdateCreditos 15, leído de .cnt [cuenta] Creditos). Cobro/polling GATEADO por
[MercadoPago] Habilitado=1 + AccessToken: ShopBuyItem crea preferencia (POST API MP, Bearer token,
async fuera del game loop) → ShopPaymentURL(160); PollLoop (cada IntervaloPollSeg, Task de fondo)
busca pagos por external_reference → acredita (ACREDITADA, suma créditos al .cnt + UpdateCreditos +
ShopItemGranted(161)) o revierte (refund/chargeback en ventana 60 días). Pendientes/ranking persistidos
en MercadoPago/. Sin token NO corre nada (no se acredita). ⚠️ Requiere token real + testing vs API MP.

## PRIORIDAD 21 — CloseGuild + Rest (cleanup) ✅

- **CloseGuild** (Protocol.bas:19537): `GuildManager.CerrarClan` 1:1 — el líder disuelve su clan
  (vivo, zona segura, único miembro): borra members/sol, lo saca de memoria+guildsinfo, limpia el
  GUILD del .chr, aviso global. Handler valida muerto/Pk.
- **Rest** (descansar): ya estaba completo (handler + case); solo se limpió la entrada muerta de PayloadSpec.
- Quedan en PayloadSpec 29 entradas MUERTAS (tienen case → se pueden limpiar) y ~20 consume-only reales,
  la mayoría NO portables (el cliente diverge: TransferGOLD/InitCrafting/CombatModeToggle/QueryMapNpcs)
  o menores (skins, premios, denounce, gamble, AbrirForms, GuildAlliance/PeaceDetails).

## PRIORIDAD 20 — ANTI-CHEAT: CENTINELA (modCentinela.bas) ✅

`Centinela.cs` 1:1. GM (Dios) `/centinelaactivado` (subcmd GM 67) togglea. Activado: cada minuto
(PasarMinuto) elige un user trabajando (no GM, no CentinelaOK), spawnea el NPC Centinela (622 tierra
/623 agua) en su pos y le pide /CENTINELA &lt;clave&gt;; CallUserAttention (1/seg, tras 5s) reintenta con
sonido+FX. CentinelReport (packet, Int clave) → CheckClave. A los 2 min sin responder → cárcel
(mapa 13) + pena en .chr + aviso a Dioses. OnUserLogout limpia. clsSecurity = el XOR de red (ya en
Connection). MIGRACIÓN FUNCIONALMENTE COMPLETA (solo MercadoPago requiere credencial+testing en vivo).

- **Alta de cuenta / borrar personaje**: hoy consumen el payload (sin desync) pero no crean/borran. Requiere portar el subsistema de cuentas (Cuentas.bas) + ops de archivo seguras.
- **Hechizos faltantes** (Estupidez/Morph completo/Warp/área): subsistema de hechizos (modHechizos.bas).
- **Domar mascotas** (WorkLeftClick skill=domar): rama de trabajo + límite de mascotas del usuario.
- **Anti-cheat de red** (clsAntiDos/clsSecurity/antiautoclicker/centinela): subsistema de seguridad, 0% portado.
- **Eventos especiales** (cacería/inframundo/cofres/ruleta/arena/subastas): subsistemas de contenido.
- **Backups** (modBackup), **clima** (modClima), **limpieza de piso** (ModLimpieza).
- **Skins/Casamiento completo/Gamble/Denounce/Premios/MercadoPago**: consume-only; varios dependen de payload del cliente o de tokens externos.
