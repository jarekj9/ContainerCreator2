namespace ContainerCreator2.Data
{
    public class ContainerInfo
    {
        public Guid Id { get; set; }
        public string ContainerGroupName { get; set; }
        public string Image { get; set; }
        public string Name { get; set; }
        public string Fqdn { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public Guid OwnerId { get; set; }
        public DateTime CreatedTime { get; set; }
        public string RandomPassword { get; set; }
        public bool IsDeploymentSuccesful { get; set; }
        public string ProblemMessage { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is ContainerInfo other)
            {
                return this.Id == other.Id;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return this.Id.GetHashCode();
        }
    }


}
