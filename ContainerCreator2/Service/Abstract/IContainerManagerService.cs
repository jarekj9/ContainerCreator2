using ContainerCreator2.Data;

namespace ContainerCreator2.Service.Abstract
{
    public interface IContainerManagerService
    {
        Task<ContainerInfo> CreateContainer(string ownerId, string dnsNameLabel, string urlToOpenEncoded);
        Task<List<ContainerInfo>> ShowContainers();
        Task<bool> DeleteContainers();
    }
}
