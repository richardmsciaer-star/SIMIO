import requests
import json
import datetime
import os

class EdgeOrchestrator:
    def __init__(self, edge_url):
        # Esta URL representa el PC físico del cliente conectado via túnel inverso
        self.edge_url = edge_url

    def find_model(self, models_dir):
        # Fallback local discovery
        if not os.path.exists(models_dir):
            return None
        for f in os.listdir(models_dir):
            if f.lower().endswith(".spfx"):
                return f
        return None

    def run(self, params=None, log_path=None, model_id=None):
        payload = params or {}
        stamp = datetime.datetime.now().strftime('%H:%M:%S')
        
        # Compatibilidad backward: si no viene el model_id explícito desde el Request, buscamos el localmente registrado.
        if not model_id:
            base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            models_dir = os.path.join(base_dir, "Models")
            model_id = self.find_model(models_dir)
            if not model_id:
                raise RuntimeError("No model_id provided and no fallback .spfx found.")

        endpoint = f"{self.edge_url}/api/model/{model_id}/simulate"
        
        if log_path:
            with open(log_path, 'a', encoding='utf-8') as f:
                f.write(f"[{stamp}] INICIANDO CONEXIÓN A EDGE NODE...\n")
                f.write(f"[{stamp}] TÚNEL: {self.edge_url}\n")
                f.write(f"[{stamp}] ENVIANDO PARÁMETROS: {json.dumps(payload)}\n")
                f.write(f"[{stamp}] SIMULANDO (Poder de cómputo delegado al cliente)...\n")
                
        try:
            # La simulación es asíncrona sobre la red, permitimos timeout de 4 hrs
            response = requests.post(endpoint, json=payload, timeout=14400)
            response.raise_for_status()
            data = response.json()
            
            stamp_end = datetime.datetime.now().strftime('%H:%M:%S')
            if log_path:
                with open(log_path, 'a', encoding='utf-8') as f:
                    f.write(f"[{stamp_end}] FLUJO COMPLETADO EXITOSAMENTE.\n")
                    f.write(f"[{stamp_end}] KPIs EXTRAÍDOS DESDE MEMORIA (ZERO-COPY):\n")
                    kpis = data.get('kpis', {})
                    for k, v in kpis.items():
                        f.write(f"[{stamp_end}]   - {k}: {v}\n")
            
            return data.get('kpis', {})
            
        except requests.exceptions.RequestException as e:
            stamp_err = datetime.datetime.now().strftime('%H:%M:%S')
            if log_path:
                with open(log_path, 'a', encoding='utf-8') as f:
                    f.write(f"[{stamp_err}] ERROR CRÍTICO DE TELEMETRÍA CON EDGE NODE: {str(e)}\n")
            raise RuntimeError(f"Fallo crítico de conexión con PC Cliente: {str(e)}")
