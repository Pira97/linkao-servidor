# Guía rápida: convertir el resto del servidor con VBUC

Objetivo: traer las ~75.000 líneas de lógica de juego (todo menos red/protocolo, que ya
está hecho a mano en `ServidorCS/`) de VB6 a C# en el menor tiempo posible.

---

## Paso 0 — Antes de empezar (5 min)

1. **Hacé un backup** de `Servidor/Codigo` (copialo a `Servidor/Codigo_vbuc_src`).
   Trabajá sobre la copia, NO sobre el original.
2. En esa copia, asegurate de que exista el `.vbp` del servidor. Si no está, abrí el
   proyecto en VB6 y guardalo, o creá uno que liste todos los `.bas`/`.cls`.
   (VBUC necesita el `.vbp` para saber qué compilar juntos.)

---

## Paso 1 — Instalar y correr VBUC (20–40 min)

1. Descargar: https://www.mobilize.net/products/vbuc  → "Visual Basic Upgrade Companion"
   (Free Edition alcanza para este volumen).
2. Instalar, abrir, **New Project** → apuntar al `.vbp` de la copia.
3. Target: **C#** (.NET). Si pregunta versión, elegir la más nueva disponible.
4. **Upgrade / Convert.** Dejarlo terminar (puede tardar varios minutos).
5. Salida: una carpeta con `.cs` por cada `.bas`/`.cls` y un reporte HTML con los
   "issues" (EWIs/Notes) que no pudo resolver solo.

> Tip de velocidad: NO intentes que compile todo perfecto de una. Convertí, copiá la
> salida, y andá resolviendo errores por bloques (ver Paso 3).

---

## Paso 2 — Integrar la salida con el núcleo (10 min)

1. Creá una carpeta `ServidorCS/Game/` y copiá ahí TODOS los `.cs` que generó VBUC.
2. Ajustá el namespace: poné `namespace ServidorCS.Game;` arriba de cada archivo
   (o un `<Using>` global). El núcleo está en `ServidorCS.Network`.
3. **Borrá de la salida de VBUC** lo que ya tenemos hecho (no dupliques):
   - Todo lo de Winsock/sockets (`wsksock`, `wskapiAO`, `TCP`, `frmRecibeDatos`).
   - `clsByteQueue` → usá el `ByteQueue` del núcleo.
   - El parser `HandleIncomingData` y el `Protocol` de red → usá `PacketHandler`.
4. Los `.frm` (formularios de admin): NO los necesitás para que el server corra.
   Borralos o dejalos sin compilar. Si algún `.bas` los llama, comentá esas llamadas.

---

## Paso 3 — Hacer que compile (la parte iterativa)

`dotnet build` y resolvé los errores por categoría. Los típicos de VBUC y su arreglo:

| Error típico de VBUC | Arreglo rápido |
|----------------------|----------------|
| `Get #f, , array` / `Put #` (I/O binario) | Reemplazar por `BinaryReader`/`BinaryWriter`. **OJO**: VB6 lee arrays en **column-major** — ver memoria del proyecto [[vb6_array_column_major]]. |
| Strings/archivos en UTF-8 | Forzar `Cp1252.GetString/GetBytes` (ya está en `Network/Cp1252.cs`). NO UTF-8 o se rompen ñ/á/é ([[vb6_encoding]]). |
| `Redim Preserve`, arrays base-1 | Mantener base-1 con tamaño+1, o ajustar índices. No cambies la convención a lo loco. |
| `Variant`, `On Error GoTo` | VBUC los traduce a `object` / `try-catch`; suele compilar, revisá la lógica después. |
| Llamadas a `Write*`/`SendData` de red | Redirigir a `ServerPackets.*` del núcleo. |
| Acceso a `UserList(i).outgoingData` | Mapear al `Connection.OutgoingData` del núcleo. |

> Estrategia más rápida: si un módulo da MUCHOS errores y no es crítico para login+
> movimiento (ej: subastas, eventos, discord, mercadopago), **excluílo del build**
> temporalmente (renombrá a `.cs.off`) y volvé a él después. Priorizá:
> **Declares → FileIO → Cuentas → Modulo_UsUaRiOs → GameLogic → SistemaCombate.**

---

## Paso 4 — Conectar login y movimiento reales (lo que prueba que sirve)

Una vez que `UserList` (modelo de usuario) compile, en `PacketHandler.cs`:

1. **Login**: en `HandleLoginExistingChar`, después del parseo que ya está, llamar a la
   cadena portada: `ChequeosServerIni → EntrarCuenta → PersonajeExiste → ConnectUser`,
   y al final la ráfaga de packets (`Logged`, `UserIndexInServer`,
   `UserCharIndexInServer`, `ChangeMap`, `CharacterCreate`, stats, inventario).
2. **Movimiento**: en `HandleWalk`, llamar a `MoveUserChar(userIndex, heading)` portado.
3. Probar con el **cliente Godot** apuntando a `127.0.0.1:7666`: si entra un personaje
   y camina, la migración del core está validada.

---

## Orden de prioridad (para tener algo jugable lo antes posible)

1. Login completo (entrar al mundo).
2. Movimiento + heading.
3. Chat (`Talk` → `ChatOverHead`/`ConsoleMsg`).
4. Inventario + equipar.
5. Combate, hechizos.
6. El resto (comercio, clanes, eventos, mercadopago...).

---

## Qué ya está resuelto en `ServidorCS/` (no lo rehagas)

- Wire format byte-idéntico (`ByteQueue`, `Cp1252`).
- Enums de packets 1:1 (`ServerPacketID` 134, `ClientPacketID` 139).
- Sockets async + colas + flush (`Connection`, `GameServer`).
- Dispatcher tolerante a packets pegados/partidos (`PacketHandler`).
- 10 handlers parseando 1:1 + 7 senders de salida.
- Lectura de `Server.ini` (puerto).
- Fix del NuGet roto de la máquina (`NuGet.config`).
