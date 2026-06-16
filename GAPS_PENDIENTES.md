# Gaps pendientes vs servidor VB6 (análisis 2026-06-07)

Análisis de lo que TODAVÍA falta para que el C# funcione 1:1 con el VB6, después de cerrar áreas,
persistencia, inventario, kit, crafteo, limpieza, cárcel y macros. Detectado por marcadores
auto-documentados (TODO / "versión núcleo" / "simplificado") + auditoría de comportamiento.

Leyenda impacto: 🔴 el jugador lo nota / rompe gameplay · 🟠 mecánica incompleta · 🟡 admin/secundario · ⚪ cosmético

---

## ✅ RESUELTOS (2026-06-07, esta tanda)
- #1 Stats de PJ nuevo por raza+clase (HP/maná/STA/atributos/oro 2M/H1). Ver [[stats_pj_nuevo]].
- #2 Combate a distancia (arcos/proyectiles + armas arrojadizas) en HandleWorkLeftClick. Ver [[combate_distancia]].
- #3 Validación de facción en NPCs (mercaderes/banco/sacerdotes rechazan por facción; sacerdote también por mapa).

## ✅ RESUELTOS (2026-06-11): ítems mágicos (orbes / anillos / collares / pendientes)
Bug raíz: `EquipItem` no tenía case para ObjType=21 (otItemsMagicos) → NINGÚN ítem mágico se podía
equipar en C#. Agregado equip/desequip completo (MagicIndex/MagicSlot + aura + efectos) y TODOS los
EfectoMagico (eMagicType) en runtime. Ver memoria [[items_magicos_efectomagico]]:
- Anillos +atributo (2) / +skill (3): Agilidad/Carisma/Guerrero/Bardo/Ladrón/Comercio/etc.
- Anillo de Regeneración (4, regen vida ×2) y Quinta Esencia (5, regen maná pasiva + meditar).
- Brazalete del Ogro (6, +daño golpe) y Anillo de Defensa Mágica (7, −% daño mágico recibido).
- Anillos Dorados de drop (8): 1607 +100% y 1610 +20% (a 1610 le faltaban los tags en obj.dat: agregados).
- Orbe de Inhibición (9): NPCs no lanzan hechizos (también Espada GM/Aire).
- Orbe Ígnea (10, incinera PvP), Orbe Acuática (11, paraliza NPC 60%/60s y PvP 60%/3-5s),
  Orbe de la Ponzoña (19, envenena PvP). Ígnea/Ponzoña no afectan NPCs (sin estado de NPC, igual VB6).
- Anillo de las Sombras (13): oculto permanente, atacar no revela.
- Pendiente del Sacrificio (15): al morir en PK cae solo el pendiente, protege el inventario.
- Amuleto del Silencio (16): castea sin mostrar palabras mágicas.
- Collar de Rykan: resurrección automática + cadena de cargas 3/3(1601)→2/3(1846)→1/3(1847)→se rompe.
- Pendiente: NadieDetecta(17) y Experto(18) no tienen comportamiento definido ni en VB6 (decorativos).

## 🔴 ALTA (gameplay, lo nota cualquier jugador)

1. ~~**Stats de personaje nuevo fijos (CharCreator)**~~ ✅ HECHO — Todo PJ nuevo nace con HP=20, MaxMAN=0, MaxHIT=2,
   STA=100, atributos=18, sin importar raza/clase. El VB6 tira dados (ThrowDices) y calcula HP/maná/STA
   por clase+constitución (GetVidaInicialN1/GetManaInicialN1 + ModRaza). → Magos sin maná, guerreros sin
   vida correcta. **Afecta a cada cuenta nueva.**

2. **Combate a distancia (Proyectiles / Armas arrojadizas)** — HandleWorkLeftClick NO procesa arcos
   (proyectil) ni dagas/shuriken arrojadizas (las cases existen en el VB6, ver Protocol.bas:3363/3460).
   Hoy: usar arco no dispara. Falta: consumir flecha/munición, intervalo de arco, daño a distancia.
   (El daño a distancia/poder ya existe en Combat; falta el handler del click.)

3. **Validación de facción en NPCs (Accion)** — Mercaderes/banqueros/sacerdotes NO rechazan por facción
   (esCiudadano/esArmada/esCaos). Un criminal puede comprar en mercaderes de ciudad, un caos usar
   sacerdote imperial, etc. (Accion.cs TODO FASE 3 ×3.)

