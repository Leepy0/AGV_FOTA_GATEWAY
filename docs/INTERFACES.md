# 접점 규격 (참조)

이 서버가 의존하는 외부 규격과 그 출처, 그리고 이 서버가 내주는 REST API 입니다. 원본이 바뀌면 여기와 `Gateway.Core` 를 함께 고칩니다.
흐름과 화면은 [SYSTEM_FLOW.md](SYSTEM_FLOW.md) 에 있습니다. 기준: 게이트웨이 0.3.0, 브리지 0.5.2.

| 규격 | 원본 | 이 저장소의 구현 |
|---|---|---|
| 브리지 UDP 패킷 (헤더 10B, 타입 0x01~0x41) | `AGV_Wifi_Bridge/main/proto.h` (v0.5.2) | `Gateway.Core/Bridge/BridgePacket.cs` |
| 브리지 heartbeat(0x10)·INFO(0x11/0x12), `mac`·`dsp`·`dspb`·`dsp_st`·`staged` | `AGV_Wifi_Bridge/main/net_bridge.c`, `dsp_ota.c` dsp_ota_status_json | `Gateway.Core/Bridge/BridgeStatus.cs` (INFO 와 heartbeat 를 합쳐 보관) |
| 호기 endpoint·번호 충돌 판정 | 이 저장소 (결정 2A) | `Gateway.Core/Bridge/BridgeOrigin.cs`, `Gateway.Server/Services/BridgeUdpService.cs` |
| 브리지 `dsp_st=nopart` (dspfw 파티션 없음 → `no_partition` 거부) | `AGV_Wifi_Bridge/main/dsp_ota.c` | `JobService` 가 작업 시작 전에 거부, 호기 현황 ⚠ 표시 |
| **메인보드 OTA B안** DSP_OTA_START(0x32) → DSP_OTA_RESULT(0x33) / DSP_OTA_APPLY(0x34) | `AGV_Wifi_Bridge/docs/DSP_OTA_DESIGN.md`, `main/dsp_ota.c` | `Gateway.Core/Fota/DspOtaClient.cs` (재전송, heartbeat 판정, 오류 안내 `BridgeErrors`) |
| 브리지 자체 OTA OTA_START(0x30) → OTA_RESULT(0x31) | `AGV_Wifi_Bridge/main/ota.c` | `Gateway.Core/Fota/EspOtaClient.cs` |
| `.dspfw` 이미지 파일 (헤더 32B + 앱 헤더 + 블록) | `AGV_Wifi_Bridge/tools/dsp_pack.py`, `dsp/common/dsp_image.c` | `Gateway.Core/Fota/DspfwImage.cs` (블록·`app_crc` 까지 검사), `DspVersion.cs` (1~8자) |
| 브리지 프레임 제한: ETX(0x03) 프레이밍, 256B | `AGV_Wifi_Bridge/main/uart_bridge.h` | `AgvFrame.BridgeFrameMax` |
| AGV↔ACS 프레임 (STX, BCC, ETX, 종류/PAN/MSG/AGV) | AGV 통신 프로토콜 레퍼런스 | `Gateway.Core/Agv/AgvFrame.cs` |
| ACS TCP 소켓 | **미확인** — 기존 ZigBee 게이트웨이 규격 필요 | `Gateway.Server/Services/AcsRelayService.cs` (프레임 그대로 통과한다고 가정), 하향 `F`·`R` 차단·허용 IP `Gateway.Core/Acs/AcsRelayPolicy.cs` |
| ACS 호기 소속 REST (GET, 응답 형식 **미확정** — 임시 매핑) | 기존 ACS 서버 | `Gateway.Core/Acs/AcsInventoryMapper.cs`, `Gateway.Core/Groups/AcsMerge.cs`, `appsettings AcsInventory.Fields` ([ACS_API.md](ACS_API.md)) |
| 서버 업데이트 출처 (GitHub 릴리스 매니페스트·API) | GitHub | `Gateway.Core/Update/UpdateFeed.cs` (https 의 github.com · api.github.com 만), 교체 배치 `SelfUpdateScript.cs` |
| 서버 데이터베이스 (SQLite `data/gateway.db`, 스키마 v3) | 이 저장소 | `Gateway.Server/Data/GatewayDb.cs` (Migrate 에 버전 단계 추가로만 변경) |

