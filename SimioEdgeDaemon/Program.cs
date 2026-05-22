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
using System.Threading.Tasks;
using System.Collections.Generic;
using SimioAPI;

namespace SimioEdgeDaemon
{
    public class Program
    {
        public static void Main(string[] args)
        {
            string simioDir = @"C:\Program Files\Simio LLC\Simio";
            AssemblyLoadContext.Default.Resolving += (ctx, name) => {
                string p = Path.Combine(simioDir, name.Name + ".dll");
                return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
            };
            string originalDir = Directory.GetCurrentDirectory();
            Environment.CurrentDirectory = simioDir;

            RunServer(args, simioDir, originalDir);
        }

        static void RunServer(string[] args, string simioDir, string originalDir)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            var simioAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(simioDir, "SimioDLL.dll"));
            var factory = simioAsm.GetTypes().First(t => t.Name == "SimioProjectFactory");
            var loadMeth = factory.GetMethod("LoadProject", new[] { typeof(string), typeof(string[]).MakeByRefType() })!;

            string modelsDir = builder.Configuration["ModelsDir"] ?? Path.Combine(originalDir, "..", "Models");
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

            var runSimulationInline = (string modelId, Dictionary<string, string> parameters, List<string> logLines) => {
                var project = GetOrLoadProject(modelId);
                var model = project.Models.FirstOrDefault(m => m.Experiments.Count > 0) ?? project.Models[0];
                var exp = model.Experiments[0];
                var scenario = exp.Scenarios[0];
                
                var swReset = System.Diagnostics.Stopwatch.StartNew();
                var swRun = new System.Diagnostics.Stopwatch();
                
                logLines.Add($"[{DateTime.Now:HH:mm:ss}] exp.Reset() START");
                exp.Reset();
                swReset.Stop();
                logLines.Add($"[{DateTime.Now:HH:mm:ss}] exp.Reset() DONE in {swReset.ElapsedMilliseconds}ms");

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
                                        logLines.Add($"[{DateTime.Now:HH:mm:ss}] RunLength set to {rl}h via 'RunSetup.EndingTime'");
                                    }
                                }
                                
                                var controlControl = exp.Controls.FirstOrDefault(c => c.Name.Equals("RunLength", StringComparison.OrdinalIgnoreCase));
                                if (controlControl != null) {
                                    scenario.SetControlValue(controlControl, param.Value);
                                    logLines.Add($"[{DateTime.Now:HH:mm:ss}] RunLength set via Scenario Controls");
                                }
                            } catch (Exception ex) {
                                logLines.Add($"[{DateTime.Now:HH:mm:ss}] WARN - Exception setting RunLength: {ex.Message}");
                            }
                        }
                        continue;
                    }
                    if (param.Key.Equals("Replications", StringComparison.OrdinalIgnoreCase)) {
                        if (double.TryParse(param.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double repD)) {
                            scenario.ReplicationsRequired = Math.Max(1, (int)repD);
                            logLines.Add($"[{DateTime.Now:HH:mm:ss}] ReplicationsRequired = {scenario.ReplicationsRequired}");
                        }
                        continue;
                    }
                    
                    var control = exp.Controls.FirstOrDefault(c => c.Name.Equals(param.Key, StringComparison.OrdinalIgnoreCase));
                    if (control != null) {
                        string safeValue = string.IsNullOrWhiteSpace(param.Value) ? "0" : param.Value;
                        scenario.SetControlValue(control, safeValue);
                        logLines.Add($"[{DateTime.Now:HH:mm:ss}] Escrito en {param.Key} = '{safeValue}'");
                    } else {
                        logLines.Add($"[{DateTime.Now:HH:mm:ss}] WARN - Control no encontrado para: {param.Key} = '{param.Value}'");
                    }
                }

                logLines.Add($"[{DateTime.Now:HH:mm:ss}] exp.Run() START (timeout=120s)");
                swRun.Start();
                bool completed = false;
                Exception? simEx = null;
                try {
                    var runTask = Task.Run(() => exp.Run());
                    completed = runTask.Wait(TimeSpan.FromSeconds(120));
                    if (!completed) throw new Exception("Simulation timed out after 120 seconds.");
                } catch (Exception runEx) {
                    logLines.Add($"[{DateTime.Now:HH:mm:ss}] WARN - exp.Run() falló: {runEx.InnerException?.Message ?? runEx.Message}");
                    logLines.Add($"[{DateTime.Now:HH:mm:ss}] Intentando Fallback (Responses cleanup)...");
                    
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
                        var fallbackTask = Task.Run(() => exp.Run());
                        completed = fallbackTask.Wait(TimeSpan.FromSeconds(120));
                    } catch (Exception fallbackEx) {
                        simEx = fallbackEx;
                    }
                }
                swRun.Stop();

                if (simEx != null) {
                    throw simEx;
                }

                if (!completed) {
                    throw new Exception("Simulation timed out after 120 seconds. Model may require Desktop-only execution.");
                }

                logLines.Add($"[{DateTime.Now:HH:mm:ss}] exp.Run() DONE in {swRun.ElapsedMilliseconds}ms");

                var results = new Dictionary<string, double>();
                var getRespMeth = scenario.GetType().GetMethod("GetResponseValue", BindingFlags.Public | BindingFlags.Instance);

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
                            logLines.Add($"[{DateTime.Now:HH:mm:ss}] Excepción extrayendo {rName}: {ex.Message}");
                        }

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
                    } catch (Exception extEx) {
                        logLines.Add($"[{DateTime.Now:HH:mm:ss}] Error en loop de variable: {extEx.Message}");
                    }
                }

                if (results.Count == 0) results["Status_Completed"] = 1.0;

                return results;
            };

            var orchestratorUrl = app.Configuration["OrchestratorUrl"] ?? "http://localhost:8000";
            var pollingEnabledStr = app.Configuration["PollingEnabled"] ?? "true";
            bool pollingEnabled = bool.TryParse(pollingEnabledStr, out bool pe) && pe;

            if (pollingEnabled) {
                Console.WriteLine($"[POLLING] Starting background poller targeting Orchestrator at: {orchestratorUrl}");
                var client = new System.Net.Http.HttpClient();
                client.Timeout = Timeout.InfiniteTimeSpan;

                _ = Task.Run(async () => {
                    while (true) {
                        try {
                            var pollUrl = $"{orchestratorUrl.TrimEnd('/')}/api/agent/poll";
                            var response = await client.GetAsync(pollUrl);
                            if (response.IsSuccessStatusCode) {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                using var doc = JsonDocument.Parse(responseBody);
                                var root = doc.RootElement;
                                
                                if (root.TryGetProperty("task_id", out var taskIdProp) && taskIdProp.GetString() is string taskId) {
                                    string modelId = root.TryGetProperty("model_id", out var modelIdProp) ? modelIdProp.GetString() ?? "" : "";
                                    
                                    if (string.IsNullOrEmpty(modelId)) {
                                        modelId = "AIFAMODEL_ver010524_Prueba_Avanzado060326_VERD.spfx";
                                    }

                                    Console.WriteLine($"[POLLING] Received task {taskId} for model {modelId}");
                                    
                                    var parameters = new Dictionary<string, string>();
                                    if (root.TryGetProperty("parameters", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Object) {
                                        foreach (var prop in paramsProp.EnumerateObject()) {
                                            parameters[prop.Name] = prop.Value.ToString();
                                        }
                                    }

                                    var logLines = new List<string>();
                                    logLines.Add($"[{DateTime.Now:HH:mm:ss}] INICIANDO SIMULACIÓN DESDE CLIENT POLLER...");
                                    logLines.Add($"[{DateTime.Now:HH:mm:ss}] MODELO: {modelId}");
                                    logLines.Add($"[{DateTime.Now:HH:mm:ss}] PARÁMETROS: {JsonSerializer.Serialize(parameters)}");

                                    Dictionary<string, double>? results = null;
                                    string? errorMsg = null;
                                    
                                    try {
                                        results = runSimulationInline(modelId, parameters, logLines);
                                        logLines.Add($"[{DateTime.Now:HH:mm:ss}] SIMULACIÓN COMPLETADA CON ÉXITO.");
                                    } catch (Exception ex) {
                                        errorMsg = ex.InnerException?.Message ?? ex.Message;
                                        logLines.Add($"[{DateTime.Now:HH:mm:ss}] ERROR CRÍTICO EN SIMULACIÓN: {errorMsg}");
                                        Console.WriteLine($"[POLLING ERROR] Task {taskId} failed: {errorMsg}");
                                    }

                                    var callbackUrl = $"{orchestratorUrl.TrimEnd('/')}/api/agent/callback";
                                    var callbackPayload = new {
                                        task_id = taskId,
                                        status = errorMsg == null ? "COMPLETED" : "FAILED",
                                        kpis = results,
                                        error_msg = errorMsg,
                                        log = string.Join("\n", logLines)
                                    };

                                    var jsonPayload = JsonSerializer.Serialize(callbackPayload);
                                    var content = new System.Net.Http.StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                                    var callbackResponse = await client.PostAsync(callbackUrl, content);
                                    if (callbackResponse.IsSuccessStatusCode) {
                                        Console.WriteLine($"[POLLING] Successfully reported callback for task {taskId}");
                                    } else {
                                        Console.WriteLine($"[POLLING ERROR] Failed to report callback for task {taskId}. Status: {callbackResponse.StatusCode}");
                                    }
                                }
                            }
                        } catch (Exception) {
                            // Silently continue on connection errors (e.g., orchestrator down)
                        }
                        await Task.Delay(3000);
                    }
                });
            }

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

            app.MapPost("/api/model/{modelId}/simulate", async (string modelId, HttpContext context) => {
                try {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    
                    Dictionary<string, object> rawParams = new();
                    if (!string.IsNullOrWhiteSpace(body)) {
                        rawParams = JsonSerializer.Deserialize<Dictionary<string, object>>(body) ?? new();
                    }
                    var parameters = rawParams.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? "");

                    var logLines = new List<string>();
                    var results = runSimulationInline(modelId, parameters, logLines);

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
