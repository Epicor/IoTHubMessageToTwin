using IoTHubTrigger = Microsoft.Azure.WebJobs.EventHubTriggerAttribute;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventHubs;
using System.Text;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Devices;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;

namespace IoTEventToTwinProperties
{
    public static class IoTHubToTwinPropertiesFunction
    {
        private static HttpClient client = new HttpClient();
        private static RegistryManager _manager;

        [FunctionName("IoTHubToTwinPropertiesFunction")]
        public static async Task Run(
            [IoTHubTrigger("messages/events", Connection = "IoTHubEventHubConnectionString", ConsumerGroup = "eventtotwinfunction")]EventData message, 
            ExecutionContext context, 
            ILogger log)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var connectionString = config["IoTHubConnectionString"];

            var deviceId = (string)message.SystemProperties["iothub-connection-device-id"]; // standard system property

            if (_manager == null)
            {
                _manager = RegistryManager.CreateFromConnectionString(connectionString);
            }

            var twin = await _manager.GetTwinAsync(deviceId);

            // merge properties
            var reported = JObject.Parse(twin.Properties.Reported.ToJson());

            var eventData = JObject.Parse(Encoding.UTF8.GetString(message.Body.ToArray()));
            eventData.Remove("timeStamp"); // this one always changes and we do not need it in the properties

            JObject updatedProps = (JObject)reported.DeepClone();

            updatedProps.Merge(eventData);

            if (!JToken.DeepEquals(updatedProps, reported))
            {
                // need to update
                twin.Properties.Reported = new TwinCollection(updatedProps.ToString());
                await _manager.UpdateTwinAsync(deviceId, twin, twin.ETag);
                log.LogInformation($"Digital twin for {deviceId} updated from {reported} to {updatedProps}");
            }
            
        }


        public static void MergeItem(this JObject original, object content)
        {
            if (!(content is JObject o))
            {
                return;
            }

            foreach (KeyValuePair<string, JToken> contentItem in o)
            {
                JProperty existingProperty = original.Property(contentItem.Key);

                if (existingProperty == null)
                {
                    original.Add(contentItem.Key, contentItem.Value);
                }
                else if (contentItem.Value != null)
                {
                    if (!(existingProperty.Value is JContainer existingContainer) || existingContainer.Type != contentItem.Value.Type)
                    {
                        existingProperty.Value = contentItem.Value;
                    }
                    else
                    {
                        existingContainer.Merge(contentItem.Value);
                    }
                }
            }
        }

        private static bool IsNull(JToken token)
        {
            if (token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token is JValue v && v.Value == null)
            {
                return true;
            }

            return false;
        }
    }
}