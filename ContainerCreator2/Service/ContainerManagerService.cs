using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;
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
        private readonly int maxConcurrentContainersPerUser;
        private readonly int maxConcurrentContainersTotal;
        private readonly List<int> containerPorts;
        public ContainerManagerService(ILogger<ContainerManagerService> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.resourceGroupName = this.configuration["ResourceGroupName"] ?? "";
            this.tenantId = this.configuration["TenantId"] ?? "";
            this.clientId = this.configuration["ClientId"] ?? "";
            this.clientSecret = this.configuration["ClientSecret"] ?? "";
            this.containerImage = this.configuration["ContainerImage"] ?? "";
            this.maxConcurrentContainersPerUser = int.TryParse(this.configuration["MaxConcurrentContainersPerUser"], out var parsed) ? parsed : 1;
            this.maxConcurrentContainersTotal = int.TryParse(this.configuration["MaxConcurrentContainersTotal"], out var parsedTotal) ? parsedTotal : 1;
            var ports = this.configuration["ContainerPorts"]?.Split(",") ?? new string[0];
            this.containerPorts = ports.Select(p => int.TryParse(p, out var parsed) ? parsed : 80).ToList();
        }

        public async Task<ContainerInfo> CreateContainer(ContainerRequest containerRequest)
        {
            var activeContainers = await GetContainers();
            if(MaxConcurrentContainersPerUserReached(activeContainers, containerRequest.OwnerId))
            {
                var oldestUsersContainer = GetOldestContainerForUser(activeContainers, containerRequest.OwnerId);
                await DeleteContainerGroup(oldestUsersContainer.ContainerGroupName);
                activeContainers = await GetContainers();
            }
            if (MaxConcurrentContainersTotalReached(activeContainers))
            {
                return new ContainerInfo() { IsDeploymentSuccesful = false, ProblemMessage = nameof(MaxConcurrentContainersTotalReached) };
            }

            containerRequest.RandomPassword = RandomPasswordGenerator.CreateRandomPassword();
            var containerGroupData = GetContainerGroupData(containerRequest);
            var containerGroupName = $"containergroup-{RandomPasswordGenerator.CreateRandomPassword(8, useSpecialChars: false)}";

            var containerGroupCollection = await GetContainerGroupsFromResourceGroup();
            var containerResourceGroupResult = await containerGroupCollection.CreateOrUpdateAsync(WaitUntil.Completed, containerGroupName, containerGroupData);

            var fqdn = containerResourceGroupResult?.Value?.Data?.IPAddress?.Fqdn ?? "";
            var ip = containerResourceGroupResult?.Value?.Data?.IPAddress?.IP.ToString() ?? "";
            var port = containerResourceGroupResult?.Value?.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0;
            var name = containerResourceGroupResult?.Value?.Data?.Containers?.FirstOrDefault()?.Name ?? "";

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            var containerInfo = new ContainerInfo()
            {
                Id = Guid.NewGuid(),
                ContainerGroupName = containerGroupName,
                Image = this.containerImage,
                Name = name,
                Fqdn = fqdn,
                Ip = ip,
                Port = port,
                OwnerId = Guid.TryParse(containerRequest.OwnerId, out var parsedId) ? parsedId : Guid.Empty,
                CreatedTime = DateTime.UtcNow,
                RandomPassword = containerRequest.RandomPassword,
                IsDeploymentSuccesful = true
            };

            return containerInfo;
        }

        private ContainerGroupData GetContainerGroupData(ContainerRequest containerRequest)
        {
            var containerResource = new ContainerResourceRequestsContent(1, 1);
            var requirements = new ContainerResourceRequirements(containerResource);
            var containerGroupPorts = this.containerPorts.Select(p => new ContainerGroupPort(p)).ToList();
            var ipAddress = new ContainerGroupIPAddress(containerGroupPorts, ContainerGroupIPAddressType.Public)
            {
                DnsNameLabel = containerRequest.DnsNameLabel,
            };

            var container = new ContainerInstanceContainer("container", this.containerImage, requirements)
            {
                EnvironmentVariables = { new ContainerEnvironmentVariable("VNCPASS") { SecureValue = containerRequest.RandomPassword } }
            };
            this.containerPorts.Select(p => new ContainerPort(p)).ToList().ForEach(p => container.Ports.Add(p));
            if (!string.IsNullOrEmpty(containerRequest.UrlToOpenEncoded))
            {
                container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("URL_TO_OPEN")
                {
                    Value = HttpUtility.UrlDecode(containerRequest.UrlToOpenEncoded)
                });
            }
            var containers = new List<ContainerInstanceContainer> { container };
            var containerGroupData = new ContainerGroupData(AzureLocation.NorthEurope, containers, ContainerInstanceOperatingSystemType.Linux)
            {
                IPAddress = ipAddress,
                Tags = { 
                    new KeyValuePair<string, string>("OwnerId", containerRequest.OwnerId), 
                    new KeyValuePair<string, string>("CreatedTime", DateTime.UtcNow.ToString()) 
                }
            };
            //TODO: test if it works for credentials setting:
            var registryCredentials = CreateRegistryCredentials();
            if(!string.IsNullOrEmpty(registryCredentials.Server))
            {
                containerGroupData.ImageRegistryCredentials.Add(registryCredentials);
            }

            return containerGroupData;
        }

        private ContainerGroupImageRegistryCredential CreateRegistryCredentials()
        {
            if (new List<string?> () { 
                configuration["RegistryCredentialsServer"], 
                configuration["RegistryCredentialsUserName"], 
                configuration["RegistryCredentialsPassword"] 
            }
            .Any(s => string.IsNullOrEmpty(s)))
            {
                new ContainerGroupImageRegistryCredential("");
            }

            var registryCredentials = new ContainerGroupImageRegistryCredential(configuration["RegistryCredentialsServer"]);
            registryCredentials.Username = configuration["RegistryCredentialsUserName"];
            registryCredentials.Password = configuration["RegistryCredentialsPassword"];
            return registryCredentials;
        }

        public async Task<List<ContainerInfo>> GetContainers()
        {
            var containerGroupCollection = await GetContainerGroupsFromResourceGroup();

            var containerInfos = new List<ContainerInfo>();
            foreach (var containerGroup in containerGroupCollection)
            {
                var ownerIdfromTags = (containerGroup.Data?.Tags?.TryGetValue("OwnerId", out var ownerId) ?? false) ? 
                    (Guid.TryParse(ownerId, out var parsed) ? parsed : Guid.Empty) : Guid.Empty;

                var createdTimefromTags = (containerGroup.Data?.Tags?.TryGetValue("CreatedTime", out var createdTime) ?? false) ?
                    (DateTime.TryParse(createdTime, out var parsedTime) ? parsedTime : DateTime.MinValue) : DateTime.MinValue;

                containerInfos.Add(new ContainerInfo()
                {
                    ContainerGroupName = containerGroup?.Data?.Name ?? "",
                    Image = containerGroup.Data?.Containers?.FirstOrDefault()?.Image ?? "",
                    Name = containerGroup.Data?.Containers?.FirstOrDefault()?.Name ?? "",
                    Fqdn = containerGroup.Data?.IPAddress?.Fqdn ?? "",
                    Ip = containerGroup.Data?.IPAddress?.IP?.ToString() ?? "",
                    Port = containerGroup.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0,
                    OwnerId = ownerIdfromTags,
                    CreatedTime = createdTimefromTags
                });
            }
            return containerInfos;
        }

        public async Task<bool> DeleteAllContainerGroups()
        {
            var containerGroupCollection = await GetContainerGroupsFromResourceGroup();
            bool hasCompleted = true;
            foreach (var containerGroup in containerGroupCollection)
            {
                var containerGroupName = containerGroup?.Data?.Name;
                if (containerGroupName == null)
                {
                    continue;
                }
                var result = await containerGroup.DeleteAsync(WaitUntil.Completed);
                hasCompleted = hasCompleted && result.HasCompleted;
            }
            return hasCompleted;
        }

        public async Task<bool> DeleteContainerGroup(string containerGroupName)
        {
            var containerGroup = await GetContainerByName(containerGroupName);
            var result = await containerGroup.DeleteAsync(WaitUntil.Completed);
            return result.HasCompleted;
        }

        private async Task<ContainerGroupCollection> GetContainerGroupsFromResourceGroup()
        {
            var resourceGroup = await GetResourceGroup();
            var containerGroupCollection = resourceGroup.GetContainerGroups();
            return containerGroupCollection;
        }

        private async Task<ContainerGroupResource> GetContainerByName(string containerGroupName)
        {
            var resourceGroup = await GetResourceGroup();
            var containerGroup = resourceGroup.GetContainerGroup(containerGroupName);
            return containerGroup;
        }

        public async Task<bool> DeleteContainersOverTimeLimit(int maxMinutes)
        {
            bool isSuccessful = true;
            var activeContainers = await GetContainers();
            var containersOverTimeLimit = activeContainers.Where(c => c.CreatedTime.AddMinutes(maxMinutes + 3) < DateTime.UtcNow).ToList();
            foreach(var conainer in containersOverTimeLimit)
            {
                isSuccessful = isSuccessful && await DeleteContainerGroup(conainer.ContainerGroupName);
            }
            return isSuccessful;
        }

        private ContainerInfo GetOldestContainerForUser(List<ContainerInfo> activeContainers, string ownerId)
        {
            var oldestContainer = activeContainers.Where(c => c.OwnerId.ToString() == ownerId).OrderBy(c => c.CreatedTime).FirstOrDefault();
            if(oldestContainer != null)
            {
                return oldestContainer;
            }
            return new ContainerInfo();
        }

        private bool MaxConcurrentContainersPerUserReached(List<ContainerInfo> activeContainers, string ownerId)
        {
            var usersActiveContainers = activeContainers.Where(c => c.OwnerId.ToString() == ownerId).Count();
            if(usersActiveContainers >= this.maxConcurrentContainersPerUser)
            {
                return true;
            }
            return false;
        }

        private bool MaxConcurrentContainersTotalReached(List<ContainerInfo> activeContainers)
        {
            if (activeContainers.Count() >= this.maxConcurrentContainersTotal)
            {
                return true;
            }
            return false;
        }

        private async Task<ResourceGroupResource> GetResourceGroup()
        {
            var clientSecretCredential = new ClientSecretCredential(this.tenantId, this.clientId, this.clientSecret);
            ArmClient armClient = !string.IsNullOrEmpty(this.clientSecret) ? new ArmClient(clientSecretCredential) : new ArmClient(new ManagedIdentityCredential());
            var subscription = await armClient.GetSubscriptions().GetAsync(configuration["SubscriptionId"]).ConfigureAwait(false);
            var resourceGroupCollection = subscription.Value.GetResourceGroups();
            var resourceGroup = await resourceGroupCollection.GetAsync(this.resourceGroupName).ConfigureAwait(false);
            return resourceGroup;
        }

    }
}
