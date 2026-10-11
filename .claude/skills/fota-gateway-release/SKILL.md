---
name: fota-gateway-release
description: "AGV FOTA Gateway(ASP.NET Core Blazor 웹 서버, 사내 PC Windows 서비스)를 빌드·릴리스·설치·업데이트할 때 사용. '릴리스 올려줘', '배포해줘', '서버 설치', '버전 올려줘', 코드 수정 후 배포가 필요할 때."
---

# AGV FOTA Gateway 빌드·릴리스·설치

웹 서버지만 배포 방식은 WPF 자동 업데이트 프로그램과 같다: **정식 릴리스를 올리면 설치된 서버들이 스스로 받아 적용**한다.
`wpf-build-release` 스킬의 "경로 A: 정식 배포" 와 `dotnet-self-update` 스킬의 핵심 결정(매니페스트 확인, SHA-256, .old 되돌리기, 채널)을 그대로 따르고,
아래가 이 프로젝트의 구체 값이다.

| 항목 | 값 |
|---|---|
| 저장소 | `Leepy0/AGV_FOTA_GATEWAY` (**비공개** — github.com 매니페스트 주소가 404 라 사내 자동 업데이트는 공개 전환 또는 사내 BaseUrl 필요) |
| 자산 | `AgvFotaGateway_win-x64.zip`(고정 이름), `.sha256`, `AgvFotaGateway.json` |
| 매니페스트 주소 | `https://github.com/Leepy0/AGV_FOTA_GATEWAY/releases/latest/download/AgvFotaGateway.json` |
| 버전 원본 | `src/Gateway.Server/Gateway.Server.csproj` `<Version>` — 태그와 다르면 CI 가 실패 |
| 채널 | CI `release` / `dev`(main 테스트 프리릴리스) / 로컬 `local` |

## 공통 전제

- 브랜치 `main`. commit·push·태그·릴리스는 묻지 않고 진행한다. 기존 릴리스·태그 삭제, force push 는 허가를 받는다.
- 빌드·릴리스는 GitHub Actions(`.github/workflows/build.yml`)가 한다. 로컬에서 `dotnet publish` 를 돌릴 필요는 없다.
- 보고는 버전, 릴리스 URL, 변경 요약 위주로 짧게.

## 1. 코드 수정 후 검증

```
dotnet build && dotnet test
```

하드웨어 없이 동작 확인: `python3 tests/fake_esp.py --agv 7 --server 127.0.0.1:50000 --listen 50010` (가짜 브리지 0.5.0, B안) 을 띄우고
`dotnet run --project src/Gateway.Server` → `GET /api/agvs` 에 7번이 보이면 REST(`/api/firmware` 에 `.dspfw`, `/api/jobs`)로 작업을 돌려 본다.
메인보드 OTA 는 브리지 B안(0x32 스테이징 → 0x34 적용 → 0x33 보고)이며 서버는 메인보드와 직접 프레임을 주고받지 않는다.

## 2. 릴리스 (정식 배포)

1. **버전 결정**: 마지막 태그(`git tag --sort=-v:refname | head -1`)보다 올린다. 버그 수정 patch, 기능 추가 minor. 사용자가 지정하면 그대로.
2. **버전 반영**: `src/Gateway.Server/Gateway.Server.csproj` 의 `<Version>` 을 같은 값으로 바꾸고 commit·push.
   서버의 업데이트 확인은 "릴리스 태그 버전 > 실행 중 버전" 비교라 csproj 와 태그가 같아야 한다.
3. **정식 배포 실행** — 둘 중 하나. Actions 가 빌드·테스트·publish 뒤 태그와 Latest Release 를 만든다.
   - `git tag vX.Y.Z && git push origin vX.Y.Z` (csproj 와 같아야 함)
   - 태그 푸시가 안 되는 환경(Claude 클라우드 세션의 git 프록시는 태그 푸시를 끊는다): Actions `build` 를 `workflow_dispatch` 로
     `release=true` 입력과 함께 실행 (GitHub MCP `actions_run_trigger` inputs `{"release":"true"}`, 또는 Actions 탭 → Run workflow → 정식 배포 체크).
     csproj 버전으로 태그를 만든다. 그 버전 태그가 이미 다른 커밋에 있으면 실패한다.
   - `AgvFotaGateway_win-x64.zip`(고정 이름) — 최상위 `AgvFotaGateway\` 폴더 안에 런타임 포함 단일 exe + appsettings.json + 배포 bat + DEPLOY.md + SYSTEM_FLOW.md + `images\`
   - `AgvFotaGateway_win-x64.zip.sha256`, `AgvFotaGateway.json`(매니페스트). 사내 서버로 배포하면 `Update.BaseUrl` 또는 `Update.ManifestUrl` 로 가리킨다
4. **릴리스 노트**: 이전 태그 이후 커밋 메시지로 자동 생성(Co-Authored-By·Claude-Session 줄 제거). 다듬고 싶으면 릴리스를 편집한다.
5. **검증**: Actions 초록(`actions_list` → `list_workflow_runs`), Release 자산 3개(`get_release_by_tag`): zip, `.sha256`, `AgvFotaGateway.json` 의 `version`·`sha256`.
   공개 저장소면 `curl -sL https://github.com/Leepy0/AGV_FOTA_GATEWAY/releases/latest/download/AgvFotaGateway.json` 에 새 버전이 보이는지.
   설치된 서버의 **서버 업데이트** 화면에서 "지금 확인" 이 새 버전을 보이는지.
