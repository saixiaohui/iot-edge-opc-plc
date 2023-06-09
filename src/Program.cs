namespace OpcPlc;

using Kusto.Cloud.Platform.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using OpcPlc.Helpers;
using OpcPlc.PluginNodes.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Sinks.AzureDataExplorer;
using Serilog.Sinks.AzureDataExplorer.Extensions;
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
    /// Logging object.
    /// </summary>
    public static Serilog.Core.Logger Logger = null;

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

    public static Metrics Meters { get; private set; }

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

        // Show usage if requested
        if (ShowHelp)
        {
            CliOptions.PrintUsage(options);
            return;
        }

        // Validate and parse extra arguments
        if (extraArgs.Count > 0)
        {
            Logger.Warning($"Found one or more invalid command line arguments: {string.Join(" ", extraArgs)}");
            CliOptions.PrintUsage(options);
        }

        LogLogo();

        Logger.Information("Current directory: {currentDirectory}", Directory.GetCurrentDirectory());
        Logger.Information("Log file: {logFileName}", Path.GetFullPath(LogFileName));
        Logger.Information("Log level: {logLevel}", LogLevel);

        //show version
        var fileVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
        Logger.Information("{ProgramName} {version} starting up ...",
            ProgramName,
            $"v{fileVersion.ProductMajorPart}.{fileVersion.ProductMinorPart}.{fileVersion.ProductBuildPart}");
        Logger.Debug("Informational version: {version}",
            $"v{(Attribute.GetCustomAttribute(Assembly.GetEntryAssembly(), typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute)?.InformationalVersion}");

        StartWebServer(args);

        try
        {
            await ConsoleServerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "OPC UA server failed unexpectedly");
        }

        Logger.Information("OPC UA server exiting...");
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
        // init OPC configuration and tracing
        var plcOpcApplicationConfiguration = new OpcApplicationConfiguration();
        ApplicationConfiguration plcApplicationConfiguration = await plcOpcApplicationConfiguration.ConfigureAsync().ConfigureAwait(false);

        // start the server.
        Logger.Information("Starting server on endpoint {endpoint} ...", plcApplicationConfiguration.ServerConfiguration.BaseAddresses[0]);
        Logger.Information("Simulation settings are:");
        Logger.Information("One simulation phase consists of {SimulationCycleCount} cycles", SimulationCycleCount);
        Logger.Information("One cycle takes {SimulationCycleLength} ms", SimulationCycleLength);
        Logger.Information("Reference test simulation: {addReferenceTestSimulation}",
            AddReferenceTestSimulation ? "Enabled" : "Disabled");
        Logger.Information("Simple events: {addSimpleEventsSimulation}",
            AddSimpleEventsSimulation ? "Enabled" : "Disabled");
        Logger.Information("Alarms: {addAlarmSimulation}", AddAlarmSimulation ? "Enabled" : "Disabled");
        Logger.Information("Deterministic alarms: {deterministicAlarmSimulation}",
            DeterministicAlarmSimulationFile != null ? "Enabled" : "Disabled");

        Logger.Information("Anonymous authentication: {anonymousAuth}", DisableAnonymousAuth ? "Disabled" : "Enabled");
        Logger.Information("Reject chain validation with CA certs with unknown revocation status: {rejectValidationUnknownRevocStatus}", DontRejectUnknownRevocationStatus ? "Disabled" : "Enabled");
        Logger.Information("Username/Password authentication: {usernamePasswordAuth}", DisableUsernamePasswordAuth ? "Disabled" : "Enabled");
        Logger.Information("Certificate authentication: {certAuth}", DisableCertAuth ? "Disabled" : "Enabled");

        // Add simple events, alarms, reference test simulation and deterministic alarms.
        PlcServer = new PlcServer(TimeService);
        PlcServer.Start(plcApplicationConfiguration);
        Logger.Information("OPC UA Server started");

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
        Logger.Information("PLC simulation started, press Ctrl+C to exit ...");

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
        var loggerConfiguration = new LoggerConfiguration();

        // enrich log events with some properties
        if (!string.IsNullOrWhiteSpace(CLUSTER_NAME))
        {
            loggerConfiguration.Enrich.WithProperty("ClusterName", CLUSTER_NAME);
        }

        if (!string.IsNullOrWhiteSpace(SIMULATION_ID))
        {
            loggerConfiguration.Enrich.WithProperty("RoleName", SIMULATION_ID);
        }

        if (!string.IsNullOrWhiteSpace(KUBERNETES_NODE))
        {
            loggerConfiguration.Enrich.WithProperty("Node", KUBERNETES_NODE);
        }

        if (!string.IsNullOrWhiteSpace(ROLE_INSTANCE))
        {
            loggerConfiguration.Enrich.WithProperty("RoleInstance", ROLE_INSTANCE);
        }

        if (!string.IsNullOrWhiteSpace(BUILD_NUMBER))
        {
            loggerConfiguration.Enrich.WithProperty("BuildNumber", BUILD_NUMBER);
        }

        if (string.IsNullOrWhiteSpace(LogLevelForOPCUAServer))
        {
            LogLevelForOPCUAServer = LogLevel;
        }

        // set the log level
        switch (LogLevel)
        {
            case "fatal":
                loggerConfiguration.MinimumLevel.Fatal();
                break;
            case "error":
                loggerConfiguration.MinimumLevel.Error();
                break;
            case "warn":
                loggerConfiguration.MinimumLevel.Warning();
                break;
            case "info":
                loggerConfiguration.MinimumLevel.Information();
                break;
            case "debug":
                loggerConfiguration.MinimumLevel.Debug();
                break;
            case "verbose":
                loggerConfiguration.MinimumLevel.Verbose();
                break;
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

        // set logging sinks
        loggerConfiguration.WriteTo.Console();

        if (LogToADX)
        {
            loggerConfiguration.WriteTo.AzureDataExplorerSink(new AzureDataExplorerSinkOptions
            {
                IngestionEndpointUri = Environment.GetEnvironmentVariable("ingestionURI"),
                DatabaseName = Environment.GetEnvironmentVariable("databaseName"),
                TableName = Environment.GetEnvironmentVariable("tableName"),
                FlushImmediately = Environment.GetEnvironmentVariable("flushImmediately").IsNotNullOrEmpty() && bool.Parse(Environment.GetEnvironmentVariable("flushImmediately")!),
                BufferBaseFileName = Environment.GetEnvironmentVariable("bufferBaseFileName"),
                BatchPostingLimit = 10,
                Period = TimeSpan.FromSeconds(5),

                ColumnsMapping = new[]
                    {
                        new SinkColumnMapping { ColumnName ="Timestamp", ColumnType ="datetime", ValuePath = "$.Timestamp" } ,
                        new SinkColumnMapping { ColumnName ="Level", ColumnType ="string", ValuePath = "$.Level" } ,
                        new SinkColumnMapping { ColumnName ="ClusterName", ColumnType ="string", ValuePath = "$.Properties.ClusterName" } ,
                        new SinkColumnMapping { ColumnName ="RoleName", ColumnType ="string", ValuePath = "$.Properties.RoleName" } ,
                        new SinkColumnMapping { ColumnName ="Node", ColumnType ="string", ValuePath = "$.Properties.Node" } ,
                        new SinkColumnMapping { ColumnName ="RoleInstance", ColumnType ="string", ValuePath = "$.Properties.RoleInstance" } ,
                        new SinkColumnMapping { ColumnName ="SourceContext", ColumnType ="string", ValuePath = "$.Properties.SourceContext" } ,
                        new SinkColumnMapping { ColumnName ="Operation", ColumnType ="string", ValuePath = "$.Properties.function" } ,
                        new SinkColumnMapping { ColumnName ="Message", ColumnType ="string", ValuePath = "$.Message" } ,
                        new SinkColumnMapping { ColumnName ="Exception", ColumnType ="string", ValuePath = "$.Error" } ,
                        new SinkColumnMapping { ColumnName ="Properties", ColumnType ="dynamic", ValuePath = "$.Properties" } ,
                        new SinkColumnMapping { ColumnName ="Position", ColumnType ="dynamic", ValuePath = "$.Properties.Position" } ,
                        new SinkColumnMapping { ColumnName ="Elapsed", ColumnType ="int", ValuePath = "$.Properties.Elapsed" } ,
                        new SinkColumnMapping { ColumnName ="BuildNumber", ColumnType ="int", ValuePath = "$.Properties.BuildNumber" } ,
                    }
            }.WithAadApplicationKey(
                Environment.GetEnvironmentVariable("appId"),
                Environment.GetEnvironmentVariable("appKey"),
                Environment.GetEnvironmentVariable("tenant")));
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_GW_LOGP")))
        {
            LogFileName = Environment.GetEnvironmentVariable("_GW_LOGP");
        }

        if (!string.IsNullOrEmpty(LogFileName))
        {
            // configure rolling file sink
            const int MAX_LOGFILE_SIZE = 1024 * 1024;
            const int MAX_RETAINED_LOGFILES = 2;
            loggerConfiguration.WriteTo.File(LogFileName, fileSizeLimitBytes: MAX_LOGFILE_SIZE, flushToDiskInterval: LogFileFlushTimeSpanSec, rollOnFileSizeLimit: true, retainedFileCountLimit: MAX_RETAINED_LOGFILES);
        }

        Logger = loggerConfiguration.CreateLogger();
    }

    /// <summary>
    /// Configure web server.
    /// </summary>
    public static void StartWebServer(string[] args)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(args);

            var baseDimensions = new Dictionary<string, object>
            {
                { "host",       ROLE_INSTANCE ?? "host"         },
                { "app",        "opc-plc"                       },
                { "simid",      SIMULATION_ID ?? "simulation"   },
                { "cluster",    CLUSTER_NAME ?? "cluster"       },
            };

            Console.WriteLine(baseDimensions.ToJson());

            Meters = new Metrics("metrics", baseDimensions);

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddAuthorization();

            builder.Services
                .AddOpenTelemetry()
                .WithMetrics(opts => opts
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("opc-plc"))
                    .AddMeter(Meters.MetricName)
                    .AddProcessInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddPrometheusExporter()
                );

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
            Logger.Information("Web server started on port {webServerPort}", WebServerPort);
        }
        catch (Exception e)
        {
            Logger.Error("Could not start web server on port {webServerPort}: {message}",
                WebServerPort,
                e.Message);
        }
    }

    private static void LogLogo()
    {
        Logger.Information(
            @"
 ██████╗ ██████╗  ██████╗    ██████╗ ██╗      ██████╗
██╔═══██╗██╔══██╗██╔════╝    ██╔══██╗██║     ██╔════╝
██║   ██║██████╔╝██║         ██████╔╝██║     ██║
██║   ██║██╔═══╝ ██║         ██╔═══╝ ██║     ██║
╚██████╔╝██║     ╚██████╗    ██║     ███████╗╚██████╗
 ╚═════╝ ╚═╝      ╚═════╝    ╚═╝     ╚══════╝ ╚═════╝
");
    }

    private static string ROLE_INSTANCE
    {
        get
        {
            try
            {
                return System.Environment.MachineName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string SIMULATION_ID
    {
        get
        {
            try
            {
                var simulationId = Environment.GetEnvironmentVariable("SIMULATION_ID");
                if (string.IsNullOrEmpty(simulationId))
                {
                    return null;
                }

                return simulationId;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string KUBERNETES_NODE
    {
        get
        {
            try
            {
                var node = Environment.GetEnvironmentVariable("KUBERNETES_NODE");
                if (string.IsNullOrEmpty(node))
                {
                    return null;
                }

                return node;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string CLUSTER_NAME
    {
        get
        {
            try
            {
                var clusterName = Environment.GetEnvironmentVariable("DEPLOYMENT_NAME");

                if (string.IsNullOrEmpty(clusterName))
                {
                    return null;
                }

                return clusterName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string BUILD_NUMBER
    {
        get
        {
            try
            {
                var buildNumber = Environment.GetEnvironmentVariable("BUILD_NUMBER");

                if (string.IsNullOrEmpty(buildNumber))
                {
                    return null;
                }

                return buildNumber;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