## 브리지와의 약속

- 브리지는 설정된 `server` IP 에서 온 하향 패킷만 받고, 상향 패킷을 그 IP 의 `server_port`(50000) 로 보낸다. 게이트웨이는 호기의 endpoint(송신 IP:포트)로 하향 송신한다.
- endpoint 는 살아 있는 동안(최근 `OfflineAfterSec` 안에 패킷) 바뀌지 않는다. 다른 주소에서 같은 호기 번호가 오면 그 패킷을 버리고 충돌로 기록한다. 기존 endpoint 가 오프라인이거나 MAC 이 같으면 새 주소로 옮긴다. 호기 번호 1~99 밖은 버린다.
- 충돌 중(마지막 충돌 패킷 뒤 `OfflineAfterSec`×2)에는 OTA_START·DSP_OTA_START·DSP_OTA_APPLY·CFG_CMD 를 보내지 않는다. MAIN_DOWN·PING·INFO_REQ 는 기존 endpoint 로 보낸다.
- 서버 하향 패킷을 받으면 브리지는 자기 새 펌웨어(`ota=pending`)·원격 설정(`cfg=trial`)을 확정한다. 게이트웨이는 heartbeat·INFO 에서 이 상태를 보면 PING 을 호기당 5초에 한 번 보낸다(충돌 중 제외).
- MAIN_DOWN 은 256B 를 넘으면 브리지가 버린다. B안은 MAIN_DOWN 으로 펌웨어를 보내지 않는다.
- 브리지 OTA URL 과 스테이징 URL 의 호스트는 **브리지에 설정된 server IP 와 같은 IPv4 리터럴**이어야 한다(`http(s)://a.b.c.d[:port]`). 게이트웨이 `PublicBaseUrl` 도 이 규칙을 따른다.
- seq 는 타입별 순번이다. PONG·CFG_RESULT 는 요청 seq 를 반송하므로 손실 집계에서 뺀다. heartbeat `up` 이 줄면(재부팅) 기준을 새로 잡는다.

## 브리지 UDP — 0.5.2 에서 바뀐 것

### heartbeat·INFO

| 필드 | 바뀐 점 |
|---|---|
| `mac` | heartbeat 에도 들어감(`fw` 바로 뒤, STA MAC, INFO 와 같은 소문자 `xx:xx:xx:xx:xx:xx`). 게이트웨이는 번호 충돌 판정에 쓴다 |
| `hb_ms` (설정) | 0(끔, 디버그용) 또는 100~2000ms 만. 범위 밖은 `set`·설정 페이지가 거부, 예전 저장값은 부팅 때 기본값 1000 으로 |
| `dsp` · `dspb` · `dsp_build` · `staged.ver` | JSON 이스케이프해서 보냄 |

게이트웨이는 INFO 를 받으면 직전 상태에 INFO 의 키만 덮어쓰고, heartbeat 를 받으면 전체를 새 값으로 하되 heartbeat 에 없는 `mac`·`ip` 는 직전 값을 남긴다.

### 메인보드 OTA JSON

| 패킷 | 필드 | 비고 |
|---|---|---|
| DSP_OTA_START (0x32) | `url` (199자 이하), `ver`, `crc32`, `size`, `job`, `apply` (0 = 스테이징만, 1 = 받은 뒤 바로 적용), `recover` (선택, `apply:1` 과 함께) | 게이트웨이는 늘 `apply:0` 을 보낸다 |
| DSP_OTA_APPLY (0x34) | `job` (스테이징 때와 같은 번호), `recover` (선택, 1 = 복구 적용) | 게이트웨이는 운용자가 **복구 적용** 을 켠 작업의 APPLY(재전송 포함)에만 `recover:1`. 앱 버전 `?` 만으로는 넣지 않음 |
| DSP_OTA_RESULT (0x33) | `job`, `stage`, `pct`, `ver`, `err` | `staged`·`done`·`fail` 은 같은 내용으로 약 300ms 간격 3회. 수신 단계 거부(`bad_request`·`url_*`·`busy`·`not_ready`)는 간격 없이 3회 |

