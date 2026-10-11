# 접점 규격 (참조)

이 서버가 의존하는 외부 규격과 그 출처입니다. 원본이 바뀌면 여기와 `Gateway.Core` 를 함께 고칩니다.

| 규격 | 원본 | 이 저장소의 구현 |
|---|---|---|
| 브리지 UDP 패킷 (헤더 10B, 타입 0x01~0x41) | `AGV_Wifi_Bridge/main/proto.h` (main 0dfbdf6, PR #4, v0.5.1) | `Gateway.Core/Bridge/BridgePacket.cs` |
| 브리지 INFO(0x11/0x12), heartbeat `dsp`/`dspb`/`dsp_st`/`staged` | `AGV_Wifi_Bridge/main/dsp_ota.c` dsp_ota_status_json | `Gateway.Core/Bridge/BridgeStatus.cs` |
| 브리지 0.5.1 `dsp_st=nopart` (dspfw 파티션 없음 → `no_partition` 거부) | `AGV_Wifi_Bridge/main/dsp_ota.c` | `JobService.RunDspAsync` 가 작업 시작 전에 거부, 호기 현황 ⚠ 표시 |
| 서버 데이터베이스 (SQLite `data/gateway.db`, 스키마 v1: firmware·jobs·job_targets·agv_groups·agv_positions·agv_last_seen) | 이 저장소 | `Gateway.Server/Data/GatewayDb.cs` (Migrate 에 버전 단계 추가로만 변경) |
| ACS 호기 소속 REST (GET, 응답 형식 **미확정** — 임시 매핑) | 기존 ACS 서버 (월요일 스키마) | `Gateway.Core/Acs/AcsInventoryMapper.cs`, `appsettings AcsInventory.Fields` (`docs/ACS_API.md`) |
| **메인보드 OTA B안** DSP_OTA_START(0x32) {url, ver, crc32, size, job, apply} → DSP_OTA_RESULT(0x33) {job, stage, pct, ver, err} / DSP_OTA_APPLY(0x34) {job} | `AGV_Wifi_Bridge/docs/DSP_OTA_DESIGN.md`, `main/dsp_ota.c` | `Gateway.Core/Fota/DspOtaClient.cs` (스테이징 → 적용 → heartbeat 버전 확인) |
| `.dspfw` 이미지 파일 (헤더 32B: magic/total_len/crc32/ver) | `AGV_Wifi_Bridge/tools/dsp_pack.py` | `Gateway.Core/Fota/DspfwImage.cs` |
| 브리지 프레임 제한: ETX(0x03) 프레이밍, 256B | `AGV_Wifi_Bridge/main/uart_bridge.h` | `AgvFrame.BridgeFrameMax` |
| AGV↔ACS 프레임 (STX, BCC, ETX, 종류/PAN/MSG/AGV) | AGV 통신 프로토콜 레퍼런스 | `Gateway.Core/Agv/AgvFrame.cs` |
| ACS TCP 소켓 | **미확인** — 기존 ZigBee 게이트웨이 규격 필요 | `Gateway.Server/Services/AcsRelayService.cs` (프레임 그대로 통과한다고 가정) |

## 브리지와의 약속

- 브리지는 설정된 `server` IP 에서 온 하향 패킷만 받고, 상향 패킷을 그 IP 의 `server_port`(50000) 로 보낸다. Gateway 는 마지막 수신 endpoint(IP:local_port) 로 하향 송신한다.
- 서버 하향 패킷을 받으면 브리지는 자기 새 펌웨어·원격 설정을 확정한다. Gateway 가 보내는 모든 패킷(PING, MAIN_DOWN, CFG_CMD)이 그 역할을 한다.
- MAIN_DOWN 은 256B 를 넘으면 브리지가 버린다. B안은 MAIN_DOWN 으로 펌웨어를 보내지 않는다.

## ACS 로 넘기는 것

상향 프레임 중 **E(이벤트)와 A(ACK)만** ACS 로 중계한다 (`AgvFrame.IsForAcs`, 화이트리스트). 기존 ACS 는 규격에 없는 종류를 받으면
오동작할 수 있으므로 브리지용 응답(`R`), 위치 프레임(`P`, 제안) 등 새 종류는 서버 안에서만 소비한다.

## 메인보드(AGV) OTA 결정 (2026-10-09)

**B안 채택.** 서버는 메인보드와 직접 프레임을 주고받지 않는다. 브리지에 `.dspfw` 를 스테이징시키고(0x32), 적용 명령(0x34)을 보내면
브리지가 메인보드를 부트로더로 보내 UART 로 굽고 단계를 보고한다(0x33). 브리지→메인보드 `F…V000`/`U000` 과 메인보드→브리지 `R` 프레임은
브리지가 만들고 소비하므로 서버에 올라오지 않는다. `DSP_OTA` 저장소(서버→AGV F 프레임 블록 전송)는 보관으로 돌렸다.
- B안에는 확정 절차가 없다. 부트로더는 앱으로 점프할 때마다 부트 카운터를 올리고, 앱은 메인 루프에 들어가면 `fwup_boot_ok()` 로 0 으로 돌린다(자기 호기 `F` 프레임을 처리할 때도 자동). 카운터가 3 을 넘으면 부트로더가 업데이트 모드에 머물러 새 이미지를 기다린다 (`AGV_Wifi_Bridge/dsp/boot/boot_main.c`, `dsp/app/app_fwupdate.c`). Gateway 는 관여하지 않는다.
