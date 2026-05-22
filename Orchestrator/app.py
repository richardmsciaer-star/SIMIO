from flask import Flask, request, jsonify, send_from_directory, render_template
import os
import sqlite3
import json
import uuid
import traceback
from datetime import datetime

import traceback
from datetime import datetime
import subprocess

# Configuración de rutas absoluta basada en la ubicación del script
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
app = Flask(__name__, template_folder=os.path.join(BASE_DIR, 'templates'))
DB_PATH = os.path.join(BASE_DIR, "jobs.db")
OUTPUTS_DIR = os.path.join(BASE_DIR, "outputs")
MODELS_DIR = os.path.join(os.path.dirname(BASE_DIR), 'Models')
RUNNER_DIR = os.path.join(os.path.dirname(BASE_DIR), 'Runner')

# Habilitar modo debug para desarrollo
app.config['DEBUG'] = True
app.config['JSON_AS_ASCII'] = False

def init_db():
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()
    cur.execute('''CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    params TEXT,
                    status TEXT,
                    retries INTEGER DEFAULT 0,
                    created_at TIMESTAMP,
                    completed_at TIMESTAMP
                )''')
    try: cur.execute("ALTER TABLE tasks ADD COLUMN short_code TEXT")
    except: pass
    try: cur.execute("ALTER TABLE tasks ADD COLUMN stage TEXT")
    except: pass
    
    # Limpieza Automática: Fallar tareas colgadas y borrar tareas de más de 5 horas
    cur.execute("UPDATE tasks SET status='FAILED (Canceled by restart)' WHERE status='RUNNING' OR status='PENDING'")
    cur.execute("DELETE FROM tasks WHERE created_at < datetime('now', '-5 hours')")
    
    conn.commit()
    conn.close()

@app.route('/')
def index():
    config = {"models": []}
    config_path = os.path.join(BASE_DIR, 'model_config.json')
    if os.path.exists(config_path):
        try:
            with open(config_path, 'r', encoding='utf-8') as f:
                config = json.load(f)
        except:
            pass

    # Auto-detect models in the Models/ directory
    if os.path.exists(MODELS_DIR):
        existing_model_names = [m.get('name') for m in config.get('models', [])]
        for f in os.listdir(MODELS_DIR):
            if f.lower().endswith('.spfx'):
                model_name = f
                if model_name not in existing_model_names:
                    config.setdefault('models', []).append({
                        "id": model_name,
                        "name": model_name,
                        "description": "Modelo detectado automáticamente.",
                        "groups": []
                    })
    
    # Obtener tareas de la base de datos
    try:
        conn = sqlite3.connect(DB_PATH)
        conn.row_factory = sqlite3.Row
        cur = conn.cursor()
        cur.execute("SELECT id, status, created_at FROM tasks ORDER BY created_at DESC LIMIT 20")
        rows = cur.fetchall()
        conn.close()
    except:
        rows = []
    
    tasks = []
    for row in rows:
        tasks.append({
            'id': row['id'],
            'status': row['status'],
            'created_at': row['created_at']
        })
    
    return render_template('index.html', config=config, tasks=tasks)

def start_task_thread(task_id, params, model_id=None):
    import threading
    from edge_orchestrator import EdgeOrchestrator
    
    def run_automation():
        try:
            # URL del Agente Local en la PC del Cliente (Edge Node)
            # Para ambiente local de pruebas es http://localhost:5050
            EDGE_URL = os.environ.get("EDGE_TUNNEL_URL", "http://localhost:5050")
            orch = EdgeOrchestrator(edge_url=EDGE_URL)
            log_path = os.path.join(BASE_DIR, f"{task_id}.log")
            # Sincronizamos el UI con la fase de simulación
            conn = sqlite3.connect(DB_PATH)
            cur = conn.cursor()
            try:
                cur.execute("UPDATE tasks SET stage=? WHERE id=?", ("SIMULANDO", task_id))
                conn.commit()
            except sqlite3.OperationalError:
                pass # Si no existe la columna stage por ser un schema viejo, ignoramos
            finally:
                conn.close()

            result = orch.run(params=params, log_path=log_path, model_id=model_id)
            
            conn = sqlite3.connect(DB_PATH)
            cur = conn.cursor()
            status = "COMPLETED" if result else "FAILED"
            cur.execute("UPDATE tasks SET status=?, completed_at=? WHERE id=?",
                        (status, datetime.now().isoformat(), task_id))
            conn.commit()
            conn.close()

            # Convertimos la respuesta JSON en un pequeño CSV sintético para mantener compatibilidad con la UI (DataTables)
            task_dir = os.path.join(OUTPUTS_DIR, f"Simulacion_{task_id}")
            os.makedirs(task_dir, exist_ok=True)
            csv_path = os.path.join(task_dir, f"Reporte_Edge_{task_id}.csv")
            with open(csv_path, 'w', encoding='utf-8') as cf:
                cf.write("Indicador_KPI,Valor\n")
                if isinstance(result, dict):
                    for k, v in result.items():
                        cf.write(f"{k},{v}\n")
            
        except Exception as e:
            conn = sqlite3.connect(DB_PATH)
            cur = conn.cursor()
            cur.execute("UPDATE tasks SET status=?, completed_at=? WHERE id=?",
                        (f"ERROR: {str(e)}", datetime.now().isoformat(), task_id))
            conn.commit()
            conn.close()
    
    thread = threading.Thread(target=run_automation)
    thread.daemon = True
    thread.start()

