namespace OpcPlc;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using OpcPlc.Helpers;
using OpcPlc.PluginNodes.Models;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static OpcPlc.OpcApplicationConfiguration;
using static OpcPlc.PlcSimulation;

public static class Program
{
    /// <summary>
    /// Name of the application.
    /// </summary>
    public const string ProgramName = "OpcPlc";

    /// <summary>
    /// Logger Factory Instance.
    /// </summary>
    public static ILoggerFactory LoggerFactoryInstance;

    /// <summary>
    /// Logger.
    /// </summary>
    public static ILogger Logger;

    /// <summary>
    /// Nodes to extend the address space.
    /// </summary>
    public static ImmutableList<IPluginNodes> PluginNodes;

    /// <summary>
    /// OPC UA server object.
    /// </summary>
    public static PlcServer PlcServer = null;

    /// <summary>
    /// Simulation object.
    /// </summary>
    public static PlcSimulation PlcSimulation = null;

    /// <summary>
    /// Service returning <see cref="DateTime"/> values and <see cref="Timer"/> instances. Mocked in tests.
    /// </summary>
    public static TimeService TimeService = new();

    /// <summary>
    /// A flag indicating when the server is up and ready to accept connections.
    /// </summary>
    public static volatile bool Ready = false;

    public static bool DisableAnonymousAuth { get; set; } = false;

    public static bool DisableUsernamePasswordAuth { get; set; } = false;

    public static bool DisableCertAuth { get; set; } = false;

    /// <summary>
    /// Admin user.
    /// </summary>
    public static string AdminUser { get; set; } = "sysadmin";

    /// <summary>
    /// Admin user password.
    /// </summary>
    public static string AdminPassword { get; set; } = "demo";

    /// <summary>
    /// Default user.
    /// </summary>
    public static string DefaultUser { get; set; } = "user1";

    /// <summary>
    /// Default user password.
    /// </summary>
    public static string DefaultPassword { get; set; } = "password";

    /// <summary>
    /// Show OPC Publisher configuration file using IP address as EndpointUrl.
    /// </summary>
    public static bool ShowPublisherConfigJsonIp { get; set; }

    /// <summary>
    /// Show OPC Publisher configuration file using plchostname as EndpointUrl.
    /// </summary>
    public static bool ShowPublisherConfigJsonPh { get; set; }

    /// <summary>
    /// Web server port for hosting OPC Publisher file.
    /// </summary>
    public static uint WebServerPort { get; set; } = 8080;

    /// <summary>
    /// Show usage help.
    /// </summary>
    public static bool ShowHelp { get; set; }

    public static string PnJson = "pn.json";

    /// <summary>
    /// Logging configuration.
    /// </summary>
    public static string LogFileName = $"{Dns.GetHostName().Split('.')[0].ToLowerInvariant()}-plc.log";
    public static string LogLevel = "info";
    public static string LogLevelForOPCUAServer = "";
    public static TimeSpan LogFileFlushTimeSpanSec = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Log to ADX or not
    /// We need to specify ADX instance endpoint, database, tablename and secret if this is set to true
    /// </summary>
    public static bool LogToADX = false;

    public enum NodeType
    {
        UInt,
        Double,
        Bool,
        UIntArray,
    }

    /// <summary>
    /// Synchronous main method of the app.
    /// </summary>
    public static void Main(string[] args)
    {
        // Start OPC UA server.
        MainAsync(args).Wait();
    }

    /// <summary>
    /// Asynchronous part of the main method of the app.
    /// </summary>
    public static async Task MainAsync(string[] args, CancellationToken cancellationToken = default)
    {
        LoadPluginNodes();

        Mono.Options.OptionSet options = CliOptions.InitCommandLineOptions();

        // Parse the command line
        List<string> extraArgs = options.Parse(args);

        InitLogging();

        StartWebServer(args);

        // Show usage if requested
        if (ShowHelp)
        {
            CliOptions.PrintUsage(options);
            return;
        }

        // Validate and parse extra arguments
        if (extraArgs.Count > 0)
        {
            Logger.LogWarning($"Found one or more invalid command line arguments: {string.Join(" ", extraArgs)}");
            CliOptions.PrintUsage(options);
        }

        LogLogo();

        Logger.LogInformation("Current directory: {currentDirectory}", Directory.GetCurrentDirectory());
        Logger.LogInformation("Log file: {logFileName}", Path.GetFullPath(LogFileName));
        Logger.LogInformation("Log level: {logLevel}", LogLevel);

        // Show version.
        var fileVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
        Logger.LogInformation("{ProgramName} {version} starting up ...",
            ProgramName,
            $"v{fileVersion.ProductMajorPart}.{fileVersion.ProductMinorPart}.{fileVersion.ProductBuildPart} (SDK {Utils.GetAssemblyBuildNumber()})");
        Logger.LogDebug("Informational version: {version}",
            $"v{(Attribute.GetCustomAttribute(Assembly.GetEntryAssembly(), typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute)?.InformationalVersion} (SDK {Utils.GetAssemblySoftwareVersion()} from {Utils.GetAssemblyTimestamp()})");
        Logger.LogDebug("Build date: {date}",
            $"{File.GetCreationTime(Assembly.GetExecutingAssembly().Location)}");

        try
        {
            await ConsoleServerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "OPC UA server failed unexpectedly");
        }