4. **Pesca / Minería / Tala reales (Work)** — DoPescar/DoMineria/DoTalar están "simplificados/inventados"
   y HayAgua no valida la capa L1 del mapa (no se carga). El flujo por herramienta (caña/piquete/hacha)
   setea el skill pero el tick de trabajo no es 1:1. Da recursos genéricos, no la fórmula VB6.

5. **Criminalidad/facciones en combate PvP** — Atacar a un jugador no aplica consecuencias de facción
   (volverse criminal, frags de facción, etc.) más allá de lo básico. (Combat: "Falta: facciones".)

---

## 🟠 MEDIA (mecánicas incompletas)

6. **Entrenadores** — No mandan su lista de criaturas (WriteTrainerCreatureList); no se puede invocar
   mascotas del entrenador desde el form. (Accion.cs:270.)

7. **Fogatas** — Usar rama/leña con DAGA equipada (TratarDeHacerFogata) no está; sin fogatas para
   descansar fuera de ciudad. (Accion.cs FOGATA_APAG.)

8. **Tijeras / botánica** — Cortar pieles de cadáveres con tijeras no implementado. (Accion.cs:615.)

9. ~~**Pociones subtipos 6-13**~~ ✅ HECHO — Scroll Intermundia(6), cambio de cara(7)/sexo(8) vía
   ChangeHead/DarCuerpoNuevo (rangos de cabezas hardcodeados 1:1, no necesitan tablas externas),
   Nareth(9), adquirir créditos(13). Todos 1:1 en UsarPocion (Inventory.cs).

10. ~~**Relaciones de clan avanzadas**~~ ✅ HECHO — la lógica (declarar guerra, ofrecer/aceptar/rechazar
    paz y alianza, website, codex, news, elecciones, propuestas) ya estaba implementada y conectada al
    PacketHandler. Faltaba la **persistencia**: ahora relaciones (&lt;Nombre&gt;-relaciones.rel),
    propuestas (&lt;Nombre&gt;-propositions.pro) y codex (guildsinfo GUILDi Codex1..8) se guardan/cargan
    1:1 con VB6. Fix: DeclararGuerra anula propuestas pendientes en ambos sentidos (AnularPropuestas).

11. **Difusión por área de FX/sonidos/chat/partículas** — hoy van a TODO el mapa (inofensivo: el cliente
    ignora lo que no ve, no genera fantasmas), pero NO es 1:1 con ToPCArea del VB6. Solo optimización de
    banda. (Combat: "placeholder de ToNPCArea/ToPCArea".)

---

## 🟡 BAJA (comandos GM y utilidades)

12. **Comandos GM stubbeados** — PARCIAL:
    - ✅ HECHO: /banclan (banea todos los miembros + PENA + kick), /miembrosclan (lista del .mem),
      /onclan (miembros online), /noestupido (quita estupidez/ceguera + packet DumbNoMore 73).
      /darfaccion ya escribía el .chr.
    - ⏳ PENDIENTE (requieren infra no portada aún): /seguir y /resetinv (necesitan **tracking de
      TargetNPC** por click + IA NPC follow + modelo de inventario NPC), /show + /sosdone (cola de SOS),
      /reloadsini + /setinivar (recarga de Server.ini en caliente), IP→nick desde log, teleport con
      cursor (/ct usa pos del GM, no el tile clickeado), /volar (FX GMFlyingToggle), /summonbot.

13. **HayAgua por capa L1** — no se carga la capa 1 del .csm para validar agua con precisión (afecta
    pesca y llenar botella en bordes). Hoy se usa el Water precalculado (suficiente para barcos).

---

## ⚪ EXTERNO / NO PORTABLE (decisión, no código)

14. **MercadoPago cobro real** — requiere credencial/token de producción en Server.ini.
15. **Discord / Statistics / History** — integraciones externas, decisión de no portar.

---

## YA RESUELTO en esta tanda (referencia)
Áreas (AOI users+NPCs+objetos), persistencia ↔ runtime completa, inventario (tirar/desequipar/oro/bloqueos),
kit inicial por clase, crafteo (forms+listas), limpieza del mundo, cárcel, macros, login duplicado.

## RECOMENDACIÓN DE ORDEN
1) Stats de PJ nuevo (#1) — afecta a todos los que crean cuenta.
2) Combate a distancia (#2) — estilo de combate entero faltante.
3) Validación de facción NPCs (#3) — coherencia de criminalidad.
4) Pesca/minería/tala 1:1 (#4) — economía de recursos.
Luego entrenadores/fogatas/tijeras/pociones y los comandos GM.
