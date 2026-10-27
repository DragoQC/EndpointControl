"""Source archives must include editable sources and exclude local private state."""

import importlib.util
import tarfile
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "meshall_packaging", Path(__file__).resolve().parents[1] / "tools/package-source.py"
)
packaging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packaging)


class SourcePackagingTests(unittest.TestCase):
    def test_only_source_layout_and_public_configuration_are_packaged(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            public = [
                "README.md",
                "THIRD_PARTY_NOTICES.md",
                "src/Server/Program.cs",
                "src/Server/appsettings.json",
                "components/agent-native/core.c",
                "tests/test_case.py",
            ]
            private = [
                "credentials.txt",
                ".env.production",
                "src/Server/credentials.json",
                "src/Server/appsettings.Production.json",
                "src/Server/Data/meshall.db",
                "src/Server/keys/private.xml",
                "src/Server/server.pfx",
                "components/agent-native/agent.msh",
                "components/agent-native/agent-state.json",
                "src/Server/Agent.msi",
                "src/Server/Agent.mst",
                "src/Server/agent.cab",
            ]
            for name in public + private:
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(
                    "PRIVATE-TEST-MARKER" if name in private else "public source"
                )
            output = root / "archive.tar.gz"
            packaging.package(root, output)
            with tarfile.open(output) as archive:
                names = set(archive.getnames())
                for name in public:
                    self.assertIn("EndpointControl/" + name, names)
                for name in private:
                    self.assertNotIn("EndpointControl/" + name, names)
                for member in archive.getmembers():
                    if member.isfile():
                        self.assertNotIn(
                            b"PRIVATE-TEST-MARKER", archive.extractfile(member).read()
                        )


if __name__ == "__main__":
    unittest.main()
