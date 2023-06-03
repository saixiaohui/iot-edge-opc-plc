namespace OpcPlc;

using AlarmCondition;
using Opc.Ua;
using Opc.Ua.Server;
using OpcPlc.CompanionSpecs.DI;
using OpcPlc.DeterministicAlarms;
using OpcPlc.Reference;
using SimpleEvents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using static Program;

public partial class PlcServer : StandardServer
{
    public PlcNodeManager PlcNodeManager = null;
    public AlarmConditionServerNodeManager AlarmNodeManager = null;
    public SimpleEventsNodeManager SimpleEventsNodeManager = null;
    public ReferenceNodeManager SimulationNodeManager = null;
    public DeterministicAlarmsNodeManager DeterministicAlarmsNodeManager = null;
    public readonly TimeService TimeService;

    public PlcServer(TimeService timeService)
    {
        TimeService = timeService;
    }

    public override ResponseHeader CreateSession(
        RequestHeader requestHeader,
        ApplicationDescription clientDescription,
        string serverUri,
        string endpointUrl,
        string sessionName,
        byte[] clientNonce,
        byte[] clientCertificate,
        double requestedSessionTimeout,
        uint maxResponseMessageSize,
        out NodeId sessionId,
        out NodeId authenticationToken,
        out double revisedSessionTimeout,
        out byte[] serverNonce,
        out byte[] serverCertificate,
        out EndpointDescriptionCollection serverEndpoints,
        out SignedSoftwareCertificateCollection serverSoftwareCertificates,
        out SignatureData serverSignature,
        out uint maxRequestMessageSize)
    {
        try
        {
            var responseHeader = base.CreateSession(requestHeader, clientDescription, serverUri, endpointUrl, sessionName, clientNonce, clientCertificate, requestedSessionTimeout, maxResponseMessageSize, out sessionId, out authenticationToken, out revisedSessionTimeout, out serverNonce, out serverCertificate, out serverEndpoints, out serverSoftwareCertificates, out serverSignature, out maxRequestMessageSize);

            Meters.AddSessionCount(sessionId.ToString());

            Logger.Information("{function} completed successfully with sesssionId: {sessionId}.", nameof(CreateSession), sessionId);

            return responseHeader;
        }
        catch (Exception ex)
        {
            Meters.RecordTotalErrors(nameof(CreateSession));
            Logger.Error(ex, "Error creating session");
            throw;
        }
    }

    public override ResponseHeader CreateSubscription(
        RequestHeader requestHeader,
        double requestedPublishingInterval,
        uint requestedLifetimeCount,
        uint requestedMaxKeepAliveCount,
        uint maxNotificationsPerPublish,
        bool publishingEnabled,
        byte priority,
        out uint subscriptionId,
        out double revisedPublishingInterval,
        out uint revisedLifetimeCount,
        out uint revisedMaxKeepAliveCount)
    {
        try
        {
            OperationContext context = ValidateRequest(requestHeader, RequestType.CreateSubscription);

            var responseHeader = base.CreateSubscription(requestHeader, requestedPublishingInterval, requestedLifetimeCount, requestedMaxKeepAliveCount, maxNotificationsPerPublish, publishingEnabled, priority, out subscriptionId, out revisedPublishingInterval, out revisedLifetimeCount, out revisedMaxKeepAliveCount);

            Meters.AddSubscriptionCount(context.SessionId.ToString(), subscriptionId.ToString());

            Logger.Information(
                "{function} completed successfully with sessionId: {sessionId} and subscriptionId: {subscriptionId}.",
                nameof(CreateSubscription),
                context.SessionId,
                subscriptionId);

            return responseHeader;
        }
        catch (Exception ex)
        {
            Meters.RecordTotalErrors(nameof(CreateSubscription));
            Logger.Error(ex, "Error creating subscription");
            throw;
        }
    }

    public override ResponseHeader CreateMonitoredItems(
        RequestHeader requestHeader,
        uint subscriptionId,
        TimestampsToReturn timestampsToReturn,
        MonitoredItemCreateRequestCollection itemsToCreate,
        out MonitoredItemCreateResultCollection results,
        out DiagnosticInfoCollection diagnosticInfos)
    {
        try
        {
            OperationContext context = ValidateRequest(requestHeader, RequestType.CreateSubscription);

            var responseHeader = base.CreateMonitoredItems(requestHeader, subscriptionId, timestampsToReturn, itemsToCreate, out results, out diagnosticInfos);

            Meters.AddMonitoredItemCount(context.SessionId.ToString(), subscriptionId.ToString(), itemsToCreate.Count);

            Logger.Information("{function} completed successfully with sessionId: {sessionId}, subscriptionId: {subscriptionId} and count: {count}.",
                nameof(CreateMonitoredItems),
                context.SessionId,
                subscriptionId,
                itemsToCreate.Count);

            return responseHeader;
        }
        catch (Exception ex)
        {
            Meters.RecordTotalErrors(nameof(CreateMonitoredItems));
            Logger.Error(ex, "Error creating monitored items");
            throw;
        }
    }

