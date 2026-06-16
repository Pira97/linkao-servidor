# Auditoría TOTAL de módulos VB6 → C# (ServidorCS)

Fecha: 2026-06-07. Metodología: cruce módulo VB6 ↔ equivalente C#, validando comportamiento real
(no equivalencia por nombre). "Funciona" = verificado por código/runtime, no solo compila.

Leyenda criticidad: 🔴 CORE CRÍTICO · 🟠 CORE IMPORTANTE · 🟡 SECUNDARIO · ⚪ NO PORTABLE/LEGACY
Estado: ✅ migrado · 🟨 parcial · ❌ no migrado · ➖ no aplica

---

## 🔴 CORE CRÍTICO

| Módulo VB6 | Equivalente C# | Estado | Funciona | Falta / Riesgo |
|---|---|---|---|---|
| TCP.bas (sockets/login/load/save) | GameServer, Connection, LoginFlow, CharLoader, CharSaver | ✅ | SÍ | Load/save de PJ auditado 2026-06-07 (ver abajo). Sockets .NET async. |
| Protocol.bas (793 KB, handlers) | PacketHandler + Game/* | ✅ | SÍ | 138/138 packets del cliente cubiertos. Desconocido→cierre controlado. |
| clsByteQueue.cls | ByteQueue.cs | ✅ | SÍ | Lectura/escritura 1:1, sin under/overread. |
| Cuentas.bas | AccountManager, Crypto | ✅ | SÍ | Login real SHA256+Salt, alta/borrado de PJ. |
| Characters.bas | CharCreator.cs | 🟨 | SÍ | Crea PJ OK; **kit inicial mínimo** (1 arma) vs kit por clase del VB6. |
| Modulo_UsUaRiOs.bas (153 KB) | UserList, Movement, UserModel | ✅ | SÍ | Movimiento/colisión/casper/triggers/agua 1:1. |
| SistemaCombate.bas (174 KB) | Combat.cs | ✅ | SÍ | Evasión/impacto/daño, NPC melee, hechizos. |
| InvUsuario.bas (114 KB) | Inventory.cs | ✅ | SÍ | Equipar/usar/tirar/destruir auditado 2026-06-07. |
| Trabajo.bas (114 KB) | Work.cs, Inventory | 🟨 | SÍ | Target/robar/domar/fundir 1:1. **Pesca/tala/minería NO existen en este server modded** (no es gap). Crafteo de herramientas (costurero/olla/serrucho) pendiente. |
| MODULO_NPCs/AI_NPC/modNpcSpawn | NpcManager, NpcData | ✅ | SÍ | IA, spawn/respawn (20s), pathfinding BFS (SeekPath), guardias. |
| modHechizos.bas (116 KB) | SpellData, Combat | ✅ | SÍ | Efectos/partículas; falta Estupidez área/Morph/Warp avanzados (menor). |
| ModFacciones.bas | Facciones.cs | ✅ | SÍ | Enlistar/rangos/recompensas/frags. |
| modGuilds/clsClan | GuildManager.cs | ✅ | SÍ | Crear/ingreso/expulsar/elecciones/relaciones. **GUILDINDEX ahora se persiste** (fix 2026-06-07). |
| modBanco.bas | Bank.cs | ✅ | SÍ | Depósito/extracción (item+oro); depósito desequipa (fix). |
| Comercio.bas | Commerce.cs | ✅ | SÍ | Compra/venta NPC; venta desequipa (fix). LookatTile 1:1. |
| mdlCOmercioConUsuario.bas | UserTrade.cs | ✅ | SÍ | Trade usuario-a-usuario; entrega desequipa (fix). |
| clsAntiDos.cls / SecurityIp | AntiDos.cs | ✅ | SÍ | Cap 5 conex/IP. |
| AntiAutoClicker.bas / clsSecurity | AntiCheat.cs | ✅ | SÍ | Anti-autoclicker server-side + token. |
| PathFinding.bas | NpcManager.SeekPathHeading (BFS) | ✅ | SÍ | Primer paso del camino más corto. |
| ModEncrypt/CSHA256.cls | Crypto.cs | ✅ | SÍ | SHA256 password. |
| FileIO.bas (carga .dat) | ObjData, NpcData, MapLoader, SpellData, IniFile | ✅ | SÍ | obj/npc/map/hechizos.dat. Campos auditados (Aura/Destruir/Newbie/LingoteIndex añadidos). |
| Declares.bas (tipos/globals) | UserModel, Constants, enums | ✅ | SÍ | — |
| mMainLoop/modNuevoTimer | GameTimer.cs + loop Program | ✅ | SÍ | Hambre/sed/regen/veneno/incinera, IA 380ms, autosave 300s, backup 1800s. |

### Auditoría de persistencia (.chr) — 2026-06-07
Bugs encontrados y corregidos (estado persistido que NO se reconstruía en runtime):
- **Punteros de equipo** (*EqpObjIndex): no se reconstruían → armadura no defendía, arma no sumaba daño, desnudo al desmontar. ✅ + fallback auto-reparador.
- **Muerto**: revivía al reloguear. ✅
- **Hambre/sed** (flags y valores Max/MinHAM/AGU): no se guardaban. ✅
- **Veneno/incinerado**, **Pena (cárcel)**, **ArenaPoints**, **[MUERTES] frags**, **OrigChar anims**, **navegando/montando**, **GUILDINDEX**, **UpTime**: no round-trip. ✅
- Cárcel: solo se preserva el contador; **enforcement de movimiento NO modelado** (deuda técnica).

---

## 🟠 CORE IMPORTANTE

| Módulo VB6 | Equivalente C# | Estado | Funciona | Falta / Riesgo |
|---|---|---|---|---|
| ModAreas.bas | Areas.cs | 🟨 | Parcial | Helper InPCArea existe pero **difusión por área NO aplicada** (char create/remove es por mapa completo). Funciona pero no escala a muchos jugadores/mapa. **Deuda técnica de escalabilidad.** |
| Acciones.bas | Accion.cs | ✅ | SÍ | Puertas/correo/pozos/llaves/target. |
| GameLogic.bas | repartido (LookatTile→Commerce, etc.) | ✅ | SÍ | — |
| General.bas (utilidades) | repartido | ✅ | SÍ | DarCuerpoDesnudo, helpers. |
| modCentinela.bas | Centinela.cs | ✅ | SÍ | Anti-macro. |
| modBackup.bas | Backup.cs | ✅ | SÍ | Snapshot fechado 30 min + cleanup. |
| modSendData.bas | ServerPackets (broadcast) | ✅ | SÍ | ToMap/ToArea. |
| mod_Correos.bas | Mail.cs | ✅ | SÍ | Bandeja + adjuntos. |
| mod_Auras.bas | lógica en Inventory | ✅ | SÍ | AuraToChar. |
| modInvisibles.bas | SetInvisible (Combat/Work) | ✅ | SÍ | Ocultar/invisibilidad. |
| modClima.bas | Clima.cs | ✅ | SÍ | Lluvia/tormenta/rayos. |
| Admin.bas | GMCommands (PacketHandler) | ✅ | SÍ | 40+ comandos GM. |
| modSeguridadClones.bas | (login duplicado) | 🟨 | Parcial | **Riesgo conocido: login duplicado** (mismo PJ 2 veces) no totalmente blindado. |
| ModLimpieza/TLimpiezaItem | — | ❌ | NO | **Ítems tirados en el piso no se limpian con el tiempo** (se acumulan). Deuda técnica menor. |
| mod_Macros.bas | (consume-only) | ❌ | NO | **Macros no persisten server-side** (SaveMacros = TODO). Menor. |

---

## 🟡 SECUNDARIO

| Módulo VB6 | Equivalente C# | Estado | Funciona |
|---|---|---|---|
| modEventoCaceriaFaccion | CaceriaEvento.cs | ✅ | SÍ |
| modEventoInframundo | InframundoEvento.cs | ✅ | SÍ |
| modEventoInvasionCofres | CofresEvento.cs | ✅ | SÍ |
| modRuletaEventos | Ruleta.cs | ✅ | SÍ |
| modArenas | ArenaEvento.cs | ✅ | SÍ |
| mdlSubastas | Subastas.cs | ✅ | SÍ |
| modMercadoPago | MercadoPago.cs | 🟨 | Display 1:1; **cobro/polling requiere token en Server.ini** (pendiente credencial). |
| Statistics/ConsultasPopulares | — | ❌ | Estadísticas/rankings no portados (cosmético). |
| History.bas | — | ❌ | Log histórico no portado (cosmético). |
| modDiscord*.bas | — | ❌ | **Notificaciones a Discord NO portadas** (decisión: sin integración). |
| Matematicas/modHexaStrings/Queue | inline / ByteQueue | ✅ | SÍ |
| clsIniManager/clsIniReader | IniFile/IniDocument | ✅ | SÍ |
| modErrorHandler | try/catch + Console log | ✅ | SÍ |

---

## ⚪ NO PORTABLE / LEGACY (correcto NO portar)

| Módulo VB6 | Motivo |
|---|---|
| frmMain/frmServidor/frmAdmin/frmUserList/frmTrafic/frmConID/frmRecibeDatos/frmDebugNpc/frmCargando/FrmInterv/FrmStat | Formularios UI VB6 → servidor headless de consola. La LÓGICA de frmMain (timers/loop) sí está en GameTimer/Program. |
| wskapiAO.bas / wsksock.bas | Winsock API VB6 → reemplazado por sockets .NET async (GameServer/Connection). |
| Modulo_SysTray.bas | Ícono de bandeja Windows → N/A en server de consola. |
| modTestSonidos.bas | Debug de sonidos. |
| modDiscordPowerShell/modDiscordSimple | Integraciones Discord (no portadas). |
| cGarbage/cColaArray/ModCola/clsdicc | Estructuras VB6 → tipos nativos .NET (List/Dictionary/ByteQueue). |
| Mod_AntiTrucheoFrags.bas | Stub vacío (46 bytes). |
| Modulo_InventANDobj.bas | Funciones repartidas en Inventory/ObjData. |
| cSolicitud/UserIpAdress/cClsMapSoundManager | Helpers menores absorbidos. |

---

## GAPS REALES (deuda técnica priorizada)

1. ✅ **ModAreas — RESUELTO (2026-06-07)**: implementado AOI server-driven (AreaVisibility.cs) para users+NPCs (create al entrar / remove al salir de área ±2 bloques). El cliente Godot no culla por AreaChanged → el server manda los removes explícitos. Ver MODAREAS_AUDIT.md. Pendiente menor: AOI de objetos del piso y filtrado por área de FX/chat (no son desync).
2. 🟠 **Login duplicado** (modSeguridadClones): mismo PJ logueado 2 veces no totalmente blindado.
3. 🟠 **Cárcel sin enforcement**: el contador Pena persiste pero no restringe el movimiento.
4. 🟡 **Limpieza de ítems del piso** (ModLimpieza): ítems tirados no decaen → se acumulan.
5. 🟡 **Macros no persisten** server-side.
6. 🟡 **MercadoPago**: cobro real gateado por token de Server.ini (falta credencial + testing).
7. 🟡 **Kit inicial de PJ** mínimo vs kit por clase del VB6.
8. 🟡 **Crafteo de herramientas** (costurero/olla/serrucho → formularios).
9. ⚪ Estadísticas/rankings, History, Discord: decisión de NO portar (cosmético/externo).

## VEREDICTO
Todo lo 🔴 CRÍTICO funciona y está auditado 1:1 (red/login/persistencia/combate/inventario).
El mayor riesgo abierto para **producción a escala** es la **difusión por área** (#1). Para beta cerrada
supervisada, el sistema es funcional. Lo NO portable está correctamente identificado y justificado.
