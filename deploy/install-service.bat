@echo off
:: Encoding: CP949 (EUC-KR) - Korean Windows cmd reads batch files in the OEM code page; keep this file CP949, not UTF-8.
:: AGV FOTA Gateway 를 Windows 서비스로 등록하고 시작 (관리자 권한 필요). 부팅 시 자동 시작.
:: 방화벽: HTTP 8080, 브리지 UDP 50000/50001 허용
setlocal
cd /d "%~dp0"
net session >nul 2>&1 || (echo 관리자 권한으로 실행하세요 ^(우클릭 - 관리자 권한으로 실행^) & pause & exit /b 1)

set NAME=AgvFotaGateway
set EXE=%~dp0AgvFotaGateway.exe
if not exist "%EXE%" (echo AgvFotaGateway.exe 가 이 폴더에 없습니다: %~dp0 & pause & exit /b 1)
echo %~dp0 | findstr /I /C:"Program Files" >nul && (
  echo [경고] Program Files 아래에서는 서비스가 data 폴더와 업데이트 파일을 쓰지 못할 수 있습니다.
  echo        C:\AgvFotaGateway 같은 일반 폴더에 풀어서 설치하는 것을 권장합니다. 계속하려면 아무 키나 누르세요.
  pause >nul
)

sc query %NAME% >nul 2>&1 && (echo 기존 서비스 중지 & sc stop %NAME% >nul 2>&1 & timeout /t 3 >nul & sc delete %NAME% >nul & timeout /t 2 >nul)

sc create %NAME% binPath= "\"%EXE%\"" start= auto DisplayName= "AGV FOTA Gateway" || (pause & exit /b 1)
sc description %NAME% "AGV Wi-Fi 브리지 / DSP 펌웨어 무선 업데이트 서버 (http://localhost:8080)"
sc failure %NAME% reset= 86400 actions= restart/5000/restart/10000/restart/30000

netsh advfirewall firewall delete rule name="AGV FOTA Gateway HTTP" >nul 2>&1
netsh advfirewall firewall delete rule name="AGV FOTA Gateway Bridge UDP" >nul 2>&1
netsh advfirewall firewall add rule name="AGV FOTA Gateway HTTP" dir=in action=allow protocol=TCP localport=8080 >nul
netsh advfirewall firewall add rule name="AGV FOTA Gateway Bridge UDP" dir=in action=allow protocol=UDP localport=50000,50001 >nul

sc start %NAME%
timeout /t 3 >nul
sc query %NAME% | findstr RUNNING >nul && (echo. & echo 서비스 실행 중: http://localhost:8080 & start "" http://localhost:8080) || (echo 서비스 시작 실패 - 이벤트 뷰어 또는 data\logs 확인)
pause
