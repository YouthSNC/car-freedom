using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Scripting;

namespace HouseholdCars
{
    /// <summary>
    /// Periodically scans resident households. Every household that has a home
    /// but owns no personal car is granted a personal car, spawned directly at
    /// its home building - mirroring the vanilla purchase flow (same
    /// PersonalCarSelectData.CreateVehicle call the game's ResourceBuyerSystem
    /// uses), except the "dealership" is the household's own home, so the car
    /// always starts (and parks) close by.
    ///
    /// Households that are mid-purchase through the vanilla economy are NOT
    /// skipped, because that wait state can never end - see the long comment in
    /// ScanHouseholds.
    ///
    /// v1.1 performance notes (see also the comments at each optimisation):
    ///  * chunk-native arrays instead of ComponentLookup for the query's own
    ///    components (Household / PropertyRenter / HouseholdCitizen),
    ///  * the most selective test (car ownership) runs first,
    ///  * the vehicle-prefab job is only scheduled when a grant will happen,
    ///  * the cooldown table is a native hash set cleared once per game day
    ///    instead of a managed dictionary scanned in full on every pass.
    ///
    /// v1.2 adds the "minimum household members" setting (Options -> Mods ->
    /// Car Freedom). Households below the threshold are dropped before any
    /// lookup happens, so raising it also makes the scan cheaper.
    /// </summary>
    public partial class HouseholdCarGrantSystem : GameSystemBase
    {
        // Max cars granted per update tick - spreads the initial backlog over
        // time so a big city does not receive thousands of cars in one frame.
        private const int kMaxGrantsPerUpdate = 32;

        // Frames a household stays on cooldown after a grant (262144 = 1 game day).
        private const uint kGrantCooldownFrames = 262144u;

        // Adaptive back-off. GetUpdateInterval stays at 64, but once a pass finds
        // nothing to do the next kIdleSkips passes return immediately without
        // touching the chunk list. A fully covered city therefore costs one scan
        // per (kIdleSkips + 1) * 64 = 1024 frames instead of one every 64, which
        // is what a large mature city would otherwise pay forever.
        private const int kIdleSkips = 15;
        private int m_RemainingIdleSkips;

        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private CityConfigurationSystem m_CityConfigurationSystem;

        private EntityQuery m_HouseholdQuery;
        private EntityQuery m_PersonalCarPrefabQuery;

        private PersonalCarSelectData m_PersonalCarSelectData;

        // Chunk-native handles: these three types are part of the query itself,
        // so every matched chunk carries them and they can be read as plain
        // contiguous NativeArrays instead of one random ComponentLookup jump
        // per entity.
        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<Household> m_HouseholdType;
        private ComponentTypeHandle<PropertyRenter> m_RenterType;
        private BufferTypeHandle<HouseholdCitizen> m_HouseholdCitizenType;

        // Not part of the query -> still needs lookups.
        private ComponentLookup<Game.Objects.Transform> m_Transforms;
        private ComponentLookup<Game.Vehicles.PersonalCar> m_PersonalCars;
        private BufferLookup<HouseholdAnimal> m_HouseholdAnimals;
        private BufferLookup<OwnedVehicle> m_OwnedVehicles;

        // Households granted a car during the current game day. Cleared wholesale
        // once per game day, which keeps the "skip recently handled" test O(1)
        // without the per-pass full scan a managed Dictionary needed.
        private NativeParallelHashSet<Entity> m_RecentGrants;
        private uint m_LastGrantResetFrame;

        // Households that passed every filter this pass. Capped: no point
        // remembering more than we are allowed to serve. The spawn position and
        // passenger count are captured during the scan so phase 2 does not have
        // to look anything up a second time.
        private struct Candidate
        {
            public Entity m_Household;
            public Entity m_Home;
            public Game.Objects.Transform m_Transform;
            public int m_Citizens;
            public int m_Animals;
        }

        private NativeList<Candidate> m_Candidates;

        private int m_TotalGranted;
        private uint m_LastSummaryFrame;
        private bool m_LoggedFirstUpdate;
        private bool m_LoggedFirstGrant;

        // Member threshold used by the pass that is currently running. Pulled
        // from the settings page once per scan (not once per household) so a
        // slider change is picked up on the next pass at zero per-entity cost.
        private int m_AppliedMinMembers = CarFreedomSetting.kMinMembersDefault;