`job` 번호: `(유닉스 초 % 10,000,000) × 100 + 호기` (브리지가 u32 로 읽음).

앱이 `U000` 에 2초 안에 답하지 않을 때 브리지 0.5.2 의 판단:

| 앱 상태 | `recover` | 브리지 동작 |
|---|---|---|
| 최근 3초 안에 앱 프레임이 옴 (살아 있으나 업데이트 진입 코드 없음) | 상관없음 | `app_no_fwup`. 부트로더로 가지 않음(중계 유지) |
| 앱이 조용함 | `1` | 부트로더로 바로 감. HELLO 3회에 답이 없으면 최대 60초 동안 HELLO 를 50ms 간격으로 보내며 메인보드 전원 재투입을 기다림(`wait_power`) |
| 앱이 조용함, 앱 버전 `?` | 없음 | 부트로더로 바로 감. HELLO 3회(약 4.5초)만, 실패하면 `no_bootloader` |
| 앱이 조용함, 앱 버전을 앎 | 없음 | `app_no_reply`. 앱 버전을 `?` 로 바꿈(중계 유지, 앱이 살아 있으면 재조회로 돌아옴) |

### 같은 job 의 중복 명령 (브리지 0.5.2)

| 받은 명령 | 브리지 동작 |
|---|---|
| 지금 받거나 굽는 job 의 START·APPLY | 무시 (보고 없음) |
| 이미 받아 둔 job 의 START | 다시 받지 않고 `staged` 를 다시 보고 (`apply:1` 이면 적용 단계에서 다시 판단) |
| 최근 10분 안에 결과가 있는 job (받기 실패, 적용 `done{ver}`·`fail{err}`) | 다시 받거나 굽지 않고 그 결과를 다시 보고 (3회) |
| 그 밖 (다른 job, 10분 초과, 재부팅 뒤) | 예전과 같음 (`job_mismatch`, job 0 의 `not_staged` 등) |

- job 0 은 중복으로 보지 않는다. 브리지는 가장 최근 결과 1건만 기억한다.
- 그래서 `done` 뒤에 재전송된 APPLY 에도 `not_staged` 가 아니라 같은 `done` 이 온다. 다시 보고된 `fail` 은 원래 job·err 를 가지므로 게이트웨이는 실제 실패로 처리한다.
- 실패한 적용을 다시 하려면 새 job 으로 스테이징부터 한다(게이트웨이는 작업마다 새 job 번호를 만든다).

### 브리지 OTA

| 패킷 | 내용 |
|---|---|
| OTA_START (0x30) | URL (ASCII). 8자 이상 255자 이하, `http` 로 시작, 호스트 = server IP |
| OTA_RESULT (0x31) | `stage`: `start` · `progress`(10% 단위) · `done` · `fail` · `busy`. 최종 결과(`done`·`fail`·`busy`)는 약 300ms 간격 3회. 성공하면 3회를 모두 보낸 뒤(약 0.8초) 재부팅 |

### 오류·진행 코드

메인보드 (DSP_OTA_RESULT `err`). 운용자용 안내 문구는 `BridgeErrors.Hint` 가 붙인다.