@app.route('/api/tasks', methods=['POST'])
def create_task():
    data = request.json
    task_id = str(uuid.uuid4())
    params = data.get("parameters", {})
    model_id = data.get("model_id")
    
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()
    cur.execute("INSERT INTO tasks (id, params, status, created_at) VALUES (?, ?, ?, ?)",
                (task_id, json.dumps(params), "RUNNING", datetime.now().isoformat()))
    conn.commit()
    conn.close()
    
    start_task_thread(task_id, params, model_id)
    
    return jsonify({"task_id": task_id, "status": "RUNNING"})

@app.route('/run-task', methods=['POST'])
def run_task():
    data = request.form
    task_id = str(uuid.uuid4())
    short_code = task_id[:4].upper()
    model_id = data.get('model_id')
    params = {}
    for key, value in data.items():
        if key.startswith('param_'):
            param_key = key[6:]
            if value.lower() in ('true', 'false'):
                params[param_key] = value.lower() == 'true'
            else:
                try:
                    params[param_key] = float(value)
                except ValueError:
                    params[param_key] = value
    
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()
    # Limpieza de 5 horas antes de insertar
    cur.execute("DELETE FROM tasks WHERE created_at < datetime('now', '-5 hours')")
    cur.execute("INSERT INTO tasks (id, params, status, created_at, stage, short_code) VALUES (?, ?, ?, ?, ?, ?)",
                (task_id, json.dumps(params), "RUNNING", datetime.now().isoformat(), "INICIANDO", short_code))
    conn.commit()
    conn.close()
    
    start_task_thread(task_id, params, model_id)
    
    return jsonify({"task_id": task_id, "short_code": short_code, "status": "RUNNING"})

@app.route('/task/<task_id>')
def task_page(task_id):
    """Página de detalle de tarea - Versión Corregida"""
    try:
        conn = sqlite3.connect(DB_PATH)
        conn.row_factory = sqlite3.Row
        cur = conn.cursor()
        cur.execute("SELECT * FROM tasks WHERE id=?", (task_id,))
        row = cur.fetchone()
        conn.close()
        
        if not row:
            return "Tarea no encontrada", 404
        
        # Extraer mensaje de error si existe
        status = row['status']
        error_msg = None
        if status and status.startswith("ERROR:"):
            error_msg = status[6:].strip()
            status = "FAILED"

        task = {
            'id': row['id'],
            'status': status,
            'stage': None,
            'params': row['params'],
            'created_at': row['created_at'],
            'completed_at': row['completed_at'],
            'error_msg': error_msg,
            'log': ''
        }
        
        params = {}
        try:
            params = json.loads(row['params']) if row['params'] else {}
        except:
            pass
            
        csv_data = []
        csv_headers = []
        if status == 'COMPLETED':
            import csv
            task_dir = os.path.join(OUTPUTS_DIR, f"Simulacion_{task_id}")
            if os.path.exists(task_dir):
                for f in os.listdir(task_dir):
                    if f.startswith("Reporte_") and f.endswith(".csv"):
                        csv_path = os.path.join(task_dir, f)
                        try:
                            with open(csv_path, 'r', encoding='utf-8') as cf:
                                reader = csv.reader(cf)
                                try:
                                    csv_headers = next(reader)
                                    csv_data = [r for r in reader]
                                except StopIteration:
                                    pass
                        except Exception as e:
                            print(f"Error reading CSV: {e}")
                        break
        
        return render_template('task.html', task=task, params=params, stats={}, csv_headers=csv_headers, csv_data=csv_data)
    except Exception as e:
        traceback.print_exc()
        return f"Error interno: {str(e)}", 500

