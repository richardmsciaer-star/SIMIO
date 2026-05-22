@echo off
echo ========================================================
echo Instalando SimioEdgeDaemon en el Inicio de Windows
echo ========================================================

set "STARTUP_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "TARGET=%~dp0SimioEdgeDaemon.exe"
set "SHORTCUT=%STARTUP_DIR%\SimioEdgeDaemon.lnk"

if not exist "%TARGET%" (
    echo ERROR: No se encuentra SimioEdgeDaemon.exe en esta carpeta.
    pause
    exit /b 1
)

echo Creando acceso directo en: %STARTUP_DIR%
powershell -Command "$wshell = New-Object -ComObject WScript.Shell; $shortcut = $wshell.CreateShortcut('%SHORTCUT%'); $shortcut.TargetPath = '%TARGET%'; $shortcut.WorkingDirectory = '%~dp0'; $shortcut.WindowStyle = 7; $shortcut.Save()"

echo.
echo INSTALACION COMPLETADA.
echo SimioEdgeDaemon se ejecutara automaticamente de forma minimizada al iniciar la PC.
pause