        /// <summary>
        /// Current "minimum household members" setting, clamped to the range the
        /// slider exposes. Falls back to the default before the settings object
        /// exists (e.g. if the system is created before OnLoad finished).
        /// </summary>
        private static int ReadMinHouseholdMembers()
        {
            CarFreedomSetting settings = Mod.Settings;
            if (settings == null)
            {
                return CarFreedomSetting.kMinMembersDefault;
            }
            return Mathf.Clamp(settings.MinHouseholdMembers, CarFreedomSetting.kMinMembersLower, CarFreedomSetting.kMinMembersUpper);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 64;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();
            m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            m_HouseholdQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[3]
                {
                    ComponentType.ReadOnly<Household>(),
                    ComponentType.ReadOnly<HouseholdCitizen>(),
                    ComponentType.ReadOnly<PropertyRenter>()
                },
                None = new ComponentType[6]
                {
                    ComponentType.ReadOnly<TouristHousehold>(),
                    ComponentType.ReadOnly<CommuterHousehold>(),
                    ComponentType.ReadOnly<HomelessHousehold>(),
                    ComponentType.ReadOnly<MovingAway>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            m_PersonalCarPrefabQuery = GetEntityQuery(PersonalCarSelectData.GetEntityQueryDesc());
            m_PersonalCarSelectData = new PersonalCarSelectData(this);

            m_EntityType = GetEntityTypeHandle();
            m_HouseholdType = GetComponentTypeHandle<Household>(isReadOnly: true);
            m_RenterType = GetComponentTypeHandle<PropertyRenter>(isReadOnly: true);
            m_HouseholdCitizenType = GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            m_Transforms = GetComponentLookup<Game.Objects.Transform>(isReadOnly: true);
            m_PersonalCars = GetComponentLookup<Game.Vehicles.PersonalCar>(isReadOnly: true);
            m_HouseholdAnimals = GetBufferLookup<HouseholdAnimal>(isReadOnly: true);
            m_OwnedVehicles = GetBufferLookup<OwnedVehicle>(isReadOnly: true);

            m_RecentGrants = new NativeParallelHashSet<Entity>(1024, Allocator.Persistent);
            m_Candidates = new NativeList<Candidate>(kMaxGrantsPerUpdate, Allocator.Persistent);

            RequireForUpdate(m_HouseholdQuery);
            RequireForUpdate(m_PersonalCarPrefabQuery);

            // Diagnostic: proves the system was created (i.e. mod registration
            // worked). The actual per-tick work only starts once the simulation
            // is unpaused, because the game does not update GameSimulation phase
            // while paused (see SimulationSystem.OnUpdate).
            UnityEngine.Debug.Log("[" + Mod.kModName + "] system created - waiting for simulation to run (unpause the game)");
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (m_Candidates.IsCreated)
            {
                m_Candidates.Dispose();
            }
            if (m_RecentGrants.IsCreated)
            {
                m_RecentGrants.Dispose();
            }
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!m_LoggedFirstUpdate)
            {
                m_LoggedFirstUpdate = true;
                UnityEngine.Debug.Log("[" + Mod.kModName + "] simulation active - first household scan");
            }

            uint frameIndex = m_SimulationSystem.frameIndex;

            // Pick up slider changes immediately: a threshold change also clears
            // the idle back-off, otherwise a city that was fully covered would
            // wait up to kIdleSkips passes before the new rule is applied.
            int wantedMinMembers = ReadMinHouseholdMembers();
            if (wantedMinMembers != m_AppliedMinMembers)
            {
                m_AppliedMinMembers = wantedMinMembers;
                m_RemainingIdleSkips = 0;
                UnityEngine.Debug.Log("[" + Mod.kModName + "] settings changed - now granting cars to households with >= " + wantedMinMembers + " member(s)");
            }

            // Back-off: the previous pass found no carless household, so let a few
            // passes go by. New households still get picked up within the next
            // scan (at most ~1024 frames, well under one game day).
            if (m_RemainingIdleSkips > 0)
            {
                m_RemainingIdleSkips--;
                return;
            }

            if (frameIndex - m_LastGrantResetFrame >= kGrantCooldownFrames)
            {
                m_LastGrantResetFrame = frameIndex;
                if (m_RecentGrants.Count() > 0)
                {
                    m_RecentGrants.Clear();
                }
            }

            m_Transforms.Update(this);
            m_PersonalCars.Update(this);
            m_HouseholdAnimals.Update(this);
            m_OwnedVehicles.Update(this);

            // ---- Phase 1: scan (no structural work, no sync point) ----
            m_Candidates.Clear();
            int carless = ScanHouseholds();

            // ---- Phase 2: grant ----
            int granted = 0;
            if (m_Candidates.Length > 0)
            {
                granted = GrantCars();
            }

            m_TotalGranted += granted;
            // Keep logging quiet: one line on the very first grant, then only the
            // periodic coverage report below. Without this a big city would emit
            // one log line per tick (hundreds/thousands of lines per game day).
            if (granted > 0 && !m_LoggedFirstGrant)
            {
                m_LoggedFirstGrant = true;
                UnityEngine.Debug.Log("[" + Mod.kModName + "] first batch: granted " + granted + " household car(s) near home");
            }
            // Periodic coverage report (~1 game day) so the mod can be verified in game.
            if (m_LastSummaryFrame == 0u)
            {
                m_LastSummaryFrame = frameIndex;
            }
            else if (frameIndex - m_LastSummaryFrame >= kGrantCooldownFrames)
            {
                m_LastSummaryFrame = frameIndex;
                UnityEngine.Debug.Log("[" + Mod.kModName + "] status: " + carless + " household(s) with >= " + m_AppliedMinMembers + " member(s) still without a car, " + m_TotalGranted + " granted in total");
            }

            // Nothing carless and nothing granted -> the city is covered. Enter
            // back-off. A pass that still has candidates keeps the fast path so
            // the backlog drains at full rate.
            if (carless == 0 && granted == 0)
            {
                m_RemainingIdleSkips = kIdleSkips;
            }
        }