    public override ResponseHeader Publish(
        RequestHeader requestHeader,
        SubscriptionAcknowledgementCollection subscriptionAcknowledgements,
        out uint subscriptionId,
        out UInt32Collection availableSequenceNumbers,
        out bool moreNotifications,
        out NotificationMessage notificationMessage,
        out StatusCodeCollection results,
        out DiagnosticInfoCollection diagnosticInfos)
    {
        try
        {
            OperationContext context = ValidateRequest(requestHeader, RequestType.CreateSubscription);

            var responseHeader = base.Publish(requestHeader, subscriptionAcknowledgements, out subscriptionId, out availableSequenceNumbers, out moreNotifications, out notificationMessage, out results, out diagnosticInfos);

            int events = 0;
            int dataChanges = 0;
            int diagnostics = 0;
            notificationMessage.NotificationData.ForEach(x =>
            {
                if (x.Body is DataChangeNotification changeNotification)
                {
                    dataChanges += changeNotification.MonitoredItems.Count;
                    diagnostics += changeNotification.DiagnosticInfos.Count;
                }
                else if (x.Body is EventNotificationList eventNotification)
                {
                    events += eventNotification.Events.Count;
                }
                else
                {
                    Console.WriteLine("Unknown notification type");
                }
            });

            Meters.AddPublishedCount(context.SessionId.ToString(), subscriptionId.ToString(), dataChanges, events);

            Logger.Debug("{function} successfully with session: {sessionId} and subscriptionId: {subscriptionId}.",
                nameof(Publish),
                context.SessionId,
                subscriptionId);

            return responseHeader;
        }
        catch (Exception ex)
        {
            Meters.RecordTotalErrors(nameof(Publish));
            Logger.Error(ex, "Error publishing.");
            throw;
        }
    }

    public override ResponseHeader Read(
        RequestHeader requestHeader,
        double maxAge,
        TimestampsToReturn timestampsToReturn,
        ReadValueIdCollection nodesToRead,
        out DataValueCollection results,
        out DiagnosticInfoCollection diagnosticInfos)
    {
        try
        {
            var responseHeader = base.Read(requestHeader, maxAge, timestampsToReturn, nodesToRead, out results, out diagnosticInfos);

            Logger.Debug("{function} completed successfully.", nameof(Read));

            return responseHeader;
        }
        catch (Exception ex)
        {
            Meters.RecordTotalErrors(nameof(Read));
            Logger.Error(ex, "Error reading.");
            throw;
        }
    }

    public override ResponseHeader Write(RequestHeader requestHeader, WriteValueCollection nodesToWrite, out StatusCodeCollection results, out DiagnosticInfoCollection diagnosticInfos)
    {
        try
        {
            var responseHeader = base.Write(requestHeader, nodesToWrite, out results, out diagnosticInfos);

            Logger.Information("{function} completed successfully.", nameof(Write));

            return responseHeader;
        }
        catch (Exception ex)
        {
            Meters.RecordTotalErrors(nameof(Write));
            Logger.Error(ex, "Error writing.");
            throw;
        }
    }

    /// <summary>
    /// Creates the node managers for the server.
    /// </summary>
    /// <remarks>
    /// This method allows the sub-class create any additional node managers which it uses. The SDK
    /// always creates a CoreNodesManager which handles the built-in nodes defined by the specification.
    /// Any additional NodeManagers are expected to handle application specific nodes.
    /// </remarks>
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new List<INodeManager>();

        // Add encodable complex types.
        server.Factory.AddEncodeableTypes(Assembly.GetExecutingAssembly());
        EncodeableFactory.GlobalFactory.AddEncodeableTypes(Assembly.GetExecutingAssembly());

        // Add DI node manager first so that it gets the namespace index 2.
        var diNodeManager = new DiNodeManager(server, configuration);
        nodeManagers.Add(diNodeManager);

        PlcNodeManager = new PlcNodeManager(
            server,
            configuration,
            TimeService);

        nodeManagers.Add(PlcNodeManager);

        if (PlcSimulation.AddSimpleEventsSimulation)
        {
            SimpleEventsNodeManager = new SimpleEventsNodeManager(server, configuration);
            nodeManagers.Add(SimpleEventsNodeManager);
        }

        if (PlcSimulation.AddAlarmSimulation)
        {
            AlarmNodeManager = new AlarmConditionServerNodeManager(server, configuration);
            nodeManagers.Add(AlarmNodeManager);
        }

        if (PlcSimulation.AddReferenceTestSimulation)
        {
            SimulationNodeManager = new ReferenceNodeManager(server, configuration);
            nodeManagers.Add(SimulationNodeManager);
        }

