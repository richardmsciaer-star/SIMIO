@echo off
echo Compilando SimioRunner (optimizando COM)...
dotnet build "D:\2026\AerostAI\AerostAI Technik\SimioConsolePackage\SimioRunner\SimioRunner.csproj" -c Release
echo Matando procesos...
taskkill /IM SimioRunner.exe /F 2>nul
echo Copiando DLL a Runner...
copy /Y "D:\2026\AerostAI\AerostAI Technik\SimioConsolePackage\SimioRunner\bin\Release\net9.0-windows\SimioRunner.dll" "Runner\SimioRunner.dll"
copy /Y "D:\2026\AerostAI\AerostAI Technik\SimioConsolePackage\SimioRunner\bin\Release\net9.0-windows\SimioRunner.exe" "Runner\SimioRunner.exe"
echo Listo. Por favor, corre una nueva simulacion.
pause
