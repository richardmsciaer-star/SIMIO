@echo off
echo ========================================================
echo   PUBLICACION DE SIMIO EDGE DAEMON (STANDALONE CLIENT)
echo ========================================================
echo.
echo Compilando ejecutable nativo para Windows x64 (Autocontenido)...
echo.

cd SimioEdgeDaemon
dotnet publish -c Release -r win-x64 --self-contained true

echo.
echo ========================================================
echo   PUBLICACION EXITOSA
echo ========================================================
echo.
echo Tu cliente puede encontrar los archivos en:
echo SimioEdgeDaemon\bin\Release\net9.0-windows\win-x64\publish\
echo.
echo Instrucciones para el cliente:
echo 1. Copiar la carpeta 'publish' completa a la PC destino.
echo 2. Configurar 'appsettings.json' con la URL de Vercel (si no usa la local).
echo 3. Ejecutar 'SimioEdgeDaemon.exe'.
echo.
pause
