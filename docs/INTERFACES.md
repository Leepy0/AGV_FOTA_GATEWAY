# 접점 규격 (참조)

이 서버가 의존하는 외부 규격과 그 출처입니다. 원본이 바뀌면 여기와 `Gateway.Core` 를 함께 고칩니다.

| 규격 | 원본 | 이 저장소의 구현 |
|---|---|---|
| 브리지 UDP 패킷 (헤더 10B, 타입 0x01~0x41) | `AGV_Wifi_Bridge/main/proto.h` (커밋 74da5df, PR #3) | `Gateway.Core/Bridge/BridgePacket.cs` |
| 브리지 INFO(0x11/0x12), 메인보드 OTA B안 DSP_OTA_START/RESULT/APPLY(0x32~0x34), heartbeat `dsp`/`dspb`/`staged` | `AGV_Wifi_Bridge/docs/DSP_OTA_DESIGN.md` | **미구현** — 작업 모델에 연결할 것 (현재 DSP OTA 는 DSP_OTA 저장소의 F 프레임 방식) |
| 브리지 heartbeat JSON | `AGV_Wifi_Bridge/main/net_bridge.c` hb_task | `Gateway.Core/Bridge/BridgeStatus.cs` |
| 브리지 OTA (OTA_START URL → OTA_RESULT JSON → pending → 서버 패킷으로 확정) | `AGV_Wifi_Bridge/README.md` "OTA 무선 업데이트" | `Gateway.Core/Fota/EspOtaClient.cs` |
| 브리지 프레임 제한: ETX(0x03) 프레이밍, 256B | `AGV_Wifi_Bridge/main/uart_bridge.h` | `AgvFrame.BridgeFrameMax` |
| AGV↔ACS 프레임 (STX, BCC, ETX, 종류/PAN/MSG/AGV) | AGV 통신 프로토콜 레퍼런스 | `Gateway.Core/Agv/AgvFrame.cs` |
| DSP OTA F 프레임 (S/B/D/E/R/C/X, NG 코드) | `DSP_OTA/docs/PROTOCOL.md` | `Gateway.Core/Fota/DspOtaClient.cs` |
| DSP 이미지 헤더 64B (DOTA) | `DSP_OTA/core/ota_image.h` | `Gateway.Core/Fota/FirmwareImage.cs` |
| ACS TCP 소켓 | **미확인** — 기존 ZigBee 게이트웨이 규격 필요 | `Gateway.Server/Services/AcsRelayService.cs` (프레임 그대로 통과한다고 가정) |

## 브리지와의 약속

- 브리지는 설정된 `server` IP 에서 온 하향 패킷만 받고, 상향 패킷을 그 IP 의 `server_port`(50000) 로 보낸다. Gateway 는 마지막 수신 endpoint(IP:local_port) 로 하향 송신한다.
- 서버 하향 패킷을 받으면 브리지는 자기 새 펌웨어·원격 설정을 확정한다. Gateway 가 보내는 모든 패킷(PING, MAIN_DOWN, CFG_CMD)이 그 역할을 한다.
- MAIN_DOWN 은 256B 를 넘으면 브리지가 버린다. DSP OTA 의 최장 프레임은 블록 112B 일 때 242B.

## ACS 로 넘기는 것

상향 프레임 중 **E(이벤트)와 A(ACK)만** ACS 로 중계한다 (`AgvFrame.IsForAcs`, 화이트리스트). 기존 ACS 는 규격에 없는 종류를 받으면
오동작할 수 있으므로 DSP OTA 응답(`A…F…`), 브리지용 응답(`R`), 위치 프레임(`P`, 제안) 등 새 종류는 서버 안에서만 소비한다.

## DSP 와의 약속

- DSP OTA 응답은 `A` + … + `F` 로 시작하므로 ACS 로 넘기지 않는다 (`AgvFrame.IsOtaReply`).
- 종류 문자 `F` 를 DSP_OTA 저장소(서버→AGV 블록 전송: S/B/D/E/R/C/X)와 브리지 B안(브리지→AGV `V000`/`U000`)이 다른 본문으로 쓴다. **둘 중 하나로 정리 필요** (PR #3 본문 참조).
- 호기 번호가 일치하는 정상 프레임을 하나라도 받으면 DSP 가 새 펌웨어를 자동 확정한다 (기본 설정). 재부팅 뒤 Gateway 가 보내는 `S` 가 그 역할을 한다.
