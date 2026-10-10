#!/usr/bin/env python3
"""Stage a complete ARM64 libsndfile shared dependency closure for Heresy.

The linux-arm64 org.sndfile.runtime NuGet (1.2.2.1) contains static .a
archives only. On an actual Ubuntu ARM64 release runner, use installed
distro-built shared objects instead. Refuse to package x64 ELF objects,
unresolved dependencies or missing libsndfile. Do not bundle glibc.
"""
from __future__ import annotations

from collections import deque
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys

# Basic ELF/glibc loader and C runtime support comes from the target OS.
# Every other transitive dependency must be staged locally.
HOST_LIBRARIES = {
    "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0",
    "libresolv.so.2", "librt.so.1", "libutil.so.1", "libgcc_s.so.1",
}
NEEDED_LINE = re.compile(r"^\s*(\S+)\s+=>\s+(/\S+)\s+\(")


def command(*args: str, env: dict[str, str] | None = None) -> str:
    import os
    result = subprocess.run(
        args, check=True, text=True, capture_output=True,
        env={**os.environ, **(env or {})},
    )
    return result.stdout


def require_arm64(path: Path) -> None:
    header = command("readelf", "-h", str(path))
    if not re.search(r"Machine:\s*AArch64\b", header):
        raise RuntimeError(f"Not an ARM64 shared library: {path}")


def dependencies(path: Path, environment: dict[str, str] | None = None) -> list[tuple[str, Path]]:
    output = command("ldd", str(path), env=environment)
    if "not found" in output:
        raise RuntimeError(f"Unresolved dependencies for {path}:\n{output}")
    return [
        (name, Path(location))
        for line in output.splitlines()
        if (match := NEEDED_LINE.match(line))
        for name, location in [match.groups()]
    ]


def copy_license(path: Path, destination: Path) -> None:
    try:
        owner = command("dpkg-query", "-S", str(path.resolve())).split(": ", 1)[0]
    except subprocess.CalledProcessError:
        print(f"WARNING: could not identify copyright owner for {path}")
        return
    package = owner.split(":", 1)[0]
    license_file = Path("/usr/share/doc") / package / "copyright"
    if license_file.is_file():
        licenses = destination / "THIRD-PARTY-LICENSES"
        licenses.mkdir(exist_ok=True)
        shutil.copy2(license_file, licenses / f"{package}.copyright")
    else:
        print(f"WARNING: missing copyright file for {package}")


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: stage-linux-arm64-sndfile.py PUBLISH_DIRECTORY")
    if platform.system() != "Linux" or platform.machine() != "aarch64":
        raise RuntimeError("Linux ARM64 native runner required")
    destination = Path(sys.argv[1]).resolve()
    if not (destination / "Heresy").is_file():
        raise RuntimeError("Publish output is missing the Heresy ARM64 apphost")
    require_arm64(destination / "Heresy")

    root = Path("/usr/lib/aarch64-linux-gnu/libsndfile.so.1")
    if not root.exists():
        root = Path("/lib/aarch64-linux-gnu/libsndfile.so.1")
    if not root.exists():
        raise RuntimeError("Install Ubuntu's libsndfile1 ARM64 package first")

    # Copy only the shared objects actually linked into libsndfile's
    # dependency closure. Use DT_NEEDED names, not distro symlink versions.
    pending: deque[tuple[str, Path]] = deque([("libsndfile.so.1", root)])
    copied: dict[str, Path] = {}
    while pending:
        soname, source = pending.popleft()
        if soname in HOST_LIBRARIES or soname.startswith("ld-linux-"):
            continue
        if soname in copied:
            continue
        if not source.is_file():
            raise RuntimeError(f"Missing shared library {soname}: {source}")
        require_arm64(source)
        target = destination / soname
        shutil.copy2(source.resolve(), target)
        # The native loader must find bundled transitive libraries next
        # to libsndfile, even if the user's LD_LIBRARY_PATH is empty.
        command("patchelf", "--set-rpath", "$ORIGIN", str(target))
        copied[soname] = target
        copy_license(source, destination)
        for dep_name, dep_path in dependencies(source):
            if dep_name not in copied and dep_name not in HOST_LIBRARIES:
                pending.append((dep_name, dep_path))

    # NAudio and .NET P/Invoke may request the unversioned libsndfile name.
    alias = destination / "libsndfile.so"
    if alias.is_symlink() or alias.exists():
        alias.unlink()
    alias.symlink_to("libsndfile.so.1")
    for name, target in copied.items():
        resolved = dependencies(target, {"LD_LIBRARY_PATH": str(destination)})
        for needed, actual in resolved:
            if needed in copied and actual.resolve() != copied[needed].resolve():
                raise RuntimeError(
                    f"Dependency {needed} of {name} did not resolve to "
                    f"packaged file: {actual}"
                )
        if command("patchelf", "--print-rpath", str(target)).strip() != "$ORIGIN":
            raise RuntimeError(f"Missing local RPATH: {target}")

    print("Verified bundled ARM64 shared libsndfile and dependencies:")
    for name in sorted(copied):
        print(f"  {name}")


if __name__ == "__main__":
    main()
