"""Extract English and align personal-island choices: uv run --with UnityPy python tools/fix-client-localization.py"""
import gettext
import io
import json
import shutil
import struct
from pathlib import Path

import UnityPy

root = Path(__file__).resolve().parents[1]
asset = root / "game/DurangoV2_Data/resources.assets"
env = UnityPy.load(str(asset))
texts = {}
preview_counts = []
for obj in env.objects:
    if obj.type.name == "TextAsset":
        data = obj.read()
        if data.m_Name in ("en_US", "constants"):
            texts[data.m_Name] = data
    elif obj.type.name == "MonoBehaviour":
        data = obj.read(check_read=False)
        if data.m_Script.path_id and data.m_Script.deref().read().m_ClassName == "SelectPersonalRegion":
            raw = obj.get_raw_data()
            # This client's serialized component: base 32 bytes, two PPtrs, then texture array.
            count = struct.unpack_from("<i", raw, 56)[0]
            if len(raw) != 60 + 12 * count:
                raise ValueError("Unexpected personal-island component layout")
            preview_counts.append(count)

assert preview_counts and min(preview_counts) == 5, preview_counts
english = texts["en_US"].m_Script.encode("utf-8", errors="surrogateescape")
catalog = gettext.GNUTranslations(io.BytesIO(english))
assert catalog.gettext("나뭇잎") == "Leaf"
assert catalog.gettext("줄기") == "Stalk"
assert catalog.gettext("갈대") == "Reed"
destination = root / "server/data/locales/en/LC_MESSAGES/messages.mo"
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_bytes(english)

ids = [f"pe10gr_{i}" for i in range(1, 6)]
for terrain in ids:
    assert (root / f"server/data/terrains/{terrain}.zip").is_file()
constants = json.loads(texts["constants"].m_Script)
assert constants["personal_region"]["region_template_ids"][:5] == ids
if constants["personal_region"]["region_template_ids"] != ids:
    backup = root / ".launcher/backups/resources-before-island-fix.assets"
    backup.parent.mkdir(parents=True, exist_ok=True)
    if not backup.exists():
        shutil.copy2(asset, backup)
    constants["personal_region"]["region_template_ids"] = ids
    texts["constants"].m_Script = json.dumps(constants, ensure_ascii=False)
    texts["constants"].save()
    asset.write_bytes(env.file.save())
server_constants = root / "server/data/assets/constants.json"
data = json.loads(server_constants.read_text(encoding="utf-8-sig"))
data["personal_region"]["region_template_ids"] = ids
server_constants.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"English catalog: {len(catalog._catalog)} entries; all personal-island pickers support {len(ids)} choices")
