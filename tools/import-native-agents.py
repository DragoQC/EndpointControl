"""Import hash-pinned baseline native artifacts; instance policies are added at download time."""

import argparse
import hashlib
import json
from pathlib import Path
import urllib.request


def verify_artifact(binary: bytes, expected: dict) -> str:
    """Verify pinned artifacts even when Python runs with optimizations enabled."""
    digest = hashlib.sha384(binary).hexdigest()
    if len(binary) != expected["size"] or digest != expected["sha384"]:
        raise RuntimeError("Native artifact hash/size mismatch")
    return digest


def import_artifact(root: Path, platform: str, expected: dict) -> dict:
    source = "meshagent_osx-x86-64" if platform == "osx-x64" else "meshagent_osx-arm-64"
    url = (
        "https://github.com/Ylianst/MeshAgent/releases/download/legacy-1.2.6/" + source
    )
    with urllib.request.urlopen(url, timeout=90) as response:
        binary = response.read()
    digest = verify_artifact(binary, expected[source])
    destination = root / "components/agent-binaries" / platform / "MeshAllAgent"
    destination.parent.mkdir(parents=True, exist_ok=True)
    if (
        destination.exists()
        and hashlib.sha384(destination.read_bytes()).hexdigest() != digest
    ):
        raise RuntimeError(
            "Preserving a different existing artifact: " + str(destination)
        )
    destination.write_bytes(binary)
    destination.chmod(0o755)
    print("Verified baseline native artifact:", platform, len(binary), "bytes")
    return {
        "platform": platform,
        "file": "MeshAllAgent",
        "size": len(binary),
        "sha384": digest,
        "origin": "Ylianst/MeshAgent legacy-1.2.6",
        "sourceFile": source,
        "license": "Apache-2.0",
        "downloadUrl": url,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--platform",
        nargs="+",
        choices=("osx-x64", "osx-arm64"),
        default=["osx-x64", "osx-arm64"],
    )
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    manifest = root / "components/agent-native/.github/release-migration.json"
    expected = {
        artifact["filename"]: artifact
        for artifact in json.loads(manifest.read_text(encoding="utf-8"))["legacy"][
            "files"
        ]
    }
    provenance_path = root / "components/agent-binaries/provenance.json"
    provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
    for platform in args.platform:
        artifact = import_artifact(root, platform, expected)
        provenance = [entry for entry in provenance if entry["platform"] != platform]
        provenance.append(artifact)
    provenance_path.write_text(
        json.dumps(provenance, indent=2) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
