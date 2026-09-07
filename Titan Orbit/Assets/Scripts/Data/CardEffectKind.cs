namespace TitanOrbit.Data
{
    /// <summary>
    /// Named card effect kinds that are not Extra Level component stats.
    /// Cards stay overlays (percent / rate bonuses), not extra engines or weapons.
    /// Paired with <see cref="CardEffect"/> on <see cref="CardData"/> and
    /// <see cref="CardEffectQuery"/> at sim time.
    /// </summary>
    public enum CardEffectKind
    {
        /// <summary>No-op placeholder.</summary>
        None = 0,
        /// <summary>Multiplies gem-moon deposit metronome speed (1.1 = +10%).</summary>
        GemDepositSpeedMul,
        /// <summary>Multiplies asteroid mining rate (gems chipped per second).</summary>
        MiningRateMul,
        /// <summary>Multiplies gem value spawned when an asteroid is mined or destroyed.</summary>
        AsteroidGemYieldMul,
        /// <summary>Multiplies people load/unload accumulator speed in orbit.</summary>
        PeopleTransferSpeedMul,
        /// <summary>Adds world-unit radius to gem pickup / tractor scoop.</summary>
        GemPickupRadiusAdd,
        /// <summary>Multiplies tractor beam reach.</summary>
        TractorRangeMul,
        /// <summary>Multiplies tractor pull strength.</summary>
        TractorPowerMul,
        /// <summary>Multiplies fighter-drone hit points.</summary>
        DroneHitPointsMul,
        /// <summary>Multiplies fighter-drone shot damage.</summary>
        DroneDamageMul,
        /// <summary>Multiplies rocket pack damage.</summary>
        RocketDamageMul,
        /// <summary>Adds extra rockets when a pack is purchased.</summary>
        RocketPackSizeAdd,
        /// <summary>Multiplies mine explosion radius.</summary>
        MineBlastRadiusMul,
        /// <summary>Multiplies mine explosion damage.</summary>
        MineDamageMul,
        /// <summary>Adds extra mines when a pack is purchased.</summary>
        MinePackSizeAdd,
        /// <summary>Multiplies OVERDRIVE energy drain (below 1 = cheaper boost).</summary>
        OverdriveDrainMul,
        /// <summary>Multiplies OVERDRIVE extra-speed percent.</summary>
        OverdriveSpeedMul,
        /// <summary>Multiplies weapon energy cost per shot (below 1 = cheaper fire).</summary>
        WeaponEnergyCostMul,
        /// <summary>Adds hull regen per second while gem-moon docked.</summary>
        DockedHullRegenAdd,
        /// <summary>Multiplies incoming hull damage (below 1 = resist).</summary>
        IncomingDamageTakenMul,
        /// <summary>Multiplies ramming damage dealt.</summary>
        RammingMul,
        /// <summary>Multiplies weapon bullet travel range.</summary>
        BulletRangeMul,
        /// <summary>Multiplies weapon fire rate.</summary>
        FireRateMul,
        /// <summary>Adds people unloaded per dispatch chunk (efficiency).</summary>
        PeopleUnloadChunkAdd,
        /// <summary>Multiplies shield-drone absorb amount.</summary>
        ShieldDroneAbsorbMul,
    }
}
