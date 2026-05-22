import sqlite3
import os

db_path = os.path.join(os.path.dirname(__file__), 'jobs.db')
conn = sqlite3.connect(db_path)
c = conn.cursor()
c.execute("UPDATE tasks SET status = 'FAILED' WHERE status = 'RUNNING'")
conn.commit()
conn.close()
print("Se han limpiado y cancelado las tareas estancadas en RUNNING.")
