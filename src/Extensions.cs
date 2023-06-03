using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace OpcPlc
{
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
    }
}
