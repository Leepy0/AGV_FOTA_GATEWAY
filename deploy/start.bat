@echo off
:: AGV FOTA Gateway — 콘솔로 실행 (창을 닫으면 종료). 서비스 등록은 install-service.bat
cd /d "%~dp0"
echo AGV FOTA Gateway 를 시작합니다. 브라우저: http://localhost:8080
start "" http://localhost:8080
AgvFotaGateway.exe
pause
