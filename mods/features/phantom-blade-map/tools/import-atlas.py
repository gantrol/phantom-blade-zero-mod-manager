"""Run inside UnrealEditor-Cmd; bake-atlas.py supplies the source and receipt."""
import hashlib
import json
import os
from pathlib import Path

import unreal


source = Path(os.environ["PBZ_ATLAS_SOURCE"]).resolve()
output = Path(os.environ["PBZ_ATLAS_COOK"]).resolve()
index = json.loads((source / "index.js").read_text(encoding="utf-8").split("module.exports=", 1)[1].rstrip(";\n\r"))
files = sorted((source / "tiles").rglob("*.png"), key=lambda file: file.as_posix())
digest = hashlib.sha256()
for file in sorted(source.glob("*.js"), key=lambda file: file.as_posix()) + files:
    digest.update(file.relative_to(source).as_posix().encode() + b"\0")
    digest.update(hashlib.sha256(file.read_bytes()).digest())

assets = []
tools = unreal.AssetToolsHelpers.get_asset_tools()
for start in range(0, len(files), 32):
    tasks = []
    for file in files[start:start + 32]:
        world, layer, level, tile = file.relative_to(source / "tiles").parts
        if world not in {entry["world"] for entry in index["worlds"]}:
            raise ValueError("Unknown atlas world: " + world)
        relative = f"{world}/L{int(layer)}/S{int(level)}/T_{Path(tile).stem.replace('-', 'm')}"
        task = unreal.AssetImportTask()
        task.filename = str(file)
        task.destination_path = "/Game/PhantomBladeMap/Atlas/" + str(Path(relative).parent).replace("\\", "/")
        task.destination_name = Path(relative).name
        task.automated = True
        task.replace_existing = True
        task.replace_existing_settings = True
        task.save = False
        tasks.append(task)
        assets.append(relative)
    tools.import_asset_tasks(tasks)
    for task in tasks:
        if len(task.imported_object_paths) != 1:
            raise RuntimeError("Texture import failed: " + task.filename)
        texture = unreal.load_asset(task.imported_object_paths[0])
        texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON)
        texture.set_editor_property("mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS)
        texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
        texture.set_editor_property("filter", unreal.TextureFilter.TF_BILINEAR)
        texture.set_editor_property("address_x", unreal.TextureAddress.TA_CLAMP)
        texture.set_editor_property("address_y", unreal.TextureAddress.TA_CLAMP)
        texture.set_editor_property("srgb", True)
        texture.set_editor_property("never_stream", True)
        if not unreal.EditorAssetLibrary.save_loaded_asset(texture):
            raise RuntimeError("Texture save failed: " + task.filename)
    unreal.log(f"Atlas imported {min(start + 32, len(files))}/{len(files)}")

output.mkdir(parents=True, exist_ok=True)
(output / "import.json").write_text(json.dumps({
    "source": str(source), "textures": len(assets), "assets": assets,
    "atlasRevision": digest.hexdigest(),
    "indexSha256": hashlib.sha256((source / "index.js").read_bytes()).hexdigest(),
}, indent=2), encoding="utf-8")
