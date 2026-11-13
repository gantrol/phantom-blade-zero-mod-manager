"""Import and cook generated atlas tiles without opening the editor UI."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--editor", type=Path, default=Path("C:/Program Files/Epic Games/UE_5.6/Engine/Binaries/Win64/UnrealEditor-Cmd.exe"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PBZ_ATLAS_SOURCE=str(args.source.resolve()), PBZ_ATLAS_COOK=str(output))
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
    payload = output / ("payload-" + imported["atlasRevision"][:16])
    cooked = root / "baker/Saved/Cooked/Windows/ProjectPBZ/Content/PhantomBladeMap/Atlas"
    for asset in imported.pop("assets"):
        source = cooked / (asset + ".uasset")
        if not source.is_file():
            raise FileNotFoundError(source)
        for extension in (".uasset", ".uexp", ".ubulk"):
            file = cooked / (asset + extension)
            if file.is_file():
                destination = payload / (asset + extension)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(file, destination)
    imported.update(payload=str(payload), runtimeValidated=False,
                    editor=json.loads((args.editor.resolve().parents[2] / "Build/Build.version").read_text(encoding="utf-8")))
    (output / "receipt.json").write_text(json.dumps(imported, indent=2), encoding="utf-8")
    print(json.dumps(imported), flush=True)


if __name__ == "__main__":
    main()
