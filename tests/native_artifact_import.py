"""Hash-pinned native imports must fail before writing mismatched artifacts."""

import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import io

spec = importlib.util.spec_from_file_location(
    "meshall_native_import",
    Path(__file__).resolve().parents[1] / "tools/import-native-agents.py",
)
native_import = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native_import)


class NativeArtifactImportTests(unittest.TestCase):
    def test_correct_artifact_matches_the_pinned_digest(self):
        binary = b"native-artifact-fixture"
        digest = hashlib.sha384(binary).hexdigest()
        self.assertEqual(
            digest,
            native_import.verify_artifact(
                binary, {"size": len(binary), "sha384": digest}
            ),
        )

    def test_mismatched_download_does_not_replace_an_existing_artifact(self):
        original = b"original-artifact"
        expected = {
            "meshagent_osx-x86-64": {
                "size": len(original),
                "sha384": hashlib.sha384(original).hexdigest(),
            }
        }
        for download in [b"wrong-size", b"X" * len(original)]:
            with self.subTest(
                download=download
            ), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                destination = root / "components/agent-binaries/osx-x64/MeshAllAgent"
                destination.parent.mkdir(parents=True)
                destination.write_bytes(original)
                with patch.object(
                    native_import.urllib.request,
                    "urlopen",
                    return_value=io.BytesIO(download),
                ):
                    with self.assertRaisesRegex(RuntimeError, "hash/size mismatch"):
                        native_import.import_artifact(root, "osx-x64", expected)
                self.assertEqual(original, destination.read_bytes())


if __name__ == "__main__":
    unittest.main()
