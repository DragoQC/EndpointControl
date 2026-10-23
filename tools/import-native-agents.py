"""Import hash-pinned baseline native artifacts; instance policies are added at download time."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--platform", nargs="+", choices=("osx-x64", "osx-arm64"), default=["osx-x64", "osx-arm64"])
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
expected = {f["filename"]: f for f in json.loads((root / "components/agent-native/.github/release-migration.json").read_text())["legacy"]["files"]}
provenance_path = root / "components/agent-binaries/provenance.json"
provenance = json.loads(provenance_path.read_text())
for platform in args.platform:
    source = "meshagent_osx-x86-64" if platform == "osx-x64" else "meshagent_osx-arm-64"
    url = "https://github.com/Ylianst/MeshAgent/releases/download/legacy-1.2.6/" + source
    with urllib.request.urlopen(url, timeout=90) as response:
        binary = response.read()
    digest = hashlib.sha384(binary).hexdigest()
    assert len(binary) == expected[source]["size"] and digest == expected[source]["sha384"], "Native artifact hash/size mismatch"
    destination = root / "components/agent-binaries" / platform / "MeshAllAgent"
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists() and hashlib.sha384(destination.read_bytes()).hexdigest() != digest:
        raise RuntimeError("Preserving a different existing artifact: " + str(destination))
    destination.write_bytes(binary)
    destination.chmod(0o755)
    provenance = [p for p in provenance if p["platform"] != platform]
    provenance.append({"platform": platform, "file": "MeshAllAgent", "size": len(binary), "sha384": digest,
                       "origin": "Ylianst/MeshAgent legacy-1.2.6", "sourceFile": source, "license": "Apache-2.0", "downloadUrl": url})
    print("Verified baseline native artifact:", platform, len(binary), "bytes")
provenance_path.write_text(json.dumps(provenance, indent=2) + "\n")
