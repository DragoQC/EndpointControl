using MeshAll.Server.Components;
using MeshAll.Server.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (Environment.GetEnvironmentVariable("MESHALL_CONNECTION_FILE") is { Length: > 0 } connectionFile)
{
    builder.Configuration.AddJsonFile(connectionFile, optional: false, reloadOnChange: true);
}

builder.AddMeshAllServices();
WebApplication app = builder.Build();

if (await ServerCommandLine.TryRunAsync(args, app.Services))
{
    return;
}

Store store = app.Services.GetRequiredService<Store>();
store.RecoverInterruptedSessions();
if (!store.HasUsers)
{
    app.Logger.LogInformation("MeshAll first run: open /setup. The setup key is in {Path}", store.BootstrapPath);
}

app.UseMeshAllMiddleware();
app.MapMeshAllEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program
{
}
