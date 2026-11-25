#!/usr/bin/env python3
"""Collect the notices shipped with the NuGet packages in a release."""

import json
import shutil
import sys
from pathlib import Path


def main() -> None:
    assets_path, output = map(Path, sys.argv[1:])
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    package_roots = [Path(path) for path in assets["packageFolders"]]
    packages = {
        info["path"]
        for info in assets["libraries"].values()
        if info["type"] == "package"
    }
    for framework in assets["project"]["frameworks"].values():
        for runtime in framework.get("downloadDependencies", ()):
            name = runtime["name"].lower()
            if name.startswith("microsoft.netcore.app.runtime."):
                packages.add(f"{name}/{runtime['version'].strip('[]').split(',')[0]}")

    output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(Path(__file__).with_name("third-party-mit.txt"), output / "third-party-mit.txt")
    for package in sorted(packages):
        folder = next((root / package for root in package_roots if (root / package).is_dir()), None)
        if folder is None:
            raise SystemExit(f"Could not locate NuGet package {package}")
        package_name = package.replace("/", "-")
        for notice in folder.iterdir():
            if notice.is_file() and notice.name.lower().startswith(("license", "third-party-notices", "copying")):
                shutil.copyfile(notice, output / f"{package_name}-{notice.name}")


if __name__ == "__main__":
    main()
