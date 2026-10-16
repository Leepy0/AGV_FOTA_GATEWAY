---
name: fota-gateway-release
description: "AGV FOTA Gateway(ASP.NET Core Blazor 웹 서버, 사내 PC Windows 서비스)를 빌드·릴리스·설치·업데이트할 때 사용. '릴리스 올려줘', '배포해줘', '서버 설치', '버전 올려줘', 코드 수정 후 배포가 필요할 때."
---

# AGV FOTA Gateway 빌드·릴리스·설치

웹 서버지만 배포 방식은 WPF 자동 업데이트 프로그램과 같다: **정식 릴리스를 올리면 설치된 서버들이 스스로 받아 적용**한다.
`wpf-build-release` 스킬의 "경로 A: 정식 배포" 와 `dotnet-self-update` 스킬의 핵심 결정(매니페스트 확인, SHA-256, .old 되돌리기, 채널)을 그대로 따르고,
아래가 이 프로젝트의 구체 값이다. 사내 업데이트 서버·`file://` 출처는 지원하지 않는다(결정 11·16 — https 의 github.com·api.github.com 만).

| 항목 | 값 |
|---|---|
| 저장소 | `Leepy0/AGV_FOTA_GATEWAY` (**공개** — 비공개로 돌리면 github.com 매니페스트 주소가 404 라 자동 업데이트가 멈춘다) |
| 사내 프록시 | 서비스(LocalSystem)는 사용자 프록시·로그인 인증을 쓰지 않는다. `start.bat` 으로는 되고 서비스로는 "확인 실패" 면 `appsettings.json` `Update.Proxy`(·`ProxyUser`·`ProxyPassword`, 0.3.4+) — `docs/DEPLOY.md` 9-2 |
| 자산 | `AgvFotaGateway_win-x64.zip`(고정 이름), `.sha256`, `AgvFotaGateway.json` |
| 매니페스트 주소 | `https://github.com/Leepy0/AGV_FOTA_GATEWAY/releases/latest/download/AgvFotaGateway.json` |
| 버전 원본 | `src/Gateway.Server/Gateway.Server.csproj` `<Version>` — 태그와 다르면 CI 가 실패 |
| 채널 | CI `release`(태그·release=true) / `dev`(main push 테스트 프리릴리스) / `local`(PR·직접 빌드) |
| 짝 브리지 | `Leepy0/AGV_Wifi_Bridge` (게이트웨이 0.3.0 ↔ 브리지 0.5.2) |

## 공통 전제

- 브랜치 `main`. commit·push·태그·릴리스는 묻지 않고 진행한다. 기존 릴리스·태그 삭제, force push 는 허가를 받는다.
- 빌드·시험·릴리스는 GitHub Actions(`.github/workflows/build.yml`)가 한다. 로컬에서 `dotnet publish` 를 돌릴 필요는 없다.
- 보고는 버전, 릴리스 URL, 변경 요약 위주로 짧게.

## 1. 코드 수정 후 검증

CI 의 `Build & test` · `End-to-end` 단계와 같은 것을 로컬에서 먼저 돌린다(경고 0 유지).

```
dotnet build -c Release
dotnet test -c Release --no-build
python3 tests/e2e/test_fake_esp.py   # 가짜 브리지 자체 시험 (서버 없이, 약 40초)
python3 tests/e2e/run_e2e.py         # 빌드한 서버 + 가짜 브리지로 배포 흐름 12개 (약 3분, Python 3.10+)
```

- `run_e2e.py` 는 임시 데이터 폴더·임의 포트로 서버를 띄운다. `--only 1,3`, `--list`, `--keep`(로그 남기기). 종료 코드 = 실패 시나리오 수. 자세한 것은 `tests/e2e/README.md`.
- 손으로 보기: `python3 tests/fake_esp.py --agv 7,12,23 --server 127.0.0.1:50000 --listen 50010`(브리지 0.5.2 흉내, 장애 주입 옵션은 `--help`) 을 띄우고
  `dotnet run --project src/Gateway.Server` → `GET /api/agvs` 에 호기가 보이면 REST(`/api/firmware` 에 `.dspfw`, `/api/jobs`, `/api/jobs/{id}/approve`)로 작업을 돌려 본다.
- 메인보드 OTA 는 브리지 B안(0x32 스테이징 → 적용 대기·승인 → 0x34 적용 → 0x33 보고)이며 서버는 메인보드와 직접 프레임을 주고받지 않는다.

## 2. 릴리스 (정식 배포)

CI 순서: 버전·채널 결정 → `dotnet build`·`dotnet test` → 종단 시험(`test_fake_esp.py`, `run_e2e.py`) → win-x64 단일 exe publish·zip·`.sha256` → (정식) 변경 내용·매니페스트·Release.
시험이 하나라도 실패하면 릴리스까지 가지 않는다.

