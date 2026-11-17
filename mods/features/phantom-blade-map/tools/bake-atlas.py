"""Import and cook generated atlas tiles without opening the editor UI."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor


def capture_inputs(source, output):
    receipt = json.loads((output / "receipt.json").read_text(encoding="utf-8"))
    digest, hashes = hashlib.sha256(), {}
    files = sorted((source / "tiles").rglob("*.png"), key=lambda f: f.as_posix())
    for file in sorted(source.glob("*.js"), key=lambda f: f.as_posix()) + files:
        value = hashlib.sha256(file.read_bytes()).digest()
        digest.update(file.relative_to(source).as_posix().encode() + b"\0")
        digest.update(value)
        if file.suffix == ".png":
            world, layer, level, tile = file.relative_to(source / "tiles").parts
            name = f"{world}/L{int(layer)}/S{int(level)}/T_{Path(tile).stem.replace('-', 'm')}"
            hashes[name] = value.hex()
    revision = digest.hexdigest()
    if revision != receipt["atlasRevision"]:
        raise ValueError("Cannot capture inputs: the atlas has changed since its last cook")
    file = output / ("inputs-" + revision + ".json")
    file.write_text(json.dumps(dict(atlasRevision=revision, textureHashes=hashes)), encoding="utf-8")
    print(json.dumps(dict(captured=len(hashes), atlasRevision=revision)), flush=True)


def assemble(imported, output, cooked, previous_payload, editor_version):
    reused = set(imported.pop("reusedAssets", []))
    payload = output / ("payload-" + imported["atlasRevision"][:16] + "-" + imported["compression"] + "-rdo" + str(imported["rdoLambda"]))
    assets = imported.pop("assets")
    for directory in {Path(asset).parent for asset in assets}:
        (payload / directory).mkdir(parents=True, exist_ok=True)
    def copy_asset(asset):
        asset_root = previous_payload if asset in reused else cooked
        if not (asset_root / (asset + ".uasset")).is_file():
            raise FileNotFoundError(asset_root / (asset + ".uasset"))
        for extension in (".uasset", ".uexp", ".ubulk"):
            file = asset_root / (asset + extension)
            if not file.is_file():
                continue
            destination = payload / (asset + extension)
            if file.resolve() == destination.resolve():
                continue
            # Resume an interrupted assembly only after checking actual bytes.
            if destination.is_file() and destination.stat().st_size == file.stat().st_size and hashlib.sha256(destination.read_bytes()).digest() == hashlib.sha256(file.read_bytes()).digest():
                continue
            shutil.copyfile(file, destination)
    with ThreadPoolExecutor(max_workers=8) as pool:
        for _ in pool.map(copy_asset, assets):
            pass
    imported.update(payload=str(payload), runtimeValidated=False, reusedTextures=len(reused), editor=editor_version)
    (output / "receipt.json").write_text(json.dumps(imported, indent=2), encoding="utf-8")
    print(json.dumps(imported), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--compression", choices=("bc7", "rgba8"), default="bc7")
    parser.add_argument("--reuse", type=Path, help="Reuse unchanged textures from a verified prior cook")
    parser.add_argument("--capture-inputs", action="store_true", help="Fingerprint an unchanged legacy cook before rebuilding")
    parser.add_argument("--editor", type=Path, default=Path("C:/Program Files/Epic Games/UE_5.6/Engine/Binaries/Win64/UnrealEditor-Cmd.exe"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if args.capture_inputs:
        capture_inputs(args.source.resolve(), output)
        return
    env = dict(os.environ, PBZ_ATLAS_SOURCE=str(args.source.resolve()), PBZ_ATLAS_COOK=str(output), PBZ_ATLAS_COMPRESSION=args.compression)
    editor_version = json.loads((args.editor.resolve().parents[2] / "Build/Build.version").read_text(encoding="utf-8"))
    previous_payload = None
    if args.reuse:
        previous = json.loads((args.reuse / "receipt.json").read_text(encoding="utf-8"))
        if previous["compression"] != args.compression or previous["rdoLambda"] != (10 if args.compression == "bc7" else 0):
            raise ValueError("Texture reuse requires the same compression settings")
        if any(previous["editor"].get(key) != editor_version.get(key) for key in ("MajorVersion", "MinorVersion", "PatchVersion", "Changelist")):
            raise ValueError("Texture reuse requires the same editor build")
        inputs = args.reuse / ("inputs-" + previous["atlasRevision"] + ".json")
        if not inputs.is_file():
            raise ValueError("Missing cook input hashes; capture them before changing the previous atlas")
        if json.loads(inputs.read_text(encoding="utf-8"))["atlasRevision"] != previous["atlasRevision"]:
            raise ValueError("Cook input hash revision mismatch")
        previous_payload = args.reuse / Path(previous["payload"]).name
        env["PBZ_ATLAS_REUSE"] = str(inputs.resolve())
    common = [str(args.editor), str(root / "baker/ProjectPBZ.uproject"), "-unattended", "-nop4", "-nosplash", "-NullRHI", "-stdout", "-UTF8Output"]
    def run(name, extra):
        log = output / (name + ".log")
        with log.open("wb") as stream:
            subprocess.run(common + extra, env=env, stdout=stream, stderr=subprocess.STDOUT,
                           creationflags=subprocess.CREATE_NO_WINDOW, check=True)
        print(f"{name} complete: {log}", flush=True)
    run("import", ["-run=pythonscript", "-script=" + str(root / "tools/import-atlas.py")])
    imported = json.loads((output / "import.json").read_text(encoding="utf-8"))
    run("cook", ["-run=cook", "-targetplatform=Windows", "-cookdir=" + str(root / "baker/Content/PhantomBladeMap/Atlas"), "-NoDefaultMaps", "-iterate"])
    cooked = root / "baker/Saved/Cooked/Windows/ProjectPBZ/Content/PhantomBladeMap/Atlas"
    assemble(imported, output, cooked, previous_payload, editor_version)


if __name__ == "__main__":
    main()
