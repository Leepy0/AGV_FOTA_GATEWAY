@echo off
:: Encoding: CP949 (EUC-KR) - Korean Windows cmd reads batch files in the OEM code page; keep this file CP949, not UTF-8.
:: 서비스 삭제 (데이터 폴더 data\ 는 남김)
net session >nul 2>&1 || (echo 관리자 권한으로 실행하세요 & pause & exit /b 1)
sc stop AgvFotaGateway >nul 2>&1
timeout /t 3 >nul
sc delete AgvFotaGateway
netsh advfirewall firewall delete rule name="AGV FOTA Gateway HTTP" >nul 2>&1
netsh advfirewall firewall delete rule name="AGV FOTA Gateway Bridge UDP" >nul 2>&1
netsh advfirewall firewall delete rule name="AGV FOTA Gateway ACS TCP" >nul 2>&1
echo 삭제했습니다.
pause
