import re

with open('Orchestrator/app.py', 'r', encoding='utf-8') as f:
    code = f.read()

# Replace imports
code = code.replace("import sqlite3", "from db_utils import execute_query, init_db\nimport sqlite3")

# Replace init_db block
init_db_block = """def init_db():
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
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
    try: cur.execute("ALTER TABLE tasks ADD COLUMN model_id TEXT")
    except: pass
    try: cur.execute("ALTER TABLE tasks ADD COLUMN results TEXT")
    except: pass
    try: cur.execute("ALTER TABLE tasks ADD COLUMN log TEXT")
    except: pass
    
    # Limpieza Automática: Fallar tareas colgadas y borrar tareas de más de 5 horas
    cur.execute("UPDATE tasks SET status='FAILED (Canceled by restart)' WHERE status='RUNNING' OR status='PENDING'")
    cur.execute("DELETE FROM tasks WHERE created_at < datetime('now', '-5 hours')")
    
    conn.commit()
    conn.close()

init_db()"""

code = code.replace(init_db_block, "init_db()")

# We'll use manual replacements for endpoints since execute_query returns dicts/lists instead of rows
code = code.replace("conn = sqlite3.connect(DB_PATH)", "")
code = code.replace("conn.row_factory = sqlite3.Row", "")
code = code.replace("cur = conn.cursor()", "")
code = code.replace("conn.commit()", "")
code = code.replace("conn.close()", "")

# Fix index
code = code.replace("""        cur.execute("SELECT id, status, created_at FROM tasks ORDER BY created_at DESC LIMIT 20")
        rows = cur.fetchall()""", "        rows = execute_query('SELECT id, status, created_at FROM tasks ORDER BY created_at DESC LIMIT 20', fetchall=True)")

# Fix start_task_thread
code = code.replace("""            try:
                cur.execute("UPDATE tasks SET stage=?, retries=retries+1 WHERE id=?", ("SIMULANDO", task_id))
            except sqlite3.OperationalError:
                pass""", "            execute_query('UPDATE tasks SET stage=?, retries=retries+1 WHERE id=?', ('SIMULANDO', task_id), commit=True)")
code = code.replace("""            try:
                cur.execute("UPDATE tasks SET stage=? WHERE id=?", ("SIMULANDO", task_id))
            except sqlite3.OperationalError:
                pass""", "            execute_query('UPDATE tasks SET stage=? WHERE id=?', ('SIMULANDO', task_id), commit=True)")
code = code.replace("""            cur.execute("UPDATE tasks SET status=?, completed_at=?, results=?, log=? WHERE id=?",
                        (status, datetime.now().isoformat(), json.dumps(result) if result else None, log_content, task_id))""", "            execute_query('UPDATE tasks SET status=?, completed_at=?, results=?, log=? WHERE id=?', (status, datetime.now().isoformat(), json.dumps(result) if result else None, log_content, task_id), commit=True)")
code = code.replace("""            cur.execute("UPDATE tasks SET status=?, completed_at=?, stage=? WHERE id=?",
                        (f"ERROR: {str(e)}", datetime.now().isoformat(), "FALLIDO", task_id))""", "            execute_query('UPDATE tasks SET status=?, completed_at=?, stage=? WHERE id=?', (f'ERROR: {str(e)}', datetime.now().isoformat(), 'FALLIDO', task_id), commit=True)")

# Fix create_task
code = code.replace("""    cur.execute("INSERT INTO tasks (id, params, status, created_at, model_id) VALUES (?, ?, ?, ?, ?)",
                (task_id, json.dumps(params), "PENDING", datetime.now().isoformat(), model_id))""", "    execute_query('INSERT INTO tasks (id, params, status, created_at, model_id) VALUES (?, ?, ?, ?, ?)', (task_id, json.dumps(params), 'PENDING', datetime.now().isoformat(), model_id), commit=True)")

# Fix run_task
code = code.replace("""    cur.execute("DELETE FROM tasks WHERE created_at < datetime('now', '-5 hours')")
    cur.execute("INSERT INTO tasks (id, params, status, created_at, stage, short_code, model_id) VALUES (?, ?, ?, ?, ?, ?, ?)",
                (task_id, json.dumps(params), "PENDING", datetime.now().isoformat(), "INICIANDO", short_code, model_id))""", """    execute_query("DELETE FROM tasks WHERE created_at < datetime('now', '-5 hours')", commit=True)
    execute_query('INSERT INTO tasks (id, params, status, created_at, stage, short_code, model_id) VALUES (?, ?, ?, ?, ?, ?, ?)', (task_id, json.dumps(params), 'PENDING', datetime.now().isoformat(), 'INICIANDO', short_code, model_id), commit=True)""")

