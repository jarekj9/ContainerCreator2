using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using ContainerCreator2.Data;
using ContainerCreator2.Service.Abstract;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Web;

namespace ContainerCreator2.Service
{
    public class ContainerManagerService : IContainerManagerService
    {
        private readonly ILogger<ContainerManagerService> logger;
        private readonly IConfiguration configuration;
        private readonly string resourceGroupName;
        private readonly string tenantId;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly string containerImage;
        public ContainerManagerService(ILogger<ContainerManagerService> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.resourceGroupName = this.configuration["ResourceGroupName"] ?? "";
            this.tenantId = this.configuration["TenantId"] ?? "";
            this.clientId = this.configuration["ClientId"] ?? "";
            this.clientSecret = this.configuration["ClientSecret"] ?? "";
            this.containerImage = this.configuration["ContainerImage"] ?? "";
        }

        public async Task<ContainerInfo> CreateContainer(string ownerId, string dnsNameLabel, string urlToOpenEncoded)
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
            };
            var containerResourceGroupResult = await containerGroupCollection.CreateOrUpdateAsync(WaitUntil.Completed, containerGroupName, data);

            var fqdn = containerResourceGroupResult?.Value?.Data?.IPAddress?.Fqdn ?? "";
            var ip = containerResourceGroupResult?.Value?.Data?.IPAddress?.IP.ToString() ?? "";
            var port = containerResourceGroupResult?.Value?.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0;
            var name = containerResourceGroupResult?.Value?.Data?.Containers?.FirstOrDefault()?.Name ?? "";

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            var containerInfo = new ContainerInfo()
            {
                ContainerGroupName = containerGroupName,
                Image = this.containerImage,
                Name = name,
                Fqdn = fqdn,
                Ip = ip,
                Port = port,
                OwnerId = Guid.TryParse(ownerId, out var parsedId) ? parsedId : Guid.Empty,
                RandomPassword = randomPassword
            };

            return containerInfo;
        }

        public async Task<List<ContainerInfo>> ShowContainers()
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
            return containerInfos;
        }

        public async Task<bool> DeleteContainers()
        {
            var containerGroupCollection = await GetContainerGroupsFromResourceGroup(this.resourceGroupName);
            bool hasCompleted = true;
            foreach (var containerGroup in containerGroupCollection)
            {
                var containerGroupName = containerGroup?.Data?.Name;
                var result = await containerGroup.DeleteAsync(WaitUntil.Completed);
                hasCompleted = hasCompleted && result.HasCompleted;
            }
            return hasCompleted;
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

    }
}