| 코드 | 언제 | 뜻 |
|---|---|---|
| `url_host` | 명령 접수·리다이렉트 | URL 호스트가 server IP 가 아님 (호스트명·사용자 정보·IPv6 도 거부) **새로** |
| `url_too_long` | 명령 접수 | URL 199자 초과, 잘라 쓰지 않음 **새로** |
| `bad_request` `busy` `not_ready` | 명령 접수 | JSON 오류 / 굽는 중·큐 가득 / 준비 전 |
| `no_partition` | 명령 접수 | `dspfw` 파티션 없음 |
| `esp_ota_busy` | 명령 접수·적용 시작 | 브리지 자체 OTA 중 |
| `http_init` `http_open` `http_status` `http_length` `http_read` `http_overrun` `http_short` | download | 파일 받기 실패 |
| `ver_invalid` | download·적용 시작 | 버전이 1~8자 `[0-9A-Za-z._-]` 가 아니거나 파일 헤더·앱 헤더 버전이 다름. 기존 스테이징·앱을 지우기 전에 거부 **새로** |
| `image_crc` | download | 헤더 CRC 오류 |
| `image_bad` | download·적용 시작 | 블록 구조 결함 또는 복원 CRC32 ≠ `app_crc`. 지우기 전에 거부 **새로** |
| `crc_mismatch` | download | 서버 `crc32` 와 다름 |
| `flash_erase` `flash_write` | download | 브리지 플래시 오류 |
| `not_staged` `stage_invalid` `job_mismatch` | 적용 시작 | 받아 둔 이미지 없음·깨짐·다른 job |
| `app_ng_01` … `app_ng_09` | enter | 앱이 진입 거부 (09 = 기타·안전 콜백 없음) |
| `app_no_fwup` | enter | 앱이 `U000` 에 답하지 않지만 최근 3초 안에 앱 프레임이 옴 — 업데이트 진입 코드가 없는 구 앱. `recover` 여도 부트로더로 가지 않음 **새로** |
| `app_no_reply` | enter·확인 | 앱이 `U000` 에 2초 안에 답하지 않고 앱 프레임도 없음(앱 버전을 알고 `recover` 가 없을 때, 중계를 끊지 않고 중단, 앱 버전을 `?` 로) / 적용 뒤 버전 조회에 답하지 않음 **뜻 바뀜** |
| `uart_busy` | enter | 메인보드 송신 대기열 가득, 또는 115200 전환 실패. 즉시 실패 **뜻 바뀜** |
| `no_bootloader` `boot_proto` | HELLO | 부트로더 무응답 / 프로토콜 불일치 |
| `erase` `stage_read` `stage_block` `write_timeout` `write` `program` `verify_block` `write_header` `verify` | 굽기 | 굽기 실패 (지운 뒤면 자동 재시도 2회 뒤 보고) |
| `ver_mismatch` | 확인 | 앱이 답한 버전이 이미지와 다름 (브리지는 정확히 비교) |

| 진행 `stage` | 뜻 |
|---|---|
| `download` `staged` `start` `enter` `erase` `progress` `verify` `done` `fail` | 예전과 같음 |
| `wait_power` **새로** | 복구 적용에서 부트로더를 기다리며 전원 재투입 안내. 최대 60초, 10초마다 반복 보고 |
| `retry` **새로** | 지운 뒤 실패해 HELLO 부터 다시. `pct` = 재시도 번호(1·2) |

브리지 자체 OTA (OTA_RESULT `err`):

| 코드 | 뜻 |
|---|---|
| `bad_url` **새로** | URL 형식 오류 또는 256자 이상 |
| `url_host` **새로** | URL 호스트가 server IP 가 아님 |
| `mainboard_programming` · `mainboard_staging` **새로** | 메인보드를 굽거나 이미지를 받는 중 |
| `no_mem` **새로** | 메모리 부족으로 시작 못 함 |
| `begin …` · `perform …` · `finish …` · `project mismatch (…)` | 받기·검증 실패 |
| (`stage` 가 `busy`) | 브리지 OTA 가 이미 진행 중 |

### 게이트웨이 쪽 처리 (요약)

- START·APPLY·OTA_START 뒤 5초 동안 수신 흔적이 없으면 같은 내용을 최대 3회 다시 보낸다. 적용 중 늦게 온 스테이징 보고(`staged`·`download`)는 수신 흔적으로 보지 않는다.
- 재전송 뒤 오는 `busy` 는 앞 명령을 처리하는 중에 닿은 것으로 보고 무시한다. 재전송 뒤 job 0 의 `fail`(`not_staged`)은 스테이징 소실로 보고 실패 처리한다. 단 heartbeat 가 이미 이 job 의 적용 완료를 보이면 완료로 본다.
- 최종 보고가 유실돼도 heartbeat 로 판정한다. 스테이징: `staged.job` = job. 적용: `dsp` 앞 8자 = 이미지 버전, `dsp_st=idle`, `staged=null`. 브리지 OTA: 슬롯 변경·`ota=pending`·fw 변경·`up` 감소.
- 같은 최종 보고가 3회 와도 한 번만 처리한다.
- 대기 시간: 스테이징 무보고 3분, 적용 무보고 90초, 브리지 OTA 결과 3분, 재부팅 90초, 확정 PING 1초 간격 최대 20초 (`OtaTimeouts`).

