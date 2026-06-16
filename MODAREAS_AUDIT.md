# MODAREAS_AUDIT.md — Auditoría del subsistema de áreas (FASE 1)

Fecha: 2026-06-07. Objetivo: portar ModAreas 1:1. **Resultado: DETENIDO en FASE 1 por bloqueante de
arquitectura** (regla del encargo: "si create/remove por área no está completo, DETENER, genera desync").

---

## FASE 1 — Cómo funciona el sistema en VB6

**Modelo de áreas (ModAreas.bas):**
- Mapa 100×100 dividido en bloques de 9×9. `AreaID(X,Y) = (X\9+1)*(Y\9+1)`.
- Región visible de cada entidad = ventana de **45×45 tiles** (5×5 bloques) centrada en su bloque.
- Cada user/NPC tiene `AreasInfo`: AreaID, MinX/MinY, y bitmasks `AreaPertenece(X/Y)=2^(pos\9)` y
  `AreaRecive(X/Y)` (±2 bloques). `SendData ToPCArea` difunde solo a quienes su `AreaRecive` cruza el
  `AreaPertenece` del emisor (vía `ConnGroups(map).UserEntrys`).

**Eventos (lado servidor):**
| Evento | Función VB6 | Qué hace |
|---|---|---|
| Mover user | `MoveUserChar` → `CheckUpdateNeededUser(idx, heading)` | difunde `CharacterMove` ToPCArea; si cruzó de bloque: manda `AreaChanged` + crea (MakeUserChar/MakeNPCChar/WriteObjectCreate) lo que entró a la franja nueva (9 de ancho según heading). |
| Mover NPC | `CheckUpdateNeededNpc` | crea el NPC en los users de la franja nueva. |
| Entrar al mapa / login / warp | `AgregarUser` → `CheckUpdateNeededUser(USER_NUEVO)` | agrega a ConnGroups + crea TODO el 45×45. |
| Salir del mapa / logout | `QuitarUser` + `EraseUserChar(desvanecer)` | saca de ConnGroups; `CharacterRemove` ToPCArea (desvanecer=True para teleport/invis). |

**Punto clave — la REMOCIÓN al alejarse caminando NO la hace el servidor.** En VB6, cuando una
entidad sale de tu área por movimiento normal, el **CLIENTE** la borra al recibir `AreaChanged`
(culling local de todo lo que quedó fuera de la nueva ventana). El servidor solo deja de mandarle los
`CharacterMove` de esa entidad (porque ToPCArea está filtrado). Verificado: el server NUNCA emite
`CharacterRemove(desvanecido=false)` en caminata; las llamadas a CharacterRemove son siempre
desvanecido=True (EraseUserChar: logout/teleport/invis, Modulo_UsUaRiOs.bas:288/291/2193/2565).

---

## EL BLOQUEANTE (por qué un port 1:1 rompería este cliente)

El cliente Godot **NO hace culling por área**. Su `handle_area_changed`
(protocol_incoming.gd:421) es **puramente informativo**: lee x,y y ajusta la posición; **no borra**
ningún char/NPC/objeto. El cliente solo remueve entidades cuando recibe un `CharacterRemove`
explícito (handle_character_remove, :820) o al cambiar de mapa (limpia todo).

Consecuencia de portar ModAreas 1:1 (SendData filtrado + CheckUpdateNeeded + AreaChanged):
- Al cruzar un borde de bloque, las entidades que salen de tu ventana **dejan de recibir CharacterMove**
  (SendData filtrado) y el cliente **no las borra** (AreaChanged no culla) →
  **chars/NPCs/objetos fantasma congelados en posiciones stale.** Exactamente el desync prohibido.

Restricciones del encargo que cierran el camino 1:1:
- "NO tocar cliente Godot" → no puedo agregarle el culling por AreaChanged.
- El lado de remoción NO existe en el servidor VB6 (es client-side) → no hay nada 1:1 que portar para
  removerlas.

**Por eso el port estricto 1:1 está INCOMPLETO en el lado remove → se DETIENE (regla del encargo).**

---

## La salida correcta (requiere decisión: deja de ser 1:1 estricto)

El cliente **SÍ soporta el lado remove server-driven**: `CharacterRemove(charIndex, desvanecido=false)`
y su propio comentario lo documenta como *"el char solo salió del área visible"* (:825). Es decir, el
autor del cliente Godot **diseñó el cliente esperando que el SERVIDOR mande el remove al salir de área**
(modelo server-authoritative), NO el culling client-side del AO clásico.

→ **Opción A (recomendada): visibilidad server-driven.** El servidor mantiene, por observador, su set
visible y al cruzar área: manda `CharacterCreate` de lo que ENTRÓ y `CharacterRemove(desvanecido=false)`
de lo que SALIÓ; el broadcast de movimiento/FX/chat se filtra por área (ConnGroups + bitmask). Usa
SOLO packets que el cliente ya implementa. **No genera fantasmas. No toca el cliente.** Diferencia con
VB6: el server maneja la remoción en vez del cliente (el cliente Godot fue hecho para esto).

→ **Opción B: dejar como está** (difusión por mapa completo). Correcto y sin desync; no escala a muchos
jugadores/mapa. Es el estado actual.

