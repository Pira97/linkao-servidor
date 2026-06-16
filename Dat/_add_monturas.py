"""
Agrega monturas al server obj.dat + al .ind del cliente Godot.

Editar MONTURAS_NUEVAS abajo con los items que se quieren agregar.
Cada montura es: (name, grh_inventario, num_ropaje).
El ID se asigna automatico: NumOBJs+1, NumOBJs+2, ...
"""
import os, re, shutil, datetime

SERVER_OBJ = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\Servidor\Dat\obj.dat"
CLIENT_IND = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\LinkAO-Godot\init\locale_obj_es.ind"

ENC = "cp1252"
NEWLINE = "\r\n"
OBJTYPE_MONTURA = 44

# ====== EDITAR ESTA LISTA ======
MONTURAS_NUEVAS = [
    # (Name, GrhIndex, NumRopaje)
    ("Montura Corcel Maldito",        11894, 724),
    ("Montura Unicornio del Bosque",  11905, 725),
]
# ===============================

stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")

# === 1) Server obj.dat ===
backup_srv = SERVER_OBJ + f".backup_{stamp}_add_monturas"
shutil.copy2(SERVER_OBJ, backup_srv)
print(f"[backup] {backup_srv}")

with open(SERVER_OBJ, "r", encoding=ENC, newline="") as f:
    srv = f.read()

m_num = re.search(r"^NumOBJs=(\d+)", srv, re.M)
if not m_num:
    print("[ERROR] No se encontro NumOBJs= en obj.dat")
    raise SystemExit(1)
current_num = int(m_num.group(1))
print(f"[info] NumOBJs actual = {current_num}")

next_id = current_num + 1
new_sections = []
assignments = []
for (name, grh, ropaje) in MONTURAS_NUEVAS:
    if re.search(rf"^\[OBJ{next_id}\]", srv, re.M):
        print(f"[ERROR] OBJ{next_id} ya existe. Abortando.")
        raise SystemExit(1)
    sec = NEWLINE.join([
        f"[OBJ{next_id}]",
        f"Name={name}",
        f"GrhIndex={grh}",
        f"NumRopaje={ropaje}",
        f"ObjType={OBJTYPE_MONTURA}",
        f"valor=280000",
        f"MinSkill=25",
        f"MinHIT=10",
        f"MaxHIT=10",
        f"MINDEF=10",
        f"MAXDEF=10",
        f"Crucial=1",
        f"Permanente=3",
        f"LocaleID={next_id}",
        f"Texto=",
        "",
    ])
    new_sections.append(sec)
    assignments.append((next_id, name, grh, ropaje))
    next_id += 1

new_num = next_id - 1

if not srv.endswith(NEWLINE):
    srv += NEWLINE
srv += NEWLINE + NEWLINE.join(new_sections) + NEWLINE
srv = re.sub(r"^NumOBJs=\d+", f"NumOBJs={new_num}", srv, count=1, flags=re.M)

with open(SERVER_OBJ, "w", encoding=ENC, newline="") as f:
    f.write(srv)
print(f"[done] obj.dat: agregadas {len(assignments)} monturas, NumOBJs={new_num}")

# === 2) Client .ind ===
backup_cli = CLIENT_IND + f".backup_{stamp}_add_monturas"
shutil.copy2(CLIENT_IND, backup_cli)
print(f"[backup] {backup_cli}")

with open(CLIENT_IND, "r", encoding=ENC, newline="") as f:
    cli_raw = f.read()
lines = cli_raw.replace("\r\n", "\n").split("\n")
while lines and lines[-1] == "":
    lines.pop()
print(f"[info] .ind tenia {len(lines)} lineas")

# Las nuevas lineas deben caer en posiciones obj_id (line N = OBJ N)
for (obj_id, name, grh, ropaje) in assignments:
    while len(lines) < obj_id - 1:
        lines.append("")  # slot fantasma
    if len(lines) >= obj_id:
        print(f"[WARN] linea {obj_id} ya existe en .ind, sobreescribo")
        lines[obj_id - 1] = f"{name}||{grh}|{OBJTYPE_MONTURA}|10|10|10|10|||0|0|0|0|0|0|||0|0|0"
    else:
        lines.append(f"{name}||{grh}|{OBJTYPE_MONTURA}|10|10|10|10|||0|0|0|0|0|0|||0|0|0")

new_text = NEWLINE.join(lines) + NEWLINE
with open(CLIENT_IND, "w", encoding=ENC, newline="") as f:
    f.write(new_text)
print(f"[done] .ind ahora tiene {len(lines)} lineas")

print("\n[OK] Monturas agregadas:")
for (obj_id, name, grh, ropaje) in assignments:
    print(f"  OBJ{obj_id}: {name} (inv grh={grh}, body={ropaje})")