## ACS 로 넘기는 것, ACS 에서 받는 것

- 상향 프레임 중 **STX·ETX·BCC 가 모두 맞는 E(이벤트)와 A(ACK)만** ACS 로 중계한다(`AgvFrame.IsForAcs`, 화이트리스트). 기존 ACS 는 규격에 없는 종류를 받으면 오동작할 수 있으므로 브리지용 응답(`R`), 위치 프레임(`P`, 제안) 등 새 종류는 서버 안에서만 소비한다.
- ACS 하향 중 종류 **`F`·`R` 은 메인보드로 넘기지 않고 버린다**(`AcsRelayPolicy.IsBlockedDown`, 10초에 한 번 경고). Client·Server 모드 모두.
- Server 모드는 `Gateway.Acs.AllowedRemotes`(IP 문자열 배열)에 있는 원격 IP 만 받는다. 비우면 모두 받는다. IP 를 그대로 비교한다(IPv4-mapped IPv6 는 IPv4 로). 해석되지 않는 항목은 무시하고, 항목이 있는데 하나도 해석되지 않으면 모두 거부한다.
- 연결 소켓에 TCP keepalive(10초, 3초 간격, 3회)를 건다. Server 모드는 새 허용 접속이 오면 기존 연결을 닫고 교체한다.

## REST API

화면과 같은 기능을 스크립트로 쓰는 API 입니다. 경로는 모두 `http://<서버>:8080` 기준입니다.

### 공통

- 오류 응답은 HTML 이 아니라 **상태 코드 + JSON 문자열 사유**입니다. 400 잘못된 입력·본문 해석 실패, 404 없음, 409 사용 중·번호 충돌, 500 서버 오류.
- `/api` 의 POST·PUT·DELETE·PATCH 는 브라우저가 **다른 출처**에서 보내면 403 입니다. `Sec-Fetch-Site: cross-site` 이거나 `Origin` 의 scheme·호스트·포트가 요청과 다르면(`Origin: null` 포함) 거부합니다. `Origin`·`Sec-Fetch-Site` 가 없는 요청(curl·스크립트)은 영향이 없습니다.
- `Host` 헤더가 이 서버의 이름이 아니면 **모든 경로·모든 메서드**가 403 입니다(DNS 리바인딩 방어). 허용: IP 주소(IPv4·IPv6), `localhost`, 이 PC 이름·FQDN, `Gateway.AllowedHostNames`(`"*"` = 검사 안 함). 사유는 `접속 주소(Host) … 가 허용 목록에 없음 — Gateway.AllowedHostNames 에 추가` 입니다. `Host` 가 없는 요청은 허용합니다.
- 시각은 ISO 8601 입니다. 오프셋이 없으면 서버 지역 시각으로 봅니다. 응답의 시각은 UTC 입니다.

### 배포 작업

| 요청 | 내용 | 응답 |
|---|---|---|
| `GET /api/jobs` | 작업 목록 (최근 것 먼저) | 200 |
| `GET /api/jobs/{id}` | 작업 하나 | 200 / 404 |
| `POST /api/jobs` | 작업 만들기 (아래 본문) | 200 작업 JSON / 400 |
| `POST /api/jobs/{id}/approve` | 적용 대기 작업의 적용 승인. **부르기 전에 대상 AGV 정지를 확인** | 200 / 400 적용 대기가 아님·마감 지남 / 404 |
| `POST /api/jobs/{id}/cancel` | 취소. `?queued=true` 면 이 작업이 끝나자마자 이어서 시작될 대기 작업도 함께 취소 | 200 / 404 |
| `DELETE /api/jobs/{id}` | 끝난 작업 기록 삭제 | 200 / 404 없는 작업 / 409 끝나지 않은 작업 (먼저 취소) |

