#!/usr/bin/env python3
"""
메인보드 펌웨어 패키지(.dsppkg, 규격 v1) 시험용 생성기 — 표준 라이브러리만.

실제 패키지는 AGV_Wifi_Bridge tools/dsp_pack.py bundle 이 CCS 의 .out·.map 으로 만든다.
여기서는 .dspfw(예: tests/Gateway.Core.Tests/TestData/app_golden.dspfw 나 fake_esp.build_dspfw 출력)에
합성 .out(TI COFF2 머리 22B 또는 ELF32 C2000 머리)과 합성 .map(CCS 링커 맵 머리)을 붙여
게이트웨이 업로드·e2e 시험에 쓴다. manifest 는 패커와 같이 json.dumps(indent=2, sort_keys=True, ensure_ascii=False) + "\\n".

  python3 tests/dsppkg.py app_1.2.3.dspfw -o app_1.2.3.dsppkg [--stem AGV_Main] [--tz 32400] [--elf] [--link-check skipped]
"""
import argparse
import calendar
import hashlib
import io
import json
import struct
import time
import zipfile
import zlib

DEFAULT_LINK_TIME = 1793422810          # 2026-10-31 05:00:10 UTC
COFF_TARGET_C2000 = 0x009D
EM_TI_C2000 = 141


def dspfw_info(dspfw: bytes):
    """파일 헤더 버전, 앱 헤더 app_len·app_crc32·build_time (dsp_pack.py 와 같은 배치)"""
    if len(dspfw) < 32 + 128 or dspfw[:8] != b"AGVDSP1\0":
        raise ValueError(".dspfw 가 아님")
    ver = dspfw[16:32].split(b"\0", 1)[0].decode("ascii")
    h = struct.unpack_from("<64H", dspfw, 32)
    return ver, h[4] | (h[5] << 16), h[6] | (h[7] << 16), h[8] | (h[9] << 16)


def coff_out(timestamp: int, target: int = COFF_TARGET_C2000, magic: int = 0x00C2, size: int = 278) -> bytes:
    """TI COFF 머리 22B: [0]u16 버전ID [2]u16 섹션 수 [4]u32 시각 [8]u32 심볼표 [12]u32 심볼 수 [16]u16 선택 머리 [18]u16 플래그 [20]u16 대상 ID"""
    head = struct.pack("<HHIIIHHH", magic, 1, timestamp, 0, 0, 0, 0, target)
    return head + bytes((i & 0xFF) for i in range(max(size, 22) - 22))


def elf_out(machine: int = EM_TI_C2000, size: int = 128) -> bytes:
    b = bytearray(max(size, 52))
    b[0:7] = b"\x7fELF\x01\x01\x01"
    struct.pack_into("<HH", b, 16, 2, machine)
    return bytes(b)


def ctime(unix: int) -> str:
    """C ctime 형식 (일자 앞 공백): 'Sat Oct 31 14:00:10 2026'"""
    return time.asctime(time.gmtime(unix))


def map_text(output_path: str, linked_unix) -> bytes:
    lines = ["*" * 78, "            TMS320C2000 Linker PC v18.12.4", "*" * 78]
    if linked_unix is not None:
        lines.append(f">> Linked {ctime(linked_unix)}")
    lines += ["", f"OUTPUT FILE NAME:   <{output_path}>", 'ENTRY POINT SYMBOL: "_c_int00"  address: 003e8040', ""]
    return "\n".join(lines).encode("latin-1")


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def manifest(dspfw, dspfw_name, out_b, out_name, map_b, map_name, link_check, created="2026-10-31T05:00:00Z"):
    ver, app_len, app_crc, build = dspfw_info(dspfw)
    elf = out_b[:4] == b"\x7fELF"
    out = {"name": out_name, "orig_name": out_name, "size": len(out_b), "sha256": sha(out_b),
           "format": "elf" if elf else ("coff1" if out_b[:2] == b"\xc1\x00" else "coff2"),
           "target": struct.unpack_from("<H", out_b, 18 if elf else 20)[0],
           "timestamp": 0 if elf else struct.unpack_from("<I", out_b, 4)[0]}
    text = map_b.decode("latin-1")
    output, linked = "", ""
    for line in text.splitlines():
        if line.startswith("OUTPUT FILE NAME:") and "<" in line:
            output = line.split("<", 1)[1].split(">", 1)[0].replace("\\", "/").rsplit("/", 1)[-1]
        if line.startswith(">> Linked "):
            linked = " ".join(line[len(">> Linked "):].split())
    return {
        "format": "agv-dsppkg", "format_ver": 1, "ver": ver, "created_utc": created,
        "tool": "tests/dsppkg.py", "hex2000": "", "link_check": link_check,
        "files": {
            "dspfw": {"name": dspfw_name, "size": len(dspfw), "sha256": sha(dspfw)},
            "out": out,
            "map": {"name": map_name, "orig_name": map_name, "size": len(map_b), "sha256": sha(map_b),
                    "output": output, "linked": linked},
        },
        "dspfw": {"app_len": app_len, "app_crc32": f"{app_crc:08x}", "build_time": build},
    }


def manifest_bytes(m) -> bytes:
    return (json.dumps(m, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")


def zip_entries(entries) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            z.writestr(name, data)
    return buf.getvalue()


def build(dspfw: bytes, stem="AGV_Main", link_time=DEFAULT_LINK_TIME, tz=9 * 3600, elf=False, link_check=None) -> bytes:
    """올바른 패키지: COFF2 .out(링크 시각 link_time) + 링크 PC 시간대 tz 초만큼 앞선 Linked 시각의 .map. elf=True 면 이름으로 연결 확인"""
    ver = dspfw_info(dspfw)[0]
    out_b = elf_out() if elf else coff_out(link_time)
    map_b = map_text(f"./Debug/{stem}.out", None if elf else link_time + tz)
    names = (f"{stem}_{ver}.dspfw", f"{stem}.out", f"{stem}.map")
    m = manifest(dspfw, names[0], out_b, names[1], map_b, names[2], link_check or ("name" if elf else "time"))
    return zip_entries([("manifest.json", manifest_bytes(m)), (names[0], dspfw), (names[1], out_b), (names[2], map_b)])


def pkg_id(pkg: bytes) -> str:
    with zipfile.ZipFile(io.BytesIO(pkg)) as z:
        return sha(z.read("manifest.json"))[:16]


def _selftest():
    # 링크 시각 규칙(규격 4절)과 같은 계산이 되는지 — ctime 왕복
    lt = calendar.timegm(time.strptime(ctime(DEFAULT_LINK_TIME + 9 * 3600), "%a %b %d %H:%M:%S %Y"))
    assert lt - DEFAULT_LINK_TIME == 9 * 3600
    assert zlib.crc32(b"123456789") == 0xCBF43926


if __name__ == "__main__":
    _selftest()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dspfw")
    ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--stem", default="AGV_Main")
    ap.add_argument("--tz", type=int, default=9 * 3600, help="링크 PC 시간대 (초, 기본 +9h)")
    ap.add_argument("--elf", action="store_true", help="ELF .out (링크 시각 없음 → 이름으로 확인)")
    ap.add_argument("--link-check", choices=("time", "name", "skipped"))
    a = ap.parse_args()
    with open(a.dspfw, "rb") as f:
        p = build(f.read(), a.stem, tz=a.tz, elf=a.elf, link_check=a.link_check)
    with open(a.out, "wb") as f:
        f.write(p)
    print(f"{a.out}: {len(p):,} B, 패키지 ID {pkg_id(p)}")
