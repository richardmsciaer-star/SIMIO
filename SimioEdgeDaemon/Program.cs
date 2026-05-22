using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using SimioAPI;

namespace SimioEdgeDaemon
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // [1] CARGA NATIVA EN MEMORIA: Previene el error de "SimioAPI no encontrado" al aislar el Assembly Resolver del JIT.
            string simioDir = @"C:\Program Files\Simio LLC\Simio";
            AssemblyLoadContext.Default.Resolving += (ctx, name) => {
                string p = Path.Combine(simioDir, name.Name + ".dll");
                return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
            };
            string originalDir = Directory.GetCurrentDirectory();
            Environment.CurrentDirectory = simioDir;

            // Delegamos a un método separado para asegurar que el JIT Compiler no intente resolver SimioAPI antes de asignar el handler.
            RunServer(args, simioDir, originalDir);
        }

        static void RunServer(string[] args, string simioDir, string originalDir)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            var simioAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(simioDir, "SimioDLL.dll"));
            var factory = simioAsm.GetTypes().First(t => t.Name == "SimioProjectFactory");
            var loadMeth = factory.GetMethod("LoadProject", new[] { typeof(string), typeof(string[]).MakeByRefType() })!;

            // Directorio local de modelos del cliente (resolución de ruta absoluta respecto al directorio original)
            string modelsDir = builder.Configuration["ModelsDir"] ?? Path.Combine(originalDir, "..", "Models");

            // Diccionario para mantener proyectos en RAM
            var loadedProjects = new Dictionary<string, ISimioProject>(StringComparer.OrdinalIgnoreCase);

            ISimioProject GetOrLoadProject(string modelId) {
                lock (loadedProjects) {
                    if (loadedProjects.TryGetValue(modelId, out var proj)) return proj;
                    string pPath = Path.Combine(modelsDir, modelId);
                    if (!File.Exists(pPath)) throw new FileNotFoundException($"Modelo no encontrado localmente en Edge Node: {pPath}");
                    var project = (ISimioProject)loadMeth.Invoke(null, new object?[] { pPath, null })!;
                    loadedProjects[modelId] = project;
                    return project;
                }
            }

            // Endpoint para extraer variables
            app.MapGet("/api/model/{modelId}/variables", (string modelId) => {
                try {
                    var project = GetOrLoadProject(modelId);
                    var model = project.Models.FirstOrDefault(m => m.Experiments.Count > 0) ?? project.Models[0];
                    var exp = model.Experiments[0];
                    var scenario = exp.Scenarios[0];

                    var vars = new List<object>();
                    double runLength = 10.0;
                    var runLenProp = scenario.GetType().GetProperty("ReplicationLength", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (runLenProp != null) { try { runLength = ((TimeSpan)runLenProp.GetValue(scenario)!).TotalHours; } catch { } }
                    
                    vars.Add(new { key = "RunLength", label = "Duración Simulación (Horas)", type = "number", @default = runLength, description = "ADVERTENCIA: Mayor duración incrementa el tiempo." });
                    vars.Add(new { key = "Replications", label = "Número de Réplicas", type = "number", @default = 1, description = "ADVERTENCIA: Multiplica linealmente el tiempo total." });

                    var labelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                        { "PassengerSpeeds", "Velocidad Pasajeros" },
                        { "DeparturesON", "Habilitar Salidas (Dep)" },
                        { "GROWTH_FACTOR_DEM_DEP", "Crecimiento (Salidas)" },
                        { "DwellTImeDepHALLCompanion", "Espera Acomp. (Salidas)" },
                        { "DwellTImeDepartureHallPAX", "Espera Pax (Salidas)" },
                        { "Percentage_CheckInManual", "% Check-In Manual" },
                        { "CheckInIslandsOpened", "Islas Check-In Abiertas" },
                        { "CheckIn_ProcessTime", "Tiempo Proceso Check-In" },
                        { "NumberPersoneelCheckIn", "Personal Check-In" },
                        { "NumberPersoneelBPCOntrol", "Personal Control BP" },
                        { "ProcessTimeBoardiPAssControl", "Tiempo Control BP" },
                        { "SECLinesOpened", "Líneas Seguridad Abiertas" },
                        { "WaitCapacityBodyScan", "Capacidad Espera BodyScan" },
                        { "BodyScan_Time", "Tiempo BodyScan" },
                        { "DivestCapacity", "Capacidad Bandejas (Seguridad)" },
                        { "DivestTIme", "Tiempo Despojo (Seguridad)" },
                        { "ArrivalsDOM_ON", "Habilitar Llegadas (Dom)" },
                        { "ArrivalsINT_ON", "Habilitar Llegadas (Int)" },
                        { "GROWTH_FACTOR_DEM_ARR", "Crecimiento (Llegadas)" },
                        { "DwellTImeARRIVALHALLCompanion", "Espera Acomp. (Llegadas)" },
                        { "DwellTImeArrivalHallPAX", "Espera Pax (Llegadas)" },
                        { "Numbe_ArrManualPssptLInesOpen", "Líneas Pasaporte Manual" },
                        { "Number_ARR_AUTOPssptLinesOPEN", "Líneas Pasaporte Auto" },
                        { "PercentManualPportContro_Arrival", "% Pasaporte Manual" },
                        { "NumberINTBeltOpen", "Cintas Equipaje (Int)" },
                        { "NumberDOMBeltOpen", "Cintas Equipaje (Dom)" },
                        { "No_DouaneLineOpen", "Líneas Aduana Abiertas" },
                        { "PercentDouaneScreen", "% Inspección Aduana" },
                        { "DivestDouaneTime", "Tiempo Inspección Aduana" },
                        { "StartCollectData", "Inicio Recolección Datos (Hr)" },
                        { "LengthCollectionData", "Duración Recolección (Hr)" },
                        { "FrecuencyofCollection", "Frecuencia Muestreo (Seg)" }
                    };

                    foreach (IExperimentControl c in exp.Controls) {
                        string current = "";
                        try { scenario.GetControlValue(c, ref current); } catch { }
                        if (string.IsNullOrEmpty(current)) current = c.DefaultValueString ?? "";
                        if (c.Name.Equals("StartCollectData", StringComparison.OrdinalIgnoreCase)) current = "0";
                        bool isBool = current.ToLower() == "true" || current.ToLower() == "false";
                        string uiLabel = labelMap.ContainsKey(c.Name) ? labelMap[c.Name] : c.Name;
                        while (uiLabel.Length < 38) uiLabel += "\u00A0";
                        vars.Add(new { key = c.Name, label = uiLabel, type = isBool ? "checkbox" : "number", @default = isBool ? (current.ToLower() == "true") : (object)current, description = "" });
                    }

                    return Results.Ok(new {
                        models = new[] {
                            new {
                                id = modelId,
                                name = modelId,
                                description = "Modelo alojado en Edge Node RAM.",
                                groups = new[] { new { title = "Variables del Modelo", fields = vars } }
                            }
                        }
                    });
                } catch (Exception ex) { return Results.Problem(ex.Message); }
            });

            // Endpoint de simulación intensiva en Edge
            app.MapPost("/api/model/{modelId}/simulate", async (string modelId, HttpContext context) => {
                try {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    
                    Dictionary<string, object> rawParams = new();
                    if (!string.IsNullOrWhiteSpace(body)) {
                        rawParams = JsonSerializer.Deserialize<Dictionary<string, object>>(body) ?? new();
                    }
                    var parameters = rawParams.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? "");

                    var project = GetOrLoadProject(modelId);
                    var model = project.Models.FirstOrDefault(m => m.Experiments.Count > 0) ?? project.Models[0];
                    var exp = model.Experiments[0];
                    var scenario = exp.Scenarios[0];
                    
                    // ─── Ejecución DIRECTA en el hilo MTA del handler ─────────────────────────────
                    Exception? simEx = null;
                    bool completed = false;
                    var swReset = System.Diagnostics.Stopwatch.StartNew();
                    var swRun = new System.Diagnostics.Stopwatch();
                    
                    Console.WriteLine($"[DIAG] → exp.Reset() START (MTA inline)");

                    try {
                        exp.Reset();
                        swReset.Stop();
                        Console.WriteLine($"[DIAG] → exp.Reset() DONE in {swReset.ElapsedMilliseconds}ms");

                        // Aplicar parámetros DESPUÉS del Reset
                        foreach (var param in parameters) {
                            if (param.Key.Equals("RunLength", StringComparison.OrdinalIgnoreCase)) {
                                if (double.TryParse(param.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rl)) {
                                    try {
                                        var modelRunSetupProp = model.GetType().GetProperty("RunSetup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (modelRunSetupProp != null) {
                                            var runSetup = modelRunSetupProp.GetValue(model);
                                            var stProp = runSetup.GetType().GetProperty("StartingTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            var endProp = runSetup.GetType().GetProperty("EndingTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            if (stProp != null && endProp != null) {
                                                DateTime st = (DateTime)stProp.GetValue(runSetup);
                                                endProp.SetValue(runSetup, st.AddHours(rl));
                                                Console.WriteLine($"[DIAG] RunLength set to {rl}h via 'RunSetup.EndingTime'");
                                            }
                                        }
                                        
                                        var controlControl = exp.Controls.FirstOrDefault(c => c.Name.Equals("RunLength", StringComparison.OrdinalIgnoreCase));
                                        if (controlControl != null) {
                                            scenario.SetControlValue(controlControl, param.Value);
                                            Console.WriteLine($"[DIAG] RunLength set via Scenario Controls");
                                        }
                                    } catch (Exception ex) {
                                        Console.WriteLine($"[DIAG] ⚠️ Exception setting RunLength: {ex.Message}");
                                    }
                                }
                                continue;
                            }
                            if (param.Key.Equals("Replications", StringComparison.OrdinalIgnoreCase)) {
                                if (double.TryParse(param.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double repD)) {
                                    scenario.ReplicationsRequired = Math.Max(1, (int)repD);
                                    Console.WriteLine($"[DIAG] ReplicationsRequired = {scenario.ReplicationsRequired}");
                                }
                                continue;
                            }
                            
                            var control = exp.Controls.FirstOrDefault(c => c.Name.Equals(param.Key, StringComparison.OrdinalIgnoreCase));
                            if (control != null) {
                                string safeValue = string.IsNullOrWhiteSpace(param.Value) ? "0" : param.Value;
                                scenario.SetControlValue(control, safeValue);
                                Console.WriteLine($"[DIAG PARAM] Escrito en {param.Key} = '{safeValue}'");
                            } else {
                                Console.WriteLine($"[DIAG PARAM WARN] Control no encontrado para: {param.Key} = '{param.Value}'");
                            }
                        }
                        Console.WriteLine($"[DIAG] → Parameters applied | Replications={scenario.ReplicationsRequired}");

                        // ─── DUMP: Propiedades del EXPERIMENT ─────────────────────────────────────
                        var expProps = exp.GetType().GetProperties(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        Console.WriteLine($"[DIAG] Experiment type: {exp.GetType().FullName} | Props: {expProps.Length}");
                        foreach (var p in expProps) {
                            try {
                                var val = p.GetValue(exp);
                                if (val is TimeSpan || val is double || val is int || val is Enum ||
                                    (val is string s && !string.IsNullOrWhiteSpace(s)))
                                    Console.WriteLine($"[DIAG EXP] {p.Name} ({p.PropertyType.Name}) = {val}");
                            } catch { }
                        }

                        // ─── DUMP: Propiedades del SCENARIO ──────────────────────────────────────
                        var scenProps = scenario.GetType().GetProperties(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        Console.WriteLine($"[DIAG] Scenario type: {scenario.GetType().FullName} | Props: {scenProps.Length}");
                        foreach (var p in scenProps) {
                            try {
                                var val = p.GetValue(scenario);
                                if (val is TimeSpan || val is double || val is int || val is Enum ||
                                    (val is string s && !string.IsNullOrWhiteSpace(s)))
                                    Console.WriteLine($"[DIAG SCEN] {p.Name} ({p.PropertyType.Name}) = {val}");
                            } catch { }
                        }
                        // ─────────────────────────────────────────────────────────────────────────────

                    // Ejecución con timeout de seguridad (120s)
                    Console.WriteLine($"[DIAG] → exp.Run() START (inline MTA, timeout=120s)");
                    swRun.Start();
                    
                    completed = false;
                    try {
                        var runTask = Task.Run(() => exp.Run());
                        completed = runTask.Wait(TimeSpan.FromSeconds(120));
                        if (!completed) throw new Exception("Simulation timed out after 120 seconds.");
                    } catch (Exception runEx) {
                        Console.WriteLine($"[DIAG] ⚠️ exp.Run() falló en el primer intento: {runEx.InnerException?.Message ?? runEx.Message}");
                        Console.WriteLine($"[DIAG] Purificando Responses inyectadas e intentando Fallback (Run puro)...");
                        
                        var toRemove = new List<dynamic>();
                        foreach (dynamic r in exp.Responses) {
                            try {
                                string rName = (string)r.Name;
                                if (rName.EndsWith("_Average") || rName.EndsWith("_Maximum")) {
                                    toRemove.Add(r);
                                }
                            } catch {}
                        }
                        
                        try {
                            dynamic dynResps = exp.Responses;
                            foreach (var r in toRemove) dynResps.Remove(r);
                        } catch { }

                        try {
                            Console.WriteLine($"[DIAG] → exp.Run() FALLBACK START");
                            var fallbackTask = Task.Run(() => exp.Run());
                            completed = fallbackTask.Wait(TimeSpan.FromSeconds(120));
                        } catch (Exception fallbackEx) {
                            simEx = fallbackEx; // Si falla de nuevo, el modelo está genuinamente roto
                        }
                    }
                    swRun.Stop();
                    } catch (Exception ex) { simEx = ex; }

                    if (simEx != null) {
                        Console.WriteLine($"[DIAG] 💥 EXCEPTION FATAL en exp.Run(): {simEx.Message}");
                        if (simEx.InnerException != null) Console.WriteLine($"[DIAG] Inner: {simEx.InnerException.Message}");
                        return Results.Problem(simEx.InnerException?.Message ?? simEx.Message);
                    }
                    
                    if (completed) {
                        Console.WriteLine($"[DIAG] → exp.Run() DONE in {swRun.ElapsedMilliseconds}ms | Completed={scenario.ReplicationsCompleted}");
                    } else if (simEx == null) {
                        Console.WriteLine($"[DIAG] ❌ exp.Run() TIMEOUT after 120s. Simulation hung.");
                        return Results.Problem("Simulation timed out after 120 seconds. Model may require Desktop-only execution.");
                    }

                    // Extracción de KPIs post-simulación
                    var results = new Dictionary<string, double>();

                    // ─── FASE 1 RESTAURADA: EXTRACCIÓN DIRECTA DESDE RESPONSES (0ms) ───
                    // Gracias al Auto-Guardado previo, exp.Run() ya no borra las Responses.
                    try {
                        int processedCount = 0;
                        var getRespMeth = scenario.GetType().GetMethod("GetResponseValue", BindingFlags.Public | BindingFlags.Instance);

                        // Extraer valores nativos
                        foreach (object objResp in exp.Responses) {
                            try {
                                IExperimentResponse typedResp = (IExperimentResponse)objResp;
                                string rName = typedResp.Name;
                                if (string.IsNullOrEmpty(rName)) rName = "Unknown_" + Guid.NewGuid().ToString().Substring(0,4);
                                
                                object[] args = new object[] { typedResp, 0.0 };
                                bool success = false;
                                
                                try {
                                    if (getRespMeth != null) {
                                        success = (bool)getRespMeth.Invoke(scenario, args);
                                    } else {
                                        double v2 = 0;
                                        success = scenario.GetResponseValue(typedResp, ref v2);
                                        args[1] = v2;
                                    }
                                } catch (Exception ex) { 
                                    Console.WriteLine($"[DIAG RESP] Excepción extrayendo {rName}: {ex.Message}");
                                }

                                Console.WriteLine($"[DIAG RESP] {rName} -> Success={success}, Value={args[1]}");

                                // Fallback: Aún si success es false (posible NaN por 0 observaciones), extraemos si es un número
                                if (success || args[1] != null) {
                                    double val = Convert.ToDouble(args[1]);
                                    if (double.IsNaN(val) || double.IsInfinity(val)) val = 0;
                                    
                                    string uiName = rName.Replace("Tally_", "")
                                                         .Replace("_M2_PAX", "")
                                                         .Replace("_M2_Pax", "")
                                                         .Replace("_WaitTime", "_Wait")
                                                         .Replace("AUTOM_IMMIGRATI", "AutoInmig")
                                                         .Replace("MANUAL_Immigration", "ManInmig")
                                                         .Replace("DOM_Baggage", "DomBag")
                                                         .Replace("INT_Baggage", "IntBag")
                                                         .Replace("BoardPass", "Board")
                                                         .Replace("CHECKIN", "CheckIn");
                                                         
                                    results[uiName] = val;
                                }
                                processedCount++;
                            } catch (Exception extEx) {
                                Console.WriteLine($"[DIAG ERROR] Error en loop de variable: {extEx.Message}");
                            }
                        }

                        Console.WriteLine($"[DIAG] Total Responses Procesadas: {processedCount}, Extraídas exitosamente: {results.Count}");

                        if (results.Count == 0) results["Status_Completed"] = 1.0;
                    } catch (Exception extEx) {
                        Console.WriteLine($"[DIAG ERROR] Fallo en extracción final de Responses: {extEx.Message}");
                    }

                    if (results.Count == 0)
                        results["Status_Completed"] = 1.0;

                    return Results.Ok(new { 
                        status = "COMPLETED", 
                        kpis = results,
                        exportedFiles = new List<string>() 
                    });
                } catch (Exception ex) { return Results.Problem(ex.Message); }
            });

            app.Run("http://0.0.0.0:5050");
        }
    }
}
