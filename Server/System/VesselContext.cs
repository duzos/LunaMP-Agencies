using System;
using System.Collections.Concurrent;

namespace Server.System
{
    public static class VesselContext
    {
        public static ConcurrentDictionary<Guid, byte> RemovedVessels { get; } = new ConcurrentDictionary<Guid, byte>();
    }
}
