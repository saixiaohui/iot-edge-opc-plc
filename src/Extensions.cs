namespace OpcPlc;

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry.Exporter.Geneva;
using System;
using System.Collections.Generic;
using System.Reflection;

public static class Extensions
{
    private static readonly IDictionary<string, Type> failedTypes = new Dictionary<string, Type>();
    private const string SerilaztionErrorMessage = "Couldn't be serialized";

    public static string ToJson<T>(this T item)
        where T : class
    {
        if (failedTypes.ContainsKey(typeof(T).ToString()))
        {
            return SerilaztionErrorMessage;
        }

        try
        {
            return JsonConvert.SerializeObject(item);
        }
        catch (Exception)
        {
            failedTypes[typeof(T).ToString()] = typeof(T);
            return SerilaztionErrorMessage;
        }
    }

    public static void AddOpenTelemetryLogging(this ILoggingBuilder builder, IDictionary<string, object> additionalPrepopulatedFields = null)
    {
        builder.AddOpenTelemetry(loggerOptions =>
        {
            var useGenevaLogging = true;    // TODO: add config
            if (useGenevaLogging)
            {
                loggerOptions.AddGenevaLogExporter(exporterOptions =>
                {
                    exporterOptions.ConnectionString = "EtwSession=OpenTelemetry";
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                    {
                        exporterOptions.ConnectionString = "Endpoint=unix:/var/run/mdsd/default_fluent.socket";
                    }
                    const string ORCA_METRIC_TABLE = "OrcaMetrics";
                    const string ORCA_API_TABLE = "OrcaAPIs";
                    exporterOptions.TableNameMappings = new Dictionary<string, string>()
                    {
                        [nameof(DiagnosticsConfig)] = ORCA_METRIC_TABLE,
                        ["Metrics"] = ORCA_METRIC_TABLE,
                        ["Microsoft.AspNetCore.Hosting.Diagnostics"] = ORCA_API_TABLE,
                        ["Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker"] = ORCA_API_TABLE,
                        ["Microsoft.AspNetCore.Mvc.Infrastructure.ObjectResultExecutor"] = ORCA_API_TABLE,
                        ["Microsoft.AspNetCore.Mvc.StatusCodeResult"] = ORCA_API_TABLE,
                        ["Microsoft.AspNetCore.Routing.EndpointMiddleware"] = ORCA_API_TABLE
                    };
                    exporterOptions.ExceptionStackExportMode = ExceptionStackExportMode.ExportAsString;
                    exporterOptions.CustomFields = new List<string>()
                    {
                                "targetId",
                                "simId",
                                "taskType",
                                "podId",
                                "clientGroup",
                                "errorCode",
                                "targetResourceId",
                                "clientType",
                                "deviceId",
                                "simRunId",
                                "metricName",
                                "metricType",
                                "metricValue",
                                "kubernetes_node",
                                "simulationId",
                                "errorType",
                                "moduleId",
                                "duration"
                    };
                    const string epoch = "Simulation";
                    var list =
                    exporterOptions.PrepopulatedFields = new Dictionary<string, object>(additionalPrepopulatedFields ?? new Dictionary<string, object>())
                    {
                        ["env_ver"] = Constants.Geneva.COMMON_SCHEMA_VERSION,
                        ["env_name"] = Constants.Geneva.SIMULATION_APPLICATION_NAMESPACE,
                        ["env_epoch"] = epoch,
                        ["env_cloud_location"] = string.Empty,  // TODO
                        ["env_cloud_ver"] = $"{Constants.Geneva.DOTNET_PREFIX}-{Constants.Geneva.SIMULATION_APPLICATION_VERSION}-{Assembly.GetExecutingAssembly().GetName().Version}",
                        ["env_cloud_name"] = Constants.Geneva.SIMULATION_APPLICATION_NAME,
                        ["env_cloud_roleVer"] = BUILD_NUMBER ?? string.Empty,
                        ["env_cloud_roleInstance"] = ROLE_INSTANCE ?? string.Empty,
                        ["env_cloud_role"] = Assembly.GetEntryAssembly().GetName().Name,
                        ["env_cloud_deploymentUnit"] = CLUSTER_NAME ?? string.Empty,
                        ["env_flags"] = Constants.Geneva.SIMULATION_APPLICATION_NAME,
                        ["env_cloud_environment"] = string.Empty,    // TODO
                        ["simulationId"] = SIMULATION_ID ?? string.Empty 
                    };
                });
            }
            loggerOptions.IncludeFormattedMessage = true;
            loggerOptions.IncludeScopes = true;
        });
    }

    private static string ROLE_INSTANCE
    {
        get
        {
            return System.Environment.MachineName;
        }
    }

    private static string SIMULATION_ID
    {
        get
        {
            return Environment.GetEnvironmentVariable("SIMULATION_ID");
        }
    }

    private static string KUBERNETES_NODE
    {
        get
        {
            return Environment.GetEnvironmentVariable("KUBERNETES_NODE");
        }
    }

    private static string CLUSTER_NAME
    {
        get
        {
            return Environment.GetEnvironmentVariable("DEPLOYMENT_NAME");
        }
    }

    private static string BUILD_NUMBER
    {
        get
        {
            return Environment.GetEnvironmentVariable("BUILD_NUMBER");
        }
    }
}