→ **Opción C: 1:1 estricto** (port literal de ModAreas). **Descartada**: genera fantasmas con este
cliente (ver bloqueante).

## Decisión tomada: **Opción A** (visibilidad server-driven). IMPLEMENTADA.

---

## FASES 2-6 — Implementación server-driven (AreaVisibility.cs)

**Modelo:** cada `User` tiene sets `VisibleUsers` (userIndex) y `VisibleNpcs` (CharIndex) = lo que su
cliente tiene renderizado, + `AreaBlockX/Y` (bloque actual). El servidor diffea y manda
`CharacterCreate` al ENTRAR al área y `CharacterRemove(desvanecido=false)` al SALIR. Área = ±2 bloques
de 9 (ventana 45×45, idéntico al AreaRecive del VB6). Los sets garantizan por construcción: **nunca un
create duplicado ni un remove huérfano**.

**Eventos conectados (FASE 3):**
| Evento | Llamada | Efecto |
|---|---|---|
| Login/entrada | `OnUserEnter` (LoginFlow) | crea usuarios+NPCs del área; lo hace visible a los del área |
| Mover usuario | `OnUserMoved` (Movement.MoveUserChar) | CharacterMove a quienes lo ven; create/remove a quienes entra/sale; rescan propio sólo al cruzar bloque |
| Warp / cambio de mapa | `OnUserLeave`+`OnUserEnter` (Movement.WarpUser) | remove en mapa viejo, create en el nuevo |
| Logout | `OnUserLeave` (UserList.CloseUser) | remove a todos los que lo veían |
| Mover NPC | `OnNpcMoved` (NpcManager.MoveNpcChar) | create/remove/move por área |
| Spawn/respawn NPC | `OnNpcSpawn` (SpawnAt / TickRespawns) | create sólo a usuarios del área |
| Muerte/quitar NPC | `OnNpcRemoved` (UserDie NPC / QuitarNPC) | remove a quienes lo veían |
| Invisible/Oculto | `CrearUsuarioParaObs` | al crear un char oculto manda SetInvisible(true) — **corrige bug previo**: quien entraba al área veía a los ocultos |

**FASE 4 — desyncs revisados:**
- Fantasmas por mover: resuelto (remove explícito al salir).
- Creates duplicados: imposibles (set guard).
- Removes huérfanos: imposibles (sólo se remueve si estaba en el set).
- NPC respawn con CharIndex nuevo: el viejo se quita de todos los sets (OnNpcRemoved) y el nuevo se
  agrega (OnNpcSpawn) → sin stale.
- Invisibles vistos por nuevos observadores: corregido (SetInvisible on create).
- Objetos: siguen full-map (estáticos, ya consistentes; NO causan stale de posición). Optimización por
  área de objetos: pendiente (no es desync).

**FASE 5 — runtime:** server bootea OK; LoadBot (bots autenticados caminando) → varios entraron al
mapa 34 y caminaron 15s ejercitando OnUserEnter/OnUserMoved/OnNpcMoved **sin una sola excepción**
(stderr vacío), server estable. (Las conexiones rechazadas fueron por AntiDos 5/IP, no por áreas.)
Validación visual fina (frente al cliente Godot real) recomendada en QA con 2+ jugadores cruzando bordes.

**Mejora real:** el tráfico de movimiento de jugadores pasa de O(jugadores del mapa) a O(jugadores del
área) → escala. Sin tocar el cliente.

**Pendiente (no crítico):** AOI de objetos del piso; filtrado por área de FX/sonidos/chat (hoy van a
todo el mapa pero son inofensivos: el cliente ignora charIndex desconocido, no generan fantasmas).

---

## AMPLIACIÓN — AOI de objetos del piso (2026-06-07, segunda iteración)

Implementado. Cada `User` tiene `VisibleObjs` (tile codificado x*101+y). Fuente autoritativa: `FloorObj`
(estructurales + drops dinámicos). 
- **FullUpdate** escanea la ventana 45×45 vía FloorObj y diffea: ObjectCreate al entrar, ObjectDelete al
  salir. Omite `ObjType.Teleport` (el cliente los renderiza desde su .csm).
- **ObjectAppeared(map,x,y,obj,amount)** / **ObjectRemoved(map,x,y)**: helpers centrales para drop/pickup.
- **Call sites ruteados:** Inventory (PickUp, DropObj, oro/TirarOro, DropItemToFloor), Combat (loot al
  morir NPC y drop de inventario al morir user), Accion (puerta), GameTimer (portal crear/cerrar),
  CofresEvento, Chat (/dest BorrarObjEnTile). Login/Warp ya no mandan la lista completa (lo hace OnUserEnter).
- **Excepción:** teleports creados por GM (/ct, obj 378) siguen full-map (son ObjType.Teleport, que el AOI
  omite a propósito; un teleport GM dinámico no está en el .csm y debe enviarse explícito a los presentes).

Runtime: 4/4 bots LoadBot caminando/atacando 12s (drops de loot) → stderr vacío, server estable.

**Estado del sistema de áreas: COMPLETO** para users, NPCs y objetos del piso. Único pendiente real:
filtrado por área de FX/sonidos/chat (optimización de banda, NO desync).
