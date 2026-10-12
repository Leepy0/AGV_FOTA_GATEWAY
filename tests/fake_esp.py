#!/usr/bin/env python3
"""가짜 브리지 (AGV_Wifi_Bridge 0.5.2 흉내) — 하드웨어 없이 게이트웨이 서버를 시험한다. 표준 라이브러리만 쓴다.

  python3 tests/fake_esp.py --agv 7 --server 127.0.0.1:50000 [--listen 50010] [--dsp 1.0.0] [--fail-at erase]
  python3 tests/fake_esp.py --agv 7,12,23 --server 127.0.0.1:50000 --listen 50010     # 여러 호기 (포트 50010, 50011, 50012)
  python3 tests/fake_esp.py --agv 7 --dup-id ...                                       # 같은 번호의 두 번째 장치 (충돌 시험)

실제 브리지 코드(main/net_bridge.c·dsp_ota.c·ota.c·app_config.c·cmd.c, 0.5.2)와 맞춘 동작
- 패킷: [A5 5A 01][type][agv][flags 0][seq u16][len u16] + payload(≤512). 타입별 seq 는 0 부터, 오프라인·유실이어도 증가.
  PONG·CFG_RESULT 는 요청 seq 를 반송하고 요청 송신원(주소·포트)으로 보낸다. 그 밖의 보고는 --server 로.
- 수신: 설정된 서버 IP(--server 의 호스트) 외 송신원은 버림. 서버 패킷을 받으면(종류·호기 무관) 미확정 OTA(ota=pending)·
  원격 설정(cfg=trial)을 확정. MAIN_DOWN·OTA_START·CFG_CMD·DSP_OTA_START/APPLY 는 호기 번호가 다르면 버림, PING·INFO_REQ 는 받음.
- heartbeat(STATUS) hb_ms 마다, 필드 순서도 같게: fw, mac, part, ota(valid/pending/busy), up, rssi, ch, bssid, disc, reason,
  m_rx, m_tx, r_rx, u_err, n_off, n_err, heap, cfg(saved/trial/default), setup, dsp, dspb, dsp_build,
  dsp_st(idle/downloading/staged/programming/nopart), staged{job,ver}|null.  INFO_REQ → INFO{agv,mac,ip,fw,part,ota,cfg,setup,up,dsp…}.
- DSP_OTA_START {url,ver,crc32,size,job,apply,recover}: url 199자 초과 url_too_long, http 아님 bad_request, 호스트 ≠ 서버 IP url_host
  (여기까지는 수신 문맥이라 3회 간격 없이). 큐(2칸)가 차면 busy. 처리 태스크: 같은 job 이 스테이징돼 있으면 staged 재보고,
  최근 10분 안의 같은 job 결과가 있으면 그 결과 재보고(다시 받지 않음), 굽는 중이면 busy, 브리지 OTA 중이면 esp_ota_busy.
  다운로드: 실제 HTTP(리다이렉트 3회까지, 리다이렉트 호스트도 검사) → 길이(http_length)·머리 검사(파일 헤더 magic·길이 image_crc,
  앱 헤더 magic·hdr_crc16·app_len image_crc, 앱 헤더 버전 = 파일 헤더 버전·1~8자 [0-9A-Za-z._-] 아니면 ver_invalid) →
  기존 스테이징 지움(받는 동안 staged=null) → 4KB 조각마다 download(pct, 20% 단위) → 전체 검사(파일 CRC image_crc,
  블록 크기·주소·순서와 복원 CRC = app_crc 아니면 image_bad) → crc32 인자와 다르면 crc_mismatch → staged(100, ver) 3회(300ms 간격).
  apply:1 이면 이어서 적용. 실패하면 이전 스테이징이 남아 있으면 복원.
- DSP_OTA_APPLY {job,recover}: 같은 job 을 굽는 중이면 무시. 최근 같은 job 적용 결과가 있으면 재보고. 스테이징이 있는데 job 이 다르면
  fail job_mismatch(ver=스테이징 버전). 스테이징이 없으면 start(job 0, ver "") → fail not_staged(job 0, ver "").
  start(0, ver) → [esp OTA 중이면 esp_ota_busy, 스테이징 재검사 실패면 stage_invalid/image_bad] → enter → 앱에 U:
  OK 면 부트로더로, NG 면 app_ng_NN. 무응답이면: 직전 3초 안에 앱 프레임이 있었으면(앱은 살아 있으나 업데이트 진입 미지원 구 앱)
  recover 와 상관없이 원시 모드 없이 app_no_fwup. 앱이 침묵(멈춤·지워짐)이면 recover:1 또는 앱 버전 '?' 일 때 원시 모드로
  부트로더 직행, 아니면 앱 버전·빌드를 '?' 로 바꾸고 app_no_reply (60초마다 재조회로 앱이 살아 있으면 버전이 돌아옴) →
  HELLO(3회×1.5초) 실패 시 recover 면 wait_power(10초마다 보고, 최대 60초, --power-cycle-sec 뒤 전원 재투입 흉내) → 아니면 no_bootloader →
  erase(이때부터 dsp='?') → progress(10% 단위) → verify → 앱 기동 확인(1초 간격 최대 8회, 앱 버전 조회) → 버전 비교(ver_mismatch) →
  done(100, ver=앱이 보고한 버전 앞 8자) 3회. 지운 뒤 실패하면 retry(n) 보고 후 HELLO 부터 최대 2회 더. 적용 중 dsp_st=programming,
  끝나면 dsp=새 버전·dsp_st=idle·staged=null. 실패하면 dsp_st=staged(스테이징 유지).
- 부팅(시작·재부팅) 3초 뒤 앱 버전 조회, 그 전과 모를 때는 dsp/dspb/dsp_build='?'. 모르면 60초마다 다시 조회.
- OTA_START(URL): 8자 미만·256자 이상·http 아님 bad_url, 호스트 ≠ 서버 IP url_host, 진행 중 busy, 메인보드 굽는 중
  mainboard_programming·받는 중 mainboard_staging (모두 3회). start{url} → 이미지 받기(0xE9·app_desc magic 아니면
  begin ESP_ERR_OTA_VALIDATE_FAILED, 프로젝트 ≠ agv_wifi_bridge 면 project mismatch) → progress{pct,bytes}(10% 단위) →
  done{ver,bytes} 3회(300ms 간격, 비동기) → 0.8초 뒤 재부팅: 다른 슬롯(part)·fw=이미지 app_desc 버전·ota=pending·up 0부터·seq 0부터.
  180초(--confirm-sec) 안에 서버 패킷을 못 받으면(또는 확정 전에 다시 재부팅하면) 이전 슬롯·버전으로 롤백(ota=valid).
- CFG_CMD: cfg / set <key> <value> / save / reboot / stat / setup / dsp [ver|apply] / help (응답 문구도 같게).
  서버에서 온 save = 확인 대기 저장. reboot(메인보드 굽는 중이면 거부) → 1초 뒤 재부팅 → cfg=trial →
  180초 안에 서버 패킷을 받으면 saved, 못 받으면 이전 설정으로 되돌리고 다시 재부팅. 바꾼 hb_ms 는 재부팅 뒤 적용.

남은 차이 (가짜라서 다른 점)
- 메인보드·부트로더는 상태 기계로만 흉내낸다: UART 프레임(F/R·부트로더 패킷)·PAN ID 학습·MAIN_UP 은 없다(MAIN_DOWN 은 로그만).
  U 무응답 때 '직전 3초 안에 앱 프레임을 받았는지'는 실제 수신 대신 --app-frames(와 메인보드 상태: 부트로더 대기 중이면 없음)로 정한다.
  boot_proto(부트로더 프로토콜 버전 불일치)·no_partition 외의 파티션 읽기 오류(stage_read)는 만들지 않는다.
- 굽기·지우기·다운로드에 걸리는 시간은 실제보다 짧다(--speed 로 늘림). 프로토콜 대기 시간(앱 응답 2초, HELLO 1.5초×3,
  wait_power 10초 간격·60초, 앱 기동 확인 1초×8, 최종 보고 300ms 간격)은 실제 값.
- 브리지 OTA 는 실제 플래시 쓰기·서명 검증 없이 앱 이미지 머리(0xE9, app_desc magic, project_name, version)만 본다.
  HTTP 오류는 모두 'begin ESP_FAIL' 로 보고한다.
- RSSI·카운터·heap 은 고정값/대략값, 설정 AP·BLE(setup)·RFID 는 없다. --legacy 는 0.4.x 를 대략 흉내(mac·dsp 필드 없음,
  0x32/0x34 무시, 최종 보고 1회).
- 같은 job 의 중복 처리는 실제와 같지만 큐 경쟁(처리 끝 직전 도착)의 타이밍은 파이썬 스레드 기준이다.

장애 주입 (시험용)
  --drop staged,done,ota_done[:N]  해당 보고를 버림(N 개만, 없으면 전부). 이름: DSP 단계명(download/staged/start/enter/erase/
                                    progress/verify/done/fail/retry/wait_power), ota_<단계>(ota_start/ota_progress/ota_done/ota_fail/ota_busy),
                                    hb, info, pong, cfg
  --drop-rx START:1,APPLY:1        받은 명령을 버림(하향 유실). 이름: START APPLY OTA PING INFO CFG MAIN
  --loss P / --rx-loss P           상향/하향 패킷을 확률 P 로 버림
  --mute-after-cmd SEC             job 마다 처음 받은 DSP START·APPLY 뒤 SEC 초 동안 상향 전부 유실(서버 재전송 유도 → 중복 처리 시험)
  --fail-at STAGE[:N]              download(flash_write) enter(uart_busy) hello(no_bootloader) erase write(write_timeout) header(write_header)
                                    verify app(굽기 뒤 앱 무응답 → app_no_reply) ver(ver_mismatch) ota(브리지 OTA finish 실패). N 회만(기본 계속)
  --app-u ok|noreply|ng:NN         메인보드 앱의 U(업데이트 진입) 응답
  --nodsp [mute|boot]              앱이 버전에 응답하지 않음(dsp='?'). mute = 구 앱(U 도 무응답, 전원 재투입해야 부트로더), boot = 앱 없음(부트로더 대기 중)
  --app-frames yes|no              앱이 메인보드 UART 로 프레임을 보내는지(기본 yes = 살아 있음). no = 앱 멈춤(침묵) —
                                    --nodsp mute 와 함께 쓰면 멈춘 앱(복구 적용 대상), --app-u noreply 와 함께 쓰면 버전 조회에는 응답했지만
                                    U 에 침묵하는 앱(app_no_reply 뒤 dsp '?' 전환 시험)
  --nopart / --legacy              dspfw 파티션 없음(dsp_st=nopart) / 0.4.x 브리지
  --dup-id                         각 호기마다 같은 번호·다른 MAC 의 두 번째 장치를 --dup-delay 초 뒤에 띄움 (포트 +100)
  --events FILE                    받은 명령·굽기·재부팅 등을 JSON 줄로 기록 (종단 시험이 검사)
"""
import argparse
import json
import queue
import random
import signal
import socket
import struct
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zlib

