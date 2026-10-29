#!/usr/bin/env python3
"""Builds the .unitypackage from this repository, without Unity.

The repository does not track Unity .meta files. Every asset instead gets a GUID derived from its Unity path
(the MD5 of e.g. "Assets/Maberikku/FermataFTConverter/Runtime/FermataNativeFTBridge.cs"), so GUIDs stay the same
across releases and references inside the package, such as the prefab's script, stay valid.
Renaming or moving a file changes its GUID.

    python .github/scripts/build_unitypackage.py --out dist/FermataNativeFT.unitypackage
    python .github/scripts/build_unitypackage.py --sync-metas   # give a local Unity checkout the same GUIDs
"""
import argparse
import gzip
import hashlib
import io
import os
import re
import subprocess
import sys
import tarfile

PACKAGE_ROOT = "Assets/Maberikku/FermataFTConverter"

# Repository files that are not part of the package. README is distributed separately; NOTES is for developers.
EXCLUDE = re.compile(r"^(README\.md|NOTES\.md|\.gitignore|\.gitattributes|\.github/.*|.*\.meta)$")

# Assets whose YAML may reference other assets by GUID.
YAML_ASSETS = (".prefab", ".asset", ".controller", ".overrideController", ".anim", ".mask", ".mat", ".unity")

IMPORTER_TAIL = "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
IMPORTERS = {
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n"
           "  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
    ".asmdef": "AssemblyDefinitionImporter:\n" + IMPORTER_TAIL,
    ".prefab": "PrefabImporter:\n" + IMPORTER_TAIL,
    ".md": "TextScriptImporter:\n" + IMPORTER_TAIL,
    ".txt": "TextScriptImporter:\n" + IMPORTER_TAIL,
    ".json": "TextScriptImporter:\n" + IMPORTER_TAIL,
}


def guid(asset_path):
    return hashlib.md5(asset_path.encode("utf-8")).hexdigest()


def meta(asset_path, is_folder):
    head = f"fileFormatVersion: 2\nguid: {guid(asset_path)}\n"
    if is_folder:
        return head + "folderAsset: yes\nDefaultImporter:\n" + IMPORTER_TAIL
    return head + IMPORTERS.get(os.path.splitext(asset_path)[1].lower(), "DefaultImporter:\n" + IMPORTER_TAIL)


def package_entries(repo):
    """(Unity asset path, repository-relative path or None for folders), folders first."""
    listed = subprocess.run(["git", "-C", repo, "ls-files", "-z"], check=True, capture_output=True).stdout
    files = sorted(p for p in listed.decode("utf-8").split("\0") if p and not EXCLUDE.match(p))
    folders = {""}
    for f in files:
        parts = f.split("/")[:-1]
        folders.update("/".join(parts[:i]) for i in range(1, len(parts) + 1))
    entries = [(f"{PACKAGE_ROOT}/{d}" if d else PACKAGE_ROOT, None) for d in sorted(folders)]
    entries += [(f"{PACKAGE_ROOT}/{f}", f) for f in files]
    return entries


def broken_references(repo, entries):
    known = {guid(path) for path, _ in entries}
    problems = []
    for _, rel in entries:
        if rel is None or not rel.endswith(YAML_ASSETS):
            continue
        with open(os.path.join(repo, rel), encoding="utf-8") as f:
            text = f.read()
        for ref in sorted(set(re.findall(r"guid: ([0-9a-f]{32})", text))):
            # Unity's built-in resources use GUIDs starting with sixteen zeros.
            if ref not in known and not ref.startswith("0" * 16):
                problems.append(f"{rel} references GUID {ref}, which is not in the package")
    return problems


def source_date(repo):
    """The time stamped on every archive entry: $SOURCE_DATE_EPOCH, or the commit time of HEAD."""
    if os.environ.get("SOURCE_DATE_EPOCH"):
        return int(os.environ["SOURCE_DATE_EPOCH"])
    return int(subprocess.run(["git", "-C", repo, "log", "-1", "--format=%ct"], check=True, capture_output=True).stdout)


def build(repo, out, entries):
    # Unity keeps an imported file's archive timestamp, and didn't recompile an assembly whose new scripts were
    # stamped 1970 (zero). Unity's own exporter stores real times, so every entry gets the commit time: real, newer
    # for every release, and the same for every build of a commit.
    mtime = source_date(repo)

    def add(tar, name, data):
        info = tarfile.TarInfo(name)
        info.size = len(data)
        info.mode = 0o644
        info.mtime = mtime
        tar.addfile(info, io.BytesIO(data))

    def add_folder(tar, name):
        info = tarfile.TarInfo(name)
        info.type = tarfile.DIRTYPE
        info.mode = 0o755
        info.mtime = mtime
        tar.addfile(info)

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    # No time or file name in the gzip header, so a commit always gives the same bytes.
    with open(out, "wb") as raw, gzip.GzipFile(filename="", fileobj=raw, mode="wb", mtime=0) as gz, \
            tarfile.open(fileobj=gz, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        for asset_path, rel in entries:
            g = guid(asset_path)
            add_folder(tar, f"{g}/")
            add(tar, f"{g}/pathname", asset_path.encode("utf-8"))
            add(tar, f"{g}/asset.meta", meta(asset_path, rel is None).encode("utf-8"))
            if rel is not None:
                with open(os.path.join(repo, rel), "rb") as f:
                    add(tar, f"{g}/asset", f.read())


def sync_metas(repo, entries):
    """Writes missing .meta files and fixes GUIDs in existing ones, keeping their importer settings."""
    for asset_path, rel in entries:
        local = os.path.join(repo, rel) if rel is not None else os.path.join(repo, asset_path[len(PACKAGE_ROOT):].lstrip("/"))
        meta_path = local.rstrip("/\\") + ".meta"
        expected = guid(asset_path)
        if not os.path.exists(meta_path):
            with open(meta_path, "w", encoding="utf-8", newline="\n") as f:
                f.write(meta(asset_path, rel is None))
            print(f"created {meta_path}")
            continue
        with open(meta_path, encoding="utf-8") as f:
            text = f.read()
        current = re.search(r"^guid: ([0-9a-f]{32})$", text, re.M)
        if current and current.group(1) != expected:
            with open(meta_path, "w", encoding="utf-8", newline="") as f:
                f.write(text.replace(current.group(0), f"guid: {expected}", 1))
            print(f"updated {meta_path}: {current.group(1)} -> {expected}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", default=os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))
    parser.add_argument("--out", help="path of the .unitypackage to write")
    parser.add_argument("--sync-metas", action="store_true", help="write .meta files next to the repository files")
    args = parser.parse_args()
    if not args.out and not args.sync_metas:
        parser.error("nothing to do: pass --out and/or --sync-metas")

    entries = package_entries(args.repo)
    problems = broken_references(args.repo, entries)
    if problems:
        sys.exit("\n".join(problems))
    if args.sync_metas:
        sync_metas(args.repo, entries)
    if args.out:
        build(args.repo, args.out, entries)
        print(f"wrote {args.out} ({len(entries)} entries)")
        for asset_path, rel in entries:
            print(f"  {guid(asset_path)}  {asset_path}{'/' if rel is None else ''}")


if __name__ == "__main__":
    main()
