using Azure.Core;
using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using System.Web;
using Microsoft.Extensions.Configuration;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.ContainerInstance;
using ContainerCreator2.Data;
using System.Text.Json;
using Azure.ResourceManager;
using Azure.Identity;

namespace ContainerCreator2
{

    public class ContainerCreator
    {
        private readonly ILogger<ContainerCreator> logger;
        private readonly IConfiguration configuration;
        private readonly string resourceGroupName;
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string containerImage;

        public ContainerCreator(ILogger<ContainerCreator> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.resourceGroupName = this.configuration["ResourceGroupName"] ?? "";
            this.tenantId = this.configuration["TenantId"] ?? "";
            this.clientId = this.configuration["ClientId"] ?? "";
            this.clientSecret = this.configuration["ClientSecret"] ?? "";
            this.containerImage = this.configuration["ContainerImage"] ?? "";
        }

        [Function("CreateContainer")]
        public async Task<IActionResult> CreateContainer([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client,
            [FromQuery] string dnsNameLabel, [FromQuery] string urlToOpenEncoded, [FromQuery] string ownerId = "00000000-0000-0000-0000-000000000000")
        {

            var containerGroupName = $"containergroup-{RandomPasswordGenerator.CreateRandomPassword(7, useSpecialChars: false)}";
            var randomPassword = RandomPasswordGenerator.CreateRandomPassword();
            var containerGroupCollection = await GetContainerGroupsFromResourceGroup(this.resourceGroupName);
            var containerResource = new ContainerResourceRequestsContent(1, 1);
            var requirements = new ContainerResourceRequirements(containerResource);
            var ipAddress = new ContainerGroupIPAddress(new List<ContainerGroupPort>() { new ContainerGroupPort(8080) }, ContainerGroupIPAddressType.Public)
            {
                DnsNameLabel = dnsNameLabel
            };
            var container = new ContainerInstanceContainer("container", this.containerImage, requirements)
            {
                Ports = { new ContainerPort(8080) },
                EnvironmentVariables = { new ContainerEnvironmentVariable("VNCPASS") { SecureValue = randomPassword } }
            };
            if (!string.IsNullOrEmpty(urlToOpenEncoded))
            {
                container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("URL_TO_OPEN") { Value = HttpUtility.UrlDecode(urlToOpenEncoded) });
            }
            var containers = new List<ContainerInstanceContainer> { container };
            var data = new ContainerGroupData(AzureLocation.NorthEurope, containers, ContainerInstanceOperatingSystemType.Linux)
            {
                IPAddress = ipAddress
                // SubnetIds = { new ContainerGroupSubnetId(new ResourceIdentifier("subscriptions/<subscriptionId >/resourceGroups/sdktestrg/providers/Microsoft.Network/virtualNetworks/sdktestnetwork/subnets/containersub")) }
            };
            var containerResourceGroupResult = await containerGroupCollection.CreateOrUpdateAsync(WaitUntil.Completed, containerGroupName, data);

            var fqdn = containerResourceGroupResult?.Value?.Data?.IPAddress?.Fqdn ?? "";
            var ip = containerResourceGroupResult?.Value?.Data?.IPAddress?.IP.ToString() ?? "";
            var port = containerResourceGroupResult?.Value?.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0;
            var name = containerResourceGroupResult?.Value?.Data?.Containers?.FirstOrDefault()?.Name ?? "";

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Add", new ContainerInfo()
            {
                ContainerGroupName = containerGroupName,
                Image = this.containerImage,
                Name = name,
                Fqdn = fqdn,
                Ip = ip,
                Port = port,
                OwnerId = Guid.TryParse(ownerId, out var parsedId) ? parsedId : Guid.Empty
            });

            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult(@$"Container created: {containerResourceGroupResult.HasCompleted},
                container group name: {containerGroupName}, url: {fqdn}:{port}, ip: {ip}, password: {randomPassword}");
        }

