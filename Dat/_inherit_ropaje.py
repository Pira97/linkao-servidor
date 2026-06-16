"""
Para cada item importado (sin NumRopaje/Anim) que sea equipable,
busca un item pre-existente en el server con nombre similar y copia
los campos visuales (NumRopaje para armaduras, Anim para armas/cascos/escudos).

Hace backup. Preserva encoding cp1252 y CRLF.
"""
import re, os, shutil, datetime, unicodedata

ROOT = r"c:\Users\Marke\OneDrive\Escritorio\clientes y servidor para trabajar\migracion a cliente godot\Servidor\Dat"
SERVER_OBJ = os.path.join(ROOT, "obj.dat")
PRE_IMPORT_BACKUP = os.path.join(ROOT, "obj.dat.backup_20260521_202457_import_from_client")

ENC = "cp1252"
NEWLINE = "\r\n"

EQUIPABLE_TYPES = {
    "3":  "armadura",  # NumRopaje
    "2":  "arma",      # Anim
    "16": "escudo",    # Anim, DosManos
    "17": "casco",     # Anim
    "18": "anillo",    # raramente tiene anim visual
    "46": "nudillos",  # Anim
}

# Campos visuales a copiar por tipo
FIELDS_TO_INHERIT = {
    "3":  ["NumRopaje"],
    "2":  ["Anim", "WeaponAnim"],
    "16": ["Anim", "DosManos"],
    "17": ["Anim"],
    "18": ["NumRopaje", "Anim"],
    "46": ["Anim"],
}

def normalize(s):
    """Normaliza nombre para matching: lowercase, sin tildes, sin paréntesis con bonos."""
    s = s.lower().strip()
    # quitar acentos
    s = "".join(c for c in unicodedata.normalize("NFD", s) if unicodedata.category(c) != "Mn")
    # quitar contenido entre paréntesis (suele ser modificador: +5, RM +5, etc.)
    s = re.sub(r"\([^)]*\)", "", s)
    # quitar sufijos comunes: +N, "rm +N", "iii", "iv", numeros romanos
    s = re.sub(r"\b(rm|drop)\s*\+?\s*\d+\b", "", s)
    s = re.sub(r"\+\s*\d+", "", s)
    s = re.sub(r"\biii\b|\biv\b|\bii\b|\bvi+\b", "", s)
    # quitar puntuación, espacios múltiples
    s = re.sub(r"[^\w\s]", " ", s)
    s = re.sub(r"\s+", " ", s)
    return s.strip()

def parse_sections(data):
    """Retorna dict id -> (raw_body_str, fields_dict)."""
    out = {}
    for match in re.finditer(r"\[OBJ(\d+)\]([^\[]*)", data):
        obj_id = int(match.group(1))
        body = match.group(2)
        fields = {}
        for line in body.splitlines():
            line = line.strip()
            if "=" in line and not line.startswith("'"):
                k, v = line.split("=", 1)
                fields[k.strip()] = v.strip()
        out[obj_id] = (body, fields)
    return out

# Backup
stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
backup_path = SERVER_OBJ + f".backup_{stamp}_inherit_ropaje"
shutil.copy2(SERVER_OBJ, backup_path)
print(f"[backup] {backup_path}")

# Read current obj.dat
with open(SERVER_OBJ, "r", encoding=ENC, newline="") as f:
    cur_raw = f.read()

# Read pre-import obj.dat to identify which IDs are imported
with open(PRE_IMPORT_BACKUP, "r", encoding=ENC, newline="") as f:
    pre_raw = f.read()
pre_ids = set(int(m) for m in re.findall(r"^\[OBJ(\d+)\]", pre_raw, re.M))
print(f"[info] IDs pre-existentes: {len(pre_ids)}")

# Parse current sections
sections = parse_sections(cur_raw)
print(f"[info] Total secciones en server actual: {len(sections)}")

# Construir indice de nombres normalizados de items pre-existentes con datos visuales
pre_by_name = {}  # normalized name -> list of (id, fields)
for obj_id, (body, fields) in sections.items():
    if obj_id not in pre_ids: continue
    obj_type = fields.get("ObjType") or fields.get("Objtype")
    if obj_type not in EQUIPABLE_TYPES: continue
    name = fields.get("Name", "")
    if not name: continue
    norm = normalize(name)
    if not norm: continue
    pre_by_name.setdefault(norm, []).append((obj_id, fields))

print(f"[info] Indice de pre-existentes equipables por nombre: {len(pre_by_name)} nombres únicos")

