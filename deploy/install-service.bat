@echo off
:: Encoding: CP949 (EUC-KR) - Korean Windows cmd reads batch files in the OEM code page; keep this file CP949, not UTF-8.
:: AGV FOTA Gateway 를 Windows 서비스로 등록하고 시작 (관리자 권한 필요). 부팅 시 자동 시작.
:: 다시 실행하면 서비스를 지우지 않고 멈춘 뒤 설정만 갱신한다 (sc config).
:: 방화벽: appsettings.json 의 Urls(HTTP), Gateway.UdpPort/RfidPort(브리지 UDP), ACS 중계 Server 모드면 Gateway.Acs.Port 를 허용
::         (appsettings.json 을 읽지 못하면 HTTP 8080, UDP 50000/50001)
:: 권한: 서비스는 LocalSystem 으로 이 폴더의 exe 를 실행하므로 설치 폴더는 Administrators, SYSTEM 만 쓰기, Users 는 읽기/실행
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

:: 포트: appsettings.json 에서 읽는다 (HTTP 포트 목록, UDP, RFID, ACS Server 포트 또는 0)
set "HTTPPORTS=8080"
set "UDPPORT=50000"
set "RFIDPORT=50001"
set "ACSPORT=0"
for /f "usebackq tokens=1-4" %%a in (`powershell -NoProfile -NonInteractive -Command "try { $j = ConvertFrom-Json -InputObject (Get-Content -Raw -LiteralPath 'appsettings.json'); $g = $j.Gateway; $h = @(foreach ($m in [regex]::Matches([string]$j.Urls, ':(\d+)')) { $m.Groups[1].Value }) -join ','; if (-not $h) { $h = '8080' }; $u = 50000; if ($null -ne $g.UdpPort) { $u = [int]$g.UdpPort }; $r = 50001; if ($null -ne $g.RfidPort) { $r = [int]$g.RfidPort }; $a = 0; if ($g.Acs.Enabled -and $g.Acs.Mode -eq 'Server') { $a = [int]$g.Acs.Port }; Write-Output ($h + ' ' + $u + ' ' + $r + ' ' + $a) } catch { }"`) do (set "HTTPPORTS=%%a" & set "UDPPORT=%%b" & set "RFIDPORT=%%c" & set "ACSPORT=%%d")
set "UDPPORTS=%UDPPORT%"
if not "%RFIDPORT%"=="0" set "UDPPORTS=%UDPPORT%,%RFIDPORT%"
for /f "tokens=1 delims=," %%p in ("%HTTPPORTS%") do set "WEBPORT=%%p"
echo 포트: HTTP %HTTPPORTS% / 브리지 UDP %UDPPORTS% / ACS 중계 수신 %ACSPORT% (0 = 없음)

:: 서비스: 있으면 멈추고 설정 갱신 (지우고 다시 만들면 services.msc 가 열려 있을 때 '삭제 표시됨' 으로 서비스가 없어질 수 있음)
sc query %NAME% >nul 2>&1 || goto create
echo 기존 서비스 중지
sc stop %NAME% >nul 2>&1
set /a N=0
:waitstop
sc query %NAME% | findstr /C:"STOPPED" >nul && goto config
set /a N+=1
if %N% GEQ 30 (echo [경고] 서비스가 30초 안에 멈추지 않았습니다. 설정은 갱신하고 계속합니다. & goto config)
timeout /t 1 /nobreak >nul
goto waitstop
:config
sc config %NAME% binPath= "\"%EXE%\"" start= auto DisplayName= "AGV FOTA Gateway" || (echo 서비스 설정 갱신 실패 - services.msc, 이벤트 뷰어를 닫고 다시 실행하세요 & pause & exit /b 1)
goto registered
:create
sc create %NAME% binPath= "\"%EXE%\"" start= auto DisplayName= "AGV FOTA Gateway" || (echo 서비스 등록 실패 - 삭제 표시 상태면 services.msc, 이벤트 뷰어를 닫거나 재부팅한 뒤 다시 실행하세요 & pause & exit /b 1)
:registered
sc description %NAME% "AGV Wi-Fi 브리지 / DSP 펌웨어 무선 업데이트 서버 (http://localhost:%WEBPORT%)"
sc failure %NAME% reset= 86400 actions= restart/5000/restart/10000/restart/30000

:: 설치 폴더 권한: 상속 끊고 Administrators(S-1-5-32-544), SYSTEM(S-1-5-18) 모든 권한, Users(S-1-5-32-545) 읽기/실행. 하위 항목은 이 설정을 상속
if "%~p0"=="\" (echo [경고] 드라이브 루트에 있어 폴더 권한은 바꾸지 않습니다. 전용 폴더에 설치하세요. & goto acl_done)
icacls "%~dp0." /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q >nul || echo [경고] 설치 폴더 권한 설정 실패
icacls "%~dp0*" /reset /T /C /Q >nul 2>&1
:acl_done

netsh advfirewall firewall delete rule name="AGV FOTA Gateway HTTP" >nul 2>&1
netsh advfirewall firewall delete rule name="AGV FOTA Gateway Bridge UDP" >nul 2>&1
netsh advfirewall firewall delete rule name="AGV FOTA Gateway ACS TCP" >nul 2>&1
netsh advfirewall firewall add rule name="AGV FOTA Gateway HTTP" dir=in action=allow protocol=TCP localport=%HTTPPORTS% >nul
netsh advfirewall firewall add rule name="AGV FOTA Gateway Bridge UDP" dir=in action=allow protocol=UDP localport=%UDPPORTS% >nul
if not "%ACSPORT%"=="0" netsh advfirewall firewall add rule name="AGV FOTA Gateway ACS TCP" dir=in action=allow protocol=TCP localport=%ACSPORT% >nul

sc start %NAME%
set /a N=0
:waitrun
timeout /t 1 /nobreak >nul
sc query %NAME% | findstr /C:"RUNNING" >nul && goto started
set /a N+=1
if %N% LSS 15 goto waitrun
echo 서비스 시작 실패 - 이벤트 뷰어 또는 data\logs 확인
pause
exit /b 1
:started
echo.
echo 서비스 실행 중: http://localhost:%WEBPORT%
start "" http://localhost:%WEBPORT%
pause