6. **사내망·비공개 주의**: 저장소가 비공개인 동안은 github.com 매니페스트 주소가 404 → 서버는 "정식 릴리스가 없거나 접근할 수 없음" 을 보인다.
   공개로 바꾸거나(AmrMapEditor 처럼), 릴리스 자산 3개를 사내 서버에 복사하고 `Update.BaseUrl` 을 그 주소로 바꾼다. 처음 릴리스할 때 한 줄로 알린다.

테스트 빌드: main push 마다 `dev` 프리릴리스를 덮어쓴다(`AgvFotaGateway-dev_win-x64.zip`). 자동 업데이트 대상이 아니며 정식 릴리스로 올리지 않는다.

## 3. 처음 설치 (원클릭)

사용자에게 알려 줄 절차. `docs/DEPLOY.md` 와 같다.

1. Release 의 zip 을 서버 PC 의 `C:\` 에 풀기 — zip 안 `AgvFotaGateway\` 폴더째 풀려 `C:\AgvFotaGateway\AgvFotaGateway.exe` 가 된다 (`C:\Program Files` 아래는 피함)
2. `install-service.bat` 우클릭 → 관리자 권한으로 실행 — 서비스 등록(자동 시작·자동 재시작), 방화벽 TCP 8080 / UDP 50000·50001 허용, 브라우저 열기
3. 브리지에 서버 PC IP 를 `set server` 로 설정 → 호기 현황에 나타남

시험만 할 때는 `start.bat`(콘솔). 삭제는 `uninstall-service.bat`. 설정은 `appsettings.json`, 데이터는 `data\`.

## 4. 업데이트 적용

설치된 서버는 시작 3초 뒤와 6시간마다 매니페스트로 새 릴리스를 확인한다. 화면 **서버 업데이트** → 지금 확인 → 받기(SHA-256 검증) → 적용(현재 exe 는 `.old`, 재시작 약 10초).
`Update.AutoApply=true` 면 배포 작업이 없을 때 자동 적용. 잘못 배포했으면 "이전 버전으로 되돌리기". 적용 기록 `data\update\apply.log`.
적용이 실패하면 수동(`docs/DEPLOY.md` 9-4): 배포 작업이 없는지 확인 → 관리자 권한으로 `net stop AgvFotaGateway`(실행 중 exe 는 잠겨 있어 덮어쓸 수 없음) →
`AgvFotaGateway.exe` 를 `AgvFotaGateway.exe.old` 로 이름 바꿈 → zip 안 `AgvFotaGateway\` 의 파일을 설치 폴더로 복사(`appsettings.json` 은 빼고, `data\` 는 zip 에 없음) →
`net start AgvFotaGateway`(또는 `install-service.bat` 재실행) → **설정** 화면에서 버전 확인.

## 하지 말 것

- csproj 버전을 안 올리고 정식 배포 (서버가 새 버전으로 인식하지 못함, 태그 푸시는 CI 가 거부)
- 테스트 빌드를 Latest 릴리스로 — dev 프리릴리스로만
- 같은 태그에 자산 덮어쓰기 — 새 버전으로
- 업데이트 확인을 api.github.com 에만 의존하도록 고치기 — 사내에서 "확인 실패" 만 뜬다
- 배포 작업이 실행 중인 서버에 수동으로 파일 교체 — 작업이 끝난 뒤 (자동 적용은 스스로 거른다)
- 릴리스에서 `appsettings.json` 기본값을 바꿀 때 설치된 서버의 설정이 바뀔 거라 기대하기 — 업데이트는 appsettings.json 을 덮어쓰지 않는다. 새 키는 코드 기본값으로 동작하게 만든다