1. **버전 결정**: 마지막 태그(`git tag --sort=-v:refname | head -1`)보다 올린다. 버그 수정 patch, 기능 추가 minor. 사용자가 지정하면 그대로.
2. **버전 반영**: `src/Gateway.Server/Gateway.Server.csproj` 의 `<Version>` 을 같은 값으로 바꾸고 commit·push.
   서버의 업데이트 확인은 "릴리스 태그 버전 > 실행 중 버전" 비교라 csproj 와 태그가 같아야 한다.
   문서(`docs/DEPLOY.md` 머리의 기준 버전 등)도 같은 커밋에서 맞춘다 — zip 에 그대로 들어간다.
3. **정식 배포 실행** — 둘 중 하나. 정식 배포는 별도 concurrency 그룹이라 뒤이은 main push 가 취소하지 않는다.
   - `git tag vX.Y.Z && git push origin vX.Y.Z` (csproj 와 같아야 함, 다르면 CI 실패)
   - 태그 푸시가 안 되는 환경(Claude 클라우드 세션의 git 프록시는 태그 푸시를 끊는다): Actions `build` 를 `workflow_dispatch` 로
     `release=true` 입력과 함께 실행 (GitHub MCP `actions_run_trigger` inputs `{"release":"true"}`, 또는 Actions 탭 → Run workflow → 정식 배포 체크).
     csproj 버전으로 이 커밋에 태그를 만든다. 그 버전 태그가 이미 다른 커밋에 있으면 실패한다.
   - `AgvFotaGateway_win-x64.zip`(고정 이름) — 최상위 `AgvFotaGateway\` 폴더 안에 런타임 포함 단일 exe + appsettings.json + 배포 bat 3개 + DEPLOY.md + SYSTEM_FLOW.md + `images\`(diagrams·screens)
   - `AgvFotaGateway_win-x64.zip.sha256`, `AgvFotaGateway.json`(매니페스트)
4. **릴리스 노트**: 이전 태그 이후 커밋 메시지로 자동 생성(Co-Authored-By·Claude-Session 줄 제거). 다듬고 싶으면 릴리스를 편집한다.
5. **검증**: Actions 초록(`actions_list` → `list_workflow_runs`, 종단 시험 단계 포함), Release 자산 3개(`get_release_by_tag`): zip, `.sha256`, `AgvFotaGateway.json` 의 `version`·`sha256`.
   공개 저장소면 `curl -sL https://github.com/Leepy0/AGV_FOTA_GATEWAY/releases/latest/download/AgvFotaGateway.json` 에 새 버전이 보이는지.
   설치된 서버의 **서버 업데이트** 화면에서 "지금 확인" 이 새 버전을 보이는지.
   **Windows 실기 확인(교체 배치를 바꾼 릴리스, 특히 v0.3.0 첫 릴리스)**: 서비스 모드와 콘솔(`start.bat`) 모드에서 각각 적용 → 되돌리기 → 자동 되돌림(새 버전이 응답하지 않게 한 빌드)을 1회씩 돌린다. `data\update\run-started.txt` 가 생겼는지, `data\update\apply.log` 의 단계(`healthy: …` 또는 `… auto rollback` → `restored previous exe`), 설치 폴더의 `.old`·`.bad`와 `services.msc` 의 **실행 중** 을 확인한다. 0.3.0 의 배치는 아직 실기로 확인하지 않았다.
6. **비공개 주의**: 저장소가 비공개인 동안은 github.com 매니페스트 주소가 404 → 서버는 "정식 릴리스가 없거나 접근할 수 없음" 을 보인다.
   해결은 저장소 공개 전환이다(사내 서버로 자산을 복사하는 방식은 막혀 있음). api.github.com 이 열린 망이면 `Update.GitHubToken`(Contents: Read)으로 API 폴백 확인은 되지만 받기까지는 확인 필요. 그 전에는 수동 업데이트(아래 4절)로 안내한다. 처음 릴리스할 때 한 줄로 알린다.

테스트 빌드: main push 마다 `dev` 프리릴리스를 덮어쓴다(`AgvFotaGateway-dev_win-x64.zip`). 자동 업데이트 대상이 아니며 정식 릴리스로 올리지 않는다.

**브리지 릴리스**(짝 버전을 함께 낼 때): 브리지 저장소 `CMakeLists.txt` 의 `PROJECT_VER` 를 올려 커밋한 뒤 Actions `build` 를 `release=true` 로 수동 실행한다 — host-test(`tests/host/run.sh`, `tests/dsp_ota_host/run.sh`, `make -C tests/bridge_util`) 통과 뒤 `vX.Y.Z` 태그·Release 를 만들고 zip·`agv_wifi_bridge.bin`·`agv_wifi_bridge_merged.bin` 을 붙인다(같은 태그가 다른 커밋에 있으면 실패).

## 3. 처음 설치 (원클릭)

사용자에게 알려 줄 절차. `docs/DEPLOY.md` 2장과 같다.

