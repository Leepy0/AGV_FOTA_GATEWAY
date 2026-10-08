---
name: fota-gateway-release
description: "AGV FOTA Gateway(ASP.NET Core Blazor 웹 서버, 사내 PC Windows 서비스)를 빌드·릴리스·설치·업데이트할 때 사용. '릴리스 올려줘', '배포해줘', '서버 설치', '버전 올려줘', 코드 수정 후 배포가 필요할 때."
---

# AGV FOTA Gateway 빌드·릴리스·설치

웹 서버지만 배포 방식은 WPF 자동 업데이트 프로그램과 같다: **정식 릴리스를 올리면 설치된 서버들이 스스로 받아 적용**한다.
`wpf-build-release` 스킬의 "경로 A: 정식 배포" 규칙을 그대로 따르고, 아래가 이 프로젝트의 구체 값이다.

## 공통 전제

- 브랜치 `main`. commit·push·태그·릴리스는 묻지 않고 진행한다. 기존 릴리스·태그 삭제, force push 는 허가를 받는다.
- 빌드·릴리스는 GitHub Actions(`.github/workflows/build.yml`)가 한다. 로컬에서 `dotnet publish` 를 돌릴 필요는 없다.
- 보고는 버전, 릴리스 URL, 변경 요약 위주로 짧게.

## 1. 코드 수정 후 검증

```
dotnet build && dotnet test
```

하드웨어 없이 동작 확인: DSP_OTA 저장소의 `dsp_ota_sim --agv 7 --server 127.0.0.1:50000 --listen 50010` 을 띄우고
`dotnet run --project src/Gateway.Server` → `GET /api/agvs` 에 7번이 보이면 REST(`/api/firmware`, `/api/jobs`)로 작업을 돌려 본다.

## 2. 릴리스 (정식 배포)

1. **버전 결정**: 마지막 태그(`git tag --sort=-v:refname | head -1`)보다 올린다. 버그 수정 patch, 기능 추가 minor. 사용자가 지정하면 그대로.
2. **버전 반영**: `src/Gateway.Server/Gateway.Server.csproj` 의 `<Version>` 을 같은 값으로 바꾸고 commit·push.
   서버의 업데이트 확인은 "릴리스 태그 버전 > 실행 중 버전" 비교라 csproj 와 태그가 같아야 한다.
3. **태그 푸시**: `git tag vX.Y.Z && git push origin vX.Y.Z`. Actions 가 빌드·테스트 후 Release 에 자산을 올린다.
   - `AgvFotaGateway_X.Y.Z_win-x64.zip` — 런타임 포함 단일 exe + appsettings.json + 배포 bat + DEPLOY.md
   - `latest.json` — 사내 HTTP 피드용 (`Update.Source=Url`)
4. **릴리스 노트**: Actions 가 자동 생성한다. 사용자 관점 변경 사항을 한글 3~6줄로 다듬고 싶으면 릴리스를 편집한다.
5. **검증**: Actions 가 초록인지, Release 에 zip 과 latest.json 두 개가 있는지. 설치된 서버의 **서버 업데이트** 화면에서 "지금 확인" 이 새 버전을 보이는지.
6. **사내망 주의**: 서버 PC 가 github.com 에 못 나가면 `Update.Source=Url` 로 바꾸고 릴리스 자산을 사내 HTTP 폴더에 복사한다 (`latest.json` 의 `url` 을 사내 주소로). 비공개 저장소면 `Update.GitHubToken` 이 필요하다. 처음 릴리스할 때 한 줄로 알린다.

테스트 빌드는 태그 없이 push → Actions 아티팩트로만 나온다(릴리스 아님). 정식 릴리스로 올리지 않는다.

## 3. 처음 설치 (원클릭)

사용자에게 알려 줄 절차. `docs/DEPLOY.md` 와 같다.

1. Release 의 zip 을 서버 PC 에 풀기 (예: `C:\AgvFotaGateway`)
2. `install-service.bat` 우클릭 → 관리자 권한으로 실행 — 서비스 등록(자동 시작·자동 재시작), 방화벽 TCP 8080 / UDP 50000·50001 허용, 브라우저 열기
3. 브리지에 서버 PC IP 를 `set server` 로 설정 → 호기 현황에 나타남

시험만 할 때는 `start.bat`(콘솔). 삭제는 `uninstall-service.bat`. 설정은 `appsettings.json`, 데이터는 `data\`.

## 4. 업데이트 적용

설치된 서버는 6시간마다(기본) 새 릴리스를 확인한다. 화면 **서버 업데이트** → 지금 확인 → 다운로드 → 적용(재시작 약 10초).
`Update.AutoApply=true` 면 배포 작업이 없을 때 자동 적용. 적용 기록 `data\update\apply.log`.
적용이 실패하면 수동: zip 을 같은 폴더에 덮어쓰고(`appsettings.json`, `data\` 제외) `install-service.bat` 재실행.

## 하지 말 것

- csproj 버전을 안 올리고 태그만 올리기 (서버가 새 버전으로 인식하지 못함)
- 같은 태그에 자산 덮어쓰기 — 새 버전으로
- 배포 작업이 실행 중인 서버에 수동으로 파일 교체 — 작업이 끝난 뒤 (자동 적용은 스스로 거른다)
- 릴리스에서 `appsettings.json` 기본값을 바꿀 때 설치된 서버의 설정이 바뀔 거라 기대하기 — 업데이트는 appsettings.json 을 덮어쓰지 않는다. 새 키는 코드 기본값으로 동작하게 만든다
