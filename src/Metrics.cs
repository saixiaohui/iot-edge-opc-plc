namespace OpcPlc;

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

public class Metrics
{
    private const string OPC_PLC_POD_COUNT_METRIC = "opc_plc_pod_count";
    private const string OPC_PLC_SESSION_COUNT_METRIC = "opc_plc_session_count";
    private const string OPC_PLC_SUBSCRIPTION_COUNT_METRIC = "opc_plc_subscription_count";
    private const string OPC_PLC_MONITORED_ITEM_COUNT_METRIC = "opc_plc_monitored_item_count";
    private const string OPC_PLC_PUBLISHED_COUNT_METRIC = "opc_plc_published_count";
    private const string OPC_PLC_PUBLISHED_COUNT_WITH_TYPE_METRIC = "opc_plc_published_count_with_type";
    private const string OPC_PLC_TOTAL_ERRORS_METRIC = "opc_plc_total_errors";

    private readonly ILogger logger = new SerilogLoggerFactory(Program.Logger).CreateLogger("Metrics");

    private readonly IDictionary<string, object> baseDimensions;

    private readonly UpDownCounter<int> podCount;
    private readonly UpDownCounter<int> sessionCount;
    private readonly UpDownCounter<int> subscriptionCount;
    private readonly UpDownCounter<int> monitoredItemCount;
    private readonly Counter<int> publishedCount;
    private readonly Counter<int> publishedCountWithType;
    private readonly Counter<int> totalErrors;

    public string MetricName { get; set; }

    public Metrics(string name, IDictionary<string, object> baseDimensions)
    {
        this.MetricName = name;
        this.baseDimensions = baseDimensions;

        var meter = new Meter(name);

        this.podCount = meter.CreateUpDownCounter<int>(OPC_PLC_POD_COUNT_METRIC, "", "Number of pods");
        this.sessionCount = meter.CreateUpDownCounter<int>(OPC_PLC_SESSION_COUNT_METRIC, "", "Number of sessions");
        this.subscriptionCount = meter.CreateUpDownCounter<int>(OPC_PLC_SUBSCRIPTION_COUNT_METRIC, "", "Number of subscriptions");
        this.monitoredItemCount = meter.CreateUpDownCounter<int>(OPC_PLC_MONITORED_ITEM_COUNT_METRIC, "", "Number of monitored items");
        this.publishedCount = meter.CreateCounter<int>(OPC_PLC_PUBLISHED_COUNT_METRIC, "", "Number of published items");
        this.publishedCountWithType = meter.CreateCounter<int>(OPC_PLC_PUBLISHED_COUNT_WITH_TYPE_METRIC, "", "Number of published items for datapoints and events");

        this.totalErrors = meter.CreateCounter<int>("opc_plc_total_errors", "", "Number of total errors of all types.");
    }

    public void AddPodCount(int delta = 1)
    {
        var dimensions = ConvertDictionaryToKeyVaultPairArray(baseDimensions);
        this.podCount.Add(delta, dimensions);
        this.logger.LogDebug("Increased metric: {MertricName} with value: {MetricValue} and dimensions: {Dimensions}", OPC_PLC_POD_COUNT_METRIC, delta, dimensions.ToJson());
    }

    public void AddSessionCount(string sessionId, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(new KeyValuePair<string, object>("session", sessionId));
        this.sessionCount.Add(delta, dimensions);
        this.logger.LogDebug("Increased metric: {MetricName} with value: {MetricValue} and dimensions: {Dimensions}", OPC_PLC_SESSION_COUNT_METRIC, delta, dimensions.ToJson());
    }

    public void AddSubscriptionCount(string sessionId, string subscriptionId, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(
                       new KeyValuePair<string, object>("session", sessionId),
                       new KeyValuePair<string, object>("subscription", subscriptionId));

        this.subscriptionCount.Add(delta, dimensions);
        this.logger.LogDebug("Increased metric: {MetricName} with value: {MetricValue} and dimensions: {Dimensions}", OPC_PLC_SUBSCRIPTION_COUNT_METRIC, delta, dimensions.ToJson());
    }

    public void AddMonitoredItemCount(string sessionId, string subscriptionId, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId));
        this.monitoredItemCount.Add(delta, dimensions);
        this.logger.LogDebug("Increased metric: {MetricName} with value: {MetricValue} and dimensions: {Dimensions}", OPC_PLC_MONITORED_ITEM_COUNT_METRIC, delta, dimensions.ToJson());
    }

    public void AddPublishedCount(string sessionId, string subscriptionId, int dataPoints, int events)
    {
        var dimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId));
        this.publishedCount.Add(1, dimensions);
        this.logger.LogDebug("Increased metric: {MetricName} with value: {MetricValue} and dimensions: {Dimensions}", OPC_PLC_PUBLISHED_COUNT_METRIC, 1, dimensions.ToJson());

        if (dataPoints > 0)
        {
            var dataPointsDimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId),
                        new KeyValuePair<string, object>("type", "data_point"));
            this.publishedCountWithType.Add(dataPoints, dataPointsDimensions);
            this.logger.LogDebug("Increased metric: {MetricName} with value: {MetricValue} and type: {Type} and dimensions: {Dimensions}", OPC_PLC_PUBLISHED_COUNT_WITH_TYPE_METRIC, dataPoints, "data_point", dataPointsDimensions.ToJson());
        }

        if (events > 0)
        {
            var eventsDimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId),
                        new KeyValuePair<string, object>("type", "event"));
            this.publishedCountWithType.Add(events, eventsDimensions);
            this.logger.LogDebug("Increased metric: {MetricName} with value: {MetricValue} and type: {Type} and dimensions: {Dimensions}", OPC_PLC_PUBLISHED_COUNT_WITH_TYPE_METRIC, events, "event", eventsDimensions.ToJson());
        }
    }

    public void RecordTotalErrors(string operation, string errorType, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(
            new KeyValuePair<string, object>("operation", operation),
            new KeyValuePair<string, object>("error_type", errorType));
        this.totalErrors.Add(delta, dimensions);
        this.logger.LogDebug("Increased TotalErrors count metric: {TotalErrors} and {Dimensions}", delta, dimensions.ToJson());
    }

    private KeyValuePair<string, object>[] MergeWithBaseDimensions(params KeyValuePair<string, object>[] items)
    {
        var newDimensions = new Dictionary<string, object>(baseDimensions);
        foreach (var item in items)
        {
            newDimensions[item.Key] = item.Value;
        }

        return ConvertDictionaryToKeyVaultPairArray(newDimensions);
    }

    private static KeyValuePair<string, object>[] ConvertDictionaryToKeyVaultPairArray(IDictionary<string, object> dictionary)
    {
        return dictionary.Select(item => new KeyValuePair<string, object>(item.Key, item.Value)).ToArray();
    }
}