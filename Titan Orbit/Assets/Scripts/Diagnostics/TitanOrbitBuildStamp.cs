using UnityEngine;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Bake-time id written by Linux dedicated-server builds into
    /// <c>Resources/TitanOrbitBuildStamp.txt</c>. Join Game compares the local stamp
    /// to the UGS lobby <c>ServerBuild</c> key so a failed GCE deploy is obvious
    /// (old binaries do not publish the key).
    /// </summary>
    public static class TitanOrbitBuildStamp
    {
        public const string ResourceName = "TitanOrbitBuildStamp";

        static string s_CachedId;

        /// <summary>Baked stamp, or <c>unspecified</c> when no Linux server build has written one.</summary>
        public static string Id
        {
            get
            {
                if (s_CachedId != null)
                    return s_CachedId;

                var asset = Resources.Load<TextAsset>(ResourceName);
                string raw = asset != null ? asset.text : null;
                if (string.IsNullOrWhiteSpace(raw))
                    s_CachedId = "unspecified";
                else
                    s_CachedId = raw.Trim();
                return s_CachedId;
            }
        }

        /// <summary>Join Game / log label for this process (Editor vs baked player/server).</summary>
        public static string LocalLabel()
        {
#if UNITY_EDITOR && !UNITY_SERVER
            return "editor last Linux bake " + FormatFriendly(Id);
#elif UNITY_SERVER
            return FormatFriendly(Id);
#else
            return "client " + FormatFriendly(Id);
#endif
        }

        /// <summary>
        /// Bake clock vs git suffix. The trailing hex is <c>git rev-parse --short HEAD</c> —
        /// it does not change until you commit. The date-time prefix is what a new Linux bake writes.
        /// </summary>
        public static string FormatFriendly(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "unspecified")
                return raw ?? "unspecified";

            Split(raw.Trim(), out string when, out string git);
            if (string.IsNullOrEmpty(when))
                return raw.Trim();
            if (string.IsNullOrEmpty(git))
                return when;
            return when + "  (git " + git + " — same until you commit)";
        }

        /// <summary>True when two lobby/client stamps are the same bake (ignore leftover @Hz).</summary>
        public static bool SameBake(string a, string b)
        {
            return NormalizeBakeId(a) == NormalizeBakeId(b) &&
                   !string.IsNullOrEmpty(NormalizeBakeId(a));
        }

        public static string NormalizeBakeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;
            raw = raw.Trim();
            int at = raw.LastIndexOf('@');
            if (at > 0)
                raw = raw.Substring(0, at);
            return raw;
        }

        public static void Split(string raw, out string whenUtc, out string git)
        {
            whenUtc = null;
            git = null;
            if (string.IsNullOrWhiteSpace(raw))
                return;

            raw = NormalizeBakeId(raw);
            int lastDash = raw.LastIndexOf('-');
            if (lastDash > 0 && lastDash < raw.Length - 1)
            {
                string tail = raw.Substring(lastDash + 1);
                if (LooksLikeGitShort(tail))
                {
                    git = tail;
                    raw = raw.Substring(0, lastDash);
                }
            }

            whenUtc = FormatWhen(raw);
        }

        static bool LooksLikeGitShort(string s)
        {
            if (s.Length < 7 || s.Length > 16)
                return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }

            return true;
        }

        static string FormatWhen(string compact)
        {
            // yyyyMMdd-HHmm or yyyyMMdd-HHmmss
            if (compact.Length >= 13 && compact[8] == '-')
            {
                string day = compact.Substring(0, 4) + "-" + compact.Substring(4, 2) + "-" + compact.Substring(6, 2);
                string tod = compact.Substring(9);
                if (tod.Length == 4)
                    tod = tod.Substring(0, 2) + ":" + tod.Substring(2, 2);
                else if (tod.Length == 6)
                    tod = tod.Substring(0, 2) + ":" + tod.Substring(2, 2) + ":" + tod.Substring(4, 2);
                return day + " " + tod + " UTC";
            }

            return compact;
        }
    }
}