# ───────── 패킷 (proto.h) ─────────
T_MAIN_UP, T_MAIN_DOWN, T_RFID = 0x01, 0x02, 0x03
T_STATUS, T_INFO_REQ, T_INFO = 0x10, 0x11, 0x12
T_PING, T_PONG = 0x20, 0x21
T_OTA_START, T_OTA_RESULT = 0x30, 0x31
T_DSP_START, T_DSP_RESULT, T_DSP_APPLY = 0x32, 0x33, 0x34
T_CFG_CMD, T_CFG_RESULT = 0x40, 0x41
HDR_LEN, PAYLOAD_MAX = 10, 512
RX_NAMES = {T_MAIN_DOWN: "MAIN", T_PING: "PING", T_OTA_START: "OTA", T_CFG_CMD: "CFG", T_INFO_REQ: "INFO",
            T_DSP_START: "START", T_DSP_APPLY: "APPLY"}


def build_pkt(t, agv, seq, pl: bytes):
    if len(pl) > PAYLOAD_MAX:
        return None
    return bytes([0xA5, 0x5A, 0x01, t, agv & 0xFF, 0]) + struct.pack("<HH", seq & 0xFFFF, len(pl)) + pl


def parse_pkt(b: bytes):
    if len(b) < HDR_LEN or b[0] != 0xA5 or b[1] != 0x5A or b[2] != 1:
        return None
    ln = struct.unpack_from("<H", b, 8)[0]
    if ln > PAYLOAD_MAX or ln != len(b) - HDR_LEN:
        return None
    return b[3], b[4], b[5], struct.unpack_from("<H", b, 6)[0], b[HDR_LEN:]


# ───────── .dspfw (dsp/common/dsp_image.h, tools/dsp_pack.py) ─────────
APP_HDR_ADDR, APP_CODE_ADDR, APP_END = 0x3E8000, 0x3E8040, 0x3F4000
APP_MAX_WORDS = APP_END - APP_CODE_ADDR
HDR_WORDS, VER_WORDS, BLOCK_WORDS = 64, 32, 128
FILE_HDR_LEN = 32
PEEK_LEN = FILE_HDR_LEN + HDR_WORDS * 2
PART_SIZE, STAGE_HDR_LEN = 0x60000, 64
HTTP_BUF = 4096
DAP_VER_MAX = 8


