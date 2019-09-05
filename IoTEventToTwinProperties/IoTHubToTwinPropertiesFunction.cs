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
            var reported = JObject.Parse(twin.Properties.Reported.ToJson());

            var eventData = JObject.Parse(Encoding.UTF8.GetString(message.Body.ToArray()));
            eventData.Remove("timeStamp"); // this one always changes and we do not need it in the properties

            // remove arrays, since they are not supported in reported properties
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



        /// <summary>
        /// Gets the parameters from an IoT Hub Connection String (decrypted)
        /// </summary>
        /// <param name="connectionString"></param>
        /// <returns></returns>
        public static Dictionary<string, string> GetIoTHubConnectionParameters(string connectionString)
        {
            var parameters = new Dictionary<string, string>();
            try
            {
                parameters = connectionString.Split(';')
                    .ToDictionary(p => p.Split('=')[0],
                        p => p.Split('=')[1],
                        StringComparer.InvariantCultureIgnoreCase);
            }
            catch
            {
                throw new ArgumentException("Connection string is malformed.");
            }
            return parameters;
        }

        public static string GetHubName(string connectionString)
        {
            var parameters = GetIoTHubConnectionParameters(connectionString);
            if (parameters.ContainsKey("HostName") == false)
            {
                throw new Exception("Hostname not found in connection string.");
            }
            var hostName = parameters["HostName"];
            return hostName.Split('.')[0];
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