`POST /api/jobs` 본문:

```json
{ "name": "B제품 / 3F / L4 전체", "firmwareId": "…", "targets": [31, 45],
  "scheduledAt": "2026-10-12T22:30:00", "notAfter": "2026-10-12T23:30:00",
  "stopOnFailure": true, "applyMode": "Approve", "recover": false }
```

| 필드 | 필수 | 뜻 |
|---|---|---|
| `firmwareId` | ✔ | `GET /api/firmware` 의 `id` |
| `targets` | ✔ | 호기 번호 1~99, 하나 이상 |
| `name` | | 비우면 "메인보드 1.0.3" 처럼 |
| `scheduledAt` | | 비우면 지금 바로. 1분 넘게 지난 시각은 400 |
| `notAfter` | | 마감 시각. 지나면 남은 호기 건너뜀. 이미 지났거나 `scheduledAt` 보다 앞서면 400 |
| `stopOnFailure` | | 기본 `true` |
| `applyMode` | | `Approve`(기본, 메인보드 스테이징 뒤 승인하면 적용) 또는 `Immediate`(바로 적용). 대소문자 무시, 숫자·다른 값은 400. 브리지 작업은 늘 `Immediate` |
| `recover` | | 기본 `false`. `true` = 메인보드 **복구 적용**(앱이 멈춘·지워진 호기용, 모든 대상 APPLY 에 `recover:1`). 브리지 펌웨어면 400. **AGV 정지 필수** — 부트로더가 바로 안 잡히면 최대 60초 동안 전원 재투입을 기다림 |

400 사유: 본문 없음, `firmwareId` 없음, `targets` 비었거나 1~99 밖, `applyMode` 잘못됨, 시각 형식 오류, 지난 예약·마감, 없는 펌웨어(`펌웨어 없음`), 지우는 중인 펌웨어, 브리지 작업에 `recover: true`(`복구 적용은 메인보드 작업만 고를 수 있습니다`).

작업 JSON 의 새 필드:

| 필드 | 뜻 |
|---|---|
| `state` | `Scheduled` · `Running` · `AwaitingApproval`(적용 대기, **새로**) · `Done` · `Failed` · `Cancelled` |
| `applyMode` | `Approve` · `Immediate` |
| `recover` | 복구 적용 작업인지 (`true`·`false`) |
| `notAfter` | 마감 시각 (없으면 null) |
| `approvedAt` | 적용 승인 시각. 서버를 다시 켜면 null 로 풀림 |
| `results[].state` | `Waiting` · `Running` · `Staged`(스테이징됨, **새로**) · `Done` · `Failed` · `Skipped` |
| `results[].stageJob` | 스테이징 때 쓴 브리지 job 번호 (적용도 같은 번호). 0 = 없음 |
| `results[].committed` | APPLY·OTA_START 를 이미 보냄 (취소해도 브리지는 끝까지 진행) |

### 호기

| 요청 | 내용 | 응답 |
|---|---|---|
| `GET /api/agvs` | 호기 목록과 상태. 새 필드 `conflict`(번호 충돌 중), `conflictEndPoint`, `conflictAt`, `conflictCount` | 200 |
| `POST /api/agvs/{n}/send` | MAIN_DOWN 수동 전송 `{ "data": "<STX>…<ETX>" }`. 256바이트 이하. 종류 `F`·`R` 프레임은 400 | 200 / 400 / 404 endpoint 없음 / 409 번호 충돌 |
| `POST /api/agvs/{n}/cfg` | CFG_CMD `{ "data": "set ip …" }`. 512바이트(UTF-8) 이하 | 200 / 400 / 404 / 409 번호 충돌 |
| `POST /api/agvs/{n}/info` | INFO 요청 | 200 / 400 / 404 |

- 세 요청 모두 호기 번호가 1~99 밖이면 400 입니다. `send`·`cfg` 는 `data` 가 비어도 400 입니다.

### 펌웨어

