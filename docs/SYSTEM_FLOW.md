# AGV FOTA 시스템 — 전체 흐름

이 문서는 AGV 펌웨어 무선 업데이트(FOTA) 시스템 전체를 한 번에 이해하기 위한 안내서입니다.
무엇이 있고, 서로 무엇을 주고받고, 업데이트·중계·연동이 어떤 순서로 흐르고, 실패하면 어떻게 되돌리는지를 다룹니다.

- 설치·운영 절차는 [DEPLOY.md](DEPLOY.md) 를 봅니다.
- 기준: 게이트웨이 서버 0.3.0(csproj), 브리지 펌웨어 0.5.1. 코드와 설정을 열어 확인한 값만 적었습니다(2026-10-10).
- 그림 원본은 `docs/diagrams/*.mmd` 입니다.

**목차**

1. [한눈에 보기](#1-한눈에-보기)
2. [구성 요소와 역할](#2-구성-요소와-역할)
3. [통신 경로와 포트](#3-통신-경로와-포트)
4. [평상시 흐름](#4-평상시-흐름)
5. [브리지 OTA](#5-브리지-ota)
6. [메인보드 OTA (B안)](#6-메인보드-ota-b안)
7. [배포 작업](#7-배포-작업)
8. [구역 (호기 소속)](#8-구역-호기-소속)
9. [서버 자체 업데이트](#9-서버-자체-업데이트)
10. [데이터](#10-데이터)
11. [화면 안내](#11-화면-안내)
12. [코드 지도](#12-코드-지도)
13. [미확정 항목과 다음 단계](#13-미확정-항목과-다음-단계)

---

## 1. 한눈에 보기

![전체 구성](images/diagrams/architecture.png)
*브라우저·ACS·GitHub 는 게이트웨이 PC 와, AGV 의 브리지는 UDP 50000 으로 게이트웨이와 통신한다.*

- 사내 PC 한 대에서 도는 **게이트웨이 서버**가 AGV 마다 달린 **Wi-Fi 브리지**와 UDP 로 통신합니다.
- 운용자는 브라우저로 펌웨어를 올리고, 대상 호기와 시각을 정해 **한 대씩 순차로** 배포합니다.
- **브리지 펌웨어**는 브리지가 서버에서 직접 HTTP 로 받아 교체합니다.
- **메인보드 펌웨어**는 브리지가 받아 두었다가(스테이징) 메인보드의 상주 부트로더로 UART 를 통해 굽습니다(B안).
- 브리지는 서버 IP 하나로만 통신하므로, ACS 와의 프레임 중계·REST 연동도 같은 서버가 맡습니다.

| 저장소 | 내용 | 상태 |
|---|---|---|
| `agv_fota_gateway` | 게이트웨이 서버. ASP.NET Core .NET 10 Blazor Server, Windows 서비스, 런타임 포함 단일 exe | csproj `<Version>` 0.3.0. 정식 릴리스 태그는 v0.2.0 까지 |
| `AGV_Wifi_Bridge` | 브리지 펌웨어(ESP32-S3, ESP-IDF v6.1) + `dsp/`(메인보드 부트로더·앱 지원 코드) + `tools/`(`dsp_pack.py`, `dsp_flash.py`, `BridgeTest.cs`, `flash.bat`) | 0.5.1 |
| `DSP_OTA` | 이전 메인보드 OTA 방식(서버가 AGV 로 프레임 블록을 보내는 방식) | 보관됨. B안으로 대체 |

---

## 2. 구성 요소와 역할

### 2.1 메인보드 (TMS320F2808) — 부트로더 + 앱

- AGV 의 주행 제어 보드입니다. 브리지와 J4(SCI-A, 115200)로 연결됩니다.
- 플래시 섹터 A 에 **상주 부트로더**가 있습니다. OTA 로 지우거나 바꾸지 않습니다.
- 섹터 B·C·D 에 **앱**이 있습니다. OTA 는 이 영역만 교체합니다.
- 앱에는 OTA 지원 코드(`dsp/app/app_fwupdate.c`)가 들어가야 합니다. 브리지의 버전 조회·업데이트 진입 요청에 답합니다(6.6절).
- 처음 한 번은 호기마다 유선(JTAG)으로 부트로더와 재링크한 앱을 굽습니다.
- 이어서 브리지로 `.dspfw` 를 한 번 적용해 앱 헤더를 채웁니다. 그 전에는 앱이 돌지 않습니다(6.6절).

### 2.2 Wi-Fi 브리지 (ESP32-S3)

- 기존 ZigBee 통신보드를 대체합니다.
- 메인보드 UART 프레임을 그대로 서버와 UDP 로 중계합니다. ACK·재전송은 기존처럼 메인보드가 관리합니다.
- RFID 리더 데이터를 메인보드로 바이패스하고, 네트워크로도 브로드캐스트합니다.
- 1초마다 heartbeat(상태 JSON)를 서버로 보냅니다.
- 자기 펌웨어를 OTA 로 교체합니다(두 슬롯 `ota_0`/`ota_1`, 180초 자동 롤백).
- 메인보드 이미지를 `dspfw` 파티션(384KB)에 받아 두었다가 부트로더로 굽습니다.
- 설정은 USB 콘솔, 서버 원격 명령(UDP), 휴대폰 BLE 로 바꿉니다.

### 2.3 게이트웨이 서버 (AGV FOTA Gateway)

Windows 서비스 `AgvFotaGateway` 로 돕니다. 서비스 하나하나의 역할은 다음과 같습니다(`src/Gateway.Server/Program.cs` 에 등록).

| 서비스 | 하는 일 |
|---|---|
| `BridgeUdpService` | UDP 50000 으로 브리지 패킷을 받고 보냅니다. UDP 50001 RFID 는 통신 로그에만 남깁니다. UDP 50000 을 열 수 없으면(다른 프로그램이 사용 중 등) 10초마다 다시 시도하고 화면에 배너를 띄웁니다 |
| `AgvRegistry` | 호기별 최신 heartbeat·주소·카운터를 들고 있습니다. 마지막 접속을 DB 에 남깁니다 |
| `JobService` | 배포 작업을 예약·순차 실행합니다. 브리지 OTA·메인보드 OTA 클라이언트를 부릅니다 |
| `FirmwareStore` | 업로드한 펌웨어를 검사해 `data/firmware/` 에 둡니다. 브리지가 받아 가도록 `/fw/` 주소로 내줍니다 |
| `AcsRelayService` | ACS TCP 중계. 기본 꺼짐 |
| `AcsPositionPushService` | 호기 위치를 ACS 로 REST 보고. 기본 꺼짐 |
| `AcsInventoryPullService` | ACS 에서 호기 소속(구역)을 REST 로 받기. 기본 꺼짐 |
| `AgvGroupStore` | 구역(호기 소속) 저장과 트리 |
| `AgvPositionStore` | 호기 위치. RTLS 결과가 들어갈 자리이며 지금은 시험 주입만 있습니다 |
| `UpdateService` | 서버 자체 업데이트(확인·받기·적용·되돌리기) |
| `TraceLog` | 통신 로그 최근 300줄(메모리) |
| `GatewayDb` | SQLite 데이터베이스 `data/gateway.db` |
| `FileLoggerProvider` | 파일 로그 `data/logs/gateway-날짜.log`(14일 보관) |

### 2.4 ACS (기존 교통 제어 서버)

- 지금까지 ZigBee 게이트웨이와 TCP 로 AGV 프레임을 주고받던 서버입니다.
- 게이트웨이와는 세 갈래로 연결됩니다: TCP 중계, 위치 보고(REST), 호기 소속 받기(REST). 4.4절에서 다룹니다.
- 세 갈래 모두 규격이 확정되지 않아 기본값은 꺼짐입니다.

### 2.5 브라우저

- PC·태블릿·휴대폰에서 `http://<게이트웨이 PC IP>:8080` 으로 접속합니다.
- 로그인은 없습니다(사내망 전제).
- 화면 7개: 호기 현황, 구역, 펌웨어, 배포 작업, 통신 로그, 서버 업데이트, 설정. 같은 기능을 REST API(`/api/...`)로도 씁니다.

---

## 3. 통신 경로와 포트

![통신 경로와 포트](images/diagrams/comm-ports.png)
*브리지는 UDP 50000 으로 상향, 서버는 브리지가 보낸 주소로 하향한다. OTA 파일만 브리지가 TCP 8080 으로 받아 간다.*

| 포트 | 방향 (게이트웨이 PC 기준) | 용도 | 설정 |
|---|---|---|---|
| TCP 8080 | 인바운드 | 웹 화면, REST API, 브리지가 받아 가는 펌웨어 파일 `/fw/{id}/{file}` | `Urls` (`http://0.0.0.0:8080`) |
| UDP 50000 | 인바운드(상향)·아웃바운드(하향) | 브리지 패킷 전부 | `Gateway.UdpPort`, 브리지 `server_port`·`local_port` |
| UDP 50001 | 인바운드 | 브리지 RFID 원본(진단용 로그) | `Gateway.RfidPort` (0 = 끔), 브리지 `rfid_dst`·`rfid_port` |
| TCP 6000 | 아웃바운드(`Client`) 또는 인바운드(`Server`) | ACS 중계. 기본 꺼짐, 포트는 임시값 | `Gateway.Acs.*` |
| HTTP(S) | 아웃바운드 | ACS REST (위치 보고·소속 받기). 기본 꺼짐 | `AcsApi.BaseUrl`, `AcsInventory.BaseUrl` |
| HTTPS 443 | 아웃바운드 | 서버 새 버전 확인·받기 | `Update.BaseUrl` |

- `install-service.bat` 은 인바운드 **TCP 8080** 과 **UDP 50000, 50001** 만 방화벽에서 허용합니다.
- `Urls`·`Gateway.UdpPort`·`Gateway.RfidPort` 포트를 바꾸거나 ACS 중계를 `Server` 모드로 쓰면 방화벽 규칙을 직접 추가해야 합니다.

### 3.1 브리지 UDP 패킷

헤더 10바이트 + payload(최대 512바이트), 리틀 엔디언입니다.

| 오프셋 | 크기 | 내용 |
|---|---|---|
| 0 | 2 | magic `A5 5A` |
| 2 | 1 | ver = 1 |
| 3 | 1 | type (아래 표) |
| 4 | 1 | AGV ID (호기 번호) |
| 5 | 1 | flags (0) |
| 6 | 2 | seq — 타입별 순번. 서버가 누락을 셉니다 |
| 8 | 2 | payload 길이 |

| type | 이름 | 방향 | payload | 서버 처리 |
|---|---|---|---|---|
| `0x01` | MAIN_UP | 브리지 → 서버 | 메인보드 UART 프레임 원본 | 통신 로그 `MAIN>`, OTA 대기자에 전달, E·A 프레임은 ACS 로 |
| `0x02` | MAIN_DOWN | 서버 → 브리지 | 메인보드로 보낼 프레임(256바이트 이하) | ACS 지령·수동 전송 |
| `0x03` | RFID | 브리지 → `rfid_dst` | RFID 리더 원본 | 통신 로그 `RFID>` |
| `0x10` | STATUS | 브리지 → 서버 | heartbeat JSON (4.1절) | 호기 상태 갱신 |
| `0x11` | INFO_REQ | 서버 → 브리지 | 없음 | 호기 현황 "INFO 요청" |
| `0x12` | INFO | 브리지 → 서버 | JSON: agv, mac, ip, fw, part, ota, cfg, setup, up, dsp, dspb, dsp_build, dsp_st, staged | 호기 상태 갱신, 통신 로그 |
| `0x20` | PING | 서버 → 브리지 | 임의(서버는 현재 시각) | 브리지 OTA 확정, 호기 현황 "PING" |
| `0x21` | PONG | 브리지 → 서버 | PING 의 seq·payload 그대로 | 무시 |
| `0x30` | OTA_START | 서버 → 브리지 | 브리지 펌웨어 URL (ASCII) | 5절 |
| `0x31` | OTA_RESULT | 브리지 → 서버 | JSON: stage(start/progress/done/fail/busy), pct, bytes, ver, err | 5절 |
| `0x32` | DSP_OTA_START | 서버 → 브리지 | JSON: url, ver, crc32, size, job, apply | 6절 |
| `0x33` | DSP_OTA_RESULT | 브리지 → 서버 | JSON: job, stage, pct, ver, err | 6절 |
| `0x34` | DSP_OTA_APPLY | 서버 → 브리지 | JSON: job | 6절 |
| `0x40` | CFG_CMD | 서버 → 브리지 | 설정 명령 한 줄 (`cfg`, `set ip …`, `save`, `reboot` …) | 통신 로그 화면 "CFG_CMD 전송" |
| `0x41` | CFG_RESULT | 브리지 → 서버 | 명령 결과 텍스트 (seq = 요청 seq) | 통신 로그 `CFG>` |

브리지와 서버의 약속:

- 서버는 호기마다 마지막으로 패킷을 보낸 주소(IP:포트)를 기억했다가 그곳으로 하향을 보냅니다. 브리지 기본 송수신 포트는 50000 입니다.
- 브리지는 설정된 `server` IP 에서 온 하향만 받습니다. MAIN_DOWN·OTA·CFG·DSP 패킷은 AGV ID 가 같을 때만 처리합니다.
- 서버 하향을 하나라도 받으면 브리지는 미확정 새 펌웨어(`ota=pending`)와 원격 변경 설정(`cfg=trial`)을 확정합니다.
- Wi-Fi 가 끊긴 동안의 상향은 버퍼링하지 않고 버립니다. seq 는 계속 오르므로 서버의 **손실** 열에 누락 수가 쌓입니다.

### 3.2 AGV 프레임 형식

메인보드 UART 프레임은 기존 AGV-ACS 규격을 따릅니다. 브리지와 서버는 내용을 바꾸지 않고 그대로 옮깁니다.

```
STX  종류  PAN  MSG  AGV(2)  본문 …  ' '  BCC  ETX
0x02  1자   1자  1자  숫자2자          공백  1자  0x03
```

| 칸 | 내용 |
|---|---|
| 종류 | 프레임 종류 문자. ACS 로는 `E`(이벤트)와 `A`(ACK)만 넘깁니다 |
| PAN · MSG | PAN ID, 메시지 ID |
| AGV | 호기 번호 두 자리(`07`). 서버는 이 값으로 대상 호기를 고릅니다 |
| BCC | STX·ETX 를 뺀 바이트(끝 공백 포함) 합의 하위 4비트를 ASCII-HEX 한 글자로 |

예: 브리지가 07호기 앱에 버전을 묻는 프레임 `<STX>F3107V000 7<ETX>`.

- 브리지는 메인보드 수신을 **ETX 기준**으로 자릅니다. ETX 없이 수신이 끊기면(115200bps 기준 약 20ms) 받은 만큼 보냅니다.
- 한 프레임은 **256바이트**를 넘으면 안 됩니다. 서버도 256바이트가 넘는 MAIN_DOWN 은 보내지 않습니다.
- `F`(브리지 → 앱)와 `R`(앱 → 브리지)은 메인보드 OTA 용 새 종류 문자입니다. 브리지가 만들고 소비하므로 서버에 올라오지 않습니다. 프로토콜 문서 등록은 아직입니다(13절).

---

## 4. 평상시 흐름

![평상시 중계 흐름](images/diagrams/normal-relay-seq.png)
*heartbeat 는 1초마다, RFID 는 메인보드로 바로 바이패스, 메인보드 프레임은 서버를 거쳐 ACS 와 오간다.*

### 4.1 heartbeat (STATUS 0x10)

- 주기: 1초. 브리지 설정 `hb_ms`(기본 1000, 0 = 끔)로 바꿉니다.
- Wi-Fi 가 연결돼 있을 때만 보냅니다.

| 필드 | 뜻 | 화면 (호기 현황) |
|---|---|---|
| `fw` | 브리지 펌웨어 버전 | 브리지 fw |
| `part` | 실행 중인 슬롯 (`ota_0`/`ota_1`) | 슬롯 |
| `ota` | `pending`(새 펌웨어 미확정) · `valid` · `busy`(OTA 중) | ota |
| `up` | 브리지 가동 시간(초) | 상세 · 가동 |
| `rssi` · `ch` · `bssid` | Wi-Fi 신호·채널·AP | RSSI, 상세 · Wi-Fi |
| `disc` · `reason` | Wi-Fi 끊김 횟수, 마지막 끊김 사유 | 상세 · Wi-Fi (끊김 횟수만. `reason` 은 heartbeat 원문에서) |
| `m_rx` · `m_tx` · `r_rx` | 메인보드 수신·송신 프레임 수, RFID 수신 수 | 상세 · UART |
| `u_err` | UART 오류 합계 | 상세 · UART |
| `n_off` · `n_err` | Wi-Fi 끊김 중 버린 상향 수, 송신 오류 수 | 상세 · 네트 |
| `heap` | 남은 메모리 | 상세 · 네트 |
| `cfg` | `saved` · `trial`(원격 변경 확인 대기) · `default` | cfg |
| `setup` | 무선 설정 모드(BLE·SoftAP) 열림 1/0 | cfg 옆 `(AP)` |
| `dsp` · `dspb` · `dsp_build` | 메인보드 앱 버전 · 부트로더 버전 · 빌드일. 모르면 `?` | 메인보드 앱 / 부트 |
| `dsp_st` | `idle` · `downloading` · `staged` · `programming` · `nopart`(파티션 없음) | 메인보드 열의 표시 |
| `staged` | 받아 둔 메인보드 이미지 `{job, ver}` 또는 `null` | 스테이징 |

오프라인 판정과 기록:

- 서버는 그 호기에서 UDP 50000 으로 **어떤 패킷이든** 받은 지 **5초**(`Gateway.OfflineAfterSec`)가 지나면 오프라인으로 봅니다.
- 마지막 접속은 DB(`agv_last_seen`)에 30초마다 남깁니다. 버전·슬롯·주소가 바뀌면 바로 남깁니다. 그래서 서버를 다시 켜도 "n분 전" 으로 보입니다.
- 오프라인이 되면 그 호기 위치 상태는 `LOST` 로 바뀝니다(1초마다 다시 판단).

### 4.2 메인보드 프레임 중계 (MAIN_UP / MAIN_DOWN)

1. 메인보드가 프레임을 보냅니다. 브리지는 ETX 를 받는 즉시 MAIN_UP 으로 서버에 보냅니다.
2. 브리지는 `R` 프레임(자기 질문에 대한 앱 응답)만 소비하고 나머지는 그대로 올립니다. `E`·`A` 프레임에서 그 호기의 PAN ID 를 배워 둡니다.
3. 서버는 받은 프레임을 통신 로그에 남깁니다. ACS 중계가 켜져 있으면 `E`·`A` 프레임만 ACS 로 넘깁니다.
4. ACS 가 보낸 지령 프레임은 프레임 안의 호기 번호로 대상을 골라 MAIN_DOWN 으로 보냅니다.
5. 브리지는 MAIN_DOWN 을 프레임 단위로 메인보드에 보냅니다. RFID 바이패스 데이터와 섞이지 않습니다.

통신 로그 화면의 **수동 전송**에서도 MAIN_DOWN 을 보낼 수 있습니다.

### 4.3 RFID 바이패스

1. RFID 리더가 브리지로 데이터를 보냅니다. 브리지는 5ms 무수신으로 한 덩어리를 자릅니다.
2. 브리지가 먼저 메인보드로 그대로 보냅니다. **Wi-Fi 상태와 무관**하게 동작합니다.
3. 이어서 `rfid_dst`(기본 `255.255.255.255:50001` 브로드캐스트)로 보냅니다.
4. 서버는 UDP 50001 에서 받아 통신 로그에만 남깁니다(`RFID>`).

메인보드 OTA 적용 중(브리지가 UART 를 독점하는 동안)에는 RFID 바이패스도 멈춥니다.
RFID 리더 보드레이트 기본값은 9600 이며, 현장 리더에 맞는지 확인이 필요합니다.

### 4.4 ACS 연동 세 갈래

![ACS 연동 세 갈래](images/diagrams/acs-flows.png)
*① 프레임 TCP 중계, ② 위치 보고 REST, ③ 소속 받기 REST. 셋 다 규격 확정 전이라 기본 꺼짐.*

| 갈래 | 서비스 | 기본값 | 동작 | 미확정 |
|---|---|---|---|---|
| ① TCP 중계 | `AcsRelayService` | `Gateway.Acs.Enabled=false`, `Mode=Client`, `127.0.0.1:6000` | 상향은 `E`·`A` 프레임만 그대로. 하향은 호기 번호로 MAIN_DOWN | 기존 ZigBee 게이트웨이의 소켓 규격(접속 방향, 프레임 구분, 호기 매핑) |
| ② 위치 보고 | `AcsPositionPushService` | `AcsApi.Enabled=false`, 켜도 `LogOnly=true` | 1초(`IntervalMs`)마다 바뀐 호기만 POST. LogOnly 면 통신 로그에 JSON 만 | REST 스키마 |
| ③ 소속 받기 | `AcsInventoryPullService` | `AcsInventory.Enabled=false` | 시작 3초 뒤, 이후 300초(`IntervalSec`)마다 GET → 구역 반영 | REST 스키마 |

- ① `Client` 는 서버가 ACS 에 접속하고 끊기면 3초(`ReconnectSec`) 뒤 다시 붙습니다. `Server` 는 ACS 의 접속 한 개를 기다립니다.
- ① 연결 중 ACS 로 보낼 프레임은 최대 512개까지 쌓고, 넘치면 오래된 것부터 버립니다. 끊긴 동안 올라온 프레임은 ACS 로 보내지 않고 버립니다.
- ② 실패하면 큐에 쌓지 않고 다음 주기에 최신값을 보냅니다. 연속 실패는 주기를 2배씩 늘려 최대 60초(`MaxBackoffSec`)까지 둡니다.
- ③ 실패하면 1분, 2분, 4분, 8분 간격으로 다시 시도합니다(주기보다 길어지지 않음).
- 위치 보고 본문·소속 응답의 임시 형식과 반영 지점은 [ACS_API.md](ACS_API.md) 에 있습니다.

---

## 5. 브리지 OTA

![브리지 OTA 순서](images/diagrams/bridge-ota-seq.png)
*OTA_START → 브리지가 HTTP 로 받기 → 재부팅 → 서버 PING 으로 확정. 확정이 없으면 180초 뒤 이전 슬롯으로 돌아간다.*

1. **펌웨어** 화면에 `agv_wifi_bridge.bin`(앱 이미지)을 올립니다. 서버가 이미지 안의 버전과 프로젝트명(`agv_wifi_bridge`)을 읽어 검사합니다. bootloader·partition-table·merged 이미지는 거부됩니다.
2. **배포 작업** 화면에서 그 펌웨어와 대상 호기·실행 시각을 골라 작업을 만듭니다(7절).
3. 작업이 그 호기 차례가 되면 서버가 온라인인지 봅니다. `ota=pending` 이 남아 있으면 PING 을 1초마다 최대 10초 보내 먼저 확정합니다.
4. 서버가 OTA_START(0x30)에 URL 을 담아 보냅니다: `http://<서버 IP>:<HTTP 포트>/fw/<펌웨어 id>/<파일>`.
   서버 IP 는 그 호기 쪽 네트워크 카드 주소를 자동으로 씁니다. `Gateway.PublicBaseUrl` 을 넣으면 그 값을 씁니다.
5. 브리지가 HTTP 로 받아 빈 슬롯에 씁니다. OTA_RESULT 로 `start` → `progress`(10% 단위) → `done` 을 보고합니다. 프로젝트명이 다르면 `fail` 입니다.
6. 브리지가 재부팅합니다. 이때 중계가 1~2초 끊깁니다. 새 슬롯은 `ota=pending` 상태로 뜹니다.
7. 서버가 heartbeat 에서 `pending` 을 보고 PING 을 보냅니다. 브리지가 새 펌웨어를 확정합니다(`ota=valid`).
8. 서버가 heartbeat 의 `fw` 를 이미지 버전과 비교해 같으면 완료로 기록합니다.

진행률은 시작 10%, 재부팅 대기 70%, 완료 100% 로 표시됩니다.

### 실패와 롤백

| 상황 | 서버 기록 (메시지) | 브리지 상태 | 복구 |
|---|---|---|---|
| 호기가 오프라인 | `호기 오프라인 (heartbeat 없음)` | 5초 안에 받은 패킷이 없음(받은 적이 없거나 끊김) | 브리지 `server` IP, 방화벽 UDP 50000 확인 |
| 시작 전 미확정이 풀리지 않음 | `브리지가 이전 업데이트를 확정하지 못함 (ota=pending 유지)` | `pending` | 브리지 재부팅 후 다시 |
| 받기 실패 | `브리지 OTA 실패: begin …` / `perform …` / `finish …` | 현재 펌웨어 그대로 | 방화벽 TCP 8080, `PublicBaseUrl` 확인 |
| 다른 프로젝트 이미지 | `브리지 OTA 실패: project mismatch (…)` | 현재 펌웨어 그대로 | 올바른 `agv_wifi_bridge.bin` |
| 이미 진행 중 | `브리지가 이미 OTA 진행 중` | 브리지 OTA 또는 메인보드 프로그래밍 중 | 끝난 뒤 다시 |
| 결과 보고 없음 | `OTA_RESULT 없음 (3분)` | 알 수 없음 | 통신 로그 확인 |
| 재부팅 뒤 무응답 | `재부팅 후 heartbeat 없음 (90초) — 180초 뒤 이전 펌웨어로 롤백됨` | 새 펌웨어가 서버와 통신 못함 → **180초 뒤 자동 롤백** | 롤백 뒤 원인 확인 |
| 확정 안 됨 | `확정(valid) 안 됨` (PING 뒤 15초) | `pending` | 180초 안에 PING. 아니면 롤백 |
| 버전 다름 | `버전 불일치: 기대 …, 실제 …` | 롤백됐거나 다른 이미지 | 확인 후 다시 |

> **주의 — 서버는 배포 작업 안에서만 PING 을 자동으로 보냅니다.**
> 작업 밖에서 `ota=pending` 이나 `cfg=trial` 이 보이면 180초 안에 **호기 현황 → 그 호기 행 → PING** 을 누릅니다.
> 통신 로그 화면에서 원격 설정(`save` → `reboot`)을 바꾼 뒤에도 같습니다. 누르지 않으면 브리지가 이전 펌웨어·설정으로 되돌아갑니다.

- 브리지 0.5.0 부터 파티션이 바뀌었습니다(OTA 슬롯 1.75MB + `dspfw` 384KB). OTA 로는 파티션이 바뀌지 않으므로 0.5.0 미만 브리지는 호기마다 USB 전체 굽기가 한 번 필요합니다.
- 구 파티션인 채 OTA 로 0.5.1 이 올라간 브리지는 `dsp_st=nopart` 로 보고합니다. 화면에 "⚠ 파티션 없음" 으로 보이고 메인보드 작업은 시작 전에 거부됩니다.

---

## 6. 메인보드 OTA (B안)

![메인보드 OTA 순서](images/diagrams/mainboard-ota-seq.png)
*① 스테이징: 브리지가 .dspfw 를 받아 보관. ② 적용: 앱 → 부트로더 → 지우기·쓰기·검증 → 앱 재기동 → 버전 확인.*

서버는 메인보드와 직접 프레임을 주고받지 않습니다. 브리지에 명령만 보내고, 브리지가 메인보드를 프로그래밍한 뒤 단계를 보고합니다.

### 6.1 스테이징과 적용을 나누는 이유

- 다운로드는 Wi-Fi 상태에 따라 오래 걸릴 수 있습니다. 받는 동안 브리지는 자기 플래시에만 쓰므로 메인보드는 계속 주행·중계해도 됩니다.
- 굽기는 브리지와 메인보드 사이의 UART 로 합니다. Wi-Fi 가 흔들려도 굽는 시간과 성공 여부에 영향이 없습니다.
- 받아 둔 이미지는 브리지 `dspfw` 파티션에 남습니다(재부팅 뒤에도). 굽기에 실패해도 다시 적용할 수 있습니다.
- AGV 를 세워야 하는 시간이 적용 단계만으로 줄어듭니다(설계 추정 20~30초, 실측 전).
- 브리지는 받는 동안 4KB 씩 지우며 기록합니다. 한 번에 지우면 중계가 멈추기 때문입니다.

**현재 서버 동작**: 호기마다 스테이징이 끝나면 곧바로 적용합니다. "여러 대를 미리 받아 두고 나중에 한꺼번에 적용" 하는 화면은 아직 없습니다.

### 6.2 단계

| stage (보고) | 하는 쪽 | 내용 | 서버 진행률 |
|---|---|---|---|
| `download` | 브리지 | `url` 을 HTTP 로 받아 `dspfw` 파티션에 기록. 20% 단위 보고 | 5~30% |
| `staged` | 브리지 | 파일 CRC·앱 헤더 검사 후 보관 완료. `ver` 포함 | 30% |
| `start` | 브리지 | 적용 시작 (0x34 수신) | 32% |
| `enter` | 브리지 → 앱 | 보고 뒤 `F…U000` 진입 요청. 앱이 정지 조건을 보고 `R…UOK00` 응답 → 부트플래그 기록 → 워치독 리셋 | 35% |
| — | 브리지 → 부트로더 | HELLO (1.5초 × 3회). 프로토콜·부트로더 버전 확인 | — |
| `erase` | 부트로더 | 섹터 B·C·D 지우기 (응답 대기 20초, 최대 3회 시도) | 40% |
| `progress` | 부트로더 | 최대 128워드 블록 쓰기, 쓰고 읽어 비교. 10% 단위 보고 | 45~90% |
| — | 부트로더 | 앱 헤더를 **맨 마지막에** 기록 | — |
| `verify` | 부트로더 | 앱 CRC 비교 | 92% |
| — | 브리지 → 앱 | RESET 뒤 `F…V000` 버전 조회 (1초 간격 최대 8회) | — |
| `done` | 브리지 | 앱이 답한 새 버전 보고. 받아 둔 이미지를 지움 | 100% |

서버는 마지막으로 두 가지를 확인합니다.

1. `done` 의 `ver` 가 이미지 버전과 같은가.
2. 30초 안에 heartbeat 의 `dsp` 가 새 버전이 되는가.

![서버가 호기 한 대를 처리하는 순서](images/diagrams/dsp-job-steps.png)
*서버는 시작 전에 온라인·dsp_st 를 검사하고, 스테이징 3분·적용 90초 동안 보고가 없으면 실패로 기록한다.*

- `job` 번호는 서버가 만듭니다: `(유닉스 초 % 10,000,000) × 100 + 호기`. 서버는 같은 `job` 번호의 보고만 받아들입니다.
- 스테이징 대기는 "보고가 3분 넘게 끊기면", 적용 대기는 "보고가 90초 넘게 끊기면" 실패입니다.

### 6.3 메인보드 메모리 배치와 부트로더

![메인보드 플래시·RAM 배치](images/diagrams/mainboard-flash-map.png)
*섹터 A 는 부트로더(교체 불가), 섹터 D·C·B 는 앱. RAM 끝 6워드는 부트 카운터와 부트플래그로 남겨 둔다.*

| 영역 | 주소 | 내용 |
|---|---|---|
| 앱 헤더 | 0x3E8000 – 0x3E803F (64워드) | magic, 앱 길이, 앱 CRC32, 빌드 시각, 버전, 헤더 CRC16. 패커가 채우고 맨 마지막에 씀 |
| 앱 codestart | 0x3E8040 | 부트로더가 여기로 점프 |
| 앱 코드 | 0x3E8042 – 0x3F3FFF | codestart 포함 앱 영역(0x3E8040 – 0x3F3FFF) 최대 49,088워드 |
| 부트로더 | 섹터 A 0x3F4000 – | OTA 로 지우지 않음. 부트로더 정보 블록 0x3F7F00 |
| CSM 비밀번호 | 0x3F7FF8 – 0x3F7FFF | 0xFFFF 유지 (0x0000 이면 영구 잠김) |
| 부트 카운터 | RAM 0x0007FA – 0x0007FB | 앱과 부트로더 링커에서 모두 제외 |
| 부트플래그 | RAM 0x0007FC – 0x0007FF | 앱과 부트로더 링커에서 모두 제외 |

![부트로더 판단 흐름](images/diagrams/bootloader-boot.png)
*부트플래그 → 앱 헤더·CRC → 부트 카운터 순서로 보고, 하나라도 걸리면 업데이트 모드에서 기다린다.*

1. 리셋되면 부트로더가 클럭(100MHz)과 플래시 API 를 준비합니다.
2. 부트플래그가 있으면(앱이 `UOK` 뒤 기록) 지우고 업데이트 모드로 갑니다.
3. 앱 헤더 magic·헤더 CRC·앱 CRC32 가 하나라도 틀리면 업데이트 모드로 갑니다. 쓰다가 끊긴 앱은 여기서 걸립니다.
4. 유효하면 부트 카운터를 1 올립니다. 3을 넘으면 업데이트 모드, 아니면 앱으로 점프합니다.
5. 업데이트 모드는 SCI-A 115200 으로 HELLO·ERASE·WRITE·VERIFY·RESET 을 처리합니다.
   - 패킷 중간에 100ms 넘게 끊기면 파서를 처음부터 다시 시작합니다.
   - 유효한 패킷 없이 10분이 지나고 앱이 유효하면 앱으로 리셋합니다.

### 6.4 실패하면 — 단계별 상태와 복구

![실패 위치에 따른 메인보드 상태](images/diagrams/mainboard-fail-state.png)
*지우기 전에 실패하면 앱이 그대로 돈다. 지운 뒤 실패하면 부트로더가 다음 적용을 기다린다.*

적용 단계에서 실패하면 브리지는 받아 둔 이미지를 남기고 `dsp_st=staged` 로 돌아갑니다. 스테이징(download) 중 실패하면 이전에 받아 둔 이미지가 지워졌을 수 있습니다(`idle`). 서버에서 작업을 다시 실행하면 어느 경우든 스테이징부터 새로 합니다.
아래 `err` 는 통신 로그의 `DSP` 줄과 배포 작업의 메시지(`브리지 보고 실패: <err>`)에 그대로 나옵니다.

| `err` | 단계 | 뜻 | 메인보드 상태 | 복구 |
|---|---|---|---|---|
| `http_init` `http_open` `http_status` `http_length` `http_read` `http_overrun` `http_short` | download | 브리지가 서버 파일을 못 받음. `http_length` 는 크기가 `size` 와 다르거나 384KB 초과 | 앱 그대로 | 방화벽 TCP 8080, `PublicBaseUrl` 확인 후 다시 |
| `flash_erase` `flash_write` | download | 브리지 `dspfw` 파티션 지우기·쓰기 실패 | 앱 그대로 | 다시. 반복되면 브리지 점검 |
| `image_crc` | download | 받은 파일의 CRC 또는 앱 헤더가 틀림 | 앱 그대로 | `dsp_pack.py` 로 다시 만들어 올림 |
| `crc_mismatch` | download | 서버가 준 `crc32` 와 파일 CRC 가 다름 | 앱 그대로 | 펌웨어를 다시 올림 |
| `no_partition` | 명령 접수 | `dspfw` 파티션 없음 (`nopart`) | 앱 그대로 | USB 전체 굽기. 서버는 작업 시작 전에 거부 |
| `busy` `not_ready` `bad_request` | 명령 접수 | `busy` 프로그래밍 중 또는 명령 큐 가득, `not_ready` 브리지 준비 전, `bad_request` URL 형식 오류 | 앱 그대로 | 잠시 뒤 다시 |
| `not_staged` `stage_invalid` | 적용 시작 | 받아 둔 이미지가 없거나 CRC 가 깨짐 | 앱 그대로 | 작업 다시 |
| `esp_ota_busy` | 적용 시작 | 브리지 자체 OTA 진행 중 | 앱 그대로 | 끝난 뒤 다시 |
| `job_mismatch` | 적용 시작 | 적용 `job` 이 받아 둔 이미지의 `job` 과 다름 | 앱 그대로 | 작업 다시 |
| `app_ng_01` … `app_ng_09` | enter | 앱이 진입 거부. 01 주행 중, 02 충전 중, 03 전압 부족, 04 에러, 09 기타 (코드는 제안값) | 앱 그대로 | AGV 정지·조건 해소 후 다시 |
| `boot_proto` | HELLO | 부트로더 프로토콜 버전·블록 크기 불일치. 브리지가 RESET 으로 앱에 되돌림 | 앱으로 복귀 | 브리지·부트로더 버전 확인 |
| `no_bootloader` | HELLO | 부트로더가 4.5초 안에 답하지 않음 | 앱이 `U000` 에 답하지 않았으면 앱 그대로. `UOK` 뒤였으면 부트로더 대기 | 부트로더 설치·J4 배선 확인. 대기 중이면 다시 적용, 전원 재투입, 또는 10분 뒤 앱 자동 복귀 |
| `uart_busy` | HELLO 전 | 브리지가 UART 독점을 못 함 | `UOK` 뒤였으면 부트로더 대기, 아니면 앱 그대로 | 다시 적용 |
| `erase` | erase | 지우기 실패·무응답 | **앱 지워짐**, 부트로더 대기. 버전 `?` | 다시 |
| `stage_read` `stage_block` | progress | 받아 둔 이미지 읽기 오류·블록 형식 오류 | 앱 지워짐 | 작업 다시 (스테이징부터) |
| `write_timeout` `write` `program` `verify_block` | progress | 블록 쓰기 무응답·거부·플래시 쓰기 실패·읽어 비교 실패 | 앱 지워짐 | J4 배선·메인보드 전원 확인 후 다시 |
| `write_header` | 헤더 | 앱 헤더 쓰기 실패 | 앱 무효, 부트로더 대기 | 다시 |
| `verify` | verify | 앱 CRC 불일치 | 앱 무효, 부트로더 대기 | 다시 |
| `app_no_reply` | 확인 | 리셋 뒤 1초 간격 8회 조회에 앱이 `V000` 으로 답하지 않음 | 기록은 끝남. 앱이 리셋을 반복하면 부트 카운터로 부트로더 대기 | 앱에 OTA 지원 코드·`fwup_boot_ok()` 가 있는지 확인, 정상 이미지 다시 적용 |
| `ver_mismatch` | 확인 | 앱이 답한 버전(앞 8자)이 이미지와 다름 | 새 앱이 돌고 있을 수 있음 | 버전 문자열·패킹 확인 |

서버가 스스로 남기는 실패 메시지:

| 메시지 | 뜻 |
|---|---|
| `호기 오프라인 (heartbeat 없음)` | 5초 안에 받은 패킷이 없음 |
| `브리지 펌웨어가 메인보드 OTA 를 지원하지 않음 (… 0.5.0 이상 필요)` | heartbeat 에 `dsp_st` 가 없음 |
| `브리지에 dspfw 파티션이 없음 … USB 로 한 번 전체 굽기 필요` | `dsp_st=nopart` |
| `브리지가 이미 메인보드를 프로그래밍 중` | `dsp_st=programming` |
| `이미지 파일 손상 (.dspfw CRC)` | 서버에 저장된 파일이 손상됨 |
| `서버 IP 를 알 수 없음 — PublicBaseUrl 설정` | 그 호기 쪽 서버 주소를 못 찾음 |
| `브리지 보고 없음 (180초, 'staged' 대기 중)` | 스테이징 중 3분간 보고 없음 |
| `브리지 보고 없음 (90초, 'done' 대기 중)` | 적용 중 90초간 보고 없음 |
| `적용 후 앱 버전 … 이 이미지 … 과 다름` | `done` 의 버전이 이미지와 다름. 버전 문자열이 8자를 넘으면 이렇게 됨(6.6절) |
| `heartbeat 의 메인보드 버전이 … 가 아님 (현재 …)` | 30초 안에 heartbeat `dsp` 가 새 버전이 되지 않음 |

### 6.5 부트 카운터 — 기동 불능 앱 복구

- CRC 는 맞지만 켜지자마자 죽는 앱(링커·클럭 설정 실수 등)을 막는 장치입니다.
- 부트로더는 앱으로 점프할 때마다 RAM 0x0007FA 의 카운터를 1 올립니다.
- 앱은 메인 루프에 들어가면 `fwup_boot_ok()` 를 불러 카운터를 0 으로 만듭니다. `F` 프레임을 처리할 때도 자동으로 부릅니다.
- `fwup_boot_ok()` 없이 3번 연속 기동하면, 다음 리셋 때 부트로더가 업데이트 모드로 들어가 새 이미지를 기다립니다.
- 전원이 끊기면 카운터의 magic 이 깨져 0 으로 취급합니다.

### 6.6 앱 쪽 요구 사항

| 항목 | 내용 |
|---|---|
| 프레임 처리 | J4 수신 프레임 처리 맨 앞에서 `fwup_handle()` 을 부르고, 결과가 있으면 `reply` 를 송신 |
| 진입 | `FWUP_REPLY_RESET` 이면 송신이 끝난 뒤(TXEMPTY) `fwup_reset()` — 부트플래그 기록 → 워치독 리셋 |
| 안전 확인 | `fwup_handle()` 에 넘기는 함수가 0(안전) 또는 사유 코드(1 주행, 2 충전, 3 전압, 4 에러, 9 기타)를 돌려줌 |
| 정상 기동 알림 | 메인 루프 진입 때 `fwup_boot_ok()` 한 번 |
| 링커 | 플래시는 `APPHDR`(0x3E8000) · `APPBEGIN`(0x3E8040) · `FLASHAPP`(0x3E8042 – 0x3F3FFF) 만. `codestart` 는 `APPBEGIN` 으로. **RAMM1 길이 0x3FA** (0x7FA – 0x7FF 제외). 예시 `dsp/app/F2808_app_example.cmd` |
| 버전 | **8자 이하** (예 `1.2.3`). 앱 응답의 버전 칸이 8자 고정입니다. 브리지는 앞 8자로 비교해 `done` 을 보고하지만, 서버는 전체 문자열을 비교하므로 8자를 넘으면 굽기는 성공해도 서버가 실패로 기록합니다 |
| 이미지 만들기 | `hex2000 -i -o app.hex app.out --memwidth=16 --romwidth=16` → `python tools/dsp_pack.py pack app.hex -o app_1.2.3.dspfw --ver 1.2.3` |
| 첫 설치 | 부트로더(섹터 A)와 재링크한 앱을 JTAG 으로 굽고, 처음 한 번은 브리지로 `.dspfw` 를 적용해 앱 헤더를 채움. 그 전까지는 앱 헤더가 비어 있어 부트로더가 업데이트 모드에서 기다립니다(앱이 돌지 않음) |

자세한 절차는 `AGV_Wifi_Bridge/dsp/README.md`, 설계는 `AGV_Wifi_Bridge/docs/DSP_OTA_DESIGN.md` 에 있습니다.

---

## 7. 배포 작업

![배포 작업 상태](images/diagrams/job-lifecycle.png)
*작업은 대기(Scheduled) → 실행 중(Running) → 완료·실패·취소로 끝난다.*

| 규칙 | 내용 |
|---|---|
| 순차 실행 | 한 작업 안의 호기는 **한 대씩** 차례로 진행합니다 |
| 한 번에 한 작업 | 실행 중인 작업이 있으면 다음 작업은 기다립니다. 스케줄러는 1초마다 확인합니다 |
| 예약 | "지금 바로" 또는 "예약" 시각. 예약 시각이 지나면 시작합니다 |
| 실패 시 중단 | "한 대라도 실패하면 나머지 중단"(기본 켜짐, `StopOnFailure`). 켜져 있으면 실패 뒤 호기는 **건너뜀** |
| 취소 | 대기 작업은 바로 취소. 실행 중이면 진행 중 호기는 실패("취소"), 남은 호기는 건너뜀 |
| 서버 재시작 | 실행 중이던 작업은 실패로 정리. 진행 중 호기는 실패, 남은 호기는 건너뜀(메시지 "서버 재시작") |
| 오래된 예약 | 서버가 꺼져 있는 동안 예약 시각이 **30분** 넘게 지난 작업은 켜질 때 자동 취소 |
| 펌웨어 보호 | 대기·실행 중 작업이 쓰는 펌웨어는 지울 수 없습니다 |
| 기록 | 호기별 상태·진행률·이전→이후 버전·시각·로그(최근 200줄)를 DB 에 남깁니다 |

- 실행 중 취소나 서버 재시작은 **서버가 기다리기를 멈출 뿐**입니다. 이미 브리지에 보낸 명령은 브리지가 끝까지 진행합니다. 결과는 호기 현황의 버전으로 확인합니다.
- 서버는 AGV 정지 여부를 확인하지 않습니다. 운용자가 **정지 상태에서** 실행합니다. 메인보드 앱도 주행 중이면 진입을 거부합니다(`app_ng_01`).

![새 작업 만들기](images/screens/05-jobs-new.png)
*새 작업: 메인보드 1.0.3 을 고르고 구역 B제품 / 3F / L4 를 단계별로 골라 2대를 추가했다. 칩의 작은 글씨는 그 호기의 현재 버전.*

![실행 중인 작업](images/screens/06-jobs-running.png)
*실행 중: 31호기가 erase 단계(40%)에 있고 45호기는 대기. 진행 중 호기의 로그가 자동으로 펼쳐진다.*

![작업 목록과 실패 로그](images/screens/07-jobs-list.png)
*작업 목록: 실패 작업을 펼치면 호기별 메시지와 로그가 보인다. `erase_timeout` 은 시연용 가짜 브리지의 실패 문자열이며, 실제 브리지는 `erase` 로 보고한다.*

---

## 8. 구역 (호기 소속)

![구역 5단계 예시](images/diagrams/zone-hierarchy.png)
*제품군 → 층 → 라인 → 공정 → 라우트 5단계 트리. 맨 끝이 호기 번호.*

| 단계 | 이름 | API·CSV 키 |
|---|---|---|
| 1 | 제품군 | `productFamily` |
| 2 | 층 | `floor` |
| 3 | 라인 | `line` |
| 4 | 공정 | `process` |
| 5 | 라우트 (대상 자재) | `route` |

- 비어 있는 중간 단계는 트리에 `(미지정)` 으로 보입니다. 전부 비면 "미분류" 입니다.
- 호기 번호는 1~99 입니다. 아직 접속하지 않은 호기도 넣을 수 있습니다.

### 소속의 출처 세 가지

| 출처 (`source`) | 들어오는 길 |
|---|---|
| `acs` | ACS 소속 받기(`AcsInventoryPullService`). 주기 GET 또는 구역 화면 "지금 받기" |
| `manual` | 구역 화면 "직접 추가"·"수정", 또는 `PUT /api/agvs/{n}/group` |
| `import` | 구역 화면 "CSV 붙여넣기" → "가져오기", 또는 `POST /api/groups/import` |

CSV 형식: `agv,제품군,층,라인,공정,라우트,이름`. 첫 줄 머리글은 건너뛰고, 엑셀에서 복사한 탭 구분도 됩니다.

### ACS 값 반영 정책

| 설정 (`AcsInventory`) | 기본 | 뜻 |
|---|---|---|
| `OverrideManual` | `false` | `false` 면 `manual`·`import` 값은 ACS 가 덮지 않습니다. `true` 면 ACS 값으로 덮습니다 |
| `RemoveMissing` | `true` | ACS 목록에서 빠진 `acs` 출처 호기의 소속을 지웁니다 |

### 구역 단위 배포

1. **구역** 화면 트리에서 항목(예: A제품 / 2F / L1)을 누릅니다.
2. "이 호기들에 배포 →" 를 누르면 **배포 작업** 화면이 그 아래 호기를 모두 대상에 넣은 채 열립니다(`/jobs?group=경로`). 작업 이름은 "<경로> 전체" 로 채워집니다.
3. 또는 **배포 작업** 화면의 "구역으로 선택" 에서 단계별로 고른 뒤 "… 추가" 를 누릅니다.

![구역 화면](images/screens/03-groups.png)
*구역 화면: 위는 ACS 소속 받기 상태, 왼쪽은 트리(A제품 / 2F / L1 선택), 오른쪽은 그 아래 호기 2대와 직접 추가·CSV 붙여넣기.*

---

## 9. 서버 자체 업데이트

![서버 자체 업데이트 순서](images/diagrams/self-update-seq.png)
*매니페스트 확인 → zip 받기(SHA-256 확인) → 배포 작업이 없을 때 apply-update.bat 이 파일을 바꾸고 서비스를 다시 시작.*

1. **확인**: 매니페스트 `{BaseUrl}/releases/latest/download/AgvFotaGateway.json` → `releases/latest` 리다이렉트 → api.github.com 순서로 봅니다. 사내망에서 api.github.com 이 막혀도 github.com 매니페스트로 확인합니다.
   - 시작 3초 뒤 한 번(`FirstCheckDelaySec`), 이후 360분마다(`CheckIntervalMin`), 화면 "지금 확인".
   - 새 버전이 있으면 **서버 업데이트** 메뉴에 ● 가 붙습니다.
2. **받기**: zip 을 `data/update/` 에 `.partial` 로 받고 SHA-256 이 맞을 때만 채택합니다. 압축을 풀어 대기합니다.
3. **적용**: 실행 중인 배포 작업이 없을 때만 됩니다.
   1. 서버가 `data/update/apply-update.bat` 을 실행하고 스스로 종료합니다.
   2. 배치가 서비스를 멈추고 프로세스 종료를 기다립니다(60초 넘으면 강제 종료).
   3. 현재 exe 를 `AgvFotaGateway.exe.old` 로 바꾸고 새 파일을 복사합니다. `appsettings.json` 은 덮지 않고, `data\` 는 zip 에 없으므로 그대로 남습니다.
   4. 복사가 실패하면 `.old` 를 되살립니다. 서비스를 다시 시작합니다(약 10초).
   5. 다시 켜진 서버가 버전을 확인해 **서버 업데이트** 화면 기록에 남깁니다. 배치 기록은 `data/update/apply.log`.
4. **자동 적용**: `Update.AutoApply=true` 면 작업이 없을 때 받기와 적용을 자동으로 합니다. 기본은 `false`.

| 채널 | 만드는 곳 | 화면 표시 | 자동 교체 |
|---|---|---|---|
| `release` | `vX.Y.Z` 태그 또는 Actions `build` 를 `release=true` 로 실행 → Latest 릴리스 | 정식 배포 | 됨 |
| `dev` | main 푸시 → `dev` 프리릴리스(`AgvFotaGateway-dev_win-x64.zip`) | 테스트 빌드 | 됨 (정식 릴리스만 받음) |
| `local` | 직접 빌드, PR 빌드 | 로컬 빌드 | 안 됨 (확인·받기만) |

- **되돌리기**: `.old` 가 있으면 "이전 버전(…)으로 되돌리기" 로 한 세대 되돌립니다. exe 만 바꿉니다. 배포 작업이 실행 중이면 안 됩니다.
- 자동 교체·되돌리기는 Windows 에서만 됩니다.
- 저장소가 **비공개**면 github.com 매니페스트 주소가 404 라 확인이 실패합니다. 공개로 바꾸거나 `Update.BaseUrl` 을 사내 서버로 바꿉니다([DEPLOY.md](DEPLOY.md)). api.github.com 이 열린 망이면 `Update.GitHubToken`(읽기 권한 토큰)으로 API 확인도 됩니다.

---

## 10. 데이터

![SQLite 표](images/diagrams/db-er.png)
*SQLite 표 6개(`meta` 제외)와 논리 관계. 실제 외래 키는 job_targets → jobs(삭제 시 함께 지움) 하나다.*

| 위치 (`data\` 아래) | 내용 |
|---|---|
| `gateway.db` (+ `-wal`, `-shm`) | SQLite 데이터베이스(WAL 모드) |
| `firmware\{id}\` | 업로드한 펌웨어 파일. 목록·해시는 DB `firmware` 표 |
| `logs\gateway-yyyyMMdd.log` | 서버 로그, 14일 보관 |
| `update\` | 받은 서버 업데이트, `apply-update.bat`, `apply.log` |

| 표 | 내용 |
|---|---|
| `firmware` | 펌웨어 목록 (종류 `Dsp`·`Esp`, 버전, 파일 이름, 크기, SHA-256) |
| `jobs` | 배포 작업 (펌웨어, 예약 시각, 실패 시 중단, 상태) |
| `job_targets` | 작업의 호기별 결과 (상태, 진행률, 이전·이후 버전, 로그) |
| `agv_groups` | 호기 소속 5단계와 출처 |
| `agv_positions` | 호기별 최신 위치 (이력 없음) |
| `agv_last_seen` | 호기별 마지막 접속, 버전, heartbeat 원문 |
| `meta` | 스키마 버전 (`schema_version` = 1) |

- `data\` 위치는 `Gateway.DataDir`(기본 `data`, 실행 파일 기준)로 바꿉니다.
- 각 저장소는 메모리에 들고 있다가 바뀔 때 바로 DB 에 씁니다(마지막 접속만 30초 간격, 4.1절). 읽기는 메모리에서 합니다.
- 스키마 변경은 서버가 시작할 때 `meta.schema_version` 을 보고 단계별로 적용합니다(현재 1단계뿐).
- **JSON 자동 이전**: 이전 버전의 `data\jobs.json`, `data\firmware\index.json`, `data\agv_groups.json` 은 첫 실행 때 DB 로 옮기고 `.migrated` 로 이름을 바꿉니다.
- **백업**: **설정** 화면 "백업 내려받기"(`GET /api/db/backup`)는 실행 중에도 일관된 사본을 줍니다.
- 통신 로그(300줄)와 서버 업데이트 기록(100줄)은 메모리에만 있어 재시작하면 사라집니다.

---

## 11. 화면 안내

아래 캡처는 가짜 브리지(`tests/fake_esp.py`) 6대와 가짜 ACS 로 띄운 시연 환경입니다. 그래서 주소가 `127.0.0.1` 로 보입니다.

**호기 현황** — 모든 호기의 접속·버전·상태를 봅니다.

![호기 현황](images/screens/01-home.png)
*위 칩으로 전체·온라인·오프라인·버전별로 거르고, 구역·검색으로 좁힌다. 50호기는 14초 전 끊겨 회색. 시연용 가짜 브리지는 적용 뒤에도 스테이징을 남기지만 실제 브리지는 `done` 뒤 지운다.*

![호기 현황 — 행 상세](images/screens/02-home-detail.png)
*행을 누르면 상세가 열린다. INFO 요청·PING 버튼, 통신 로그·이 호기에 배포·구역 바로가기, heartbeat 원문.*

**구역** — 호기 소속을 트리로 관리하고 구역 단위로 배포 대상을 넘깁니다. (8절의 캡처 참고)

**펌웨어** — 펌웨어를 올리고 목록을 관리합니다.

![펌웨어](images/screens/04-firmware.png)
*종류(메인보드 .dspfw / 브리지 .bin)를 고르고 파일을 올린다. 버전은 파일에서 읽는다. 작업이 쓰는 펌웨어는 삭제 버튼이 꺼진다.*

**배포 작업** — 작업을 만들고 진행·결과를 봅니다. (7절의 캡처 05·06·07 참고)

**통신 로그** — 브리지와 주고받는 패킷을 실시간으로 봅니다.

![통신 로그](images/screens/08-trace.png)
*최근 300줄, 최신이 위. `DSP<` 는 서버 명령, `DSP` 는 브리지 보고, `ACS<` 는 위치 보고(LogOnly 면 `ACS< (LogOnly)`). 아래 수동 전송에서 MAIN_DOWN·CFG_CMD 를 보낸다.*

**설정** — 지금 적용된 설정과 경로를 봅니다. 값은 여기서 바꾸지 않습니다.

![설정](images/screens/09-settings.png)
*서버·ACS 중계·위치 보고·소속 받기·서버 업데이트 설정과 상태, 이 PC 주소, DB 백업 내려받기. 바꾸려면 appsettings.json 을 고치고 서비스를 다시 시작한다.*

**서버 업데이트** — 서버 새 버전을 확인·받기·적용·되돌리기 합니다.

![서버 업데이트](images/screens/10-update.png)
*지금 확인 → 받기 → 적용(서비스 재시작). 시연 환경은 로컬 빌드에 `Update.Enabled=false` 라 적용 버튼이 꺼져 있다.*

**휴대폰 폭** — 메뉴가 위로 올라가고 표는 옆으로 밀어서 봅니다.

![휴대폰 — 호기 현황](images/screens/11-mobile-home.png)
*휴대폰 폭의 호기 현황.*

![휴대폰 — 배포 작업](images/screens/12-mobile-jobs.png)
*휴대폰 폭의 새 작업 만들기. 칩의 작은 글씨는 브리지 버전(펌웨어 선택 전).*

---

## 12. 코드 지도

### 게이트웨이 서버 (`agv_fota_gateway`)

| 기능 | 파일 |
|---|---|
| 시작·서비스 등록·REST API 전체 | `src/Gateway.Server/Program.cs` |
| 설정 기본값 | `src/Gateway.Server/appsettings.json`, `src/Gateway.Server/Options/*.cs` |
| 브리지 UDP 패킷 규격 | `src/Gateway.Core/Bridge/BridgePacket.cs` |
| heartbeat·INFO 해석 | `src/Gateway.Core/Bridge/BridgeStatus.cs` |
| UDP 송수신, RFID 로그 | `src/Gateway.Server/Services/BridgeUdpService.cs` |
| 호기 상태·오프라인·seq 손실 | `src/Gateway.Server/Services/AgvRegistry.cs` |
| AGV 프레임·BCC·ACS 화이트리스트 | `src/Gateway.Core/Agv/AgvFrame.cs` |
| 브리지 OTA 클라이언트 | `src/Gateway.Core/Fota/EspOtaClient.cs`, `EspAppImage.cs` |
| 메인보드 OTA 클라이언트 (B안) | `src/Gateway.Core/Fota/DspOtaClient.cs`, `DspfwImage.cs` |
| 배포 작업 예약·실행 | `src/Gateway.Server/Services/JobService.cs` |
| 펌웨어 업로드·검사·보관 | `src/Gateway.Server/Services/FirmwareStore.cs` |
| ACS TCP 중계 | `src/Gateway.Server/Services/AcsRelayService.cs` |
| ACS 위치 보고 | `src/Gateway.Server/Services/AcsPositionPushService.cs`, `src/Gateway.Core/Acs/AcsPositionMapper.cs`, `AcsRestPublisher.cs` |
| ACS 소속 받기 | `src/Gateway.Server/Services/AcsInventoryPullService.cs`, `src/Gateway.Core/Acs/AcsInventoryMapper.cs` |
| 구역 모델·트리·CSV | `src/Gateway.Core/Groups/AgvGroup.cs`, `src/Gateway.Server/Services/AgvGroupStore.cs` |
| 호기 위치 | `src/Gateway.Core/Positions/AgvPosition.cs`, `src/Gateway.Server/Services/AgvPositionStore.cs` |
| 서버 자체 업데이트 | `src/Gateway.Server/Services/UpdateService.cs`, `src/Gateway.Core/Update/UpdateFeed.cs` |
| 데이터베이스 | `src/Gateway.Server/Data/GatewayDb.cs` |
| 통신 로그·파일 로그 | `src/Gateway.Server/Services/TraceLog.cs`, `FileLoggerProvider.cs` |
| 화면 | `src/Gateway.Server/Components/Pages/` (`Home`, `Groups`, `Firmware`, `Jobs`, `Trace`, `Update`, `Settings`), 메뉴 `Components/Layout/MainLayout.razor` |
| 설치 스크립트 | `deploy/start.bat`, `install-service.bat`, `uninstall-service.bat` |
| 빌드·릴리스 | `.github/workflows/build.yml` |
| 시험 | `tests/Gateway.Core.Tests/`, 가짜 브리지 `tests/fake_esp.py` |

### 브리지 펌웨어 (`AGV_Wifi_Bridge/main`)

| 기능 | 파일 |
|---|---|
| 시작 순서, 메인보드·RFID 프레임 처리 | `main.c` |
| UDP 패킷 규격 | `proto.h`, `proto.c` |
| Wi-Fi, UDP 송수신, heartbeat, INFO, 서버 명령 분기 | `net_bridge.c` |
| UART 프레임 구분(ETX·무수신), 송신 큐, 원시 모드 | `uart_bridge.c`, `uart_bridge.h` |
| 브리지 자체 OTA, 180초 확정 대기 | `ota.c` |
| 메인보드 OTA: 스테이징·부트로더 클라이언트·err 문자열 | `dsp_ota.c` |
| `F`/`R` 프레임 만들기·해석, PAN 학습 | `dsp_app_proto.c` |
| 설정 저장, 원격 변경 180초 확인 대기 | `app_config.c` |
| 콘솔·원격 명령 | `cmd.c`, `console_cmd.c` |
| 무선 설정 모드 (BLE, 선택 SoftAP) | `setup.c`, `setup_ble.c`, `setup_ap.c` |
| 기본값 (메뉴) | `Kconfig.projbuild`, 파티션 `../partitions.csv` |

### 메인보드 지원 코드 (`AGV_Wifi_Bridge/dsp`)와 도구

| 기능 | 파일 |
|---|---|
| 부트로더 판단 흐름 (부트플래그·헤더·부트 카운터·업데이트 모드) | `dsp/boot/boot_main.c` |
| 클럭·보드레이트·대기 시간 | `dsp/boot/boot_cfg.h` |
| 플래시 API·하드웨어 | `dsp/boot/boot_hw.c`, 링커 `dsp/boot/F2808_boot.cmd` |
| 브리지 ↔ 부트로더 프로토콜 | `dsp/common/bootproto.c`, `bootproto.h` |
| 앱 헤더·CRC·주소 상수 | `dsp/common/dsp_image.c`, `dsp_image.h` |
| 앱에 넣는 OTA 지원 코드 | `dsp/app/app_fwupdate.c`, `app_fwupdate.h`, 링커 예시 `dsp/app/F2808_app_example.cmd` |
| `.dspfw` 만들기 | `tools/dsp_pack.py` |
| PC 에서 직접 굽기 (벤치) | `tools/dsp_flash.py` |
| 벤치 서버 (UDP 명령) | `tools/BridgeTest.cs` |
| 브리지 USB 굽기 | `tools/flash.bat`, `tools/flash.sh` |

---

## 13. 미확정 항목과 다음 단계

| 항목 | 지금 상태 | 정해지면 할 일 |
|---|---|---|
| ACS REST 스키마 — 위치 보고 | 임시 형식. `AcsApi.Enabled=false`, `LogOnly=true` | `AcsPositionMapper.cs` 와 `AcsApi` 섹션 수정 ([ACS_API.md](ACS_API.md)) |
| ACS REST 스키마 — 호기 소속 | 임시 형식. `AcsInventory.Enabled=false`. 구역은 화면·CSV 로 관리 중 | 대개 `AcsInventory.Fields` 만 수정. 구조가 다르면 `AcsInventoryMapper.cs` |
| ACS TCP 소켓 규격 | 미확인. 프레임이 그대로 흐른다고 가정한 뼈대, `Gateway.Acs.Enabled=false` | 접속 방향·프레임 구분·호기 매핑 확인 후 `AcsRelayService.cs` 수정 |
| RTLS (위치 추정) | 설계만 ([RTLS.md](RTLS.md)). 위치는 시험 주입 API(`POST /api/positions/{n}`)만 | AGV 1대 실측부터 |
| 메인보드 OTA 실기 검증 | 브리지 0.5.x·부트로더·앱 모두 실기 검증 전. 서버는 가짜 브리지로만 확인 | 벤치 → 1대 실차 순서로 검증 (`dsp/README.md` 3절) |
| 메인보드 앱 크기 | 앱 영역(codestart 포함 최대 49,088워드) 안에 드는지 미확인 | `.map` 의 FLASH 사용량 확인 |
| `F`/`R` 종류 문자, 진입 거부 사유 코드 | 제안값 | AGV 통신 프로토콜 문서에 등록 |
| 적용 시간 | 설계 추정 20~30초 | 실측 |
| 저장소 공개 여부 | 결정 대기. 비공개면 github.com 매니페스트가 404 라 자동 업데이트 불가 | 공개 전환 또는 사내 `Update.BaseUrl` |
| v0.3.0 정식 릴리스 | 결정 대기. csproj 는 0.3.0, 릴리스 태그는 v0.2.0 까지 | 태그 `v0.3.0` 또는 Actions `release=true` |
| AGV 정지 확인 | 자동화 없음. 운용자가 확인하고, 메인보드 앱이 주행 중이면 거부 | ACS 중계가 붙으면 검토 |
| 로그인·권한 | 없음 (사내망 전제로 결정) | 필요하면 인증 추가 |

### 점검에서 나온 보완점 (2026-10-10)

전체 점검 결과(검증 후 91건: 높음 1 · 중간 40 · 낮음 50)는 [REVIEW_2026-10-10.md](REVIEW_2026-10-10.md) 에 있습니다. 이 문서의 흐름과 직접 관련된 것만 추립니다. 아직 코드는 고치지 않았고, 각 행의 **당장의 우회**는 [DEPLOY.md](DEPLOY.md) 에 넣었습니다.

![보완점 위치](images/diagrams/review-hotspots.png)

| 흐름 | 보완점 | 당장의 우회 | 자세히 |
|---|---|---|---|
| 서버 자체 업데이트 ([9절](#9-서버-자체-업데이트)) | **적용 뒤 Windows 서비스로 다시 뜨지 않음**(높음). 배치 파일의 `set WASSVC=1 &` 공백 | 적용 뒤 `services.msc` 확인, 중지됨이면 `taskkill` → `sc start`. 또는 수동 업데이트 | [4-1](REVIEW_2026-10-10.md#4-1-서버-업데이트-뒤-서비스로-재시작-안-됨-i-01-높음) |
| 브리지·메인보드 OTA ([5절](#5-브리지-ota) · [6절](#6-메인보드-ota-b안)) | 명령과 최종 보고를 한 번씩만 보냄 → 패킷 하나 유실로 정상 업데이트가 실패로 남거나 브리지가 롤백 | '보고 없음' 실패면 호기 현황의 실제 버전 확인, `pending` 이면 180초 안에 PING | [4-2](REVIEW_2026-10-10.md#4-2-udp-명령보고를-한-번만-보냄-c-01--t-02--c-05--b-02) |
| 메인보드 버전 ([6.6절](#66-앱-쪽-요구-사항)) | 8자 규칙을 어디서도 막지 않음 → 9자 버전은 굽기 성공 뒤 '실패' | 버전은 8자 이하 | [4-3](REVIEW_2026-10-10.md#4-3-메인보드-버전-8자-규칙-미강제-c-02--b-03--d-04--t-03) |
| 정지 확인 ([6.1절](#61-스테이징과-적용을-나누는-이유)) | 서버 확인 없이 앱 확인 한 겹. 화면의 '지금 시작'·수동 전송·업데이트 적용에 확인창 없음 | AGV 를 세운 뒤 시작, 호기 칸 확인 | [4-4](REVIEW_2026-10-10.md#4-4-agv-정지-확인과-운영자-실수-방지-x-01--u-01--u-05--d-07--b-01--u-03--u-04) |
| 평상시 중계 ([4절](#4-평상시-흐름)) | 같은 호기 번호를 쓰는 장치가 둘이면 하향 프레임이 번갈아 감. 감지 없음 | 새 브리지는 호기 번호부터 설정 | [4-5](REVIEW_2026-10-10.md#4-5-같은-호기-번호--송신원-검증-없음-c-04--s-03--x-02) |
| 배포 작업 ([7절](#7-배포-작업)) | 기동 직후 heartbeat 전에 작업 시작, 밀린 예약은 늦어도 그대로 시작 | 재부팅 시각과 예약 시각 분리 | [4-6](REVIEW_2026-10-10.md#4-6-작업-시작-시점-s-01--g-05--g-08--g-07) |
| 구역 ([8절](#8-구역-호기-소속)) | ACS 빈 응답이면 ACS 소속 전부 삭제, 이름 안의 `/` 로 경로 충돌 | 소속 받기 꺼 둠, 이름에 `/` 금지 | [4-7](REVIEW_2026-10-10.md#4-7-acs-빈-응답이-소속을-지움-s-04--i-04) · [5-1](REVIEW_2026-10-10.md#5-1-구역-경로-충돌-g-01--g-02--묶음-11) |
| 데이터 ([10절](#10-데이터)) | UDP 수신 루프가 DB 를 동기로 씀 → DB 를 쓰기 모드로 열어 두면 통신 전체 정지 | DB 는 읽기 전용이나 백업 사본으로 열기 | [4-9](REVIEW_2026-10-10.md#4-9-udp-수신-루프의-db-동기-쓰기-s-02) |
| 부트로더 ([6.3절](#63-메인보드-메모리-배치와-부트로더) · [6.5절](#65-부트-카운터--기동-불능-앱-복구)) | 수신 오류(BRKDT) 처리 없음, 부트플래그 진입 뒤 10분 정지, 지운 뒤 자동 재시도 없음. **부트로더는 JTAG 로만 교체** → 현장 설치 전에 보완 | `main_baud` 115200 유지, 실패하면 같은 펌웨어로 다시 | [4-10](REVIEW_2026-10-10.md#4-10-부트로더메인보드-복구-한계-d-03--d-06--d-02--b-04--d-05--d-01--x-09--d-08) |

결정이 필요한 항목(서버 쪽 정지 확인 방식, 복구 범위, BLE 비밀번호, 서명 등 13건)은 [REVIEW_2026-10-10.md 8절](REVIEW_2026-10-10.md#8-결정이-필요한-항목) 에 정리했습니다.

