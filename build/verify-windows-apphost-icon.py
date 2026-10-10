#!/usr/bin/env python3
"""Assert a published Windows PE apphost embeds actual RT_ICON/RT_GROUP_ICON resources.

Pure stdlib: verifies the PE resources rather than just checking a csproj property,
an adjacent .ico file, or the existence of the native apphost.
"""
from pathlib import Path
from struct import unpack_from
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def resource_ids(path: Path):
    data = path.read_bytes()

    def u16(offset):
        return unpack_from("<H", data, offset)[0]

    def u32(offset):
        return unpack_from("<I", data, offset)[0]

    require(data[:2] == b"MZ", "Not a Windows PE executable")
    pe = u32(0x3C)
    require(data[pe:pe + 4] == b"PE\0\0", "Missing PE signature")
    number_of_sections = u16(pe + 6)
    optional_size = u16(pe + 20)
    optional = pe + 24
    magic = u16(optional)
    require(magic in (0x10B, 0x20B), "Unsupported PE optional header")
    directory = optional + (96 if magic == 0x10B else 112)
    resource_rva = u32(directory + 2 * 8)
    require(resource_rva != 0, "Executable has no PE resource directory")

    sections = optional + optional_size
    def rva_offset(rva):
        for index in range(number_of_sections):
            entry = sections + index * 40
            virtual_size = u32(entry + 8)
            virtual_address = u32(entry + 12)
            raw_size = u32(entry + 16)
            raw_pointer = u32(entry + 20)
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_pointer + (rva - virtual_address)
        raise ValueError(f"Resource RVA 0x{rva:x} is outside PE sections")

    root = rva_offset(resource_rva)
    count = u16(root + 12) + u16(root + 14)
    require(count > 0, "PE resource directory is empty")
    ids = set()
    for index in range(count):
        entry = root + 16 + index * 8
        name_or_id = u32(entry)
        if not name_or_id & 0x80000000:
            ids.add(name_or_id)
    return ids


def main():
    require(len(sys.argv) == 2, "Expected a single .exe path")
    path = Path(sys.argv[1])
    require(path.is_file(), f"Windows apphost missing: {path}")
    ids = resource_ids(path)
    require({3, 14} <= ids,
            f"Apphost must contain RT_ICON (3) and RT_GROUP_ICON (14); found {sorted(ids)}")
    print(f"Verified native apphost {path.name}: RT_ICON and RT_GROUP_ICON embedded")


if __name__ == "__main__":
    main()