| 요청 | 내용 | 응답 |
|---|---|---|
| `GET /api/firmware` | 목록 | 200 |
| `POST /api/firmware` | `multipart/form-data` 필수. `kind` = `dsp`(빈 값도) 또는 `esp`, 파일 하나, 선택 `version`·`project`·`notes` | 200 / 400 |
| `DELETE /api/firmware/{id}` | 삭제. 끝나지 않은 작업(대기·실행 중·적용 대기)이 쓰면 거부 | 200 / 404 / 409 (본문에 작업 이름) |
| `GET /fw/{id}/{file}` | 브리지가 받아 가는 파일 | 200 / 404 |

업로드 400 사유: 폼이 아님, `kind` 잘못됨, 파일 없음, `.dspfw` 형식·CRC·블록 구조 오류, 메인보드 버전이 1~8자 `[0-9A-Za-z._-]` 가 아님, 입력 버전과 파일 버전이 다름, ESP 이미지가 아님·프로젝트가 `agv_wifi_bridge` 가 아님. 저장 파일명은 영문·숫자·`._-` 60자 이하로 바뀝니다.

### 그 밖

| 요청 | 내용 |
|---|---|
| `GET /api/update` · `POST /api/update/check` · `/download` · `/apply` · `/rollback` | 서버 자체 업데이트 |
| `GET /api/health` | `{ ok, version, channel, time }` — 자체 업데이트 배치가 60초 기동 확인에 씀 |
| `GET /api/positions[/{n}]`, `POST /api/positions/{n}` | 호기 위치, 시험 주입 (1~99 밖은 400) |
| `GET /api/acs/status` · `POST /api/acs/push` · `GET /api/acs/preview` | ACS 위치 보고 |
| `GET /api/groups` · `/api/groups/list` · `/api/groups/agvs?path=` · `/api/groups/export.csv`, `POST /api/groups/import`, `GET·PUT·DELETE /api/agvs/{n}/group` | 구역 ([ACS_API.md](ACS_API.md)) |
| `GET /api/acs/inventory/status`, `POST /api/acs/inventory/preview` · `/pull` | ACS 소속 받기 |
| `GET /api/db` · `GET /api/db/backup` | DB 정보(`schema` = 3)·백업 |

## 메인보드(AGV) OTA 결정 (2026-10-09, 2026-10-10 점검 반영)

**B안 채택.** 서버는 메인보드와 직접 프레임을 주고받지 않는다. 브리지에 `.dspfw` 를 스테이징시키고(0x32), 적용 명령(0x34)을 보내면
브리지가 메인보드를 부트로더로 보내 UART 로 굽고 단계를 보고한다(0x33). 브리지→메인보드 `F…V000`/`U000` 과 메인보드→브리지 `R` 프레임은
브리지가 만들고 소비하므로 서버에 올라오지 않는다. `DSP_OTA` 저장소(서버→AGV F 프레임 블록 전송)는 보관으로 돌렸다.

- **적용 전 정지 확인은 두 겹**이다(결정 1B). 서버는 전 호기 스테이징 뒤 작업을 적용 대기(`AwaitingApproval`)에 두고, 운용자가 정지를 확인하고 승인하면 한 대씩 APPLY 한다. 메인보드 앱은 `U000` 을 안전 콜백으로 거부할 수 있고, 콜백이 없으면 늘 거부한다.
- B안에는 확정 절차가 없다. 부트로더는 앱으로 점프할 때마다 부트 카운터를 올리고, 앱은 메인 루프에 들어가면 `fwup_boot_ok()` 로 0 으로 돌린다(자기 호기 `F` 프레임을 처리할 때도 자동). 카운터가 3 을 넘으면 부트로더가 업데이트 모드에 머물러 새 이미지를 기다린다 (`AGV_Wifi_Bridge/dsp/boot/boot_main.c`, `dsp/app/app_fwupdate.c`). 게이트웨이는 관여하지 않는다.
- 기동 불능 앱 복구(결정 8): 앱 워치독은 필수다. 부트로더는 앱으로 점프하기 전 200ms 동안 HELLO 만 받는 창을 열고, 게이트웨이는 운용자가 **복구 적용** 을 켠 작업에서 `recover:1` 로 적용한다(앱 버전 `?` 만으로는 붙이지 않음).
