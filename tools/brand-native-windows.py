"""Apply EndpointControl artwork/metadata to pinned Windows baselines, preserving code sections."""

import hashlib
import io
import json
from pathlib import Path
import struct

import lief
from PIL import Image


def align(data):
    return data + b"\0" * (-len(data) % 4)


def block(key, value=b"", children=(), text=False):
    result = align(b"\0" * 6 + key.encode("utf-16le") + b"\0\0")
    result = align(result + value) + b"".join(align(child) for child in children)
    return (
        struct.pack(
            "<HHH", len(result), len(value) // 2 if text else len(value), int(text)
        )
        + result[6:]
    )


def version_resource():
    values = {
        "CompanyName": "EndpointControl",
        "FileDescription": "EndpointControl Agent",
        "FileVersion": "0.10.0.0",
        "InternalName": "MeshAllAgent",
        "OriginalFilename": "MeshAllAgent.exe",
        "ProductName": "EndpointControl Agent",
        "ProductVersion": "0.10.0",
        "LegalCopyright": "Copyright Intel Corporation and contributors. Apache-2.0; EndpointControl modifications by DragoQC.",
    }
    strings = [
        block(key, value.encode("utf-16le") + b"\0\0", text=True)
        for key, value in values.items()
    ]
    table = block(
        "StringFileInfo",
        children=[block("040904b0", children=strings, text=True)],
        text=True,
    )
    variables = block(
        "VarFileInfo",
        children=[block("Translation", struct.pack("<HH", 1033, 1200))],
        text=True,
    )
    fixed = struct.pack(
        "<13I", 0xFEEF04BD, 0x10000, 10, 0, 10, 0, 0x3F, 0, 0x40004, 1, 0, 0, 0
    )
    return block("VS_VERSION_INFO", fixed, [table, variables])


def string_end(data, offset):
    first = struct.unpack_from("<H", data, offset)[0]
    if first == 0:
        return offset + 2
    if first == 0xFFFF:
        return offset + 4
    while struct.unpack_from("<H", data, offset)[0]:
        offset += 2
    return offset + 2


def brand_dialog(data):
    if data[:4] != b"\1\0\xff\xff":
        return data
    caption = string_end(data, string_end(data, 26))
    end = string_end(data, caption)
    title = data[caption : end - 2].decode("utf-16le")
    if title not in {"MeshCentral Agent", "MeshAll Agent", "MeshAll Agent Installer"}:
        return data
    font_end = (
        string_end(data, end + 6)
        if struct.unpack_from("<I", data, 12)[0] & 0x40
        else end
    )
    controls = (font_end + 3) & ~3
    header = (
        data[:caption]
        + "EndpointControl Agent".encode("utf-16le")
        + b"\0\0"
        + data[end:font_end]
    )
    return align(header) + data[controls:]


def leaves(node):
    if node.is_data:
        yield node
    else:
        for child in node.childs:
            yield from leaves(child)


def main():
    root = Path(__file__).resolve().parents[1]
    manifest_path = root / "components/agent-binaries/provenance.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    logo = Image.open(root / "src/MeshAll.Server/wwwroot/logo.png")
    bitmap = (root / "components/agent-native/meshservice/mc.bmp").read_bytes()[14:]
    for item in manifest:
        if item["platform"] not in {"win-x64", "win-arm64"}:
            continue
        path = root / "components/agent-binaries" / item["platform"] / item["file"]
        original = path.read_bytes()
        if hashlib.sha384(original).hexdigest() != item["sha384"]:
            raise RuntimeError(
                "The Windows baseline differs from its reviewed provenance: "
                + str(path)
            )
        if item.get("resourceBranding") == "EndpointControl 0.10.0":
            continue
        binary = lief.PE.parse(original)
        code = {
            section.name: bytes(section.content)
            for section in binary.sections
            if section.name != ".rsrc"
        }
        manager = binary.resources_manager
        for old in list(manager.icons):
            stream = io.BytesIO()
            logo.save(
                stream, format="ICO", sizes=[(old.width or 256, old.height or 256)]
            )
            icon = lief.PE.ResourceIcon.from_serialization(stream.getvalue())
            icon.id, icon.lang, icon.sublang, icon.planes = (
                old.id,
                old.lang,
                old.sublang,
                1,
            )
            manager.change_icon(old, icon)
        for kind in binary.resources.childs:
            if kind.id == 2:
                for data in leaves(kind):
                    data.content = bitmap
            elif kind.id == 5:
                for data in leaves(kind):
                    data.content = brand_dialog(bytes(data.content))
            elif kind.id == 16:
                for data in leaves(kind):
                    data.content = version_resource()
        config = lief.PE.Builder.config_t()
        config.resources = True
        config.overlay = True
        builder = lief.PE.Builder(binary, config)
        builder.build()
        staging = path.with_suffix(".branded.exe")
        builder.write(str(staging))
        branded = lief.PE.parse(staging)
        for name, content in code.items():
            if bytes(branded.get_section(name).content) != content:
                raise RuntimeError(
                    "Branding modified a native code/data section: " + name
                )
        resources = branded.resources_manager
        if not any(
            "EndpointControl Agent" in str(version) for version in resources.version
        ):
            raise RuntimeError("Branded native version resources failed validation")
        updated = staging.read_bytes()
        staging.replace(path)
        item["baselineSha384"] = item["sha384"]
        item["sha384"] = hashlib.sha384(updated).hexdigest()
        item["size"] = len(updated)
        item["resourceBranding"] = "EndpointControl 0.10.0"
        item["resourceBrandingTool"] = "LIEF 0.17.6"
        manifest_path.write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
        )
        print(
            "Branded native Windows resources without changing code sections:",
            item["platform"],
        )
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
