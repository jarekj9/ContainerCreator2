using ContainerCreator2.Data;

namespace ContainerCreator2.Service.Abstract
{
    public interface IContainerManagerService
    {
        Task<ContainerInfo> CreateContainer(ContainerRequest containerRequest);
        Task<List<ContainerInfo>> GetContainers();
        Task<bool> DeleteAllContainerGroups();
        Task<bool> DeleteContainerGroup(string containerGroupName);
        Task<List<ContainerInfo>> GetContainersOverTimeLimit(int maxMinutes);
        bool UsersContainersLimitReachedOrExceeded(List<ContainerInfo> activeContainers, string ownerId);
        bool MaxConcurrentContainersTotalReached(List<ContainerInfo> activeContainers);
        bool UsersContainersCountEqualsLimit(List<ContainerInfo> activeContainers, string ownerId);
        ContainerInfo GetOldestContainerForUser(List<ContainerInfo> activeContainers, string ownerId);
    }
}
