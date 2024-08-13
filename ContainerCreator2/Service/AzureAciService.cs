using Azure.ResourceManager.Resources;
using ContainerCreator2.Service.Abstract;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Azure.ResourceManager.ContainerInstance;
using Azure;
using ContainerCreator2.Data;

namespace ContainerCreator2.Service;

public class AzureAciService: IAzureAciService
{
    private readonly ILogger<ContainerManagerService> logger;
    private readonly IArmClientFactory armClientFactory;
    private readonly IConfiguration configuration;
    private readonly string resourceGroupName;
    private readonly string tenantId;
    private readonly string clientId;
    private readonly string clientSecret;

    public AzureAciService(ILogger<ContainerManagerService> logger, IConfiguration configuration, IArmClientFactory armClientFactory)
    {
            this.logger = logger;
            this.armClientFactory = armClientFactory;
            this.configuration = configuration;
            this.resourceGroupName = this.configuration["ResourceGroupName"] ?? "";
            this.tenantId = this.configuration["TenantId"] ?? "";
            this.clientId = this.configuration["ClientId"] ?? "";
            this.clientSecret = this.configuration["ClientSecret"] ?? "";      
    }

    public async Task<ResourceGroupResource> GetResourceGroup()
    {
        var armClient = !string.IsNullOrEmpty(this.clientSecret) ? armClientFactory.GetArmClient(this.tenantId, this.clientId, this.clientSecret) : armClientFactory.GetArmClient();
        var subscription = await armClient.GetSubscriptions().GetAsync(configuration["SubscriptionId"]).ConfigureAwait(false);
        var resourceGroupCollection = subscription.Value.GetResourceGroups();
        var resourceGroup = await resourceGroupCollection.GetAsync(this.resourceGroupName).ConfigureAwait(false);
        return resourceGroup;
    }

    public async Task<ContainerGroupCollection> GetContainerGroupsFromResourceGroup()
    {
        var resourceGroup = await GetResourceGroup();
        var containerGroupCollection = resourceGroup.GetContainerGroups();
        return containerGroupCollection;
    }

    public async Task<ContainerGroupResource> GetContainerByName(string containerGroupName)
    {
        var resourceGroup = await GetResourceGroup();
        var containerGroup = resourceGroup.GetContainerGroup(containerGroupName);
        return containerGroup;
    }

    public async Task<ContainerInfo> CreateOrUpdateAsync(string containerGroupName, ContainerGroupData containerGroupData)
    {
        var containerGroupCollection = await GetContainerGroupsFromResourceGroup();
        var containerResourceGroupResult = await containerGroupCollection.CreateOrUpdateAsync(WaitUntil.Completed, containerGroupName, containerGroupData);

        var fqdn = containerResourceGroupResult?.Value?.Data?.IPAddress?.Fqdn ?? "";
        var ip = containerResourceGroupResult?.Value?.Data?.IPAddress?.IP.ToString() ?? "";
        var port = containerResourceGroupResult?.Value?.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0;
        var name = containerResourceGroupResult?.Value?.Data?.Containers?.FirstOrDefault()?.Name ?? "";

        var containerInfo = new ContainerInfo()
        {
            Id = Guid.NewGuid(),
            ContainerGroupName = containerGroupName,
            Name = name,
            Fqdn = fqdn,
            Ip = ip,
            Port = port,
            CreatedTime = DateTime.UtcNow,
            IsDeploymentSuccesful = true
        };

        return containerInfo;
    }

    public async Task<bool> DeleteAsync(string containerGroupName)
    {
        var containerGroup = await GetContainerByName(containerGroupName);
        var result = await containerGroup.DeleteAsync(WaitUntil.Completed);
        return result.HasCompleted;
    }
}
