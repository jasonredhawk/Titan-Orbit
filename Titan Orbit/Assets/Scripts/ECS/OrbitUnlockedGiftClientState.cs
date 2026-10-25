using TitanOrbit;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// What the local client knows about Orbit Unlocked gifts. The server fills
    /// this through RPCs (and, on Local Host, by writing it directly). The gift
    /// panel reads it. Not a ghost and not sent to other players.
    /// Lives in the simulation assembly so the client RPC system can write it
    /// without reaching account or UI code. The panel performs the Cloud Save read.
    /// </summary>
    public static class OrbitUnlockedGiftClientState
    {
        /// <summary>One person the admin can see in this match.</summary>
        public struct Row
        {
            public int NetworkId;
            public string Name;
            public bool SignedIn;

            /// <summary>255 means no Orbit Unlocked result yet. Otherwise a server status byte.</summary>
            public byte Status;

            /// <summary>255 means no beta-tester result yet. Otherwise a server status byte.</summary>
            public byte BetaStatus;
        }

        const int MaxRows = 32;
        const byte StatusNone = 255;

        static readonly Row[] Rows = new Row[MaxRows];

        /// <summary>True after the server says this account may gift.</summary>
        public static bool IsAdmin { get; private set; }

        /// <summary>Bumps whenever the list or a row status changes. The panel rebuilds then.</summary>
        public static int Version { get; private set; }

        public static int RowCount { get; private set; }

        public static byte ActiveRequestId { get; private set; }

        /// <summary>Short line after the last gift click. Stays up if the list is refreshed.</summary>
        public static string LastNote { get; private set; }

        /// <summary>Short line for the player who was gifted. Empty when there is nothing to show.</summary>
        public static string Toast { get; private set; }

        static float _toastUntil;
        static bool _needsCloudRefresh;
        static bool _betaGifted;

        /// <summary>[UNITY] Domain Reload off: the last match's admin flag must not survive.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            ResetSession();
        }

        /// <summary>Clears the button, the list, and the toast. Called when the match ends.</summary>
        public static void ResetSession()
        {
            IsAdmin = false;
            RowCount = 0;
            Version++;
            ActiveRequestId = 0;
            Toast = null;
            LastNote = null;
            _toastUntil = 0f;
            _needsCloudRefresh = false;
            _betaGifted = false;
            BetaTesterClientFlags.ClearVerified();
        }

        /// <summary>The server accepted this connection as an admin.</summary>
        public static void GrantAdmin()
        {
            if (IsAdmin)
                return;
            IsAdmin = true;
            Version++;
        }

        /// <summary>Next roster request id. Clears rows so a refresh does not keep stale names.</summary>
        public static byte BeginRosterRequest()
        {
            ActiveRequestId++;
            if (ActiveRequestId == 0)
                ActiveRequestId = 1;
            RowCount = 0;
            Version++;
            return ActiveRequestId;
        }

        /// <summary>Stores one roster row when it belongs to the latest request.</summary>
        public static void ApplyEntry(GiftRosterEntryRpc rpc)
        {
            if (rpc.RequestId != ActiveRequestId)
                return;

            if (rpc.Count == 0)
            {
                RowCount = 0;
                Version++;
                return;
            }

            if (rpc.Index >= MaxRows || rpc.Index >= rpc.Count)
                return;

            Rows[rpc.Index] = new Row
            {
                NetworkId = rpc.NetworkId,
                Name = rpc.DisplayName.Length > 0 ? rpc.DisplayName.ToString() : "PLAYER " + rpc.NetworkId,
                SignedIn = rpc.SignedIn != 0,
                Status = StatusNone,
                BetaStatus = StatusNone,
            };
            if (RowCount < rpc.Index + 1)
                RowCount = rpc.Index + 1;
            Version++;
        }

        /// <summary>Paints the Worker result onto the matching row and keeps a line on the card.</summary>
        public static void ApplyResult(int networkId, byte status)
        {
            LastNote = NoteForStatus(status);
            for (int i = 0; i < RowCount; i++)
            {
                if (Rows[i].NetworkId != networkId)
                    continue;
                Row row = Rows[i];
                row.Status = status;
                Rows[i] = row;
                Version++;
                return;
            }

            Version++;
        }

        /// <summary>Paints a beta-tester gift result onto the matching row.</summary>
        public static void ApplyBetaResult(int networkId, byte status)
        {
            LastNote = BetaNoteForStatus(status);
            for (int i = 0; i < RowCount; i++)
            {
                if (Rows[i].NetworkId != networkId)
                    continue;
                Row row = Rows[i];
                row.BetaStatus = status;
                Rows[i] = row;
                Version++;
                return;
            }

            Version++;
        }

        static string BetaNoteForStatus(byte status)
        {
            switch (status)
            {
                case OrbitUnlockedGiftServerSystem.StatusGranted:
                    return "BETA SENT";
                case OrbitUnlockedGiftServerSystem.StatusAlready:
                    return "ALREADY BETA";
                case OrbitUnlockedGiftServerSystem.StatusBusy:
                    return "BETA STILL SENDING";
                default:
                    return "BETA FAILED";
            }
        }

        static string NoteForStatus(byte status)
        {
            switch (status)
            {
                case OrbitUnlockedGiftServerSystem.StatusGranted:
                    return "GIFT SENT";
                case OrbitUnlockedGiftServerSystem.StatusAlready:
                    return "ALREADY UNLOCKED";
                case OrbitUnlockedGiftServerSystem.StatusBusy:
                    return "GIFT STILL SENDING";
                default:
                    return "GIFT FAILED";
            }
        }

        public static Row GetRow(int index)
        {
            return Rows[index];
        }

        /// <summary>
        /// Shows the gifted-player line and asks the panel to read Cloud Save.
        /// A repeat toast in that window is ignored. The read flag stays set
        /// until the panel takes it.
        /// </summary>
        public static void NoteGifted()
        {
            _needsCloudRefresh = true;
            if (!string.IsNullOrEmpty(Toast) && Time.unscaledTime < _toastUntil)
                return;
            Toast = "ORBIT UNLOCKED IS YOURS";
            _toastUntil = Time.unscaledTime + 6f;
            Version++;
        }

        /// <summary>
        /// Shows the beta-tester line and asks the panel to read Cloud Save.
        /// Same refresh path as Orbit Unlocked. The read pulls both keys.
        /// </summary>
        public static void NoteBetaTesterGifted()
        {
            _needsCloudRefresh = true;
            _betaGifted = true;
            Toast = "BETA TESTER IS YOURS";
            _toastUntil = Time.unscaledTime + 6f;
            Version++;
        }

        /// <summary>
        /// True once after a beta gift. The panel turns on the local entitlement.
        /// Separate from the Orbit Unlocked refresh so a full-version gift cannot grant beta.
        /// </summary>
        public static bool ConsumeBetaGift()
        {
            if (!_betaGifted)
                return false;
            _betaGifted = false;
            return true;
        }

        /// <summary>True once. The panel calls this and then reads Cloud Save.</summary>
        public static bool ConsumeCloudRefresh()
        {
            if (!_needsCloudRefresh)
                return false;
            _needsCloudRefresh = false;
            return true;
        }

        /// <summary>True while the gifted-player line should stay on screen.</summary>
        public static bool ToastVisible()
        {
            return !string.IsNullOrEmpty(Toast) && Time.unscaledTime < _toastUntil;
        }
    }
}