        [Function("ShowContainers")]
        public async Task<IActionResult> ShowContainers([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client)
        {

            var containerGroupCollection = await GetContainerGroupsFromResourceGroup(this.resourceGroupName);

            var containerInfos = new List<ContainerInfo>();
            foreach (var containerGroup in containerGroupCollection)
            {
                containerInfos.Add(new ContainerInfo()
                {
                    ContainerGroupName = containerGroup?.Data?.Name ?? "",
                    Image = containerGroup.Data?.Containers?.FirstOrDefault()?.Image ?? "",
                    Name = containerGroup.Data?.Containers?.FirstOrDefault()?.Name ?? "",
                    Fqdn = containerGroup.Data?.IPAddress?.Fqdn ?? "",
                    Ip = containerGroup.Data?.IPAddress?.IP?.ToString() ?? "",
                    Port = containerGroup.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0
                });
            }
            var containersReadFromAzure = JsonSerializer.Serialize(containerInfos);

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            EntityMetadata<ContainersDurableEntity>? entity = await client.Entities.GetEntityAsync<ContainersDurableEntity>(entityId);
            var containersReadFromDurableEntity = JsonSerializer.Serialize(entity?.State?.Get());

            logger.LogInformation($"C# HTTP trigger function processed a request. ");
            return new OkObjectResult($"{containersReadFromAzure} --------- {containersReadFromDurableEntity}");
        }

        [Function("DeleteContainers")]
        public async Task<IActionResult> DeleteContainers([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client)
        {

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Reset");

            var containerGroupCollection = await GetContainerGroupsFromResourceGroup(this.resourceGroupName);
            bool completed = true;
            foreach (var containerGroup in containerGroupCollection)
            {
                var containerGroupName = containerGroup?.Data?.Name;
                var result = await containerGroup.DeleteAsync(WaitUntil.Completed);
                completed = completed && result.HasCompleted;
            }

            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult($"Deleted containers: {completed}");
        }

        private async Task<ContainerGroupCollection> GetContainerGroupsFromResourceGroup(string resourceGroupName)
        {
            var clientCredential = new ClientSecretCredential(this.tenantId, this.clientId, this.clientSecret);
            ArmClient armClient = new ArmClient(clientCredential);
            var subscription = await armClient.GetSubscriptions().GetAsync(configuration["SubscriptionId"]).ConfigureAwait(false);
            var resourceGroupCollection = subscription.Value.GetResourceGroups();
            var resourceGroup = await resourceGroupCollection.GetAsync(resourceGroupName).ConfigureAwait(false);
            var containerGroupCollection = resourceGroup.Value.GetContainerGroups();

            return containerGroupCollection;
        }


        // For orchestration:

        //[Function("Orchestrate")]
        //public async Task<List<string>> Run(
        //    [OrchestrationTrigger] TaskOrchestrationContext context)
        //{
        //    var outputs = new List<string>();
        //    outputs.Add(await context.CallActivityAsync<string>("Act", "Tokyo"));
        //    return outputs;
        //}

        //[Function(nameof(Act))]
        //public async Task<string> Act([ActivityTrigger] string name, [DurableClient] DurableTaskClient client)
        //{
        //    logger.LogInformation("Saying hello to {name}.", name);
        //    return $"Hello {name}!";
        //}

        //[Function("Function1_HttpStart")]
        //public async Task<IActionResult> HttpStart(
        //    [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
        //    [DurableClient] DurableTaskClient client)
        //{
        //    string instanceId = await client.ScheduleNewOrchestrationInstanceAsync("Orchestrate", null);
        //    logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
        //    return new OkObjectResult("ok");
        //}

    }


    public class ContainersDurableEntity
    {
        public List<ContainerInfo> Containers { get; set; } = new List<ContainerInfo>();

        public void Add(ContainerInfo newContainer) => this.Containers.Add(newContainer);

        public void Reset() => this.Containers.Clear();

        public List<ContainerInfo> Get() => this.Containers;

        [Function(nameof(ContainersDurableEntity))]
        public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher)
        {
            return dispatcher.DispatchAsync<ContainersDurableEntity>();
        }
    }

}
