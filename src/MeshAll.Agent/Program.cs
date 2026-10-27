using System.ServiceProcess;
using MeshAll.Agent;

if (args.Contains("--broker") && OperatingSystem.IsWindows())
{
    WindowsDesktop.RunBroker(AgentSettings.Option(args, "--pipe") ?? throw new ArgumentException("Missing pipe."));
    return;
}
if (args.Contains("--help"))
{
    Console.WriteLine("EndpointControl Agent — independent .NET remote-management agent\n" +
        "First run: EndpointControlAgent --server https://server:5188 --token ENROLLMENT_KEY [--fingerprint SHA256]\n" +
        "Options: --state FILE --name NAME --file-root DIRECTORY --require-consent\n" +
        "Windows service: MeshAllAgent --service --state C:\\ProgramData\\MeshAll\\agent.json");
    return;
}
AgentSettings? settings = await AgentSettings.LoadAsync(args);

if (args.Contains("--enroll-only"))
{
    Console.WriteLine("EndpointControl Agent enrolled.");
    return;
}
if (args.Contains("--service") && OperatingSystem.IsWindows())
{
    ServiceBase.Run(new MeshAllService(settings));
    return;
}
using CancellationTokenSource? stop = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

await new AgentClient(settings).RunAsync(stop.Token);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class MeshAllService : ServiceBase
{
    private readonly AgentSettings settings;
    private readonly CancellationTokenSource stop = new();
    private Task? running;

    public MeshAllService(AgentSettings settings)
    {
        this.settings = settings;
        ServiceName = "MeshAllAgent";
    }
    protected override void OnStart(string[] args)
    {
        running = new AgentClient(settings).RunAsync(stop.Token);
    }
    protected override void OnStop()
    {
        stop.Cancel();
        try
        {
            running?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }
}
