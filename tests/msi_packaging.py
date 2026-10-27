"""Compile and inspect the real native-agent MSI using wixl and msitools (Linux)."""

import argparse
import csv
import io
import json
import os
from pathlib import Path
import sqlite3
import struct
import subprocess
import tempfile
import unittest


class MsiPackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="meshall-msi-tests-")
        cls.root = Path(cls.temporary.name)
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.binary = Path(options.server_dll).resolve()
        cls.compiler = Path(options.compiler).resolve()
        cls.info = Path(options.msiinfo).resolve()
        cls.extract = Path(options.msiextract).resolve()
        cls.config = cls.root / "server.json"
        cls.config.write_text(
            json.dumps(
                {
                    "MeshAll": {
                        "DataDirectory": str(cls.root / "data"),
                        "PublicUrl": "https://msi-fixture.example.invalid:5188",
                        "MsiCompilerPath": str(cls.compiler),
                    }
                }
            ),
            encoding="utf-8",
        )
        cls.environment = dict(os.environ, MESHALL_CONNECTION_FILE=str(cls.config))
        result = cls.server("--create-enrollment", "MSI fixture")
        cls.token = next(
            json.loads(line)["token"]
            for line in result.splitlines()
            if line.startswith("{")
        )
        with sqlite3.connect(cls.root / "data/meshall.db") as database:
            database.execute(
                "INSERT INTO sessions VALUES('active-fixture','device','Device','Actor','Desktop','2026-10-26T00:00:00Z',NULL,'Active')"
            )
        cls.package = cls.root / "agent.msi"
        cls.server("--agent-msi", cls.token, "--output", str(cls.package))
        cls.payload = cls.root / "payload"
        subprocess.run(
            [str(cls.extract), "--directory", str(cls.payload), str(cls.package)],
            env=cls.environment,
            check=True,
            capture_output=True,
            text=True,
        )

    @classmethod
    def server(cls, *arguments):
        result = subprocess.run(
            ["dotnet", str(cls.binary), *arguments],
            cwd=cls.binary.parent,
            env=cls.environment,
            capture_output=True,
            text=True,
            timeout=90,
        )
        if result.returncode:
            raise RuntimeError(
                "MSI fixture command failed: " + result.stdout + result.stderr
            )
        return result.stdout

    def table(self, name, package=None):
        result = subprocess.check_output(
            [str(self.info), "export", str(package or self.package), name],
            env=self.environment,
            text=True,
        )
        rows = list(csv.reader(io.StringIO(result), delimiter="\t"))
        return [dict(zip(rows[0], row)) for row in rows[3:]]

    def test_real_package_is_self_contained_machine_service_with_ordered_upgrade(self):
        self.assertEqual(
            bytes.fromhex("d0cf11e0a1b11ae1"), self.package.read_bytes()[:8]
        )
        properties = {row["Property"]: row["Value"] for row in self.table("Property")}
        self.assertEqual("1", properties["ALLUSERS"])
        self.assertEqual("EndpointControl Agent", properties["ProductName"])
        self.assertNotIn(self.token, json.dumps(properties))
        services = self.table("ServiceInstall")
        self.assertEqual(1, len(services))
        self.assertEqual("MeshAllAgent", services[0]["Name"])
        self.assertEqual("16", services[0]["ServiceType"])
        self.assertEqual("2", services[0]["StartType"])
        self.assertEqual(32769, int(services[0]["ErrorControl"]))
        self.assertEqual("", services[0]["StartName"])
        self.assertEqual("163", self.table("ServiceControl")[0]["Event"])
        sequence = {
            row["Action"]: int(row["Sequence"])
            for row in self.table("InstallExecuteSequence")
        }
        self.assertLess(sequence["AppSearch"], sequence["LaunchConditions"])
        self.assertLess(
            sequence["InstallInitialize"], sequence["RemoveExistingProducts"]
        )
        self.assertLess(sequence["RemoveExistingProducts"], sequence["InstallFiles"])
        self.assertLess(sequence["InstallServices"], sequence["StartServices"])
        self.assertTrue(all(int(row["Type"]) & 16 for row in self.table("RegLocator")))
        conditions = " ".join(row["Condition"] for row in self.table("LaunchCondition"))
        self.assertIn("EXISTINGINSTANCE = SERVERINSTANCE", conditions)
        self.assertIn("NOT EXISTINGAGENTSERVICE", conditions)
        self.assertIn("WIX_SAME_VERSION_UPGRADE_DETECTED", conditions)
        upgrades = self.table("Upgrade")
        self.assertTrue(
            any(
                row["VersionMax"] == properties["ProductVersion"]
                and int(row["Attributes"]) & 512
                and not int(row["Attributes"]) & 2
                for row in upgrades
            )
        )
        self.assertTrue(all(row["FileName"] == "" for row in self.table("RemoveFile")))
        summary = subprocess.check_output(
            [str(self.info), "suminfo", str(self.package)],
            env=self.environment,
            text=True,
        )
        self.assertIn("x64;1033", summary)

    def test_embedded_native_payload_and_notices_are_intact_without_cloned_identity(
        self,
    ):
        executable = next(self.payload.rglob("MeshAllAgent.exe"))
        files = [file.name for file in self.payload.rglob("*") if file.is_file()]
        self.assertEqual(
            ["EndpointControl-Agent-Notices.txt", "MeshAllAgent.exe"], sorted(files)
        )
        base = (
            self.binary.parent / "NativeBinaries/win-x64/MeshAllAgent.exe"
        ).read_bytes()
        content = executable.read_bytes()
        self.assertEqual(base, content[: len(base)])
        self.assertEqual(
            bytes.fromhex("b996015880544a19b7f7e9be44914c19"), content[-16:]
        )
        size = struct.unpack(">i", content[-20:-16])[0]
        policy = content[-20 - size : -20].decode("utf-8")
        self.assertIn(
            "MeshServer=wss://msi-fixture.example.invalid:5188/agent.ashx", policy
        )
        self.assertIn("meshServiceName=MeshAllAgent", policy)
        self.assertIn(b"MESHALL-EMBEDDED-LICENSES-BEGIN", content)
        self.assertNotIn(self.token, policy)
        with sqlite3.connect(self.root / "data/meshall.db") as database:
            self.assertEqual(
                1, database.execute("SELECT remaining FROM tokens").fetchone()[0]
            )
            self.assertEqual(
                0, database.execute("SELECT COUNT(*) FROM devices").fetchone()[0]
            )
            self.assertEqual(
                (None, "Active"),
                database.execute(
                    "SELECT ended,status FROM sessions WHERE id='active-fixture'"
                ).fetchone(),
            )

    def test_rebuilding_same_payload_keeps_product_identity_and_existing_outputs_are_not_overwritten(
        self,
    ):
        other = self.root / "repeat.msi"
        self.server("--agent-msi", self.token, "--output", str(other))
        original = {row["Property"]: row["Value"] for row in self.table("Property")}
        repeat = {
            row["Property"]: row["Value"] for row in self.table("Property", other)
        }
        self.assertEqual(original["ProductCode"], repeat["ProductCode"])
        self.assertEqual(original["UpgradeCode"], repeat["UpgradeCode"])
        existing = self.root / "preserved.msi"
        existing.write_bytes(b"existing-assigned-package")
        with self.assertRaises(RuntimeError):
            self.server("--agent-msi", self.token, "--output", str(existing))
        self.assertEqual(b"existing-assigned-package", existing.read_bytes())

    def test_offline_exe_packaging_does_not_open_controller_state(self):
        private_data = self.root / "offline-controller-data"
        config = self.root / "offline.json"
        config.write_text(
            json.dumps(
                {
                    "MeshAll": {
                        "DataDirectory": str(private_data),
                        "MsiCompilerPath": str(self.compiler),
                    }
                }
            ),
            encoding="utf-8",
        )
        executable = next(self.payload.rglob("MeshAllAgent.exe"))
        package = self.root / "offline.msi"
        result = subprocess.run(
            [
                "dotnet",
                str(self.binary),
                "--agent-msi-from",
                str(executable),
                "--output",
                str(package),
            ],
            cwd=self.binary.parent,
            env=dict(self.environment, MESHALL_CONNECTION_FILE=str(config)),
            capture_output=True,
            text=True,
            timeout=90,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue(package.exists())
        self.assertFalse(private_data.exists())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--server-dll",
        default="src/MeshAll.Server/bin/Release/net10.0/MeshAll.Server.dll",
    )
    parser.add_argument("--compiler", default="/usr/bin/wixl")
    parser.add_argument("--msiinfo", default="/usr/bin/msiinfo")
    parser.add_argument("--msiextract", default="/usr/bin/msiextract")
    options, remaining = parser.parse_known_args()
    unittest.main(argv=[__file__, *remaining])
