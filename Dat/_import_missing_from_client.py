"""
Lee Servidor/Dat/obj.dat y LinkAO-Godot/init/locale_obj_es.ind.
Para cada ID presente en el .ind con Name != "" pero ausente como [OBJ#] en obj.dat,
genera la seccion correspondiente y la agrega al final de obj.dat.
Tambien actualiza NumOBJs=.
Mantiene encoding latin-1 (CP1252) y CRLF.
"""
import re, os, sys, shutil, datetime

ROOT_SERVER = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\Servidor\Dat"
ROOT_CLIENT = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\LinkAO-Godot\init"

SERVER_OBJ = os.path.join(ROOT_SERVER, "obj.dat")
CLIENT_IND = os.path.join(ROOT_CLIENT, "locale_obj_es.ind")

ENC = "cp1252"
NEWLINE = "\r\n"

# Limites del server VB6
NUMCLASES = 18  # ClaseProhibida(1 To 18) en Declares.bas
NUMRAZAS  = 6   # RazaProhibida(1 To 6)  en Declares.bas

def filter_ids(csv_str: str, max_val: int) -> str:
    """Filtra una lista CSV de ids dejando solo los del rango 1..max_val (sin duplicados, orden preservado)."""
    seen = set()
    out = []
    for tok in csv_str.split(","):
        tok = tok.strip()
        if not tok: continue
        try:
            v = int(tok)
        except ValueError:
            continue
        if 1 <= v <= max_val and v not in seen:
            seen.add(v)
            out.append(str(v))
    return ",".join(out)

# Backup
stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
backup_path = SERVER_OBJ + f".backup_{stamp}_import_from_client"
shutil.copy2(SERVER_OBJ, backup_path)
print(f"[backup] {backup_path}")

# Read server obj.dat
with open(SERVER_OBJ, "r", encoding=ENC, newline="") as f:
    server_raw = f.read()

# Find existing [OBJ#] ids
existing_ids = set(int(m) for m in re.findall(r"^\[OBJ(\d+)\]", server_raw, re.MULTILINE))
print(f"[server] secciones [OBJ#] existentes: {len(existing_ids)}")

# Find NumOBJs= line
m_num = re.search(r"^NumOBJs=(\d+)\s*$", server_raw, re.MULTILINE)
old_num = int(m_num.group(1)) if m_num else 0
print(f"[server] NumOBJs actual = {old_num}")

# Read client .ind
with open(CLIENT_IND, "r", encoding=ENC, newline="") as f:
    client_lines = f.read().splitlines()
print(f"[client] lineas en .ind = {len(client_lines)}")

# Build new sections
def field(parts, i):
    return parts[i].strip() if i < len(parts) else ""

new_sections = []
added_ids = []
skipped_empty = 0
for idx, line in enumerate(client_lines, start=1):
    if idx in existing_ids:
        continue
    parts = line.split("|")
    name = field(parts, 0)
    if not name:
        skipped_empty += 1
        continue
    desc      = field(parts, 1)
    grh       = field(parts, 2)
    obj_type  = field(parts, 3)
    max_def   = field(parts, 4)
    min_def   = field(parts, 5)
    max_hit   = field(parts, 6)
    min_hit   = field(parts, 7)
    crea_luz  = field(parts, 8)
    rango_luz = field(parts, 9)
    snd1      = field(parts, 10)
    snd2      = field(parts, 11)
    snd3      = field(parts, 12)
    min_elv   = field(parts, 13)
    sta_req   = field(parts, 15)
    clases    = field(parts, 16)
    aura      = field(parts, 20)

    lines_out = [f"[OBJ{idx}]"]
    lines_out.append(f"Name={name}")
    if grh and grh != "0":       lines_out.append(f"GrhIndex={grh}")
    if obj_type:                  lines_out.append(f"ObjType={obj_type}")
    if max_def and max_def != "0": lines_out.append(f"MaxDef={max_def}")
    if min_def and min_def != "0": lines_out.append(f"MinDef={min_def}")
    if max_hit and max_hit != "0": lines_out.append(f"MaxHIT={max_hit}")
    if min_hit and min_hit != "0": lines_out.append(f"MinHIT={min_hit}")
    if crea_luz:
        if rango_luz and rango_luz != "0":
            lines_out.append(f"CreaLuz={rango_luz}:{crea_luz}")
        else:
            lines_out.append(f"CreaLuz={crea_luz}")
    if snd1 and snd1 != "0":       lines_out.append(f"Snd1={snd1}")
    if snd2 and snd2 != "0":       lines_out.append(f"Snd2={snd2}")
    if snd3 and snd3 != "0":       lines_out.append(f"Snd3={snd3}")
    if min_elv and min_elv != "0": lines_out.append(f"MinELV={min_elv}")
    if sta_req and sta_req != "0": lines_out.append(f"StaRequerido={sta_req}")
    if clases:
        clases_ok = filter_ids(clases, NUMCLASES)
        if clases_ok: lines_out.append(f"ClasesProhibidas={clases_ok}")
    if aura and aura != "0":       lines_out.append(f"Aura={aura}")
    lines_out.append(f"LocaleID={idx}")
    if desc:                       lines_out.append(f"Texto={desc}")
    lines_out.append("")  # blank line between sections
    new_sections.append(NEWLINE.join(lines_out))
    added_ids.append(idx)

print(f"[plan] objetos a agregar = {len(added_ids)}")
print(f"[plan] saltados por nombre vacio = {skipped_empty}")
if added_ids:
    print(f"[plan] primeros 10 IDs nuevos = {added_ids[:10]}")
    print(f"[plan] ultimos 10 IDs nuevos  = {added_ids[-10:]}")

if not added_ids:
    print("[done] nada que agregar.")
    sys.exit(0)

new_max = max(existing_ids | set(added_ids))
print(f"[plan] nuevo NumOBJs sera = {new_max}")

# Update NumOBJs= line
if m_num:
    server_raw_new = re.sub(
        r"^NumOBJs=\d+",
        f"NumOBJs={new_max}",
        server_raw,
        count=1,
        flags=re.MULTILINE,
    )
else:
    server_raw_new = f"NumOBJs={new_max}{NEWLINE}" + server_raw

# Asegurar que terminamos en CRLF antes de pegar nuevo bloque
if not server_raw_new.endswith(NEWLINE):
    server_raw_new += NEWLINE

header_comment = (
    NEWLINE +
    f"'==============================================================" + NEWLINE +
    f"' Objetos importados desde locale_obj_es.ind ({stamp})" + NEWLINE +
    f"' Total importados: {len(added_ids)}" + NEWLINE +
    f"'==============================================================" + NEWLINE +
    NEWLINE
)

server_raw_new += header_comment + (NEWLINE.join(new_sections)) + NEWLINE

# Write
with open(SERVER_OBJ, "w", encoding=ENC, newline="") as f:
    f.write(server_raw_new)

print(f"[done] obj.dat actualizado.")
print(f"[done] backup en: {backup_path}")
