using System;

namespace DeezSpoTag.Web.Services
{
    public sealed class AppInstanceIdProvider
    {
        public AppInstanceIdProvider()
        {
            InstanceId = Guid.NewGuid().ToString("N");
        }

        public string InstanceId { get; }
    }
}
