using ContainerCreator2.Data;

namespace ContainerCreator2.Service.Abstract
{
    public interface IContainerManagerService
    {
        Task<ContainerInfo> CreateContainer(ContainerRequest containerRequest);
        Task<List<ContainerInfo>> ShowContainers();
        Task<bool> DeleteAllContainerGroups();
        Task<bool> DeleteContainerGroup(string containerGroupName);
    }
}
