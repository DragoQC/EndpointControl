"""Distribution artwork and pinned native branding must match the application identity."""

import base64
import hashlib
import json
from pathlib import Path
import re
import struct
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / "src/MeshAll.Server/wwwroot"


class BrandingTests(unittest.TestCase):
    def test_canonical_monitor_logo_and_native_source_icons_are_consistent(self):
        svg = ET.parse(WEB / "favicon.svg").getroot()
        self.assertEqual("0 0 48 48", svg.attrib["viewBox"])
        self.assertGreaterEqual(len(svg.findall("{http://www.w3.org/2000/svg}rect")), 2)
        icon = (WEB / "favicon.ico").read_bytes()
        self.assertEqual((0, 1), struct.unpack("<HH", icon[:4]))
        for path in [
            "components/branding/EndpointControl.ico",
            "components/agent-native/meshservice/MeshService.ico",
            "components/agent-native/meshconsole/MeshService.ico",
        ]:
            self.assertEqual(icon, (ROOT / path).read_bytes())

    def test_approval_png_is_branded_and_bounded_for_the_private_pipe(self):
        module = (
            ROOT / "components/agent-core/modules_meshcore/meshall-consent-logo.js"
        ).read_text()
        self.assertIn("EndpointControl", module)
        png = base64.b64decode(re.search(r"module\.exports\s*=\s*'([^']+)'", module)[1])
        self.assertEqual((48, 48), struct.unpack(">II", png[16:24]))
        self.assertLess(len(png), 10_000)

    def test_windows_bundles_have_reviewed_provenance_and_branded_visual_resources(
        self,
    ):
        manifest = json.loads(
            (ROOT / "components/agent-binaries/provenance.json").read_text()
        )
        bitmap = (ROOT / "components/agent-native/meshservice/mc.bmp").read_bytes()[14:]
        for item in manifest:
            if item["platform"] not in {"win-x64", "win-arm64"}:
                continue
            payload = (
                ROOT / "components/agent-binaries" / item["platform"] / item["file"]
            ).read_bytes()
            self.assertEqual(item["sha384"], hashlib.sha384(payload).hexdigest())
            self.assertEqual("EndpointControl 0.10.0", item["resourceBranding"])
            self.assertIn("EndpointControl Agent".encode("utf-16le"), payload)
            self.assertIn(bitmap, payload)


if __name__ == "__main__":
    unittest.main()
