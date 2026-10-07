#!/usr/bin/env python3
"""Preflight: lay every sheet over each other; list unfilled cells, bad types and refs that don't resolve.
Exit 1 if any blocking problem. Unverified cells are listed as open items (not blocking a build, but a row is
not finished until they are checked against the real game)."""
import json, pathlib, sys

SHEETS = pathlib.Path(__file__).resolve().parent.parent / "design" / "sheets"

def load():
    return {p.stem: json.loads(p.read_text()) for p in sorted(SHEETS.glob("*.json"))}

def check(sheets):
    errors, unverified = [], []
    ids = {name: {r["id"] for r in s["rows"]} for name, s in sheets.items()}
    for name, s in sheets.items():
        cols = s["columns"]
        seen = set()
        for i, row in enumerate(s["rows"]):
            rid = row.get("id", f"#{i}")
            where = f"{name}.{rid}"
            if rid in seen: errors.append(f"{where}: duplicate id")
            seen.add(rid)
            for extra in set(row) - set(cols) - {"_unverified"}:
                errors.append(f"{where}.{extra}: column not declared in sheet")
            for col, typ in cols.items():
                if col not in row:
                    errors.append(f"{where}.{col}: unfilled"); continue
                v = row[col]
                if v is None or v == "":
                    errors.append(f"{where}.{col}: unfilled"); continue
                if typ == "int" and not (isinstance(v, int) and not isinstance(v, bool)): errors.append(f"{where}.{col}: not int")
                elif typ == "number" and not (isinstance(v, (int, float)) and not isinstance(v, bool)): errors.append(f"{where}.{col}: not number")
                elif typ == "bool" and not isinstance(v, bool): errors.append(f"{where}.{col}: not bool")
                elif typ == "string" and not isinstance(v, str): errors.append(f"{where}.{col}: not string")
                elif typ.startswith("enum:") and v not in typ[5:].split("|"): errors.append(f"{where}.{col}: '{v}' not in {typ}")
                elif typ.startswith("ref:"):
                    tgt = typ[4:]
                    if tgt not in ids: errors.append(f"{where}.{col}: sheet '{tgt}' missing")
                    elif v != "none" and v not in ids[tgt]: errors.append(f"{where}.{col}: '{v}' not found in {tgt}")
                elif typ.startswith("refs:"):
                    tgt = typ[5:]
                    if not isinstance(v, list): errors.append(f"{where}.{col}: not a list")
                    else:
                        for x in v:
                            if x not in ids.get(tgt, ()): errors.append(f"{where}.{col}: '{x}' not found in {tgt}")
            for col in row.get("_unverified", []):
                if col not in cols: errors.append(f"{where}._unverified: unknown column {col}")
                else: unverified.append(f"{where}.{col}")
    # every systems row must have its class in the game or extractor code
    code = "\n".join(p.read_text() for p in (SHEETS.parent.parent).glob("game/src/*.cs"))
    code += "\n".join(p.read_text() for p in (SHEETS.parent.parent).glob("extract/*/Program.cs"))
    for row in sheets.get("systems", {"rows": []})["rows"]:
        import re
        if not re.search(r"(class|record)\s+" + re.escape(row["class"]) + r"\b", code):
            errors.append(f"systems.{row['id']}.class: {row['class']} not found in game/src")
    return errors, unverified

if __name__ == "__main__":
    sheets = load()
    errors, unverified = check(sheets)
    rows = sum(len(s["rows"]) for s in sheets.values())
    cells = sum(len(s["rows"]) * len(s["columns"]) for s in sheets.values())
    print(f"preflight: {len(sheets)} sheets, {rows} rows, {cells} cells")
    for e in errors: print("  BLOCK", e)
    for u in unverified: print("  OPEN ", u, "(unverified against the real game)")
    print(f"preflight: {len(errors)} blocking, {len(unverified)} open, {cells - len(errors) - len(unverified)} checked")
    sys.exit(1 if errors else 0)