def crc16_ccitt(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def words_le(ws) -> bytes:
    return struct.pack("<%dH" % len(ws), *ws)


def ver_valid(v: str) -> bool:
    """dap_ver_valid: 1~8자, [0-9A-Za-z._-]"""
    if not v or len(v) > DAP_VER_MAX:
        return False
    return all(c.isascii() and (c.isalnum() or c in "._-") for c in v)


def build_app_header(app_len: int, app_crc: int, ver: str, build_time: int = 0, flags: int = 0):
    h = [0xFFFF] * HDR_WORDS
    h[0], h[1], h[2], h[3] = 0xA55A, 0x4650, 1, flags
    h[4], h[5] = app_len & 0xFFFF, app_len >> 16
    h[6], h[7] = app_crc & 0xFFFF, app_crc >> 16
    h[8], h[9] = build_time & 0xFFFF, (build_time >> 16) & 0xFFFF
    vb = ver.encode("ascii")
    for i in range(VER_WORDS):
        h[10 + i] = vb[i] if i < len(vb) and i < VER_WORDS - 1 else 0
    c = crc16_ccitt(words_le(h[:62]))
    h[62], h[63] = c, (~c) & 0xFFFF
    return h


def wrap_dspfw(body: bytes, file_ver: str) -> bytes:
    """파일 헤더 32B 로 본문을 감싼다 (DspfwImage.Build 와 같음)"""
    fv = file_ver.encode("ascii")[:15]
    total = FILE_HDR_LEN + len(body)
    return b"AGVDSP1\0" + struct.pack("<II", total, zlib.crc32(body) & 0xFFFFFFFF) + fv.ljust(16, b"\0") + body


def build_dspfw(code_words, ver: str, build_time: int = 0, file_ver=None) -> bytes:
    """dsp_pack.py pack() 과 같은 .dspfw (앱 헤더 + 128워드 블록, 전부 0xFFFF 인 블록 생략). file_ver 로 파일 헤더 버전을 따로 줄 수 있다"""
    n = len(code_words)
    if n == 0 or n > APP_MAX_WORDS:
        raise ValueError("code length")
    acrc = zlib.crc32(words_le(code_words)) & 0xFFFFFFFF
    body = bytearray(words_le(build_app_header(n, acrc, ver, build_time)))
    for off in range(0, n, BLOCK_WORDS):
        chunk = code_words[off:off + BLOCK_WORDS]
        if all(w == 0xFFFF for w in chunk):
            continue
        body += struct.pack("<IH", APP_CODE_ADDR + off, len(chunk)) + words_le(chunk)
    return wrap_dspfw(bytes(body), ver if file_ver is None else file_ver)


def test_code(n_words: int = 700, seed: int = 1):
    """시험용 앱 코드 (빈 블록 하나 포함)"""
    r = random.Random(seed)
    code = [r.randrange(0, 0xFFFF) for _ in range(n_words)]
    for i in range(BLOCK_WORDS, min(2 * BLOCK_WORDS, n_words)):
        code[i] = 0xFFFF
    return code


def build_esp_app(version: str, project: str = "agv_wifi_bridge", size: int = 512) -> bytes:
    """ESP-IDF 앱 이미지 머리 흉내 (EspAppImage.Build 와 같은 배치)"""
    f = bytearray(max(size, 256))
    f[0] = 0xE9
    struct.pack_into("<I", f, 32, 0xABCD5432)
    f[48:48 + 32] = version.encode()[:31].ljust(32, b"\0")
    f[80:80 + 32] = project.encode()[:31].ljust(32, b"\0")
    f[112:112 + 16] = b"12:00:00".ljust(16, b"\0")
    f[128:128 + 16] = b"Oct 11 2026".ljust(16, b"\0")
    f[144:144 + 32] = b"v6.1".ljust(32, b"\0")
    return bytes(f)


def hdr_parse(words):
    """dspimg_hdr_parse → dict 또는 None"""
    if words[0] != 0xA55A or words[1] != 0x4650 or words[2] != 1:
        return None
    c = crc16_ccitt(words_le(words[:62]))
    if words[62] != c or words[63] != ((~c) & 0xFFFF):
        return None
    app_len = words[4] | (words[5] << 16)
    app_crc = words[6] | (words[7] << 16)
    chars = [w & 0xFF for w in words[10:10 + VER_WORDS]]
    chars[VER_WORDS - 1] = 0
    ver = bytes(chars).split(b"\0")[0].decode("latin-1")
    if app_len == 0 or app_len > APP_MAX_WORDS:
        return None
    return {"app_len": app_len, "app_crc": app_crc, "ver": ver, "words": list(words)}


def head_check(b: bytes, file_len: int):
    """dsp_ota.c head_check → (err|None, info)"""
    if b[:7] != b"AGVDSP1" or struct.unpack_from("<I", b, 8)[0] != file_len:
        return "image_crc", None
    words = list(struct.unpack_from("<%dH" % HDR_WORDS, b, FILE_HDR_LEN))
    ah = hdr_parse(words)
    if ah is None:
        return "image_crc", None
    fver = b[16:31].split(b"\0")[0].decode("latin-1")
    if not ver_valid(ah["ver"]) or ah["ver"] != fver:
        return "ver_invalid", None
    return None, ah


def check_blocks(f: bytes, app_len: int, app_crc: int):
    """dap_image_check_blocks → None(정상) / 'block' / 'addr' / 'crc'"""
    pos, nxt, crc_end, crc = PEEK_LEN, APP_CODE_ADDR, APP_CODE_ADDR + app_len, 0
    if len(f) < PEEK_LEN or app_len == 0 or app_len > APP_MAX_WORDS:
        return "block"
    while pos < len(f):
        if len(f) - pos < 6:
            return "block"
        addr, nw = struct.unpack_from("<IH", f, pos)
        pos += 6
        if nw == 0 or nw > BLOCK_WORDS or len(f) - pos < nw * 2:
            return "block"
        if addr < APP_CODE_ADDR or addr > APP_END or nw > APP_END - addr or addr < nxt:
            return "addr"
        data = f[pos:pos + nw * 2]
        pos += nw * 2
        if nxt < crc_end:
            crc = zlib.crc32(b"\xff" * ((min(addr, crc_end) - nxt) * 2), crc)
        if addr < crc_end:
            crc = zlib.crc32(data[:min(crc_end - addr, nw) * 2], crc)
        nxt = addr + nw
    if nxt < crc_end:
        crc = zlib.crc32(b"\xff" * ((crc_end - nxt) * 2), crc)
    return None if (crc & 0xFFFFFFFF) == app_crc else "crc"


def stage_check(f: bytes):
    """dsp_ota.c stage_check → (err|None, file_crc, info)"""
    if len(f) < PEEK_LEN:
        return "image_crc", 0, None
    err, ah = head_check(f[:PEEK_LEN], len(f))
    if err:
        return err, 0, None
    fcrc = struct.unpack_from("<I", f, 12)[0]
    if (zlib.crc32(f[FILE_HDR_LEN:]) & 0xFFFFFFFF) != fcrc:
        return "image_crc", 0, None
    if check_blocks(f, ah["app_len"], ah["app_crc"]):
        return "image_bad", 0, None
    return None, fcrc, ah


def dspfw_blocks(f: bytes):
    """적용 때 보낼 블록 수"""
    pos, n = PEEK_LEN, 0
    while pos + 6 <= len(f):
        nw = struct.unpack_from("<H", f, pos + 4)[0]
        pos += 6 + nw * 2
        n += 1
    return n


# ───────── 브리지 도우미 (bridge_util.h, dsp_app_proto.c) ─────────

def parse_ipv4(s: str):
    parts = s.split(".")
    if len(parts) != 4:
        return None
    o = []
    for p in parts:
        if not p or len(p) > 3 or not p.isdigit() or not p.isascii() or (len(p) > 1 and p[0] == "0") or int(p) > 255:
            return None
        o.append(int(p))
    return tuple(o)


def url_host_is(url: str, server_ip: str) -> bool:
    """bu_url_host_is: http(s)://<10진 IPv4>[:port] 이고 호스트 = 서버 IP"""
    low = url[:8].lower()
    if low.startswith("http://"):
        p = url[7:]
    elif low.startswith("https://"):
        p = url[8:]
    else:
        return False
    a = 0
    while a < len(p) and p[a] not in "/?#":
        if not (p[a].isdigit() and p[a].isascii()) and p[a] not in ".:":
            return False
        a += 1
    auth = p[:a]
    host, _, port = auth.partition(":")
    if ":" in auth:
        if not port or len(port) > 5 or not port.isdigit() or not (0 < int(port) <= 65535):
            return False
    ip = parse_ipv4(host)
    return ip is not None and ip == parse_ipv4(server_ip)


JSON_OK, JSON_MISSING, JSON_TOO_LONG = 1, 0, -1


def _json_find(js: str, key: str):
    """dsp_app_proto.c json_find: 첫 "key" 뒤 공백·콜론 다음 위치 (콜론이 없으면 None — 다음 출현은 찾지 않음)"""
    k = '"' + key + '"'
    i = js.find(k)
    if i < 0:
        return None
    p = i + len(k)
    while p < len(js) and js[p] in " :":
        if js[p] == ":":
            p += 1
            while p < len(js) and js[p] == " ":
                p += 1
            return p
        p += 1
    return None


def json_str(js: str, key: str, cap: int):
    """dap_json_str: (rc, value). 값이 cap-1 자를 넘으면 TOO_LONG"""
    p = _json_find(js, key)
    if p is None or p >= len(js) or js[p] != '"':
        return JSON_MISSING, ""
    end = js.find('"', p + 1)
    if end < 0:
        return JSON_MISSING, ""
    v = js[p + 1:end]
    if len(v) > cap - 1:
        return JSON_TOO_LONG, ""
    return JSON_OK, v


def json_u32(js: str, key: str) -> int:
    p = _json_find(js, key)
    if p is None or p >= len(js) or not js[p].isdigit():
        return 0
    v = 0
    while p < len(js) and js[p].isdigit() and js[p].isascii():
        v = (v * 10 + int(js[p])) & 0xFFFFFFFF
        p += 1
    return v


def tdiv(a: int, b: int) -> int:
    """C 정수 나눗셈 (0 쪽으로 자름) — 진행 보고 단위 판정이 실제와 같게"""
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def json_safe(s: str) -> str:
    """ota.c json_safe: 따옴표·역슬래시·제어 문자 → '_'"""
    return "".join("_" if (c in '"\\' or ord(c) < 0x20) else c for c in s)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_OPENER = urllib.request.build_opener(_NoRedirect, urllib.request.ProxyHandler({}))

ST_IDLE, ST_DOWNLOADING, ST_STAGED, ST_PROGRAMMING = "idle", "downloading", "staged", "programming"
FINAL_REPORTS, FINAL_GAP = 3, 0.3
RECENT_S = 600
CFG_KEYS = ("agv", "ssid", "pass", "ip", "mask", "gw", "server", "server_port", "local_port", "rfid_dst", "rfid_port",
            "main_baud", "rfid_baud", "hb_ms")

_events_lock = threading.Lock()


class Abort(Exception):
    """재부팅으로 진행 중이던 처리가 끊김 (보고 없이 사라짐)"""


class Bridge:
    def __init__(self, agv: int, listen: int, opts, mac: str, name: str):
        self.agv, self.listen, self.o, self.mac, self.name = agv, listen, opts, mac, name
        host, port = opts.server.rsplit(":", 1)
        self.server = (host, int(port))
        self.server_ip = host
        self.lock = threading.RLock()
        self.stop = threading.Event()
        self.rng = random.Random((opts.seed or 0) * 1000 + agv + (500 if "#2" in name else 0))
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind((opts.bind, listen))
        self.sock.settimeout(0.2)
        # 브리지 자체
        self.fw, self.part = opts.fw, opts.part
        self.prev = None                    # 롤백할 (fw, part)
        self.ota_pending = False
        self.ota_busy = False
        self.confirm_deadline = 0.0
        self.deaf = False                   # --ota-deaf: 새 펌웨어가 서버 패킷을 못 받음
        self.final_gen = 0
        # 설정
        self.cfg_saved = opts.cfg != "default"
        self.cfg_trial = False
        self.trial_deadline = 0.0
        self.cfg_bak = None
        self.run_cfg = {"hb_ms": int(round(opts.hb * 1000))}
        self.edit_cfg = dict(self.run_cfg)
        self.nvs_cfg = dict(self.run_cfg)
        # 메인보드 (상태 기계): mode app=앱 동작(F 응답), mute=앱 동작하지만 F 무응답(구 앱), boot=부트로더 업데이트 모드(앱 없음)
        self.has_part = not opts.nopart
        self.board = "app" if opts.nodsp is None else opts.nodsp
        self.board_ver = opts.dsp if self.board != "boot" else None
        self.board_boot = opts.dspb
        self.board_build = opts.dsp_build
        self.dsp_ver = self.dsp_boot = self.dsp_build = "?"
        self.state = ST_IDLE
        self.stage = None                   # RAM 의 s_stage (dict: job, ver, data, ah)
        self.stage_flash = None             # 파티션에 남아 있는 스테이징 (재부팅·실패 시 복원)
        self.recent = None
        self.cur_start = self.cur_apply = 0
        self.q = queue.Queue(maxsize=2)
        self.last_query = 0.0
        self.query_at = 0.0
        self.fail_left = None if opts.fail_n == 0 else opts.fail_n
        # 망
        self.online = False
        self.epoch = 0
        self.seq = {}
        self.boot_t = time.monotonic()
        self.up_offset = opts.up_start
        self.counters = {"rx": 0, "rx_bad": 0, "rx_foreign": 0, "tx": 0, "tx_off": 0, "drop": 0}
        self.drop_left = dict(opts.drop)
        self.drop_rx_left = dict(opts.drop_rx)
        self.mute_until = 0.0
        self.muted = set()
        self._boot(first=True)

    # ───── 공통 ─────
    def log(self, m):
        if not self.o.quiet:
            print(time.strftime("%H:%M:%S"), f"[esp {self.name}]", m, flush=True)

    def event(self, ev, **kw):
        if not self.o.events:
            return
        rec = {"t": round(time.time(), 3), "agv": self.agv, "dev": self.name, "ev": ev, **kw}
        with _events_lock, open(self.o.events, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")

    def sleep(self, sec, epoch=None):
        """재부팅되면 Abort (진행 중이던 처리는 보고 없이 사라짐)"""
        end = time.monotonic() + max(0.0, sec)
        while True:
            if self.stop.is_set() or (epoch is not None and epoch != self.epoch):
                raise Abort()
            left = end - time.monotonic()
            if left <= 0:
                return
            time.sleep(min(left, 0.05))

    def sim(self, sec):
        return sec * self.o.speed

    def up(self):
        return int(time.monotonic() - self.boot_t) + self.up_offset

    def _take_budget(self, budget, name):
        if name not in budget:
            return False
        left = budget[name]
        if left is None:
            return True
        if left <= 0:
            return False
        budget[name] = left - 1
        return True

    def send(self, t, pl: bytes, kind: str, dst=None, seq=None):
        """송신 (실제와 같이 순번은 유실·오프라인이어도 증가)"""
        with self.lock:
            if seq is None:
                seq = self.seq.get(t, 0)
                self.seq[t] = (seq + 1) & 0xFFFF
            if not self.online:
                self.counters["tx_off"] += 1
                return False
            if self._take_budget(self.drop_left, kind):
                self.counters["drop"] += 1
                self.log(f"(drop {kind}) {pl[:120].decode(errors='replace')}")
                self.event("tx_drop", kind=kind)
                return False
            if time.monotonic() < self.mute_until or (self.o.loss and self.rng.random() < self.o.loss):
                self.counters["drop"] += 1
                return False
        p = build_pkt(t, self.agv, seq, pl)
        if p is None:
            return False
        try:
            self.sock.sendto(p, dst or self.server)
        except OSError:
            return False
        self.counters["tx"] += 1
        return True

    # ───── 상태 JSON ─────
    def dsp_json(self):
        st = self.state if self.has_part else "nopart"
        staged = {"job": self.stage["job"], "ver": self.stage["ver"]} if self.stage else None
        return {"dsp": self.dsp_ver, "dspb": self.dsp_boot, "dsp_build": self.dsp_build, "dsp_st": st, "staged": staged}

    def ota_state(self):
        return "busy" if self.ota_busy else "pending" if self.ota_pending else "valid"

    def cfg_state(self):
        return "trial" if self.cfg_trial else "saved" if self.cfg_saved else "default"

    def status_json(self):
        with self.lock:
            d = {"fw": self.fw}
            if not self.o.legacy:
                d["mac"] = self.mac
            d.update({"part": self.part, "ota": self.ota_state(), "up": self.up(), "rssi": -55, "ch": 6,
                      "bssid": "02:00:00:00:00:01", "disc": 0, "reason": 0, "m_rx": 0, "m_tx": 0, "r_rx": 0, "u_err": 0,
                      "n_off": self.counters["tx_off"], "n_err": 0, "heap": 180000, "cfg": self.cfg_state(), "setup": 0})
            if not self.o.legacy:
                d.update(self.dsp_json())
        return json.dumps(d, separators=(",", ":"))

    def info_json(self):
        with self.lock:
            d = {"agv": self.agv, "mac": self.mac, "ip": self.o.ip, "fw": self.fw, "part": self.part, "ota": self.ota_state(),
                 "cfg": self.cfg_state(), "setup": 0, "up": self.up()}
            if not self.o.legacy:
                d.update(self.dsp_json())
        return json.dumps(d, separators=(",", ":"))

    # ───── 부팅·재부팅 ─────
    def _boot(self, first=False, ota_ver=None, rollback=False):
        with self.lock:
            now = time.monotonic()
            if ota_ver is not None:
                self.prev = (self.fw, self.part)
                self.fw, self.part = ota_ver, ("ota_1" if self.part == "ota_0" else "ota_0")
                self.ota_pending = True
                self.confirm_deadline = now + self.o.confirm_sec
                self.deaf = self.o.ota_deaf
            elif self.ota_pending or rollback:
                # 확정 전 재부팅·확정 시간 초과 → 부트로더가 이전 슬롯으로 롤백
                if self.prev:
                    self.fw, self.part = self.prev
                self.ota_pending = False
                self.deaf = False
                self.log(f"rollback → fw={self.fw} part={self.part}")
                self.event("rollback", fw=self.fw, part=self.part)
            self.ota_busy = False
            self.final_gen += 1
            # 설정: NVS 값으로 기동, 백업이 있으면 확인 대기
            self.run_cfg = dict(self.nvs_cfg)
            self.edit_cfg = dict(self.run_cfg)
            if self.cfg_bak is not None:
                self.cfg_trial = True
                self.trial_deadline = now + self.o.confirm_sec
            # 망·메인보드
            self.seq = {}
            self.boot_t = now
            if not first:
                self.up_offset = 0
            self.stage = self.stage_flash if self.has_part else None
            self.state = ST_STAGED if self.stage else ST_IDLE
            self.recent = None
            self.cur_start = self.cur_apply = 0
            while not self.q.empty():
                try:
                    self.q.get_nowait()
                except queue.Empty:
                    break
            self.dsp_ver = self.dsp_boot = self.dsp_build = "?"
            # 실제 dsp_task 는 부팅 3초 뒤 앱 버전을 묻는다. 처음 시작은 오래 켜져 있던 브리지로 보고 바로 묻는다
            self.query_at = now if first else now + 3.0
            self.online = True

    def reboot(self, why, ota_ver=None, rollback=False):
        with self.lock:
            self.epoch += 1
            self.online = False
        self.log(f"reboot ({why})")
        self.event("reboot", why=why, fw=ota_ver or self.fw)
        time.sleep(self.o.boot_sec)
        self._boot(ota_ver=ota_ver, rollback=rollback)
        self.log(f"booted fw={self.fw} part={self.part} ota={self.ota_state()} cfg={self.cfg_state()} up=0")
        self.event("booted", fw=self.fw, part=self.part, ota=self.ota_state(), cfg=self.cfg_state())

    # ───── 수신 ─────
    def rx_loop(self):
        while not self.stop.is_set():
            try:
                b, src = self.sock.recvfrom(2048)
            except socket.timeout:
                continue
            except OSError:
                if self.stop.is_set():
                    return
                continue
            if not self.online:
                continue                    # 재부팅 중 — 유실
            p = parse_pkt(b)
            if p is None:
                self.counters["rx_bad"] += 1
                continue
            if src[0] != self.server_ip or self.deaf:
                self.counters["rx_foreign"] += 1
                continue
            t, agv, _flags, seq, pl = p
            name = RX_NAMES.get(t, "0x%02X" % t)
            with self.lock:
                lost = self._take_budget(self.drop_rx_left, name) or (self.o.rx_loss and self.rng.random() < self.o.rx_loss)
            if lost:
                self.log(f"(drop rx {name}) {pl[:120].decode(errors='replace')}")
                self.event("rx_drop", type=name, payload=pl.decode(errors="replace"))
                continue
            self.counters["rx"] += 1
            self.ota_confirm(name)
            self.cfg_confirm(name)
            if t not in (T_PING, T_INFO_REQ):
                self.event("rx", type=name, payload=pl.decode(errors="replace"), seq=seq)
            try:
                self.dispatch(t, agv, seq, pl, src)
            except Exception as e:     # 가짜 자체 오류로 수신 루프가 죽지 않게
                self.log(f"handler error: {e!r}")

    def dispatch(self, t, agv, seq, pl, src):
        mine = agv == self.agv
        if t == T_MAIN_DOWN:
            if not mine:
                self.counters["rx_foreign"] += 1
                return
            self.log("MAIN< " + pl.decode(errors="replace"))
        elif t == T_PING:
            self.send(T_PONG, pl, "pong", dst=src, seq=seq)
        elif t == T_OTA_START:
            if not mine:
                self.counters["rx_foreign"] += 1
                return
            url = pl.decode("latin-1") if len(pl) < 257 else ""
            self.log("OTA< START " + url)
            self.ota_start(url.split("\0")[0])
        elif t == T_CFG_CMD:
            if not mine:
                self.counters["rx_foreign"] += 1
                return
            self.handle_cfg(src, seq, pl)
        elif t == T_INFO_REQ:
            self.send(T_INFO, self.info_json().encode(), "info", dst=src)
        elif t == T_DSP_START and not self.o.legacy:
            if not mine:
                self.counters["rx_foreign"] += 1
                return
            self.log("DSP< START " + pl.decode(errors="replace"))
            self.dsp_ota_start(pl.decode("latin-1"))
        elif t == T_DSP_APPLY and not self.o.legacy:
            if not mine:
                self.counters["rx_foreign"] += 1
                return
            self.log("DSP< APPLY " + pl.decode(errors="replace"))
            self.dsp_ota_apply(pl.decode("latin-1"))
        else:
            self.counters["rx_bad"] += 1

    def ota_confirm(self, by):
        with self.lock:
            if self.ota_pending:
                self.ota_pending = False
                self.prev = None
                self.log(f"firmware confirmed by {by} (ota=valid)")
                self.event("ota_confirmed", fw=self.fw, by=by)

    def cfg_confirm(self, by):
        with self.lock:
            if self.cfg_trial:
                self.cfg_trial = False
                self.cfg_bak = None
                self.log(f"remote config confirmed by {by} (cfg=saved)")
                self.event("cfg_confirmed", by=by)

    # ───── 주기 작업 ─────
    def hb_loop(self):
        while not self.stop.is_set():
            hb = self.run_cfg.get("hb_ms", 1000)
            time.sleep(hb / 1000.0 if hb > 0 else 0.5)
            if hb <= 0 or not self.online:
                continue
            self.send(T_STATUS, self.status_json().encode(), "hb")

    def sys_loop(self):
        while not self.stop.is_set():
            time.sleep(0.1)
            now = time.monotonic()
            if not self.online:
                continue
            if self.ota_pending and now >= self.confirm_deadline:
                self.log(f"no server contact within {self.o.confirm_sec:.0f}s - rolling back")
                self.reboot("ota confirm timeout", rollback=True)
            elif self.cfg_trial and now >= self.trial_deadline:
                self.log(f"no server contact within {self.o.confirm_sec:.0f}s after remote config change - restoring previous config")
                with self.lock:
                    if self.cfg_bak is not None:
                        self.nvs_cfg = dict(self.cfg_bak)
                    self.cfg_bak = None
                    self.cfg_trial = False
                self.event("cfg_reverted")
                self.reboot("cfg trial timeout")

    # ───── 브리지 자체 OTA (ota.c) ─────
    def ota_send(self, js: str):
        stage = json.loads(js).get("stage", "?")
        self.log("OTA> " + js)
        sent = self.send(T_OTA_RESULT, js.encode(), "ota_" + stage)
        self.event("ota_report", stage=stage, js=js, sent=sent)
        return sent

    def ota_final(self, js: str):
        """지금 1회 + 300ms 간격 2회 (비동기, 새 최종 결과가 오면 대체)"""
        self.ota_send(js)
        if self.o.legacy:
            return
        with self.lock:
            self.final_gen += 1
            gen = self.final_gen

        def resend():
            for _ in range(FINAL_REPORTS - 1):
                time.sleep(FINAL_GAP)
                if gen != self.final_gen or self.stop.is_set():
                    return
                self.ota_send(js)
        threading.Thread(target=resend, daemon=True).start()

    def ota_start(self, url: str):
        if len(url) < 8 or len(url) >= 256 or not url.startswith("http"):
            self.ota_final('{"stage":"fail","err":"bad_url"}')
            return
        if not url_host_is(url, self.server_ip):
            self.log("rejected: url host is not the configured server - " + url)
            self.ota_final('{"stage":"fail","err":"url_host"}')
            return
        with self.lock:
            if self.ota_busy:
                busy = True
            else:
                busy = False
                self.ota_busy = True
                why = "mainboard_programming" if self.state == ST_PROGRAMMING else "mainboard_staging" if self.state == ST_DOWNLOADING else None
                if why:
                    self.ota_busy = False
        if busy:
            self.ota_final('{"stage":"busy"}')
            return
        if why:
            self.ota_final('{"stage":"fail","err":"%s"}' % why)
            return
        self.event("ota_start", url=url)
        threading.Thread(target=self.ota_task, args=(url, self.epoch), daemon=True).start()

    def ota_task(self, url, epoch):
        try:
            self._ota_task(url, epoch)
        except Abort:
            pass
        finally:
            with self.lock:
                if epoch == self.epoch:
                    self.ota_busy = False

    def _ota_task(self, url, epoch):
        self.ota_send(json.dumps({"stage": "start", "url": json_safe(url)}, separators=(",", ":")))
        try:
            with _OPENER.open(url, timeout=10) as r:
                data = r.read()
        except Exception as e:
            self.log(f"download error {e!r}")
            self.ota_final('{"stage":"fail","err":"begin ESP_FAIL"}')
            return
        if len(data) < 256 or data[0] != 0xE9 or struct.unpack_from("<I", data, 32)[0] != 0xABCD5432:
            self.ota_final('{"stage":"fail","err":"begin ESP_ERR_OTA_VALIDATE_FAILED"}')
            return
        proj = data[80:112].split(b"\0")[0].decode(errors="replace")
        ver = data[48:80].split(b"\0")[0].decode(errors="replace")
        if proj != "agv_wifi_bridge":
            self.ota_final('{"stage":"fail","err":"project mismatch (%s)"}' % json_safe(proj))
            return
        total, last = len(data), -1
        chunks = max(1, (total + HTTP_BUF - 1) // HTTP_BUF)
        read = 0
        while read < total:
            self.sleep(self.sim(self.o.ota_sec) / chunks, epoch)
            read = min(total, read + HTTP_BUF)
            pct = read * 100 // total
            if tdiv(pct, 10) != tdiv(last, 10):
                last = pct
                self.ota_send(json.dumps({"stage": "progress", "pct": pct, "bytes": read}, separators=(",", ":")))
        if self.take_fail("ota"):
            self.ota_final('{"stage":"fail","err":"finish ESP_ERR_OTA_VALIDATE_FAILED"}')
            return
        self.ota_final(json.dumps({"stage": "done", "ver": json_safe(ver), "bytes": total}, separators=(",", ":")))
        self.event("ota_done", ver=ver)
        self.sleep((FINAL_REPORTS - 1) * FINAL_GAP + 0.2, epoch)
        self.reboot("ota done", ota_ver=ver)

    # ───── 원격 설정 (cmd.c) ─────
    def handle_cfg(self, src, seq, pl):
        line = pl[:199].decode("latin-1").split("\r")[0].split("\n")[0].rstrip(" ")
        self.log("CFG< " + line)
        out, reboot = self.cmd_exec(line)
        self.send(T_CFG_RESULT, out.encode()[:PAYLOAD_MAX], "cfg", dst=src, seq=seq)
        if reboot:
            def later():
                time.sleep(1.0)                         # REBOOT_DELAY_MS
                while self.state == ST_PROGRAMMING:     # 메인보드 굽기 중이면 끝날 때까지 미룸
                    time.sleep(0.5)
                self.reboot("cfg reboot")
            threading.Thread(target=later, daemon=True).start()

    def cfg_text(self):
        c = self.edit_cfg
        s = (f"agv={self.agv} ssid=\"AGV_TEST\" pass=(8 chars)\nip=dhcp mask=255.255.255.0 gw=0.0.0.0\n"
             f"server={self.server_ip}:{self.server[1]} local_port={self.listen}\nrfid_dst=off:50001\n"
             f"main_baud=115200 rfid_baud=9600 hb_ms={c.get('hb_ms', 1000)}\nstate={self.cfg_state()}\n")
        if c != self.run_cfg:
            s += "* differs from running config - 'save' then 'reboot' to apply\n"
        return s

    def cmd_exec(self, line: str):
        toks = line.split(None, 2)          # strtok_r(" ") — 값은 줄 끝까지 (공백 있는 SSID)
        cmd = toks[0] if toks else None
        if cmd is None or cmd == "cfg":
            return self.cfg_text(), False
        if cmd == "set":
            key = toks[1] if len(toks) > 1 else None
            val = toks[2].lstrip(" ") if len(toks) > 2 else ""
            if key is None or not val:
                return ("usage: set <key> <value>\n"
                        "keys : agv ssid pass(- = none) ip(dhcp) mask gw server server_port local_port\n"
                        "       rfid_dst(off / 255.255.255.255) rfid_port main_baud rfid_baud hb_ms(0 = off, 100-2000)\n"), False
            ok = key in CFG_KEYS
            if key == "hb_ms":
                ok = val.isdigit() and (int(val) == 0 or 100 <= int(val) <= 2000)
                if ok:
                    self.edit_cfg["hb_ms"] = int(val)
            elif ok:
                self.edit_cfg[key] = val
            if ok:
                return f"ok {key} - 'save' then 'reboot' to apply\n", False
            return f"invalid key or value: {key}\n", False
        if cmd == "save":
            with self.lock:
                self.cfg_bak = dict(self.run_cfg)       # 서버 원격 저장 = 확인 대기 (지금 동작 중인 설정 백업)
                self.nvs_cfg = dict(self.edit_cfg)
                self.cfg_saved = True
            self.event("cfg_saved", trial=True)
            return ("saved (trial) - 'reboot' to apply. After reboot the bridge needs a server packet within 180 s "
                    "or it restores the previous config (gateway 0.3.0+ pings automatically)\n"), False
        if cmd == "reboot":
            if self.state == ST_PROGRAMMING:
                return "busy: mainboard programming - reboot refused, retry when done\n", False
            return "rebooting\n", True
        if cmd == "stat":
            return (f"fw={self.fw} part={self.part} ota={self.ota_state()} cfg={self.cfg_state()}\n"
                    f"wifi: UP ip={self.o.ip} rssi=-55 ch=6 bssid=02:00:00:00:00:01\n"
                    f"net : tx={self.counters['tx']} tx_err=0 tx_offline={self.counters['tx_off']} rx={self.counters['rx']} "
                    f"rx_bad={self.counters['rx_bad']} rx_foreign={self.counters['rx_foreign']} disc=0 last_reason=0\n"
                    f"{self.dsp_format()}"), False
        if cmd == "setup":
            return "setup: off\n", False
        if cmd == "dsp":
            arg = toks[1] if len(toks) > 1 else ""
            if arg == "ver":
                self.query_at = time.monotonic()
                return "version query sent - 'dsp' to see\n", False
            if arg == "apply":
                self.dsp_ota_apply("{}")
                return "apply requested - progress goes to server (DSP_OTA_RESULT)\n", False
            return self.dsp_format(), False
        if cmd == "help":
            return "cfg | set <key> <value> | save | reboot | stat | setup [on|off] | dsp [ver|apply]\n", False
        return f"unknown command '{cmd}' - type help\n", False

    def dsp_format(self):
        st = self.state if self.has_part else "nopart (USB reflash needed)"
        sv, sj = (self.stage["ver"], self.stage["job"]) if self.stage else ("-", 0)
        return f"dsp : app={self.dsp_ver} boot={self.dsp_boot} build={self.dsp_build} state={st} staged={sv}(job {sj}) pan=1\n"

    # ───── 메인보드 OTA: 보고 (dsp_ota.c) ─────
    def report(self, job, stage, pct=-1, ver=None, err=None):
        d = {"job": job, "stage": stage}
        if pct >= 0:
            d["pct"] = pct
        if ver is not None:
            d["ver"] = ver
        if err is not None:
            d["err"] = err
        js = json.dumps(d, separators=(",", ":"))
        self.log("DSP> " + js)
        sent = self.send(T_DSP_RESULT, js.encode(), stage)
        self.event("dsp_report", job=job, stage=stage, pct=pct, ver=ver, err=err, sent=sent)

    def report_final(self, job, stage, pct=-1, ver=None, err=None, gap=True):
        n = 1 if self.o.legacy else FINAL_REPORTS
        for i in range(n):
            if i > 0 and gap:
                time.sleep(FINAL_GAP)
            self.report(job, stage, pct, ver, err)

    def report_result(self, job, applied, stage, pct=-1, ver=None, err=None):
        self.report_final(job, stage, pct, ver, err)
        if job == 0:
            return
        self.recent = {"job": job, "applied": applied, "stage": stage, "pct": pct, "ver": ver or "", "err": err or "",
                       "at": time.monotonic()}

    def replay_recent(self, job, applied_only):
        r = self.recent
        if job == 0 or r is None or r["job"] != job or (applied_only and not r["applied"]) or time.monotonic() - r["at"] > RECENT_S:
            return False
        self.log(f"job {job}: duplicate command - repeating last result ({r['stage']})")
        self.event("replay", job=job, stage=r["stage"])
        self.report_final(job, r["stage"], r["pct"], r["ver"] or None, r["err"] or None)
        return True

    def stage_ver(self):
        return self.stage["ver"] if self.stage else ""

    def stage_job(self):
        return self.stage["job"] if self.stage else 0

    def take_fail(self, stage):
        if self.o.fail_stage != stage:
            return False
        with self.lock:
            if self.fail_left is None:
                return True
            if self.fail_left <= 0:
                return False
            self.fail_left -= 1
            return True

    def maybe_mute(self, kind, job):
        if self.o.mute_after_cmd > 0 and (kind, job) not in self.muted:
            self.muted.add((kind, job))
            self.mute_until = time.monotonic() + self.o.mute_after_cmd
            self.log(f"(uplink mute {self.o.mute_after_cmd:.1f}s after {kind} job {job})")
            self.event("mute", cmd=kind, job=job, sec=self.o.mute_after_cmd)

    # ───── 메인보드 OTA: 서버 명령 (수신 문맥) ─────
    def dsp_ota_start(self, js: str):
        job = json_u32(js, "job")
        if job != 0 and job == self.cur_start:
            self.log(f"job {job}: duplicate START ignored (in progress)")
            self.event("dup_ignored", cmd="START", job=job)
            return
        rc, url = json_str(js, "url", 200)
        if rc == JSON_TOO_LONG:
            self.report_final(job, "fail", err="url_too_long", gap=False)
            return
        if rc != JSON_OK or not url.startswith("http"):
            self.report_final(job, "fail", err="bad_request", gap=False)
            return
        if not url_host_is(url, self.server_ip):
            self.log("staging url host is not the configured server: " + url)
            self.report_final(job, "fail", err="url_host", gap=False)
            return
        c = {"type": "START", "job": job, "url": url, "ver": json_str(js, "ver", 16)[1], "crc32": json_u32(js, "crc32"),
             "size": json_u32(js, "size"), "apply": json_u32(js, "apply") != 0, "recover": json_u32(js, "recover") == 1,
             "epoch": self.epoch}
        self.maybe_mute("START", job)
        try:
            self.q.put_nowait(c)
        except queue.Full:
            self.report_final(job, "fail", err="busy", gap=False)

    def dsp_ota_apply(self, js: str):
        job = json_u32(js, "job")
        if job != 0 and job == self.cur_apply:
            self.log(f"job {job}: duplicate APPLY ignored (in progress)")
            self.event("dup_ignored", cmd="APPLY", job=job)
            return
        c = {"type": "APPLY", "job": job, "recover": json_u32(js, "recover") == 1, "apply": True, "epoch": self.epoch}
        self.maybe_mute("APPLY", job)
        try:
            self.q.put_nowait(c)
        except queue.Full:
            self.report_final(job, "fail", err="busy", gap=False)

    # ───── 메인보드 OTA: 처리 태스크 ─────
    def dsp_loop(self):
        while not self.stop.is_set():
            try:
                c = self.q.get(timeout=0.2)
            except queue.Empty:
                now = time.monotonic()
                try:
                    if self.online and self.query_at and now >= self.query_at:
                        self.query_at = 0.0
                        self.query_version(self.epoch)
                    elif (self.online and not self.query_at and self.dsp_ver == "?" and self.state != ST_PROGRAMMING
                          and now - self.last_query > 60):
                        self.query_version(self.epoch)
                except Abort:
                    pass
                continue
            if c["epoch"] != self.epoch:
                continue
            if c["type"] == "START":
                self.cur_start = c["job"]
            try:
                self.handle_cmd(c, c["epoch"])
            except Abort:
                self.log("command aborted by reboot")
            except Exception as e:     # 가짜 자체 오류로 처리 태스크가 죽지 않게
                self.log(f"dsp handler error: {e!r}")
            finally:
                if c["epoch"] == self.epoch:
                    self.cur_start = 0
                    self.cur_apply = 0

    def app_alive(self):
        """직전 약 3초 안에 메인보드 UART 에서 앱 프레임을 받았는지 (앱이 돌고 있음 — 부트로더 대기 중이면 프레임 없음)"""
        return self.board in ("app", "mute") and self.o.app_frames == "yes"

    def app_query(self, cmd, epoch):
        """메인보드 앱 F→R. 응답 dict 또는 None(응답 없음, 2초 대기)"""
        if self.board == "app":
            if cmd == "V":
                return {"app_ver": (self.board_ver or "")[:8].strip(), "boot_ver": self.board_boot[:8], "build": self.board_build[:8]}
            u = self.o.app_u
            if u == "ok":
                self.board = "boot"         # 앱이 정지 확인 후 부트로더로 리셋
                self.event("app_enter_boot")
                return {"ok": True}
            if u.startswith("ng:"):
                return {"ok": False, "reason": int(u[3:] or 0)}
        self.sleep(2.0, epoch)               # APP_REPLY_MS
        return None

    def query_version(self, epoch):
        self.last_query = time.monotonic()
        r = self.app_query("V", epoch)
        if r is None:
            return False
        with self.lock:
            self.dsp_ver = r["app_ver"] or "-"
            self.dsp_boot = r["boot_ver"] or "-"
            self.dsp_build = r["build"] or "-"
        self.log(f"main board app {self.dsp_ver} boot {self.dsp_boot} build {self.dsp_build}")
        return True

    def download(self, c, epoch):
        url = c["url"]
        resp = None
        for redirects in range(4):
            try:
                resp = _OPENER.open(urllib.request.Request(url), timeout=10)
                break
            except urllib.error.HTTPError as e:
                if e.code in (301, 302, 303, 307, 308) and redirects < 3:
                    url = urllib.parse.urljoin(url, e.headers.get("Location", ""))
                    if not url_host_is(url, self.server_ip):
                        return "url_host"
                    continue
                return "http_status"
            except Exception:
                return "http_open"
        if resp is None:
            return "http_status"
        with resp:
            if resp.status != 200:
                return "http_status"
            clen = int(resp.headers.get("Content-Length") or -1)
            if clen < PEEK_LEN or clen > PART_SIZE - STAGE_HDR_LEN or (c["size"] and clen != c["size"]):
                return "http_length"
            try:
                data = resp.read()
            except Exception:
                return "http_read"
        if len(data) < PEEK_LEN:
            return "http_short"
        err, ah = head_check(data[:PEEK_LEN], clen)
        if err:
            return err
        # 머리 섹터부터 지움 — 받는 동안 '스테이징 없음'
        with self.lock:
            self.stage = None
            self.stage_flash = None
        self.event("download", job=c["job"], url=c["url"])
        chunks = [data[i:i + HTTP_BUF] for i in range(0, len(data), HTTP_BUF)]
        pos, last = 0, -1
        for ch in chunks:
            self.sleep(self.sim(self.o.dl_sec) / len(chunks), epoch)
            if self.take_fail("download"):
                return "flash_write"
            if pos + len(ch) > clen:
                return "http_overrun"
            pos += len(ch)
            pct = pos * 100 // clen
            if tdiv(pct, 20) != tdiv(last, 20):
                last = pct
                self.report(c["job"], "download", pct)
        if pos != clen:
            return "http_short"
        err, fcrc, ah = stage_check(data)
        if err:
            return err
        if c["crc32"] and c["crc32"] != fcrc:
            return "crc_mismatch"
        with self.lock:
            self.stage = {"job": c["job"], "ver": ah["ver"], "data": data, "ah": ah}
            self.stage_flash = self.stage
        return None

    def handle_cmd(self, c, epoch):
        if not self.has_part:
            self.report_final(c["job"], "fail", err="no_partition")
            return
        if c["type"] == "START":
            if c["job"] != 0 and self.stage and self.stage["job"] == c["job"]:
                self.log(f"job {c['job']}: duplicate START - already staged")
                self.event("dup_staged", job=c["job"])
                self.report_final(c["job"], "staged", 100, self.stage["ver"])
                if not c["apply"]:
                    return
            elif self.replay_recent(c["job"], False):
                return
            else:
                if self.state == ST_PROGRAMMING:
                    self.report_final(c["job"], "fail", err="busy")
                    return
                self.state = ST_DOWNLOADING
                err = "esp_ota_busy" if self.ota_busy else self.download(c, epoch)
                if err:
                    with self.lock:
                        self.stage = self.stage_flash            # stage_load
                        self.state = ST_STAGED if self.stage else ST_IDLE
                    self.report_result(c["job"], False, "fail", err=err)
                    return
                self.state = ST_STAGED
                self.report_result(c["job"], False, "staged", 100, self.stage["ver"])
                if not c["apply"]:
                    return
        # 적용
        if self.replay_recent(c["job"], True):
            return
        if c["type"] == "APPLY" and self.stage and c["job"] != 0 and c["job"] != self.stage["job"]:
            self.report_result(c["job"], True, "fail", ver=self.stage["ver"], err="job_mismatch")
            return
        job = self.stage_job() if c["type"] == "APPLY" else c["job"]
        self.cur_apply = job
        self.report(job, "start", 0, self.stage_ver())
        err = self.apply(job, c["recover"], epoch)
        if err:
            self.report_result(job, True, "fail", ver=self.stage_ver(), err=err)
        else:
            self.report_result(job, True, "done", 100, self.dsp_ver)
        self.cur_apply = 0

    def apply(self, job, recover, epoch):
        if not self.stage:
            return "not_staged"
        with self.lock:
            self.state = ST_PROGRAMMING
            if self.ota_busy:
                self.state = ST_STAGED
                return "esp_ota_busy"
        err, _fcrc, ah = stage_check(self.stage["data"])
        if err:
            self.state = ST_STAGED
            return "stage_invalid" if err == "image_crc" else err
        self.event("apply", job=job, recover=recover, board=self.board, dsp=self.dsp_ver)
        erased = [False]
        err = self.program(job, ah, True, recover, erased, epoch)
        n = 1
        while err and erased[0] and n <= 2:
            self.log(f"failed after erase ({err}) - retry {n} from HELLO")
            self.report(job, "retry", n)
            err = self.program(job, ah, False, False, erased, epoch)
            n += 1
        if err:
            self.state = ST_STAGED
            return err
        self.dsp_ver = "?"
        for _ in range(8):                  # APP_UP_TRIES
            self.sleep(1.0, epoch)
            if self.query_version(epoch):
                break
        if self.dsp_ver == "?":
            self.state = ST_STAGED
            return "app_no_reply"
        if self.dsp_ver != ah["ver"]:
            self.state = ST_STAGED
            return "ver_mismatch"
        with self.lock:
            self.stage = None
            self.stage_flash = None
            self.state = ST_IDLE
        return None

    def hello(self, epoch, tries=3, timeout=1.5):
        if self.board == "boot" and not self.take_fail("hello"):
            return True
        self.sleep(tries * timeout, epoch)
        return False

    def wait_power(self, job, epoch):
        self.log("no bootloader - sending HELLO for 60 s, power-cycle the main board")
        t0 = time.monotonic()
        for _ in range(0, 60, 10):
            self.report(job, "wait_power")
            end = time.monotonic() + 10
            while time.monotonic() < end:
                pc = self.o.power_cycle_sec
                if pc >= 0 and time.monotonic() - t0 >= pc and self.board in ("mute", "app"):
                    self.log("(main board power-cycled - bootloader caught HELLO)")
                    self.event("power_cycle", job=job)
                    self.board = "boot"
                if self.board == "boot" and not self.take_fail("hello"):
                    return True
                self.sleep(0.05, epoch)
        return False

    def program(self, job, ah, enter, recover, erased, epoch):
        if enter:
            self.report(job, "enter")
            if self.take_fail("enter"):
                return "uart_busy"
            r = self.app_query("U", epoch)
            if r is not None:
                if not r["ok"]:
                    return "app_ng_%02u" % r["reason"]
                self.sleep(0.4, epoch)      # BOOT_START_MS
            elif self.app_alive():
                # 앱은 살아 있으나 업데이트 진입(F/R)을 모르는 구 앱 — 원시 모드로 RFID·ACS 중계를 끊지 않는다
                self.log("no U reply but app frames seen - app does not support update entry")
                return "app_no_fwup"
            elif recover or self.dsp_ver == "?":
                self.log("no U reply - trying bootloader directly (%s)" % ("recover" if recover else "app version unknown"))
                self.event("raw_mode", job=job, recover=recover)
            else:
                with self.lock:
                    self.dsp_ver = "?"          # heartbeat 가 '?' 를 보고 — 다음 배포에서 복구 적용을 고를 수 있게
                    self.dsp_build = "?"
                return "app_no_reply"
        ok = self.hello(epoch)
        if not ok and recover and enter:
            ok = self.wait_power(job, epoch)
        if not ok:
            return "no_bootloader"
        self.dsp_boot = self.board_boot[:8]
        self.report(job, "erase")
        with self.lock:
            self.dsp_ver = "?"
            self.dsp_build = "?"
            self.board_ver = None           # 앱 지워짐
        erased[0] = True
        self.event("erase", job=job)
        self.sleep(self.sim(self.o.erase_sec), epoch)
        if self.take_fail("erase"):
            return "erase"
        data = self.stage["data"]
        nblk = max(1, dspfw_blocks(data))
        pos, total, sent, last, i = PEEK_LEN, len(data) - PEEK_LEN, 0, -1, 0
        while pos < len(data):
            nw = struct.unpack_from("<H", data, pos + 4)[0]
            self.sleep(self.sim(self.o.write_sec) / nblk, epoch)
            if i >= nblk // 2 and self.take_fail("write"):
                return "write_timeout"
            pos += 6 + nw * 2
            sent += 6 + nw * 2
            i += 1
            pct = sent * 100 // total
            if tdiv(pct, 10) != tdiv(last, 10):
                last = pct
                self.report(job, "progress", pct)
        if self.take_fail("header"):
            return "write_header"
        self.report(job, "verify")
        self.sleep(self.sim(0.3), epoch)
        if self.take_fail("verify"):
            return "verify"
        # 앱으로 리셋
        newver = ah["ver"]
        if self.take_fail("app"):
            self.board, self.board_ver = "mute", newver
        elif self.take_fail("ver"):
            self.board, self.board_ver = "app", (newver + "x")[:8] if len(newver) < 8 else newver[:7] + "x"
        else:
            self.board, self.board_ver = "app", newver
        self.board_build = time.strftime("%Y%m%d")
        self.event("programmed", job=job, ver=self.board_ver)
        return None

    # ───── 시작·정리 ─────
    def start(self):
        for fn in (self.rx_loop, self.hb_loop, self.sys_loop, self.dsp_loop):
            threading.Thread(target=fn, daemon=True, name=f"{fn.__name__}-{self.name}").start()
        self.log(f"agv {self.agv:02d} udp :{self.listen} -> {self.o.server} fw={self.fw} part={self.part} mac={self.mac} "
                 f"dsp={'?' if self.board != 'app' else self.board_ver}{' nopart' if not self.has_part else ''}{' legacy' if self.o.legacy else ''}")
        self.event("start", fw=self.fw, listen=self.listen, mac=self.mac)

    def close(self):
        self.stop.set()
        try:
            self.sock.close()
        except OSError:
            pass


def parse_budget(text: str):
    out = {}
    for item in filter(None, (x.strip() for x in (text or "").split(","))):
        name, _, n = item.partition(":")
        out[name] = int(n) if n else None
    return out


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description="가짜 브리지 (AGV_Wifi_Bridge 0.5.2 흉내)", epilog=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--agv", default="7", help="호기 번호, 여러 대는 쉼표 (7,12,23)")
    ap.add_argument("--server", default="127.0.0.1:50000", help="서버 IP:UDP 포트 (브리지 server/server_port 설정)")
    ap.add_argument("--listen", type=int, default=50010, help="이 브리지의 UDP 포트 (여러 대면 +1 씩)")
    ap.add_argument("--bind", default="", help="바인드 주소 (기본 전체)")
    ap.add_argument("--ip", default="127.0.0.1", help="INFO 에 보고할 IP")
    ap.add_argument("--mac", default="", help="MAC (기본 02:fa:ce:00:<n>:<agv>)")
    ap.add_argument("--fw", default="0.5.2", help="브리지 펌웨어 버전 (OTA_START 로 바뀜)")
    ap.add_argument("--part", default="ota_0")
    ap.add_argument("--hb", type=float, default=1.0, help="heartbeat 주기(초)")
    ap.add_argument("--dsp", default="1.0.0", help="메인보드 앱 버전")
    ap.add_argument("--dspb", default="1.0.0", help="메인보드 부트로더 버전")
    ap.add_argument("--dsp-build", default="20261009")
    ap.add_argument("--nodsp", nargs="?", const="mute", choices=("mute", "boot"), default=None,
                    help="앱이 버전에 응답하지 않음 (mute=구 앱, boot=앱 없음·부트로더 대기)")
    ap.add_argument("--app-u", default="ok", help="앱의 U 응답: ok | noreply | ng:NN")
    ap.add_argument("--app-frames", choices=("yes", "no"), default="yes", help="앱이 UART 프레임을 보냄(살아 있음) / 안 보냄(멈춤)")
    ap.add_argument("--power-cycle-sec", type=float, default=3.0, help="wait_power 시작 후 전원 재투입 흉내까지 초 (음수 = 안 함)")
    ap.add_argument("--fail-at", default="", help="STAGE[:N] 단계 실패 주입")
    ap.add_argument("--nopart", action="store_true", help="dspfw 파티션 없음")
    ap.add_argument("--legacy", action="store_true", help="0.4.x 브리지 (mac·dsp 필드 없음)")
    ap.add_argument("--cfg", choices=("saved", "default"), default="saved", help="시작 설정 상태")
    ap.add_argument("--dup-id", action="store_true", help="같은 번호·다른 MAC 의 두 번째 장치도 띄움 (포트 +100)")
    ap.add_argument("--dup-delay", type=float, default=2.0)
    ap.add_argument("--drop", default="", help="버릴 보고 이름[:N],...")
    ap.add_argument("--drop-rx", default="", help="버릴 수신 명령 이름[:N],...")
    ap.add_argument("--loss", type=float, default=0.0)
    ap.add_argument("--rx-loss", type=float, default=0.0)
    ap.add_argument("--mute-after-cmd", type=float, default=0.0)
    ap.add_argument("--speed", type=float, default=1.0, help="흉내 작업 시간(다운로드·지우기·쓰기·OTA) 배율")
    ap.add_argument("--dl-sec", type=float, default=0.6)
    ap.add_argument("--erase-sec", type=float, default=1.0)
    ap.add_argument("--write-sec", type=float, default=2.0)
    ap.add_argument("--ota-sec", type=float, default=1.0)
    ap.add_argument("--boot-sec", type=float, default=2.0, help="재부팅에 걸리는 시간 (패킷 없음)")
    ap.add_argument("--confirm-sec", type=float, default=180.0, help="새 펌웨어·원격 설정 확정 대기 (실제 180초)")
    ap.add_argument("--ota-deaf", action="store_true", help="OTA 로 올라간 새 펌웨어가 서버 패킷을 못 받음 (롤백 시험)")
    ap.add_argument("--up-start", type=int, default=3600, help="처음 up 값 (오래 켜져 있던 브리지 흉내)")
    ap.add_argument("--events", default="", help="사건 기록 파일 (JSON 줄)")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--quiet", action="store_true")
    o = ap.parse_args(argv)
    stage, _, n = o.fail_at.partition(":")
    o.fail_stage, o.fail_n = stage, int(n) if n else 0
    o.drop, o.drop_rx = parse_budget(o.drop), parse_budget(o.drop_rx)
    if o.legacy and o.fw == "0.5.2":
        o.fw = "0.4.3"
    return o


def main(argv=None):
    o = parse_args(argv)
    agvs = [int(x) for x in str(o.agv).split(",") if x.strip()]

    bridges = []
    for i, agv in enumerate(agvs):
        mac = o.mac if (o.mac and len(agvs) == 1) else "02:fa:ce:00:%02x:%02x" % (0, agv)
        bridges.append(Bridge(agv, o.listen + i, o, mac, "%02d" % agv))
    dups = []
    if o.dup_id:
        for i, agv in enumerate(agvs):
            dups.append(Bridge(agv, o.listen + 100 + i, o, "02:fa:ce:01:%02x:%02x" % (1, agv), "%02d#2" % agv))
    for b in bridges:
        b.start()

    done = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: done.set())
    signal.signal(signal.SIGINT, lambda *_: done.set())
    if dups:
        if not done.wait(o.dup_delay):
            for b in dups:
                b.start()
    done.wait()
    for b in bridges + dups:
        b.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
