using IoTHubTrigger = Microsoft.Azure.WebJobs.EventHubTriggerAttribute;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.EventHubs;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Devices;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using Microsoft.Azure.Devices.Client;
using IotHubConnectionStringBuilder = Microsoft.Azure.Devices.IotHubConnectionStringBuilder;

namespace IoTEventToTwinProperties
{
    public static class IoTHubToTwinPropertiesFunction
    {
        private static RegistryManager _manager;

        [FunctionName("IoTHubToTwinPropertiesFunction")]
        public static async Task RunAsync(
            [IoTHubTrigger("messages/events", Connection = "IoTHubEventHubConnectionString", ConsumerGroup = "eventtotwinfunction")]EventData message,
            Microsoft.Azure.WebJobs.ExecutionContext context,
            CancellationToken token,
            ILogger log)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            
            if (!message.Properties.ContainsKey("UpdateTwin"))
            {
                return;
            }

            var connectionString = config["IoTHubConnectionString"];

            var deviceId = (string)message.SystemProperties["iothub-connection-device-id"]; // standard system property

            if (_manager == null)
            {
                _manager = RegistryManager.CreateFromConnectionString(connectionString);
            }

            var twin = await _manager.GetTwinAsync(deviceId, token);

            // merge properties
            twin.Properties.Reported.ClearMetadata();

            var reported = CleanupJTokenForTwin(JObject.Parse(twin.Properties.Reported.ToJson()));

            var eventData = JObject.Parse(Encoding.UTF8.GetString(message.Body.ToArray()));
            eventData.Remove("timeStamp"); // this one always changes and we do not need it in the properties

            // remove arrays, since they are not supported in reported properties.
            // Cleanup function above should have dealt with them, but do a second pass just in case
            var arrays = eventData.Values().OfType<JArray>().Select(item => item.Path).ToList();
            foreach (var arrayPath in arrays)
            {
                eventData.Remove(arrayPath);
            }

            JObject updatedProps = (JObject)reported.DeepClone();

            updatedProps.Merge(eventData);

            if (!JToken.DeepEquals(updatedProps, reported))
            {
                // need to update
                try
                {
                    var device = await _manager.GetDeviceAsync(deviceId, token);
                    var builder = IotHubConnectionStringBuilder.Create(connectionString);

                    var deviceClient = DeviceClient.Create(builder.HostName,
                        new DeviceAuthenticationWithRegistrySymmetricKey(deviceId,
                            device.Authentication.SymmetricKey.PrimaryKey));

                    await deviceClient.UpdateReportedPropertiesAsync(new TwinCollection(updatedProps.ToString()), token);
                    // call below can only be used for desired properties! So we cannot use it even though it is tempting to do so!
                    //await _manager.UpdateTwinAsync(deviceId, twin, twin.ETag, token);
                    log.LogInformation($"Digital twin for {deviceId} updated from {reported} to {updatedProps}");
                }
                catch (Exception e)
                {
                    log.LogError($"Error updating reported properties of digital twin of {deviceId} to {updatedProps}: {e}");
                    throw;
                }
                
            }
            
        }



        static JToken CleanupJTokenForTwin(JToken token)
        {
            JToken result = token;
            switch (token)
            {
                case JObject jo:
                    var props = jo.Properties().ToArray();
                    foreach (var child in props)
                    {
                        var newName = FixReportedPropertyName(child.Name);
                        jo[newName] = CleanupJTokenForTwin(child.Value);
                        if (newName != child.Name)
                        {
                            jo.Remove(child.Name);
                        }
                    }
                    result = jo;
                    break;
                case JArray ja:
                    result = new JObject();
                    for (int i = 0; i < ja.Count; i++)
                    {
                        result[(i + 1).ToString("D")] = CleanupJTokenForTwin(ja[i]);
                    }
                    break;
                case JValue jv:
                    result = jv;
                    break;

            }
            return result;
        }

        static string FixReportedPropertyName(string name)
        {
            return name.Replace('.', '_').Replace('$', '_').Replace('#', '_').Replace(' ', '_');
        }
    }
}