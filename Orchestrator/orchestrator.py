import json
import os
import sys
import time
import datetime
import subprocess

def log(msg, log_path=None):
    stamp = f"[{datetime.datetime.now().strftime('%H:%M:%S')}] {msg}\n"
    sys.stdout.write(stamp)
    sys.stdout.flush()
    if log_path:
        with open(log_path, 'a', encoding='utf-8') as f:
            f.write(stamp)

class Orchestrator:
    def __init__(self):
        # Base paths
        self.base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        self.runner_dll = os.path.join(self.base_dir, "Runner", "SimioRunner.dll")
        self.models_dir = os.path.join(self.base_dir, "Models")
        self.outputs_dir = os.path.join(self.base_dir, "Outputs")
        
        # Ensure outputs exists
        os.makedirs(self.outputs_dir, exist_ok=True)

    def find_model(self):
        # Find the first .spfx file in the Models folder
        if not os.path.exists(self.models_dir):
            return None
        
        for f in os.listdir(self.models_dir):
            if f.lower().endswith(".spfx"):
                return os.path.join(self.models_dir, f)
        return None

    def run(self, params=None, timeout_seconds=14400, log_path=None):
        params = params or {}
        
        model_path = self.find_model()
        if not model_path:
            raise RuntimeError(f"No se encontro ningun archivo .spfx en la carpeta: {self.models_dir}")
        
        log(f"=== Iniciando ejecucion automatica ===", log_path)
        log(f"Modelo: {os.path.basename(model_path)}", log_path)
        
        # Create output directory for this simulation
        task_id = os.path.basename(log_path).split('.')[0] if log_path else datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
        task_output_dir = os.path.join(self.outputs_dir, f"Simulacion_{task_id}")
        os.makedirs(task_output_dir, exist_ok=True)
        
        # Build command: dotnet Runner.dll "model.spfx" --output-dir="..." --Param=Value
        cmd = ["dotnet", self.runner_dll, model_path, f"--output-dir={task_output_dir}"]
        
        # Pasamos los parámetros
        for k, v in params.items():
            if v is not None and str(v).strip() != "":
                cmd.append(f"--{k}={v}")

        log(f"Comando: {' '.join(cmd)}", log_path)
        
        # Run subprocess headless
        try:
            # Run in the same directory as the model to keep temp files there
            process = subprocess.Popen(
                cmd, 
                stdout=subprocess.PIPE, 
                stderr=subprocess.STDOUT,
                text=True,
                encoding='utf-8',
                cwd=self.models_dir
            )
            
            success_killed = False
            # Stream output in real time
            for line in process.stdout:
                decoded_line = ""
                try:
                    decoded_line = line
                    sys.stdout.write(line)
                except UnicodeEncodeError:
                    decoded_line = line.encode(sys.stdout.encoding or 'utf-8', errors='replace').decode(sys.stdout.encoding or 'utf-8')
                    sys.stdout.write(decoded_line)
                sys.stdout.flush()
                
                if log_path:
                    with open(log_path, 'a', encoding='utf-8') as f:
                        f.write(decoded_line)
                
                # Detect the end of the export to prevent COM hang
                if "CSV:" in decoded_line and "filas" in decoded_line:
                    log("Exportacion CSV finalizada. Terminando proceso runner.", log_path)
                    success_killed = True
                    process.kill()
                    break
                
            process.wait(timeout=timeout_seconds)
            
            if not success_killed and process.returncode != 0:
                raise RuntimeError(f"SimioRunner fallo con codigo {process.returncode}")
                
            log(f"Resultados guardados en: {task_output_dir}", log_path)
            log("=== FLUJO COMPLETADO ===", log_path)
            return True

        except subprocess.TimeoutExpired:
            process.kill()
            raise RuntimeError(f"Timeout: la simulacion tomo mas de {timeout_seconds} segundos")

def load_params_from_json(path):
    if not path or not os.path.exists(path):
        return {}
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)

def main(params_path=None):
    params = load_params_from_json(params_path) if params_path else {}
    orch = Orchestrator()
    return orch.run(params=params)

if __name__ == "__main__":
    default_params = os.path.join(os.getcwd(), "variables_ejemplo.json")
    params_path = default_params if os.path.exists(default_params) else None
    main(params_path=params_path)
