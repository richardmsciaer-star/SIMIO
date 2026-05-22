import os
import sqlite3
import json

try:
    import psycopg2
    from psycopg2.extras import RealDictCursor
    PSYCOPG2_AVAILABLE = True
except ImportError:
    PSYCOPG2_AVAILABLE = False

DATABASE_URL = os.environ.get("DATABASE_URL") or os.environ.get("POSTGRES_URL") or os.environ.get("SUPABASE_DB_URL") or os.environ.get("POSTGRES_URL_NON_POOLING")
USE_POSTGRES = bool(DATABASE_URL and PSYCOPG2_AVAILABLE)

if not USE_POSTGRES:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))
    if os.environ.get("VERCEL"):
        DB_PATH = "/tmp/jobs.db"
    else:
        DB_PATH = os.path.join(BASE_DIR, "jobs.db")

def get_connection():
    if USE_POSTGRES:
        conn = psycopg2.connect(DATABASE_URL)
        return conn
    else:
        conn = sqlite3.connect(DB_PATH)
        conn.row_factory = sqlite3.Row
        return conn

def execute_query(query, params=(), commit=False, fetchone=False, fetchall=False):
    conn = get_connection()
    try:
        if USE_POSTGRES:
            query = query.replace("?", "%s")
            cur = conn.cursor(cursor_factory=RealDictCursor)
        else:
            cur = conn.cursor()
            
        cur.execute(query, params)
        
        if commit:
            conn.commit()
            
        if fetchone:
            row = cur.fetchone()
            return dict(row) if row else None
            
        if fetchall:
            rows = cur.fetchall()
            return [dict(row) for row in rows]
            
        return None
    except Exception as e:
        if commit:
            conn.rollback()
        raise e
    finally:
        conn.close()

def init_db():
    if not USE_POSTGRES:
        os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    
    conn = get_connection()
    cur = conn.cursor()
    try:
        # Create table
        cur.execute('''CREATE TABLE IF NOT EXISTS tasks (
                        id TEXT PRIMARY KEY,
                        params TEXT,
                        status TEXT,
                        retries INTEGER DEFAULT 0,
                        created_at TIMESTAMP,
                        completed_at TIMESTAMP,
                        short_code TEXT,
                        stage TEXT,
                        model_id TEXT,
                        results TEXT,
                        log TEXT
                    )''')
        
        if USE_POSTGRES:
            cur.execute("UPDATE tasks SET status='FAILED (Canceled by restart)' WHERE status='RUNNING' OR status='PENDING'")
            cur.execute("DELETE FROM tasks WHERE created_at < NOW() - INTERVAL '5 hours'")
        else:
            cur.execute("UPDATE tasks SET status='FAILED (Canceled by restart)' WHERE status='RUNNING' OR status='PENDING'")
            cur.execute("DELETE FROM tasks WHERE created_at < datetime('now', '-5 hours')")
            
        conn.commit()
    except Exception as e:
        print(f"DB Init Error: {e}")
    finally:
        conn.close()