        /// <summary>
        /// Walks the household chunks and fills <see cref="m_Candidates"/>.
        /// Returns the number of households that genuinely still need a car.
        ///
        /// The chunk is read through chunk-native arrays for the three components
        /// the query already guarantees, so a household costs zero random memory
        /// jumps for Household/PropertyRenter/HouseholdCitizen. Only the vehicle
        /// buffer and the home Transform need a lookup.
        /// </summary>
        private int ScanHouseholds()
        {
            int carless = 0;
            int minMembers = m_AppliedMinMembers;
            NativeArray<ArchetypeChunk> chunks = m_HouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            for (int c = 0; c < chunks.Length; c++)
            {
                ArchetypeChunk chunk = chunks[c];
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Household> households = chunk.GetNativeArray(ref m_HouseholdType);
                NativeArray<PropertyRenter> renters = chunk.GetNativeArray(ref m_RenterType);
                BufferAccessor<HouseholdCitizen> citizens = chunk.GetBufferAccessor(ref m_HouseholdCitizenType);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity household = entities[i];
                    if ((households[i].m_Flags & HouseholdFlags.MovedIn) == 0)
                    {
                        continue;
                    }

                    // Member threshold from the settings page. The buffer LENGTH
                    // lives in the chunk's buffer header, so reading it is a
                    // cache-local read - this runs before any random lookup and
                    // rejects households before they cost anything.
                    int memberCount = citizens[i].Length;
                    if (memberCount < minMembers)
                    {
                        continue;
                    }

                    // Ownership next: in a mature city this single test rejects
                    // the overwhelming majority of households, so the remaining
                    // (more expensive) checks below run only a few hundred times
                    // per pass instead of tens of thousands.
                    if (HasPersonalCar(household))
                    {
                        continue;
                    }

                    if (m_RecentGrants.Contains(household))
                    {
                        continue;
                    }
                    // No "is vanilla already buying one?" wait state here, on
                    // purpose. Verified in Game.dll - that wait can never end:
                    //  1) HouseholdBehaviorSystem.Worker never re-evaluates a
                    //     HouseholdNeed whose m_Resource is anything but
                    //     NoResource, so a Vehicles need is sticky until it is
                    //     actually served.
                    //  2) It is only set when spendableMoney > kCarBuyingMinimum
                    //     Money (10,000), but ResourceBuyerSystem caps what the
                    //     buyer can pay to (money - kMinimumShoppingMoney) /
                    //     (marketPrice * 1.4). When that cap lands below
                    //     kCarAmount (50) the sale still goes through, yet the
                    //     car spawn is guarded by item.m_Amount == kCarAmount,
                    //     so the household pays and receives nothing - then
                    //     retries the same doomed purchase.
                    //  3) The car is only created when the SELLER has a
                    //     PropertyRenter, i.e. a real occupied dealership. No
                    //     dealership in the city means no vanilla car ever.
                    // If vanilla ever does complete a purchase, HasPersonalCar
                    // rejects the household on the next pass; the only cost of
                    // that rare race is one extra car.
                    Entity home = renters[i].m_Property;
                    if (!m_Transforms.TryGetComponent(home, out Game.Objects.Transform transform))
                    {
                        continue;
                    }
                    // No empty-household check here: minMembers is always >= 1,
                    // so memberCount == 0 was already rejected above.

                    carless++;
                    if (m_Candidates.Length < kMaxGrantsPerUpdate)
                    {
                        Candidate candidate;
                        candidate.m_Household = household;
                        candidate.m_Home = home;
                        candidate.m_Transform = transform;
                        candidate.m_Citizens = memberCount;
                        candidate.m_Animals = (m_HouseholdAnimals.HasBuffer(household) ? m_HouseholdAnimals[household].Length : 0);
                        m_Candidates.Add(candidate);
                    }
                }
            }
            chunks.Dispose();
            return carless;
        }

