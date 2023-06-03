namespace OpcPlc;

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

public class Metrics
{
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

        this.podCount = meter.CreateUpDownCounter<int>("opc_plc_pod_count", "", "Number of pods");
        this.sessionCount = meter.CreateUpDownCounter<int>("opc_plc_session_count", "", "Number of sessions");
        this.subscriptionCount = meter.CreateUpDownCounter<int>("opc_plc_subscription_count", "", "Number of subscriptions");
        this.monitoredItemCount = meter.CreateUpDownCounter<int>("opc_plc_monitored_item_count", "", "Number of monitored items");
        this.publishedCount = meter.CreateCounter<int>("opc_plc_published_count", "", "Number of published items");
        this.publishedCountWithType = meter.CreateCounter<int>("opc_plc_published_count_with_type", "", "Number of published items for datapoints and events");

        this.totalErrors = meter.CreateCounter<int>("opc_plc_total_errors", "", "Number of total errors of all types.");
    }

    public void AddPodCount(int delta = 1)
    {
        var dimensions = ConvertDictionaryToKeyVaultPairArray(baseDimensions);
        this.podCount.Add(delta, dimensions);
        // Logger.Information("Increased Pod count metric: {PodCount} and {Dimensions}", delta, dimensions.ToJson());
    }

    public void AddSessionCount(string sessionId, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(new KeyValuePair<string, object>("session", sessionId));
        this.sessionCount.Add(delta, dimensions);
        // Logger.Information("Increased Session count metric: {SessionCount} and {Dimensions}", delta, dimensions.ToJson());
    }

    public void AddSubscriptionCount(string sessionId, string subscriptionId, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(
                       new KeyValuePair<string, object>("session", sessionId),
                       new KeyValuePair<string, object>("subscription", subscriptionId));

        this.subscriptionCount.Add(delta, dimensions);
        // Logger.Information("Increased Subscription count metric: {SubscriptionCount} and {Dimensions}", delta, dimensions.ToJson());
    }

    public void AddMonitoredItemCount(string sessionId, string subscriptionId, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId));
        this.monitoredItemCount.Add(delta, dimensions);
        // Logger.Information("Increased MonitoredItem count metric: {MonitoredItemCount} and {Dimensions}", delta, dimensions.ToJson());
    }

    public void AddPublishedCount(string sessionId, string subscriptionId, int dataPoints, int events)
    {
        var dimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId));
        this.publishedCount.Add(1, dimensions);
        // Logger.Information("Increased Published count metric: {PublishedCount} and {Dimensions}", 1, dimensions.ToJson());

        if (dataPoints > 0)
        {
            var dataPointsDimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId),
                        new KeyValuePair<string, object>("type", "data_point"));
            this.publishedCountWithType.Add(dataPoints, dataPointsDimensions);
            // Logger.Information("Increased Published count with type metric: {PublishedCount} and {Type} and {Dimensions}", dataPoints, "data_point", dataPointsDimensions.ToJson());
        }

        if (events > 0)
        {
            var eventsDimensions = MergeWithBaseDimensions(
                        new KeyValuePair<string, object>("session", sessionId),
                        new KeyValuePair<string, object>("subscription", subscriptionId),
                        new KeyValuePair<string, object>("type", "event"));
            this.publishedCountWithType.Add(events, eventsDimensions);
            // Logger.Information("Increased Published count with type metric: {PublishedCount} and {Type} and {Dimensions}", dataPoints, "event", eventsDimensions.ToJson());
        }
    }

    public void RecordTotalErrors(string errorType, int delta = 1)
    {
        var dimensions = MergeWithBaseDimensions(new KeyValuePair<string, object>("error_type", errorType));
        this.totalErrors.Add(delta, dimensions);
        // Logger.Information("Increased TotalErrors count metric: {TotalErrors} and {Dimensions}", delta, dimensions.ToJson());
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