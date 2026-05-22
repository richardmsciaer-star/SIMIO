@echo off
title Servidor Orchestrador Simio
color 0A

echo ======================================================
echo    INICIANDO SIMIO ORCHESTRATOR
echo ======================================================
echo.

cd "%~dp0Orchestrator"

:: Instalar requerimientos si no existen
if not exist "venv" (
    echo [INFO] Creando entorno virtual...
    python -m venv venv
    call venv\Scripts\activate.bat
    echo [INFO] Instalando dependencias...
    pip install flask
) else (
    call venv\Scripts\activate.bat
)

echo [INFO] Levantando servidor web...
python app.py

pause
