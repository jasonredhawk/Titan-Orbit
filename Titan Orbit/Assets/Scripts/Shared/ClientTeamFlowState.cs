namespace TitanOrbit.Core
{
    /// <summary>Client-side team pick flow flags shared between ECS RPC handlers and UI.</summary>
    public static class ClientTeamFlowState
    {
        public static bool TeamChoiceConfirmed { get; private set; }

        public static void ConfirmTeamChoice() => TeamChoiceConfirmed = true;

        public static void Reset() => TeamChoiceConfirmed = false;
    }
}
