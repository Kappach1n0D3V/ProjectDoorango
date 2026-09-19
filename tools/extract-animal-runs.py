"""Usage: uv run --with UnityPy python tools/extract-animal-runs.py [--write]"""
import json
from pathlib import Path
import sys

import UnityPy

root = Path(__file__).resolve().parents[1]
bundles = root / "game/DurangoV2_Data/StreamingAssets/AssetBundles"
table_path = root / "server/data/assets/derived/animal_motions.json"
source = table_path.read_text(encoding="utf-8-sig")
table = json.loads(source)
candidates = {}
evidence = []

for bundle in sorted(bundles.glob("models$animals$*framework*.bundle")):
    env = UnityPy.load(str(bundle))
    objects = {obj.path_id: obj for obj in env.objects}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        framework = obj.read_typetree()
        for group in framework.get("move_sets", []):
            for move_set in group.get("elems", []):
                if "pet" in move_set["name"].lower():
                    continue
                motions = [m for m in move_set.get("motions", [])
                           if not m.get("conditions", {}).get("flag")]
                for run in motions:
                    name = run.get("motion", "")
                    if "run" not in name.lower():
                        continue
                    ref = run.get("_clipObjMove", {})
                    clip = objects.get(ref.get("m_PathID"))
                    if ref.get("m_FileID") != 0 or clip is None or clip.type.name != "AnimationClip":
                        continue
                    if clip.read().m_Name != name:
                        raise ValueError(f"Clip name mismatch: {bundle.name}: {name}")
                    evidence.append({"bundle": bundle.name, "set": move_set["name"], "run": name})
                    for motion in motions:
                        candidates.setdefault(motion["motion"], set()).add(name)

mapped = {}
unresolved = {}
for entity_type, motions in table.items():
    matches = candidates.get(motions.get("move"), set())
    if len(matches) == 1:
        mapped[entity_type] = next(iter(matches))
    else:
        unresolved[entity_type] = {"move": motions.get("move"), "candidates": sorted(matches)}

if not mapped:
    raise RuntimeError("No verified run clips found; table unchanged")
if "--write" in sys.argv:
    # Preserve the existing one-line-per-animal format and unrelated fields.
    lines = source.splitlines(keepends=True)
    for i, line in enumerate(lines):
        key = line.strip().split(":", 1)[0].strip('"')
        if key not in mapped:
            continue
        motions = table[key]
        motions["run"] = mapped[key]
        suffix = "," if line.rstrip().endswith(",") else ""
        lines[i] = f'  "{key}": {json.dumps(motions, ensure_ascii=False, separators=(",", ":"))}{suffix}\n'
    table_path.write_text("".join(lines), encoding="utf-8")

report = {"mapped_types": len(mapped), "total_types": len(table),
          "unresolved": unresolved, "verified_assets": evidence, "runs": mapped}
(root / "animal-framework-audit.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"Verified run clips for {len(mapped)}/{len(table)} types; {len(unresolved)} retain existing movement")
print("Unresolved:", sorted({v["move"] for v in unresolved.values()}))
