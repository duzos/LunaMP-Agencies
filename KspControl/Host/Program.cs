using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using KspControl.Host;
// Human-run CLI branch. It is never an MCP tool and the MCP path below never touches grant material.
if (args.Length > 0 && args[0] == "grant") return GrantCli.Run(args[1..], Console.Out, Console.Error);
var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<BridgeClient>();
builder.Services.AddSingleton<IBeatSender, BridgeBeatSender>();
builder.Services.AddSingleton<LeaseKeeper>();
builder.Services.AddHostedService(services => services.GetRequiredService<LeaseKeeper>());
builder.Services.AddSingleton<JournalAccess>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ObservationTools>().WithTools<ControlTools>();
await builder.Build().RunAsync();
return 0;
