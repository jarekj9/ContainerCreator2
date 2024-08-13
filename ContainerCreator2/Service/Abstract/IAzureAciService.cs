using Azure;
using Azure.ResourceManager.ContainerInstance;
using ContainerCreator2.Data;
using Azure.ResourceManager.Resources;

namespace ContainerCreator2.Service.Abstract
{
    public interface IAzureAciService
    {
        Task<ContainerInfo> CreateOrUpdateAsync(string containerGroupName, ContainerGroupData containerGroupData);
        Task<ContainerGroupCollection> GetContainerGroupsFromResourceGroup();
        Task<ResourceGroupResource> GetResourceGroup();
        Task<bool> DeleteAsync(string containerGroupName);
        Task<ContainerGroupResource> GetContainerByName(string containerGroupName);
    }
}