        /// <summary>
        /// Spawns the actual vehicles. Only reached when there is at least one
        /// candidate, which is why the prefab query / job is prepared here rather
        /// than at the top of every update.
        /// </summary>
        private int GrantCars()
        {
            m_PersonalCarSelectData.PreUpdate(this, m_CityConfigurationSystem, m_PersonalCarPrefabQuery, Allocator.TempJob, out JobHandle jobHandle);
            jobHandle.Complete();

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            Unity.Mathematics.Random random = RandomSeed.Next().GetRandom(0);
            int granted = 0;

            for (int i = 0; i < m_Candidates.Length; i++)
            {
                Candidate candidate = m_Candidates[i];
                Entity household = candidate.m_Household;

                // Vanilla ResourceBuyerSystem passes the rented building as `source`
                // (it becomes TripSource + Unspawned on the car). Matching that
                // exactly, only with the spawn transform being the home itself.
                Entity car = m_PersonalCarSelectData.CreateVehicle(commandBuffer, ref random, candidate.m_Citizens, 1 + candidate.m_Animals, avoidTrailers: true, noSlowVehicles: false, bicycle: false, candidate.m_Transform, candidate.m_Home, Entity.Null, (PersonalCarFlags)0u, stopped: true);
                if (car == Entity.Null)
                {
                    continue;
                }
                commandBuffer.AddComponent(car, new Owner(household));
                // Ownership registration. Verified against Game.dll + Unity.Entities.dll:
                //  * ReferencesSystem (Game.Vehicles) registers new cars in the owner's
                //    OwnedVehicle buffer only when that buffer already exists (TryGetBuffer),
                //    exactly like the vanilla ResourceBuyerSystem purchase flow.
                //  * PersonalCarOwnerSystem deletes any car that is not listed in its
                //    owner's OwnedVehicle buffer (checked every 1024 frames).
                //  * ECB.AddBuffer<T> playback calls SetBufferRaw - it OVERWRITES the
                //    buffer content, so it must never be issued for an existing buffer,
                //    hence the HasBuffer guard (same as vanilla).
                //  * ECB.AppendToBuffer<T> playback only appends (IL-verified), and it is
                //    recorded after the AddBuffer command on the same chain, so the buffer
                //    is guaranteed to exist at playback. ReferencesSystem uses
                //    TryAddUniqueValue, so a duplicate registration is a harmless no-op.
                if (!m_OwnedVehicles.HasBuffer(household))
                {
                    commandBuffer.AddBuffer<OwnedVehicle>(household);
                }
                commandBuffer.AppendToBuffer(household, new OwnedVehicle(car));
                m_RecentGrants.Add(household);
                granted++;
            }

            m_PersonalCarSelectData.PostUpdate(jobHandle);
            return granted;
        }

        /// <summary>
        /// Behaviourally identical to <c>CitizenUtils.HouseholdHasCar</c>, but uses
        /// TryGetBuffer so a household costs one buffer lookup instead of two
        /// (HasBuffer + indexer).
        /// </summary>
        private bool HasPersonalCar(Entity household)
        {
            if (!m_OwnedVehicles.TryGetBuffer(household, out DynamicBuffer<OwnedVehicle> owned))
            {
                return false;
            }
            for (int i = 0; i < owned.Length; i++)
            {
                if (m_PersonalCars.HasComponent(owned[i].m_Vehicle))
                {
                    return true;
                }
            }
            return false;
        }

        [Preserve]
        public HouseholdCarGrantSystem()
        {
        }
    }
}