        Logger.LogInformation("OPC UA server exiting...");
    }

    /// <summary>
    /// Load plugin nodes using reflection.
    /// </summary>
    private static void LoadPluginNodes()
    {
        var pluginNodesType = typeof(IPluginNodes);

        PluginNodes = pluginNodesType.Assembly.ExportedTypes
            .Where(t => pluginNodesType.IsAssignableFrom(t) &&
                        !t.IsInterface &&
                        !t.IsAbstract)
            .Select(t => Activator.CreateInstance(t))
            .Cast<IPluginNodes>()
            .ToImmutableList();
    }

    /// <summary>
    /// Get IP address of first interface, otherwise host name.
    /// </summary>
    private static string GetIpAddress()
    {
        string ip = Dns.GetHostName();

        try
        {
            // Ignore System.Net.Internals.SocketExceptionFactory+ExtendedSocketException
            var hostEntry = Dns.GetHostEntry(ip);
            if (hostEntry.AddressList.Length > 0)
            {
                ip = hostEntry.AddressList[0].ToString();
            }
        }
        catch { }

        return ip;
    }

    /// <summary>
    /// Run the server.
    /// </summary>
    private static async Task ConsoleServerAsync(CancellationToken cancellationToken)
    {
        var stackLogger = LoggerFactoryInstance.CreateLogger("OPC");

        // init OPC configuration and tracing
        var plcOpcApplicationConfiguration = new OpcApplicationConfiguration();
        ApplicationConfiguration plcApplicationConfiguration = await plcOpcApplicationConfiguration.ConfigureAsync(stackLogger).ConfigureAwait(false);

        // start the server.
        Logger.LogInformation("Starting server on endpoint {endpoint} ...", plcApplicationConfiguration.ServerConfiguration.BaseAddresses[0]);
        Logger.LogInformation("Simulation settings are:");
        Logger.LogInformation("One simulation phase consists of {SimulationCycleCount} cycles", SimulationCycleCount);
        Logger.LogInformation("One cycle takes {SimulationCycleLength} ms", SimulationCycleLength);
        Logger.LogInformation("Reference test simulation: {addReferenceTestSimulation}",
            AddReferenceTestSimulation ? "Enabled" : "Disabled");
        Logger.LogInformation("Simple events: {addSimpleEventsSimulation}",
            AddSimpleEventsSimulation ? "Enabled" : "Disabled");
        Logger.LogInformation("Alarms: {addAlarmSimulation}", AddAlarmSimulation ? "Enabled" : "Disabled");
        Logger.LogInformation("Deterministic alarms: {deterministicAlarmSimulation}",
            DeterministicAlarmSimulationFile != null ? "Enabled" : "Disabled");

        Logger.LogInformation("Anonymous authentication: {anonymousAuth}", DisableAnonymousAuth ? "Disabled" : "Enabled");
        Logger.LogInformation("Reject chain validation with CA certs with unknown revocation status: {rejectValidationUnknownRevocStatus}", DontRejectUnknownRevocationStatus ? "Disabled" : "Enabled");
        Logger.LogInformation("Username/Password authentication: {usernamePasswordAuth}", DisableUsernamePasswordAuth ? "Disabled" : "Enabled");
        Logger.LogInformation("Certificate authentication: {certAuth}", DisableCertAuth ? "Disabled" : "Enabled");

        var logger = LoggerFactoryInstance.CreateLogger<PlcServer>();

        // Add simple events, alarms, reference test simulation and deterministic alarms.
        PlcServer = new PlcServer(TimeService, logger);
        PlcServer.Start(plcApplicationConfiguration);
        Logger.LogInformation("OPC UA Server started");

        // Add remaining base simulations.
        PlcSimulation = new PlcSimulation(PlcServer);
        PlcSimulation.Start();

        if (ShowPublisherConfigJsonIp)
        {
            await PnJsonHelper.PrintPublisherConfigJsonAsync(
                PnJson,
                $"{GetIpAddress()}:{ServerPort}{ServerPath}",
                PluginNodes,
                Logger).ConfigureAwait(false);
        }
        else if (ShowPublisherConfigJsonPh)
        {
            await PnJsonHelper.PrintPublisherConfigJsonAsync(
                PnJson,
                $"{Hostname}:{ServerPort}{ServerPath}",
                PluginNodes,
                Logger).ConfigureAwait(false);
        }

        Ready = true;
        Logger.LogInformation("PLC simulation started, press Ctrl+C to exit ...");

        // wait for Ctrl-C

        // allow canceling the connection process
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, eArgs) =>
        {
            cancellationTokenSource.Cancel();
            eArgs.Cancel = true;
        };

        while (!cancellationTokenSource.Token.WaitHandle.WaitOne(1000))
        {
        }

        PlcSimulation.Stop();
        PlcServer.Stop();
    }

    /// <summary>
    /// Initialize logging.
    /// </summary>
    private static void InitLogging()
    {
        if (string.IsNullOrWhiteSpace(LogLevelForOPCUAServer))
        {
            LogLevelForOPCUAServer = LogLevel;
        }

        switch (LogLevelForOPCUAServer)
        {
            case "fatal":
                OpcStackTraceMask = OpcTraceToLoggerFatal = 0;
                break;
            case "error":
                OpcStackTraceMask = OpcTraceToLoggerError = Utils.TraceMasks.Error;
                break;
            case "warn":
                OpcStackTraceMask = OpcTraceToLoggerError = Utils.TraceMasks.Error | Utils.TraceMasks.StackTrace;
                OpcTraceToLoggerWarning = Utils.TraceMasks.StackTrace;
                OpcStackTraceMask |= OpcTraceToLoggerWarning;
                break;
            case "info":
                OpcTraceToLoggerError = Utils.TraceMasks.Error;
                OpcTraceToLoggerWarning = Utils.TraceMasks.StackTrace;
                OpcTraceToLoggerInformation = Utils.TraceMasks.Security;
                OpcStackTraceMask = OpcTraceToLoggerError | OpcTraceToLoggerInformation | OpcTraceToLoggerWarning;
                break;
            case "debug":
                OpcTraceToLoggerError = Utils.TraceMasks.Error;
                OpcTraceToLoggerWarning = Utils.TraceMasks.StackTrace;
                OpcTraceToLoggerInformation = Utils.TraceMasks.Security;
                OpcTraceToLoggerDebug = Utils.TraceMasks.Operation | Utils.TraceMasks.StartStop | Utils.TraceMasks.ExternalSystem | Utils.TraceMasks.OperationDetail | Utils.TraceMasks.Service | Utils.TraceMasks.ServiceDetail;
                OpcStackTraceMask = OpcTraceToLoggerError | OpcTraceToLoggerInformation | OpcTraceToLoggerDebug | OpcTraceToLoggerWarning;
                break;
            case "verbose":
                OpcTraceToLoggerError = Utils.TraceMasks.Error | Utils.TraceMasks.StackTrace;
                OpcTraceToLoggerInformation = Utils.TraceMasks.Security;
                OpcStackTraceMask = OpcTraceToLoggerVerbose = Utils.TraceMasks.All;
                break;
        }
    }

    /// <summary>
    /// Configure web server.
    /// </summary>
    public static void StartWebServer(string[] args)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddAuthorization();

            builder.Services
                .AddOpenTelemetry()
                .WithMetrics(opts => opts
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("opc-plc"))
                    .AddMeter(DiagnosticsConfig.Meter.Name)
                    .AddProcessInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddPrometheusExporter()
                );

            LoggerFactoryInstance = LoggerFactory.Create(builder =>
            {
                // sets up OpenTelemetry logs for Information and above. * refers to all categories.
                builder.ClearProviders();

                // set the log level
                switch (LogLevel)
                {
                    case "fatal":
                        builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Critical);
                        break;
                    case "error":
                        builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Error);
                        break;
                    case "warn":
                        builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
                        break;
                    case "info":
                        builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                        break;
                    case "debug":
                        builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
                        break;
                    case "verbose":
                        builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                        break;
                }

                builder.AddConsole();
                builder.AddOpenTelemetryLogging();
            });

            Logger = LoggerFactoryInstance.CreateLogger(nameof(Program));

            var metricLogger = LoggerFactoryInstance.CreateLogger("Metrics");
            DiagnosticsConfig.SetLogger(metricLogger);

            builder.WebHost.UseUrls($"http://*:{WebServerPort}");
            builder.WebHost.UseContentRoot(Directory.GetCurrentDirectory());

            var app = builder.Build();
            app.UseOpenTelemetryPrometheusScrapingEndpoint();
            app.UseAuthorization();
            app.MapControllers();
            app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}");

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.RunAsync();
            /*
            app.Run(async context =>
            {
                if (context.Request.Method == "GET" && context.Request.Path == (Program.PnJson[0] != '/' ? "/" : string.Empty) + Program.PnJson &&
                    File.Exists(Program.PnJson))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(await File.ReadAllTextAsync(Program.PnJson).ConfigureAwait(false)).ConfigureAwait(false);
                }
                else
                {

                    context.Response.StatusCode = 404;
                }
            });
            */
            Logger.LogInformation("Web server started on port {webServerPort}", WebServerPort);
        }
        catch (Exception e)
        {
            Logger.LogError("Could not start web server on port {webServerPort}: {message}",
                WebServerPort,
                e.Message);
        }
    }

    private static void LogLogo()
    {
        Logger.LogInformation(
            @"
 ██████╗ ██████╗  ██████╗    ██████╗ ██╗      ██████╗
██╔═══██╗██╔══██╗██╔════╝    ██╔══██╗██║     ██╔════╝
██║   ██║██████╔╝██║         ██████╔╝██║     ██║
██║   ██║██╔═══╝ ██║         ██╔═══╝ ██║     ██║
╚██████╔╝██║     ╚██████╗    ██║     ███████╗╚██████╗
 ╚═════╝ ╚═╝      ╚═════╝    ╚═╝     ╚══════╝ ╚═════╝
");
    }

}