# Fix agent_poll
code = code.replace("""        cur.execute("SELECT id, params, model_id FROM tasks WHERE status='PENDING' ORDER BY created_at ASC LIMIT 1")
        row = cur.fetchone()
        if row:
            task_id = row['id']
            cur.execute("UPDATE tasks SET status='RUNNING', stage='SIMULANDO' WHERE id=?", (task_id,))""", """        row = execute_query("SELECT id, params, model_id FROM tasks WHERE status='PENDING' ORDER BY created_at ASC LIMIT 1", fetchone=True)
        if row:
            task_id = row['id']
            execute_query("UPDATE tasks SET status='RUNNING', stage='SIMULANDO' WHERE id=?", (task_id,), commit=True)""")

# Fix agent_callback
code = code.replace("""        cur.execute("UPDATE tasks SET status=?, completed_at=?, stage=?, results=?, log=? WHERE id=?",
                    (db_status, datetime.now().isoformat(), "COMPLETADO" if status == "COMPLETED" else "FALLIDO",
                     json.dumps(results) if results else None, log_content, task_id))""", """        execute_query("UPDATE tasks SET status=?, completed_at=?, stage=?, results=?, log=? WHERE id=?", (db_status, datetime.now().isoformat(), "COMPLETADO" if status == "COMPLETED" else "FALLIDO", json.dumps(results) if results else None, log_content, task_id), commit=True)""")

# Fix task_page
code = code.replace("""        cur.execute("SELECT * FROM tasks WHERE id=?", (task_id,))
        row = cur.fetchone()""", """        row = execute_query("SELECT * FROM tasks WHERE id=?", (task_id,), fetchone=True)""")

# Fix task_status
code = code.replace("""        try:
            cur.execute('SELECT status, created_at, completed_at, stage, log FROM tasks WHERE id = ?', (task_id,))
        except sqlite3.OperationalError:
            cur.execute('SELECT status, created_at, completed_at, "UNKNOWN" as stage, NULL as log FROM tasks WHERE id = ?', (task_id,))
            
        row = cur.fetchone()""", """        row = execute_query('SELECT status, created_at, completed_at, stage, log FROM tasks WHERE id = ?', (task_id,), fetchone=True)
        if not row:
            row = execute_query('SELECT status, created_at, completed_at, "UNKNOWN" as stage, NULL as log FROM tasks WHERE id = ?', (task_id,), fetchone=True)""")

code = code.replace("""        cur.execute("SELECT COUNT(*) as queue_pos FROM tasks WHERE (status='RUNNING' OR status='PENDING') AND created_at < ?", (row['created_at'],))
        queue_row = cur.fetchone()""", """        queue_row = execute_query("SELECT COUNT(*) as queue_pos FROM tasks WHERE (status='RUNNING' OR status='PENDING') AND created_at < ?", (row['created_at'],), fetchone=True)""")

# Fix task_by_code
code = code.replace("""        try:
            cur.execute("SELECT id FROM tasks WHERE short_code=?", (short_code.upper(),))
            row = cur.fetchone()
        except sqlite3.OperationalError:
            row = None""", """        row = execute_query("SELECT id FROM tasks WHERE short_code=?", (short_code.upper(),), fetchone=True)""")

# Fix run_simio
code = code.replace("""    cur.execute("INSERT INTO tasks (id, params, status, created_at, model_id) VALUES (?, ?, ?, ?, ?)",
                (task_id, json.dumps(params), "PENDING", datetime.now().isoformat(), model_id))""", """    execute_query("INSERT INTO tasks (id, params, status, created_at, model_id) VALUES (?, ?, ?, ?, ?)", (task_id, json.dumps(params), "PENDING", datetime.now().isoformat(), model_id), commit=True)""")

# Fix agent_failed
code = code.replace("""        cur.execute("UPDATE tasks SET status=?, completed_at=?, stage=?, log=? WHERE id=?",
                    (f"ERROR: {error_msg}", datetime.now().isoformat(), "FALLIDO", log_content, task_id))""", """        execute_query("UPDATE tasks SET status=?, completed_at=?, stage=?, log=? WHERE id=?", (f"ERROR: {error_msg}", datetime.now().isoformat(), "FALLIDO", log_content, task_id), commit=True)""")

with open('Orchestrator/app.py', 'w', encoding='utf-8') as f:
    f.write(code)

print("Done replacing.")
