import React, { useState, useEffect } from 'react';

const SimioDashboard = () => {
    const [tasks, setTasks] = useState([]);
    const [loading, setLoading] = useState(false);
    const [formData, setFormData] = useState({
        "ModelVar_01": "100",
        "ModelVar_02": "0.5"
    });

    const fetchTasks = async () => {
        try {
            const res = await fetch('/api/tasks');
            const data = await res.json();
            setTasks(data);
        } catch (e) { console.error(e); }
    };

    useEffect(() => {
        fetchTasks();
        const interval = setInterval(fetchTasks, 5000);
        return () => clearInterval(interval);
    }, []);

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);
        try {
            await fetch('/api/tasks', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ parameters: formData })
            });
            fetchTasks();
        } catch (e) { console.error(e); }
        setLoading(false);
    };

    return (
        <div style={{ padding: '40px', background: '#0f172a', color: '#f8fafc', minHeight: '100vh', fontFamily: 'Inter, sans-serif' }}>
            <header style={{ marginBottom: '40px' }}>
                <h1 style={{ fontSize: '2.5rem', fontWeight: 'bold', background: 'linear-gradient(to right, #38bdf8, #818cf8)', WebkitBackgroundClip: 'text', WebkitTextFillColor: 'transparent' }}>
                    Simio-Agent-Pro
                </h1>
                <p style={{ color: '#94a3b8' }}>Sistema de Automatización Visual por Objetivos</p>
            </header>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr', gap: '40px' }}>
                {/* Formulario */}
                <section style={{ background: '#1e293b', padding: '24px', borderRadius: '16px', border: '1px solid #334155' }}>
                    <h2 style={{ marginBottom: '20px', fontSize: '1.25rem' }}>Nueva Simulación</h2>
                    <form onSubmit={handleSubmit}>
                        {Object.keys(formData).map(key => (
                            <div key={key} style={{ marginBottom: '16px' }}>
                                <label style={{ display: 'block', fontSize: '0.875rem', color: '#94a3b8', marginBottom: '4px' }}>{key}</label>
                                <input 
                                    type="text" 
                                    value={formData[key]} 
                                    onChange={(e) => setFormData({...formData, [key]: e.target.value})}
                                    style={{ width: '100%', padding: '10px', borderRadius: '8px', background: '#334155', border: '1px solid #475569', color: '#fff' }}
                                />
                            </div>
                        ))}
                        <button 
                            type="submit" 
                            disabled={loading}
                            style={{ 
                                width: '100%', 
                                padding: '12px', 
                                background: '#3b82f6', 
                                border: 'none', 
                                borderRadius: '8px', 
                                color: '#fff', 
                                fontWeight: 'bold', 
                                cursor: 'pointer',
                                transition: 'opacity 0.2s'
                            }}
                        >
                            {loading ? 'Procesando...' : 'Lanzar Tarea'}
                        </button>
                    </form>
                </section>

                {/* Cola de Tareas */}
                <section>
                    <h2 style={{ marginBottom: '20px', fontSize: '1.25rem' }}>Cola de Ejecución</h2>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
                        {tasks.map(task => (
                            <div key={task.id} style={{ background: '#1e293b', padding: '20px', borderRadius: '16px', border: '1px solid #334155' }}>
                                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                                    <div>
                                        <span style={{ 
                                            padding: '4px 8px', 
                                            borderRadius: '4px', 
                                            fontSize: '0.75rem', 
                                            background: task.status === 'COMPLETED' ? '#059669' : (task.status === 'RUNNING' ? '#2563eb' : '#475569') 
                                        }}>
                                            {task.status}
                                        </span>
                                        <span style={{ marginLeft: '12px', color: '#64748b', fontSize: '0.875rem' }}>ID: {task.id.slice(0,8)}</span>
                                    </div>
                                    <div style={{ color: '#94a3b8', fontSize: '0.875rem' }}>{task.created_at.split('T')[1].slice(0,8)}</div>
                                </div>
                                
                                {task.status === 'COMPLETED' && (
                                    <div style={{ marginTop: '20px', display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '10px' }}>
                                        <img src={`/api/outputs/${task.id}_hist.png`} style={{ width: '100%', borderRadius: '8px' }} />
                                        <img src={`/api/outputs/${task.id}_box.png`} style={{ width: '100%', borderRadius: '8px' }} />
                                        <a href={`/api/outputs/${task.id}_report.pdf`} target="_blank" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', background: '#334155', borderRadius: '8px', textDecoration: 'none', color: '#fff', fontSize: '0.875rem' }}>
                                            📄 PDF Report
                                        </a>
                                    </div>
                                )}
                            </div>
                        ))}
                    </div>
                </section>
            </div>
        </div>
    );
};

export default SimioDashboard;
