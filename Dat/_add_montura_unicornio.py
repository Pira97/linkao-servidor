"""
Agrega 'Montura Unicornio Blanco' como OBJ2016 al server y al .ind del cliente.

Replica el formato de OBJ1342 (Burro).
"""
import os, re, shutil, datetime

SERVER_OBJ = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\Servidor\Dat\obj.dat"
CLIENT_IND = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\LinkAO-Godot\init\locale_obj_es.ind"

ENC = "cp1252"
NEWLINE = "\r\n"

NEW_ID         = 2016
NEW_NAME       = "Montura Unicornio Blanco"
NEW_GRH        = 5841
NEW_NUM_ROPAJE = 723
NEW_OBJTYPE    = 44  # otMonturas

stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")

# === 1) Server obj.dat ===
backup_srv = SERVER_OBJ + f".backup_{stamp}_add_unicornio"
shutil.copy2(SERVER_OBJ, backup_srv)
print(f"[backup] {backup_srv}")

with open(SERVER_OBJ, "r", encoding=ENC, newline="") as f:
    srv = f.read()

# Verificar que OBJ2016 no exista ya
if re.search(rf"^\[OBJ{NEW_ID}\]", srv, re.M):
    print(f"[ERROR] OBJ{NEW_ID} ya existe en obj.dat. Abortando.")
    raise SystemExit(1)

new_section = NEWLINE.join([
    f"[OBJ{NEW_ID}]",
    f"Name={NEW_NAME}",
    f"GrhIndex={NEW_GRH}",
    f"NumRopaje={NEW_NUM_ROPAJE}",
    f"ObjType={NEW_OBJTYPE}",
    f"valor=280000",
    f"MinSkill=25",
    f"MinHIT=10",
    f"MaxHIT=10",
    f"MINDEF=10",
    f"MAXDEF=10",
    f"Crucial=1",
    f"Permanente=3",
    f"LocaleID={NEW_ID}",
    f"Texto=",
    "",  # blank line after
])

if not srv.endswith(NEWLINE):
    srv += NEWLINE
srv += NEWLINE + new_section + NEWLINE

# Actualizar NumOBJs
srv = re.sub(r"^NumOBJs=\d+", f"NumOBJs={NEW_ID}", srv, count=1, flags=re.M)

with open(SERVER_OBJ, "w", encoding=ENC, newline="") as f:
    f.write(srv)
print(f"[done] obj.dat actualizado con OBJ{NEW_ID} y NumOBJs={NEW_ID}")

# === 2) Client .ind ===
backup_cli = CLIENT_IND + f".backup_{stamp}_add_unicornio"
shutil.copy2(CLIENT_IND, backup_cli)
print(f"[backup] {backup_cli}")

with open(CLIENT_IND, "r", encoding=ENC, newline="") as f:
    cli_raw = f.read()

# Contar lineas actuales (parsear igual que el cliente)
lines = cli_raw.replace("\r\n", "\n").split("\n")
# quitar trailing empty
while lines and lines[-1] == "":
    lines.pop()
print(f"[info] .ind tiene {len(lines)} lineas")

if len(lines) >= NEW_ID:
    print(f"[ERROR] .ind ya tiene linea {NEW_ID}. Abortando.")
    raise SystemExit(1)

# Formato del .ind: Name|Desc|GrhIndex|ObjType|MaxDef|MinDef|MaxHit|MinHit|...
# Para una montura: solo necesitamos Name, "", Grh, ObjType
new_line = f"{NEW_NAME}||{NEW_GRH}|{NEW_OBJTYPE}|10|10|10|10|||0|0|0|0|0|0|||0|0|0"

# Si necesitamos rellenar lineas faltantes (entre lines.size() y NEW_ID-1)
while len(lines) < NEW_ID - 1:
    lines.append("")  # slot fantasma

lines.append(new_line)

new_text = NEWLINE.join(lines) + NEWLINE
with open(CLIENT_IND, "w", encoding=ENC, newline="") as f:
    f.write(new_text)
print(f"[done] .ind actualizado, ahora tiene {len(lines)} lineas")

print(f"\n[OK] OBJ{NEW_ID} '{NEW_NAME}' agregado:")
print(f"     Inventario: grh {NEW_GRH}")
print(f"     Cuerpo montura: NumRopaje {NEW_NUM_ROPAJE}")
