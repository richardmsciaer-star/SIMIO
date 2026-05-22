using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using SimioAPI;

if (args.Length == 0) return;
string spfxPath = args[0];
string SimioDir = @"C:\Program Files\Simio LLC\Simio";

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string p = Path.Combine(SimioDir, name.Name + ".dll");
    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
};

Thread t = new Thread(() =>
{
    Environment.CurrentDirectory = SimioDir;
    var simioAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(SimioDir, "SimioDLL.dll"));
    var factory = simioAsm.GetTypes().First(type => type.Name == "SimioProjectFactory");
    var loadMeth = factory.GetMethod("LoadProject", new[] { typeof(string), typeof(string[]).MakeByRefType() });

    var project = (ISimioProject)loadMeth.Invoke(null, new object[] { spfxPath, null });
    var model = project.Models.FirstOrDefault(m => m.Experiments.Count > 0) ?? project.Models[0];
    var exp = model.Experiments[0];
    var scenario = exp.Scenarios[0];

    var variables = new List<object>();

    double runLength = 10.0;
    var runLenProp = scenario.GetType().GetProperty("ReplicationLength", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (runLenProp != null)
    {
        try { runLength = ((TimeSpan)runLenProp.GetValue(scenario)).TotalHours; } catch { }
    }
    variables.Add(new { key = "RunLength", label = "Duración Simulación (Horas)", type = "number", @default = runLength, description = "Tiempo de duración de la simulación" });

    foreach (IExperimentControl c in exp.Controls)
    {
        string current = "";
        try { scenario.GetControlValue(c, ref current); } catch { }
        if (string.IsNullOrEmpty(current)) current = c.DefaultValueString ?? "";

        bool isBool = current.ToLower() == "true" || current.ToLower() == "false";

        variables.Add(new {
            key = c.Name,
            label = c.Name,
            type = isBool ? "checkbox" : "number",
            @default = isBool ? (current.ToLower() == "true") : (object)current,
            description = c.Description ?? ""
        });
    }

    var output = new {
        models = new[] {
            new {
                id = Path.GetFileName(spfxPath),
                name = Path.GetFileName(spfxPath),
                description = model.Description ?? "Modelo detectado automáticamente.",
                groups = new[] {
                    new {
                        title = "Variables del Modelo",
                        fields = variables
                    }
                }
            }
        }
    };

    Console.WriteLine("===JSON_START===");
    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("===JSON_END===");
});

t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
