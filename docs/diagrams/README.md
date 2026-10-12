# 도식 원본

`docs/*.md` 에 들어간 그림(`docs/images/diagrams/*.png`)의 원본입니다. [Mermaid](https://mermaid.js.org/) 문법이라 GitHub 에서 `.mmd` 파일을 열어도 바로 그려집니다.
PNG 는 GitHub 밖(설치 zip 의 DEPLOY.md, 사내 문서 뷰어)에서도 보이도록 미리 만들어 둔 것입니다.

| 원본 | 내용 | 쓰는 문서 |
|---|---|---|
| `architecture.mmd` | 전체 구성 | SYSTEM_FLOW, README |
| `comm-ports.mmd` | 통신 경로와 포트 | SYSTEM_FLOW |
| `deploy-ports.mmd` | 포트와 방화벽 규칙 이름 | DEPLOY |
| `normal-relay-seq.mmd` | 평상시 흐름 (heartbeat, 자동 PING, MAIN_UP/DOWN, RFID 바이패스, 번호 충돌) | SYSTEM_FLOW |
| `acs-flows.mmd` | ACS 연동 세 갈래 (하향 F·R 버림 포함) | SYSTEM_FLOW |
| `bridge-ota-seq.mmd` | 브리지 OTA 순서 (재전송 · 확정 PING · 롤백) | SYSTEM_FLOW (브리지 FLASH_GUIDE 에 사본) |
| `mainboard-ota-seq.mmd` | 메인보드 OTA 순서 (전 호기 스테이징 → 적용 대기 → 승인 → 한 대씩 적용 · U 무응답 때 앱 생존 판단) | SYSTEM_FLOW (브리지 FLASH_GUIDE 에 사본) |
| `dsp-job-steps.mmd` | 서버가 호기 한 대를 처리하는 단계와 실패 지점 (복구 적용 작업만 recover:1) | SYSTEM_FLOW (브리지 FLASH_GUIDE 에 사본) |
| `mainboard-flash-map.mmd` | 메인보드 플래시·RAM 배치 | SYSTEM_FLOW (브리지 FLASH_GUIDE 에 사본) |
| `bootloader-boot.mmd` | 부트로더 판단 흐름 (HELLO 창 · 진입 사유별 대기) | SYSTEM_FLOW (브리지 FLASH_GUIDE 에 사본) |
| `mainboard-fail-state.mmd` | 실패 위치에 따른 메인보드 상태와 복구 (app_no_fwup · 자동 재시도 · 복구 적용) | SYSTEM_FLOW, DEPLOY |
| `mainboard-ota-errors.mmd` | 메인보드 OTA 오류 코드 (단계별 · app_no_fwup 포함) | DEPLOY |
| `job-lifecycle.mmd` | 배포 작업 상태 (적용 대기 포함) | SYSTEM_FLOW, DEPLOY |
| `approval-flow.mmd` | 메인보드 작업의 적용 대기와 승인 화면 흐름 | DEPLOY, SYSTEM_FLOW, REVIEW_2026-10-10 |
| `zone-hierarchy.mmd` | 구역 5단계 예시 | SYSTEM_FLOW, DEPLOY, REVIEW_2026-10-10 |
| `self-update-seq.mmd` | 서버 자체 업데이트 (배치 시작 확인 · .prev → .old · 60초 기동 확인 · 자동 되돌림) | SYSTEM_FLOW, DEPLOY |
| `db-er.mmd` | SQLite 표 (스키마 v3 · jobs.recover) | SYSTEM_FLOW |
| `rollout-order.mmd` | 전체 도입 순서 | DEPLOY (브리지 FLASH_GUIDE 에 사본) |
| `install-service-steps.mmd` | install-service.bat 이 하는 일 (다시 실행 · 폴더 권한 · 방화벽) | DEPLOY |
| `troubleshoot-agv-missing.mmd` | 호기가 안 보일 때 (번호 충돌 포함) | DEPLOY |
| `review-method.mmd` | 전체 점검 방법 | REVIEW_2026-10-10 |
| `review-hotspots.mmd` | 보완점이 몰린 곳 (묶음 번호) | REVIEW_2026-10-10, SYSTEM_FLOW |
| `review-udp-loss.mmd` | UDP 보고 1개 유실로 생기는 오판 | REVIEW_2026-10-10 |
| `review-findings-chart.html` | 관점별 지적 수 차트 (`docs/images/review/findings-by-area.png`). 브라우저로 열어 `#root` 를 2배율로 캡처 | REVIEW_2026-10-10 |

브리지 저장소(AGV_Wifi_Bridge)의 `docs/diagrams/` 에는 브리지 전용 도식(파티션 배치, 굽기 방법 선택, 설정 모드, 원격 설정 trial, 메인보드 첫 설치)이 있고, 위 표에서 "사본" 으로 적은 그림은 PNG 를 복사해 씁니다. 원본을 고치면 양쪽 PNG 를 함께 바꿉니다.

## 다시 만들기

```bash
curl -o /tmp/mermaid.min.js https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js
npm i -D playwright && npx playwright install chromium
for f in docs/diagrams/*.mmd; do
  node docs/diagrams/render.mjs /tmp/mermaid.min.js "$f" "docs/images/diagrams/$(basename "$f" .mmd).png" 1200
done
```

세로로 긴 sequence 도식(`mainboard-ota-seq`, `bridge-ota-seq`, `self-update-seq`, `normal-relay-seq`)은 첫 줄에 `%%{init: {"sequence": {"mirrorActors": true, ...}}}%%` 를 둡니다. 빼면 그림 높이가 약 2000px 를 넘을 때 참여자 세로선이 중간에서 끊깁니다.

화면 캡처(`docs/images/screens/`)는 가짜 브리지(`tests/fake_esp.py`) 8대와 가짜 ACS 로 띄운 시연 환경에서 찍었습니다. 화면이 바뀌면 같은 방식으로 다시 찍습니다.
