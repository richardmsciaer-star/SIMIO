# GUÍA OPERACIONAL: DIGITAL TWIN EDGE NODE (B2B)

Esta guía detalla los pasos deterministas para levantar la arquitectura distribuida tanto en tu entorno de pruebas local como en la PC de tu cliente final.

## 1. PREPARACIÓN DEL MODELO SIMIO (CRÍTICO PARA DASHBOARD)
Para que el tablero web reciba y alinee los indicadores (como tamaño de colas, costos, uso de recursos) sin escribir archivos masivos, debes configurarlos explícitamente en el modelo `.spfx`:
1. Abre tu modelo en **Simio Desktop**.
2. Navega a la pestaña de tu **Experiment** (ej. *Experiment1*).
3. En la cinta superior (Ribbon), busca la sección **Responses** y añade tus indicadores (KPIs).
   - Ejemplo de Nombre: `Cola_Equipaje_Max`
   - Expresión: `ServerEquipaje.InputBuffer.Contents.Maximum`
   - Ejemplo de Nombre: `Tiempo_Proceso_Promedio`
   - Expresión: `ModelEntity.Population.TimeInSystem.Average`
4. **Guarda el modelo `.spfx`** en la carpeta `Models` de este proyecto. El nombre que le des a la Respuesta (Response) será el nombre exacto que aparecerá en el dashboard web.

---

## 2. PRUEBA LOCAL INMEDIATA (TU MÁQUINA)
Para probar todo el flujo de extremo a extremo ahora mismo sin necesidad de Cloudflare (usando tu propia PC como Cliente y como Nube simultáneamente):

### Terminal 1: Iniciar el Edge Daemon (El "Motor de Simio" en RAM)
Abre PowerShell o CMD y ejecuta:
```powershell
cd "d:\2026\Sciaer\Proyectos\20260201 - Miguel IGAM\SimioOrchestratorSuite\SimioEdgeDaemon"
dotnet run
```
*Esto cargará Simio en memoria y quedará escuchando en `http://localhost:5050`.*

### Terminal 2: Iniciar el Dashboard Web (La Nube)
Abre otra ventana de PowerShell y ejecuta:
```powershell
cd "d:\2026\Sciaer\Proyectos\20260201 - Miguel IGAM\SimioOrchestratorSuite"
python Orchestrator\app.py
```
*(Nota: Por defecto, el código apunta localmente a `http://localhost:5050` si no declaras una variable de túnel).*

**Prueba:** Abre tu navegador en `http://localhost:8000`, selecciona el modelo, ingresa variables y lánzalo. El log de la web mostrará la ejecución delegada al puerto 5050.

---

## 3. DESPLIEGUE FINAL (PC DEL CLIENTE + TU SERVIDOR CLOUD)
Cuando vayas a instalar esto en el cliente físico:

1. **En la PC del Cliente:** 
   - Copia la carpeta `SimioEdgeDaemon`.
   - Ejecuta `dotnet run` (requiere .NET 8 SDK).
   - Instala y ejecuta Cloudflare: `cloudflared tunnel --url http://localhost:5050`
   - Copia la URL HTTPS generada (ej. `https://cliente-aeropuerto.trycloudflare.com`).

2. **En tu Servidor en la Nube:**
   - Antes de iniciar `app.py` o tu servicio web, inyecta la URL del túnel.
   - En Windows Server: `$env:EDGE_TUNNEL_URL="https://cliente-aeropuerto.trycloudflare.com"`
   - En Linux/Docker: `export EDGE_TUNNEL_URL="https://cliente-aeropuerto.trycloudflare.com"`
   - Inicia la App: `python Orchestrator/app.py`