# Para cada importado equipable sin NumRopaje/Anim, buscar match
matched = 0
no_match = 0
no_need = 0
updates = []  # (obj_id, dict_de_campos_a_agregar)
no_match_list = []

for obj_id, (body, fields) in sections.items():
    if obj_id in pre_ids: continue  # es pre-existente, skip
    obj_type = fields.get("ObjType") or fields.get("Objtype")
    if obj_type not in EQUIPABLE_TYPES: continue

    fields_needed = FIELDS_TO_INHERIT.get(obj_type, [])
    # ¿Falta alguno?
    missing = [f for f in fields_needed if f not in fields and f.lower() not in {k.lower() for k in fields}]
    if not missing:
        no_need += 1
        continue

    name = fields.get("Name", "")
    norm = normalize(name)

    # 1) match exacto normalizado
    candidates = pre_by_name.get(norm, [])

    # 2) substring match: imported contiene preexistente o viceversa
    if not candidates:
        for pre_norm, lst in pre_by_name.items():
            if not pre_norm: continue
            # solo aceptar si la parte común es significativa (>=10 chars)
            if len(pre_norm) >= 10 and (pre_norm in norm or norm in pre_norm):
                candidates = lst
                break

    if not candidates:
        no_match += 1
        no_match_list.append((obj_id, name))
        continue

    # Tomar el primero (heurística: el id menor suele ser el original "base")
    src_id, src_fields = min(candidates, key=lambda x: x[0])
    # Mismo ObjType?
    src_type = src_fields.get("ObjType") or src_fields.get("Objtype")
    if src_type != obj_type:
        no_match += 1
        no_match_list.append((obj_id, name + f" [type mismatch: src={src_type}]"))
        continue

    new_fields = {}
    for f in missing:
        # buscar el campo en src_fields (case-insensitive)
        for k, v in src_fields.items():
            if k.lower() == f.lower() and v and v != "0":
                new_fields[f] = v
                break
    if new_fields:
        updates.append((obj_id, name, src_id, new_fields))
        matched += 1
    else:
        no_match += 1
        no_match_list.append((obj_id, name + f" [src tiene los campos vacios: src_id={src_id}]"))

print(f"\n[result] Importados equipables que NO necesitaban herencia: {no_need}")
print(f"[result] Importados que SI encontraron match: {matched}")
print(f"[result] Importados SIN match: {no_match}")
print(f"[result] Total updates a aplicar: {len(updates)}")

if updates:
    print("\n[sample] Primeros 5 matches:")
    for obj_id, name, src_id, new_fields in updates[:5]:
        print(f"  OBJ{obj_id} '{name[:40]}' <- OBJ{src_id}: {new_fields}")

if no_match_list:
    print(f"\n[sample] Primeros 10 SIN match:")
    for obj_id, name in no_match_list[:10]:
        print(f"  OBJ{obj_id}: {name[:60]}")

# Aplicar updates al cur_raw: insertar las nuevas lineas al final de cada seccion
def apply_updates(text, updates):
    for obj_id, name, src_id, new_fields in updates:
        # encontrar la seccion
        pattern = rf"(\[OBJ{obj_id}\][^\[]*?)(\n\n|\r\n\r\n|$)"
        m = re.search(pattern, text, re.MULTILINE)
        if not m:
            print(f"  [warn] no encontre seccion OBJ{obj_id}")
            continue
        section_body = m.group(1)
        # construir lineas nuevas
        new_lines = []
        for k, v in new_fields.items():
            # solo agregar si no esta ya
            if not re.search(rf"^{re.escape(k)}\s*=", section_body, re.MULTILINE | re.IGNORECASE):
                new_lines.append(f"{k}={v}")
        if new_lines:
            insert_str = NEWLINE + NEWLINE.join(new_lines)
            new_section = section_body.rstrip() + insert_str
            text = text[:m.start(1)] + new_section + text[m.end(1):]
    return text

if matched > 0:
    cur_raw_new = apply_updates(cur_raw, updates)
    with open(SERVER_OBJ, "w", encoding=ENC, newline="") as f:
        f.write(cur_raw_new)
    print(f"\n[done] obj.dat actualizado con {matched} herencias")
    # save no-match list
    nm_path = os.path.join(ROOT, "_no_match_ropaje.txt")
    with open(nm_path, "w", encoding="utf-8") as f:
        for obj_id, name in no_match_list:
            f.write(f"OBJ{obj_id}\t{name}\n")
    print(f"[done] lista de no-match en: {nm_path}")
else:
    print("[done] no se hicieron updates")
