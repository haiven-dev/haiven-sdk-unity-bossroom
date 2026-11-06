using System.IO;
using UnityEngine;

namespace Haiven.Sdk
{
    /// <summary>
    /// Where a Unity player keeps reward claim intents. The file is claim ids,
    /// not a credential, under <see cref="Application.persistentDataPath"/>.
    /// </summary>
    public static class UnityRewardClaims
    {
        public static FileRewardClaimIntentStore PersistentStore()
        {
            return new FileRewardClaimIntentStore(Path.Combine(Application.persistentDataPath, "haiven-reward-claims.json"));
        }
    }
}