        if (PlcSimulation.DeterministicAlarmSimulationFile != null)
        {
            var scriptFileName = PlcSimulation.DeterministicAlarmSimulationFile;
            if (string.IsNullOrWhiteSpace(scriptFileName))
            {
                string errorMessage = "The script file for deterministic testing is not set (deterministicalarms).";
                Logger.Error(errorMessage);
                throw new Exception(errorMessage);
            }
            if (!File.Exists(scriptFileName))
            {
                string errorMessage = $"The script file ({scriptFileName}) for deterministic testing does not exist.";
                Logger.Error(errorMessage);
                throw new Exception(errorMessage);
            }

            DeterministicAlarmsNodeManager = new DeterministicAlarmsNodeManager(server, configuration, TimeService, scriptFileName);
            nodeManagers.Add(DeterministicAlarmsNodeManager);
        }

        var masterNodeManager = new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, nodeManagers.ToArray());

        return masterNodeManager;
    }

    public override ResponseHeader DeleteMonitoredItems(RequestHeader requestHeader, uint subscriptionId, UInt32Collection monitoredItemIds, out StatusCodeCollection results, out DiagnosticInfoCollection diagnosticInfos)
    {
        return base.DeleteMonitoredItems(requestHeader, subscriptionId, monitoredItemIds, out results, out diagnosticInfos);
    }

    public override ResponseHeader CloseSession(RequestHeader requestHeader, bool deleteSubscriptions)
    {
        return base.CloseSession(requestHeader, deleteSubscriptions);
    }

    /// <summary>
    /// Loads the non-configurable properties for the application.
    /// </summary>
    /// <remarks>
    /// These properties are exposed by the server but cannot be changed by administrators.
    /// </remarks>
    protected override ServerProperties LoadServerProperties()
    {
        var properties = new ServerProperties
        {
            ManufacturerName = "Microsoft",
            ProductName = "IoTEdge OPC UA PLC",
            ProductUri = "https://github.com/Azure/iot-edge-opc-plc.git",
            SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
            BuildNumber = Utils.GetAssemblyBuildNumber(),
            BuildDate = Utils.GetAssemblyTimestamp()
        };
        return properties;
    }

    /// <summary>
    /// Creates the resource manager for the server.
    /// </summary>
    protected override ResourceManager CreateResourceManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        var resourceManager = new ResourceManager(server, configuration);

        FieldInfo[] fields = typeof(StatusCodes).GetFields(BindingFlags.Public | BindingFlags.Static);

        foreach (FieldInfo field in fields)
        {
            uint? id = field.GetValue(typeof(StatusCodes)) as uint?;

            if (id != null)
            {
                resourceManager.Add(id.Value, "en-US", field.Name);
            }
        }

        return resourceManager;
    }

    /// <summary>
    /// Initializes the server before it starts up.
    /// </summary>
    protected override void OnServerStarting(ApplicationConfiguration configuration)
    {
        base.OnServerStarting(configuration);

        // it is up to the application to decide how to validate user identity tokens.
        // this function creates validator for X509 identity tokens.
        CreateUserIdentityValidators(configuration);
    }

    protected override void OnServerStarted(IServerInternal server)
    {
        // start the simulation
        base.OnServerStarted(server);

        // request notifications when the user identity is changed, all valid users are accepted by default.
        server.SessionManager.ImpersonateUser += new ImpersonateEventHandler(SessionManager_ImpersonateUser);
    }

    /// <summary>
    /// Cleans up before the server shuts down.
    /// </summary>
    /// <remarks>
    /// This method is called before any shutdown processing occurs.
    /// </remarks>
    protected override void OnServerStopping()
    {
        try
        {
            // check for connected clients
            IList<Session> currentessions = ServerInternal.SessionManager.GetSessions();

            if (currentessions.Count > 0)
            {
                // provide some time for the connected clients to detect the shutdown state.
                ServerInternal.Status.Value.ShutdownReason = new LocalizedText("en-US", "Application closed.");
                ServerInternal.Status.Variable.ShutdownReason.Value = new LocalizedText("en-US", "Application closed.");
                ServerInternal.Status.Value.State = ServerState.Shutdown;
                ServerInternal.Status.Variable.State.Value = ServerState.Shutdown;
                ServerInternal.Status.Variable.ClearChangeMasks(ServerInternal.DefaultSystemContext, true);

                for (uint timeTillShutdown = _plcShutdownWaitPeriod; timeTillShutdown > 0; timeTillShutdown--)
                {
                    ServerInternal.Status.Value.SecondsTillShutdown = timeTillShutdown;
                    ServerInternal.Status.Variable.SecondsTillShutdown.Value = timeTillShutdown;
                    ServerInternal.Status.Variable.ClearChangeMasks(ServerInternal.DefaultSystemContext, true);

                    Thread.Sleep(1000);
                }
            }
        }
        catch
        {
            // ignore error during shutdown procedure
        }

        base.OnServerStopping();
    }

    private const uint _plcShutdownWaitPeriod = 10;
}