1. Release 의 zip 을 서버 PC 의 `C:\` 에 풀기 — zip 안 `AgvFotaGateway\` 폴더째 풀려 `C:\AgvFotaGateway\AgvFotaGateway.exe` 가 된다 (`C:\Program Files` 아래와 드라이브 루트는 피함)
2. `install-service.bat` 우클릭 → 관리자 권한으로 실행 — 서비스 등록(자동 시작·자동 재시작, 이미 있으면 멈추고 `sc config` 로 갱신),
   설치 폴더 권한(Administrators·SYSTEM 쓰기, Users 읽기·실행), `appsettings.json` 의 포트로 방화벽(HTTP·브리지 UDP, ACS 중계 Server 모드면 ACS TCP), 최대 15초 시작 확인, 브라우저 열기
3. 브리지(0.5.2)에 서버 PC IP 를 `set server` 로 설정 → 호기 현황에 나타남

시험만 할 때는 `start.bat`(콘솔, 서비스 설치 뒤에는 관리자 권한 필요). 삭제는 `uninstall-service.bat`. 설정은 `appsettings.json`(수정에 관리자 권한), 데이터는 `data\`. 포트를 바꾸면 `install-service.bat` 재실행.

## 4. 업데이트 적용

설치된 서버는 시작 3초 뒤와 6시간마다 매니페스트로 새 릴리스를 확인한다. 화면 **서버 업데이트** → 지금 확인 → 받기(SHA-256 검증) → 적용(확인창, 새 배포 작업 보류, 서비스 재시작 약 10초).
서버는 배치가 첫 줄에서 `run-ticket.txt` 를 `run-started.txt` 로 바꾼 것을 5초 안에 확인한 뒤에만 멈춘다(못 보면 오류, 서버는 계속 실행). 배치는 서비스를 멈추고 현재 exe 를 `.prev` 로 비켜 둔 채 파일을 바꾼 뒤(`appsettings.json` 제외) `.prev` 를 `.old` 로 바꾸고(복사 실패면 `.prev` 를 되살리고 `apply-failed`) 서비스를 시작한다. 2초마다 최대 60초 `/api/health` 를 확인해 새 버전이 응답하지 않으면 `.bad` 로 두고 `.old`(`.prev` 가 남아 있으면 그것)를 복원한다. 자동 되돌림·교체 실패 버전은 자동 적용 제외(`data\update\skip-auto.txt`).
`Update.AutoApply=true` 면 배포 작업이 없을 때 자동 적용하되, ACS 중계가 연결돼 있으면 미루고 화면에서 적용하게 한다. 잘못 배포했으면 "이전 버전으로 되돌리기". 적용 기록 `data\update\apply.log`.

- **v0.2.x → v0.3.0 첫 업데이트**: 교체 배치는 실행 중인 옛 버전이 만들므로 I-01 문제(서비스로 재시작 안 됨)가 한 번 남는다. 0.3.0 은 SCM 밖에서 돌면 **서버 업데이트** 화면에 빨간 띠를 띄운다. 수동 업데이트를 권하고, 화면에서 적용했다면 `services.msc` 를 확인해 **중지됨** 이면 `taskkill /f /im AgvFotaGateway.exe` → `sc start AgvFotaGateway`. 끝나면 `install-service.bat` 을 한 번 다시 실행(폴더 권한·ACS 방화벽 반영).

수동 업데이트(`docs/DEPLOY.md` 9-4): 배포 작업이 없는지 확인 → 관리자 권한으로 `net stop AgvFotaGateway`(실행 중 exe 는 잠겨 있어 덮어쓸 수 없음) →
`AgvFotaGateway.exe` 를 `AgvFotaGateway.exe.old` 로 이름 바꿈 → zip 안 `AgvFotaGateway\` 의 파일을 설치 폴더로 복사(`appsettings.json` 은 빼고, `data\` 는 zip 에 없음) →
`install-service.bat` 재실행(또는 `net start AgvFotaGateway`) → **설정** 화면에서 버전 확인.
수동 되돌리기(9-5): 서비스 중지 → `AgvFotaGateway.exe` 를 `.bad` 로 → `.old` 를 `AgvFotaGateway.exe` 로 → 서비스 시작.

## 하지 말 것

- csproj 버전을 안 올리고 정식 배포 (서버가 새 버전으로 인식하지 못함, 태그 푸시는 CI 가 거부)
- 종단 시험이 실패한 채로 정식 배포를 다시 돌리기 — 원인을 고친 커밋으로
- 테스트 빌드를 Latest 릴리스로 — dev 프리릴리스로만
- 같은 태그에 자산 덮어쓰기 — 새 버전으로
- 업데이트 출처를 사내 서버·`file://`·http 로 바꾸라고 안내하기 — 서버가 거부한다(github.com·api.github.com 만)
- 업데이트 확인을 api.github.com 에만 의존하도록 고치기 — 사내에서 "확인 실패" 만 뜬다
- 배포 작업이 실행 중인 서버에 수동으로 파일 교체 — 작업이 끝난 뒤 (자동 적용은 스스로 거른다)
- 릴리스에서 `appsettings.json` 기본값을 바꿀 때 설치된 서버의 설정이 바뀔 거라 기대하기 — 업데이트는 appsettings.json 을 덮어쓰지 않는다. 새 키는 코드 기본값으로 동작하게 만든다
