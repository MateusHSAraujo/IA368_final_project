using NetMQ;
using UnityEngine;

namespace VRRobot.SimWorld.Transport
{
    /// <summary>
    /// Global NetMQ lifecycle in Unity. AsyncIO.ForceDotNet.Force() before the first socket and
    /// NetMQConfig.Cleanup(false) after the last user leaves. Without the Cleanup the Editor
    /// hangs when leaving Play mode or reloading the domain.
    /// </summary>
    public static class NetMQLifecycle
    {
        static int s_users;
        static readonly object s_lock = new object();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // With "Enter Play Mode Options" and no domain reload, statics survive.
            s_users = 0;
        }

        public static void Acquire()
        {
            lock (s_lock)
            {
                if (s_users++ == 0) AsyncIO.ForceDotNet.Force();
            }
        }

        public static void Release()
        {
            lock (s_lock)
            {
                if (s_users == 0) return;
                if (--s_users == 0) NetMQConfig.Cleanup(false);
            }
        }
    }
}