@app.route('/api/task/<task_id>/status')
def task_status(task_id):
    try:
        conn = sqlite3.connect(DB_PATH)
        conn.row_factory = sqlite3.Row
        cur = conn.cursor()
        
        # Primero revisamos si la columna 'stage' existe (por si es una BD vieja)
        try:
            cur.execute('SELECT status, created_at, completed_at, stage FROM tasks WHERE id = ?', (task_id,))
        except sqlite3.OperationalError:
            cur.execute('SELECT status, created_at, completed_at, "UNKNOWN" as stage FROM tasks WHERE id = ?', (task_id,))
            
        row = cur.fetchone()
        
        if not row:
            conn.close()
            return jsonify({"error": "Task not found"}), 404
            
        # Calcular posición en la cola
        cur.execute("SELECT COUNT(*) as queue_pos FROM tasks WHERE (status='RUNNING' OR status='PENDING') AND created_at < ?", (row['created_at'],))
        queue_row = cur.fetchone()
        queue_pos = queue_row['queue_pos'] if queue_row else 0
        conn.close()
            
        log_path = os.path.join(BASE_DIR, f"{task_id}.log")
        log_content = None
        stage = row['stage']
        if os.path.exists(log_path):
            with open(log_path, 'r', encoding='utf-8', errors='replace') as f:
                lines = f.readlines()
                log_content = "".join(lines[-50:])
                
                # Inferencia de stage si no está en DB
                if stage == "UNKNOWN" or not stage:
                    last_line = log_content.strip().split('\n')[-1] if log_content else ""
                    if "SIMULANDO" in last_line: stage = "SIMULANDO"
                    elif "INICIANDO" in last_line: stage = "INICIANDO"
                    elif "EXITOSAMENTE" in last_line: stage = "COMPLETADO"

        return jsonify({
            "status": row['status'],
            "log": log_content,
            "stage": stage,
            "queue_position": queue_pos
        })
    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500

@app.route('/task/code/<short_code>')
def task_by_code(short_code):
    try:
        conn = sqlite3.connect(DB_PATH)
        conn.row_factory = sqlite3.Row
        cur = conn.cursor()
        try:
            cur.execute("SELECT id FROM tasks WHERE short_code=?", (short_code.upper(),))
            row = cur.fetchone()
        except sqlite3.OperationalError:
            row = None # Columna short_code no existe
        conn.close()
        if row:
            from flask import redirect
            return redirect(f"/task/{row['id']}")
        return "Código de tarea no encontrado o expirado (borrado por sistema automático tras 5 horas).", 404
    except Exception as e:
        return f"Error interno: {str(e)}", 500

@app.route('/api/model/<model_id>/variables')
def get_model_variables(model_id):
    import requests
    EDGE_URL = os.environ.get("EDGE_TUNNEL_URL", "http://localhost:5050")
    
    try:
        # Aumentamos el timeout a 300s (5 minutos) porque los modelos aeroportuarios pesados tardan más de 60s en cargarse a la RAM por primera vez.
        response = requests.get(f"{EDGE_URL}/api/model/{model_id}/variables", timeout=300)
        response.raise_for_status()
        model_data = response.json().get("models", [{}])[0]
        
        # Cacheado local para la nube
        config_path = os.path.join(BASE_DIR, 'model_config.json')
        config = {"models": []}
        if os.path.exists(config_path):
            try:
                with open(config_path, 'r', encoding='utf-8') as f:
                    config = json.load(f)
            except:
                pass
        
        existing = next((m for m in config.get("models", []) if m["id"] == model_id), None)
        if existing:
            existing["groups"] = model_data["groups"]
        else:
            config.setdefault("models", []).append(model_data)
            
        with open(config_path, 'w', encoding='utf-8') as f:
            json.dump(config, f, indent=4, ensure_ascii=False)
            
        return jsonify(model_data)
        
    except requests.exceptions.RequestException as e:
        return jsonify({"error": f"Fallo al contactar el PC Cliente (Edge Node): {str(e)}"}), 504
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/api/run-simio', methods=['POST'])
def run_simio():
    """Ejecutar automatización completa de Simio"""
    data = request.json or {}
    params = data.get("parameters", {})
    model_id = data.get("model_id")
    task_id = str(uuid.uuid4())
    
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()
    cur.execute("INSERT INTO tasks (id, params, status, created_at) VALUES (?, ?, ?, ?)",
                (task_id, json.dumps(params), "RUNNING", datetime.now().isoformat()))
    conn.commit()
    conn.close()
    
    start_task_thread(task_id, params, model_id)
    
    return jsonify({"task_id": task_id, "status": "RUNNING"})

@app.route('/download/<task_id>/csv')
def download_csv(task_id):
    task_dir = os.path.join(OUTPUTS_DIR, f"Simulacion_{task_id}")
    if os.path.exists(task_dir):
        for f in os.listdir(task_dir):
            if f.startswith("Reporte_") and f.endswith(".csv"):
                return send_from_directory(task_dir, f, as_attachment=True)
    return "Archivo CSV no encontrado para esta tarea.", 404

if __name__ == "__main__":
    init_db()
    app.run(host="0.0.0.0", port=8000, debug=True)
