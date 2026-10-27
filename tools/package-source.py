#!/usr/bin/env python3
"""Package the complete editable source while excluding machine state/secrets."""
import argparse
import hashlib
from pathlib import Path
import tarfile

PUBLIC_ROOTS = {"src", "components", "docs", "tools", "tests", ".github"}
PUBLIC_FILES = {
    "README.md",
    "LICENSE",
    "LICENSE.md",
    "NOTICE",
    "NOTICES.md",
    "THIRD-PARTY-NOTICES.md",
    "THIRD_PARTY_NOTICES.md",
    "install.sh",
    "MeshAll.slnx",
    "Directory.Build.props",
    ".gitignore",
    ".editorconfig",
    ".prettierrc.json",
    "global.json",
}
PUBLIC_JSON = {
    "package.json",
    "package-lock.json",
    "appsettings.json",
    "appsettings.Development.json",
    "launchSettings.json",
    "global.json",
    ".prettierrc.json",
}
excluded = {
    ".git",
    "node_modules",
    "bin",
    "obj",
    "Data",
    "artifacts",
    "test-results",
    ".vs",
    ".idea",
    ".venv",
    "__pycache__",
    ".pytest_cache",
    "downloads",
    "meshcentral-data",
    "meshcentral-files",
    "meshcentral-backups",
    "keys",
    ".keys",
    "secrets",
}


def public_path(relative):
    if not relative.parts or (
        relative.parts[0] not in PUBLIC_ROOTS
        and relative.as_posix() not in PUBLIC_FILES
    ):
        return False
    if any(part in excluded for part in relative.parts):
        return False
    name = relative.name.lower()
    if name in {
        "agent-state.json",
        "agent.json",
        "server.json",
        "credentials.json",
        "secrets.json",
        "browser.json",
        "storage-state.json",
        "appsettings.local.json",
    }:
        return False
    if name.startswith(".env") or name.endswith(
        (
            ".o",
            ".log",
            ".db",
            ".db-wal",
            ".db-shm",
            ".user",
            ".pfx",
            ".p12",
            ".msh",
            ".msi",
            ".mst",
            ".cab",
            ".pid",
            ".env",
        )
    ):
        return False
    if (
        relative.suffix.lower() == ".json"
        and relative.parts[0] != "components"
        and relative.name not in PUBLIC_JSON
    ):
        return False
    if (
        len(relative.parts) > 3
        and relative.parts[-2] == "MeshAgent"
        and relative.name.startswith(("meshagent_", "DEBUG_meshagent_"))
    ):
        return False
    if "upstream" not in relative.parts and relative.name.endswith((".pem", ".key")):
        return False
    return True


def package(root, output):
    root, output = Path(root).resolve(), Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)

    def include(info):
        relative = Path(*Path(info.name).parts[1:])
        if not public_path(relative):
            return None
        if info.issym():
            target = (root / relative.parent / info.linkname).resolve()
            if not target.is_relative_to(root) or not public_path(
                target.relative_to(root)
            ):
                return None
        if not (info.isfile() or info.isdir() or info.issym()):
            return None
        info.uid = info.gid = 0
        info.uname = info.gname = "root"
        return info

    with tarfile.open(output, "w:gz") as archive:
        for entry in sorted(root.iterdir()):
            if entry.resolve() != output:
                archive.add(entry, arcname="EndpointControl/" + entry.name, filter=include)


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        default=str(
            root / "src/MeshAll.Server/wwwroot/downloads/EndpointControl-source.tar.gz"
        ),
    )
    output = Path(parser.parse_args().output).resolve()
    package(root, output)
    with output.open("rb") as content:
        digest = hashlib.file_digest(content, "sha256").hexdigest()
    print(f"{output}\nSHA256 {digest}\nBytes {output.stat().st_size}")
