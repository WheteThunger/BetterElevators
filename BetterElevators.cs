using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Better Elevators", "WhiteThunder", "1.2.11")]
    [Description("Allows elevators to be taller, faster, powerless, and more.")]
    internal class BetterElevators : CovalencePlugin
    {
        #region Fields

        private const string PermissionPowerless = "betterelevators.powerless";
        private const string PermissionLiftCounter = "betterelevators.liftcounter";

        private const string PermissionMaxFloorsPrefix = "betterelevators.maxfloors";
        private const string PermissionSpeedPrefix = "betterelevators.speed";

        private const string PrefabElevator = "assets/prefabs/deployable/elevator/elevator.prefab";
        private const string PrefabPowerCounter = "assets/prefabs/deployable/playerioents/counter/counter.prefab";

        private const int VanillaMaxFloors = 6;
        private const float ElevatorHeight = 3;
        private const float ElevatorLiftLocalOffsetY = 1;
        private const float MaxCounterUpdateFrequency = 0.4f;

        private static readonly PropertyInfo ElevatorLiftOwnerProperty = typeof(ElevatorLift).GetProperty("owner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private readonly object False = false;

        private readonly Vector3 LiftCounterPosition = new(-1.18f, -0.16f, -0.1f);
        private readonly Quaternion LiftCounterRotation = Quaternion.Euler(0, 90, 0);

        private readonly Vector3 StaticLiftCounterPosition = new(1.183f, -0.09f, -0.92f);
        private readonly Quaternion StaticLiftCounterRotation = Quaternion.Euler(0, -90, 0);

        private readonly Dictionary<NetworkableId, Action> _liftTimerActions = new();
        private ProtectionProperties _immortalProtection;
        private Configuration _config;

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionPowerless, this);
            permission.RegisterPermission(PermissionLiftCounter, this);

            foreach (var maxFloorsAmount in _config.MaxFloorsRequiringPermission)
            {
                permission.RegisterPermission(GetMaxFloorsPermission(maxFloorsAmount), this);
            }

            foreach (var speedConfig in _config.SpeedsRequiringPermission)
            {
                if (!string.IsNullOrWhiteSpace(speedConfig.Name))
                {
                    permission.RegisterPermission(GetSpeedPermission(speedConfig.Name), this);
                }
            }

            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void OnServerInitialized()
        {
            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "BetterElevatorsCounterProtection";
            _immortalProtection.Add(1);

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var elevator = entity as Elevator;
                if (elevator != null)
                {
                    OnEntitySpawned(elevator);
                    continue;
                }

                var elevatorIoEntity = entity as ElevatorIOEntity;
                if (elevatorIoEntity != null)
                {
                    OnEntitySpawned(elevatorIoEntity);
                    continue;
                }

                var lift = entity as ElevatorLift;
                if (lift != null)
                {
                    OnEntitySpawned(lift);

                    if (lift is ElevatorLiftStatic)
                    {
                        var ownerElevator = GetOwnerElevator(lift);
                        if (ownerElevator != null)
                        {
                            // Fix issue caused by previous version where elevators could not move.
                            if (ownerElevator.IsBusy())
                            {
                                lift.SetFlag(BaseEntity.Flags.Reserved3, true);
                                lift.SetFlag(BaseEntity.Flags.Reserved4, true);
                            }
                            else
                            {
                                lift.NotifyNewFloor(ownerElevator.LiftPositionToFloor(), ownerElevator.Floor);
                            }
                        }
                    }

                    continue;
                }
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            UnityEngine.Object.Destroy(_immortalProtection);

            var liftCounters = new List<PowerCounter>();

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var lift = entity as ElevatorLift;
                if (lift != null)
                {
                    if (lift is not ElevatorLiftStatic)
                    {
                        CustomParentTrigger.RemoveFromLift(lift);
                    }

                    continue;
                }

                var counter = entity as PowerCounter;
                if (counter != null)
                {
                    if (IsLiftCounter(counter))
                    {
                        liftCounters.Add(counter);
                    }

                    continue;
                }
            }

            foreach (var counter in liftCounters)
            {
                counter.Kill();
            }
        }

        private void OnEntitySpawned(Elevator elevator)
        {
            // This is required to allow placement to succeed above 6 floors
            // Note: This doesn't contribute to the placement guides appearing client-side
            var elevatorSockets = elevator.GetEntityLinks().Select(link => link.socket).OfType<ConstructionSocket_Elevator>();
            foreach (var socket in elevatorSockets)
            {
                socket.MaxFloor = 999;
            }
        }

        private void OnEntitySpawned(ElevatorLift lift)
        {
            var topElevator = GetOwnerElevator(lift);
            if (topElevator == null)
                return;

            if (lift is not ElevatorLiftStatic)
            {
                CustomParentTrigger.AddToLift(lift);
            }

            // Add a counter to the lift when it spawns
            // Check for an existing counter since this is also called when loading a save
            if (AllowLiftCounter(lift, topElevator) && GetLiftCounter(lift) == null)
            {
                AddLiftCounter(lift, topElevator.LiftPositionToFloor() + 1, topElevator.OwnerID, startPowered: ElevatorHasPower(topElevator));
            }
        }

        private void OnEntitySpawned(ElevatorIOEntity ioEntity)
        {
            var topElevator = ioEntity.GetParentEntity() as Elevator;
            if (topElevator == null)
                return;

            if (IsPowerlessElevator(topElevator))
            {
                ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
            }

            MaybeToggleLiftCounter(topElevator);
        }

        private object CanBuild(Planner planner, Construction construction, Construction.Target target)
        {
            if (planner == null || construction == null)
                return null;

            var elevatorBelow = target.entity as Elevator;
            if (elevatorBelow == null)
                return null;

            if (construction.deployable?.fullName != PrefabElevator)
                return null;

            var deployingPlayer = planner.GetOwnerPlayer();
            if (deployingPlayer == null)
                return null;

            var maxFloors = GetPlayerMaxFloors(deployingPlayer.UserIDString);
            if (elevatorBelow.Floor + 1 >= maxFloors)
            {
                ChatMessage(deployingPlayer, Lang.NoPermissionToFloor, maxFloors);
                return False;
            }

            return null;
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var entity = go.ToBaseEntity();
            if (entity == null)
                return;

            var topElevator = entity as Elevator;
            if (topElevator == null)
                return;

            var elevatorBelow = topElevator.GetElevatorInDirection(Elevator.Direction.Down);
            if (elevatorBelow == null)
                return;

            if (_config.EnsureConsistentOwner && elevatorBelow.OwnerID != 0)
            {
                topElevator.OwnerID = elevatorBelow.OwnerID;
            }

            if (!_config.MaintainLiftPositionWhenHeightChanges
                || !elevatorBelow.liftEntity.TryGet(true, out var lift)
                || lift == null)
                return;

            var didStopMovement = TryStopLiftMovement(lift, elevatorBelow, out var targetFloor);

            lift.SetParent(topElevator, worldPositionStays: true, sendImmediate: true);
            lift.SetOwnerElevator(topElevator);
            topElevator.liftEntity.Set(lift);
            elevatorBelow.liftEntity.Set(null);
            topElevator.ClearBusy();

            if (didStopMovement)
            {
                NextTick(() =>
                {
                    if (topElevator == null)
                        return;

                    topElevator.RequestMoveLiftTo(targetFloor, out _, topElevator);
                });
            }
        }

        private void OnEntityKill(Elevator elevator)
        {
            if (!_config.MaintainLiftPositionWhenHeightChanges || elevator == null || elevator.Floor == 0)
                return;

            var elevatorBelow = elevator.GetElevatorInDirection(Elevator.Direction.Down);
            if (elevatorBelow == null)
                return;

            var topElevator = GetTopElevator(elevator);
            if (!topElevator.liftEntity.TryGet(true, out var lift))
                return;

            // One reason for the lift being null is that another OnEntityKill call removed it from the top elevator
            if (lift == null)
                return;

            var liftFloor = topElevator.LiftPositionToFloor();

            // If the lift is above the next top elevator, allow it to be destroyed and recreated like normal
            if (liftFloor > elevatorBelow.Floor)
                return;

            var didStopMovement = TryStopLiftMovement(lift, topElevator, out var targetFloor);

            lift.SetParent(elevatorBelow, worldPositionStays: true, sendImmediate: true);
            elevatorBelow.liftEntity.Set(lift);
            topElevator.liftEntity.Set(null);
            topElevator.ClearBusy();

            if (didStopMovement)
            {
                CancelHorseDropToGround(lift);
                elevatorBelow.RequestMoveLiftTo(Math.Min(targetFloor, elevatorBelow.Floor), out _, elevator);
            }
        }

        private void OnEntityKill(ElevatorLift lift)
        {
            _liftTimerActions.Remove(lift.net.ID);
        }

        private object OnElevatorMove(Elevator topElevator, int targetFloor)
        {
            if (!topElevator.liftEntity.TryGet(true, out var lift)
                || lift == null)
                return null;

            var liftFloor = topElevator.LiftPositionToFloor();
            if (targetFloor == liftFloor)
                return null;

            if (!CanElevatorMoveToFloor(topElevator, targetFloor))
                return null;

            var worldSpaceFloorPosition = topElevator.GetWorldSpaceFloorPosition(targetFloor);
            var distance = Mathf.Abs(lift.transform.position.y - worldSpaceFloorPosition.y);
            var timeToTravel = distance / topElevator.LiftSpeedPerMetre;

            if (!TryGetSpeedConfig(topElevator, out var speedConfig))
            {
                if (GetLiftCounter(lift) != null && timeToTravel > 0)
                    StartUpdatingLiftCounter(lift, timeToTravel);

                return null;
            }

            // Custom movement starts here.
            topElevator.OnMoveBegin();

            LeanTweenType leanTweenType;
            switch (speedConfig.EaseType)
            {
                case EaseType.Quadratic:
                    timeToTravel = Convert.ToSingle(Math.Sqrt(distance)) / speedConfig.BaseSpeed;
                    leanTweenType = LeanTweenType.easeInOutQuad;
                    break;

                case EaseType.Cubic:
                    timeToTravel = Convert.ToSingle(Math.Pow(distance, 1.0 / 3.0)) / speedConfig.BaseSpeed;
                    leanTweenType = LeanTweenType.easeInOutCubic;
                    break;

                default:
                    timeToTravel = distance / speedConfig.GetSpeedForLevels(Math.Abs(targetFloor - liftFloor));
                    leanTweenType = LeanTweenType.linear;
                    break;
            }

            LeanTween.moveY(lift.gameObject, worldSpaceFloorPosition.y, timeToTravel).setEase(leanTweenType);

            // Duplicating vanilla logic since this is replacing default movement
            topElevator.SetFlag(BaseEntity.Flags.Busy, true);
            if (targetFloor < topElevator.Floor)
            {
                lift.ToggleHurtTrigger(true);
            }

            lift.SetFlag(BaseEntity.Flags.Busy, true);
            topElevator.Invoke(topElevator.ClearBusy, timeToTravel);
            lift.NotifyNewFloor(targetFloor, topElevator.Floor);

            if (topElevator.ioEntity != null)
            {
                topElevator.ioEntity.SetFlag(BaseEntity.Flags.Busy, true);
                topElevator.ioEntity.SendChangedToRoot(forceUpdate: true);
            }

            if (GetLiftCounter(lift) != null && timeToTravel > 0)
            {
                StartUpdatingLiftCounter(lift, timeToTravel);
            }

            return False;
        }

        private void OnEntitySaved(Elevator elevator, BaseNetworkable.SaveInfo info)
        {
            // This is where the magic happens... thanks to @JakeRich
            if (!info.forDisk)
            {
                info.msg.elevator.floor = 1;
            }
        }

        private object OnCounterTargetChange(PowerCounter counter, BasePlayer player, int amount)
        {
            var lift = counter.GetParentEntity() as ElevatorLift;
            if (lift == null)
                return null;

            var topElevator = GetOwnerElevator(lift);
            if (topElevator == null)
                return null;

            // After this point, we return false to disable the default action since we know the counter is attached to an elevator
            if (topElevator.IsBusy())
                return False;

            if (!ElevatorHasPower(topElevator))
                return False;

            // The lift is parented to the top elevator so elevator.Floor is always the top floor
            var targetFloor = Math.Min(Math.Max(0, amount - 1), topElevator.Floor);
            if (player.IsBuildingBlocked() || FloorSelectionWasBlocked(lift, player, targetFloor))
                return False;

            topElevator.RequestMoveLiftTo(targetFloor, out _, topElevator);

            return False;
        }

        private object OnCounterModeToggle(PowerCounter counter, BasePlayer player, bool doShowPassthrough)
        {
            // Prevent lift counter from being toggled to "show passthrough" mode
            if (doShowPassthrough && IsLiftCounter(counter))
                return False;

            return null;
        }

        private void OnInputUpdate(ElevatorIOEntity ioEntity, int inputAmount)
        {
            if (ioEntity == null)
                return;

            var topElevator = ioEntity.GetParentEntity() as Elevator;
            if (topElevator != null)
            {
                var ioEntity2 = ioEntity;
                if (!topElevator.liftEntity.TryGet(true, out var lift))
                    return;

                var isPowerless = IsPowerlessElevator(topElevator);

                NextTick(() =>
                {
                    if (isPowerless)
                    {
                        // Allow electricity to function normally when there is a wire plugged in
                        // For example, so trap base designs can prevent players from using the buttons
                        // When no wire is connected, force power to be on
                        if (ioEntity2.inputs[0].connectedTo.Get() == null)
                        {
                            ioEntity2.SetFlag(IOEntity.Flag_HasPower, true);
                        }
                    }

                    if (lift != null)
                    {
                        // Get the elevator again since the lift could have changed parent
                        var nextTopElevator = GetOwnerElevator(lift);
                        if (nextTopElevator == null)
                            return;

                        // Update the power state of the lift counter to match elevator power state
                        MaybeToggleLiftCounter(nextTopElevator);
                    }
                });
            }
        }

        #endregion

        #region Helper Methods

        public static void LogDebug(string message) => Interface.Oxide.LogDebug($"[Better Elevators] {message}");
        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Better Elevators] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Better Elevators] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Better Elevators] {message}");

        private bool FloorSelectionWasBlocked(ElevatorLift lift, BasePlayer player, int targetFloor)
        {
            return Interface.CallHook("OnElevatorFloorSelect", lift, player, targetFloor) is false;
        }

        private string GetSpeedPermission(string permissionName) => $"{PermissionSpeedPrefix}.{permissionName}";

        private string GetMaxFloorsPermission(int maxFloors) => $"{PermissionMaxFloorsPrefix}.{maxFloors}";

        private static Elevator GetOwnerElevator(ElevatorLift lift)
        {
            return ElevatorLiftOwnerProperty?.GetValue(lift) as Elevator;
        }

        private void CancelHorseDropToGround(ElevatorLift lift)
        {
            foreach (var child in lift.children)
            {
                var horse = child as RidableHorse;
                if (horse != null)
                {
                    horse.Invoke(() => horse.CancelInvoke(horse.DelayedDropToGround), 0);
                }
            }
        }

        private bool CanElevatorMoveToFloor(Elevator topElevator, int targetFloor)
        {
            // Duplicating vanilla logic.
            if (topElevator.IsBusy())
                return false;

            if (!topElevator.IsStatic && topElevator.ioEntity != null && !topElevator.ioEntity.IsPowered())
                return false;

            if (!topElevator.IsValidFloor(targetFloor))
                return false;

            if (!topElevator.liftEntity.TryGet(true, out var topElevatorLiftEntity)
                || !topElevatorLiftEntity.CanMove())
                return false;

            if (topElevator.LiftPositionToFloor() == targetFloor)
            {
                (topElevator as ElevatorStatic)?.OnLiftArrivedAtFloor();
                return false;
            }

            return true;
        }

        private bool TryStopLiftMovement(ElevatorLift lift, Elevator topElevator, out int targetFloor)
        {
            var tweens = LeanTween.descriptions(lift.gameObject);
            targetFloor = 0;

            if (tweens.Length == 0)
                return false;

            // we only expect one tween to be running for each elevator at a time
            var tween = tweens[0];
            var originalY = tween.to.x;

            var totalElevatorHeight = ElevatorHeight * (topElevator.Floor + 1);
            var bottomElevatorLiftPosition = topElevator.transform.position.y - totalElevatorHeight + ElevatorLiftLocalOffsetY;
            var localHeight = originalY - bottomElevatorLiftPosition;

            targetFloor = (int)(localHeight / ElevatorHeight) - 1;

            LeanTween.cancel(tween.uniqueId);
            return true;
        }

        private bool AllowLiftCounter(ElevatorLift lift, Elevator topElevator)
        {
            if (topElevator.IsStatic)
                return _config.StaticElevators.EnableLiftCounter
                    && !lift.ShortPrefabName.Contains("elevator_office_lift.static");

            var ownerId = topElevator.OwnerID;
            return !_config.RequirePermissionForLiftCounter
                || ownerId != 0 && permission.UserHasPermission(ownerId.ToString(), PermissionLiftCounter);
        }

        private bool AllowPowerless(Elevator topElevator)
        {
            if (topElevator.IsStatic)
                return false;

            var ownerId = topElevator.OwnerID;
            return !_config.RequirePermissionForPowerless
                || ownerId != 0 && permission.UserHasPermission(ownerId.ToString(), PermissionPowerless);
        }

        private Elevator GetTopElevator(Elevator elevator)
        {
            return GetFarthestElevatorInDirection(elevator, Elevator.Direction.Up);
        }

        private bool IsPowerlessElevator(Elevator elevator)
        {
            return AllowPowerless(elevator);
        }

        private bool IsLiftCounter(PowerCounter counter)
        {
            return counter.GetParentEntity() is ElevatorLift;
        }

        private bool ElevatorHasPower(Elevator topElevator)
        {
            return topElevator.IsStatic || topElevator.ioEntity != null && topElevator.ioEntity.IsPowered();
        }

        private void RemoveGroundWatch(BaseEntity entity)
        {
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
        }

        private void HideInputsAndOutputs(IOEntity ioEntity)
        {
            // Trick to hide the inputs and outputs on the client
            foreach (var input in ioEntity.inputs)
            {
                input.type = IOEntity.IOType.Generic;
            }

            foreach (var output in ioEntity.outputs)
            {
                output.type = IOEntity.IOType.Generic;
            }
        }

        private void AddLiftCounter(ElevatorLift lift, int currentDisplayFloor, ulong ownerId, bool startPowered = false)
        {
            var position = LiftCounterPosition;
            var rotation = LiftCounterRotation;

            if (lift is ElevatorLiftStatic)
            {
                position = StaticLiftCounterPosition;
                rotation = StaticLiftCounterRotation;
            }

            var counter = GameManager.server.CreateEntity(PrefabPowerCounter, position, rotation) as PowerCounter;
            if (counter == null)
                return;

            RemoveGroundWatch(counter);
            HideInputsAndOutputs(counter);

            counter.pickup.enabled = false;
            counter.baseProtection = _immortalProtection;
            counter.OwnerID = ownerId;
            counter.SetParent(lift);
            counter.Spawn();

            if (startPowered)
            {
                InitializeCounter(counter, currentDisplayFloor);
            }
        }

        private PowerCounter GetLiftCounter(ElevatorLift lift)
        {
            foreach (var child in lift.children)
            {
                var counter = child as PowerCounter;
                if (counter != null)
                    return counter;
            }

            return null;
        }

        private void UpdateFloorCounter(ElevatorLift lift, PowerCounter counter)
        {
            // Get the elevator on every update, since the lift can be re-parented
            var topElevator = GetOwnerElevator(lift);
            if (topElevator == null || counter == null)
                return;

            var floor = topElevator.LiftPositionToFloor() + 1;

            if (counter.counterNumber == floor)
                return;

            counter.counterNumber = floor;
            counter.targetCounterNumber = floor;
            counter.SendNetworkUpdate();
        }

        private void StartUpdatingLiftCounter(ElevatorLift lift, float timeToTravel)
        {
            var liftCounter = GetLiftCounter(lift);
            if (liftCounter == null)
                return;

            if (_liftTimerActions.TryGetValue(lift.net.ID, out var existingTimerAction))
            {
                lift.CancelInvoke(existingTimerAction);
            }

            var lastCounterUpdateTime = Time.time;
            Action timerAction = null;
            var stepsRemaining = timeToTravel / MaxCounterUpdateFrequency;
            timerAction = () =>
            {
                stepsRemaining--;

                var reachedEnd = stepsRemaining <= 0;
                if (reachedEnd || Time.time >= lastCounterUpdateTime + MaxCounterUpdateFrequency)
                {
                    UpdateFloorCounter(lift, liftCounter);
                    lastCounterUpdateTime = Time.time;
                }

                if (reachedEnd)
                {
                    lift.CancelInvoke(timerAction);
                    _liftTimerActions.Remove(lift.net.ID);
                }
            };
            lift.InvokeRepeating(timerAction, MaxCounterUpdateFrequency, MaxCounterUpdateFrequency);
            _liftTimerActions[lift.net.ID] = timerAction;
        }

        private Elevator GetFarthestElevatorInDirection(Elevator elevator, Elevator.Direction direction)
        {
            var currentElevator = elevator;

            Elevator nextElevator;
            while ((nextElevator = currentElevator.GetElevatorInDirection(direction)) != null)
            {
                currentElevator = nextElevator;
            }

            return currentElevator;
        }

        private void InitializeCounter(PowerCounter counter, int floor)
        {
            counter.SetFlag(IOEntity.Flag_HasPower, true);
            counter.SetFlag(BaseEntity.Flags.Busy, false);
            counter.counterNumber = floor;
            counter.targetCounterNumber = floor;
            counter.SendNetworkUpdate();
        }

        private void ResetCounter(PowerCounter counter)
        {
            counter.SetFlag(IOEntity.Flag_HasPower, false);
            counter.SetFlag(BaseEntity.Flags.Busy, true);
            counter.counterNumber = 0;
            counter.targetCounterNumber = 0;
            counter.SendNetworkUpdate();
        }

        private void MaybeToggleLiftCounter(Elevator topElevator)
        {
            if (!topElevator.liftEntity.TryGet(true, out var lift)
                || lift == null)
                return;

            var liftCounter = GetLiftCounter(lift);
            if (liftCounter == null)
                return;

            if (ElevatorHasPower(topElevator))
            {
                InitializeCounter(liftCounter, topElevator.LiftPositionToFloor() + 1);
            }
            else
            {
                ResetCounter(liftCounter);
            }
        }

        #endregion

        #region Custom Parent Trigger

        private class CustomParentTrigger : TriggerParentElevator
        {
            public static void AddToLift(ElevatorLift lift)
            {
                var originalTrigger = GetChildComponent<TriggerParentElevator>(lift);
                if (originalTrigger == null)
                    return;

                var customTrigger = originalTrigger.gameObject.AddComponent<CustomParentTrigger>();
                customTrigger._original = originalTrigger;

                customTrigger.contents = originalTrigger.contents?.ToHashSet();
                customTrigger.entityContents = originalTrigger.entityContents?.ToHashSet();

                // TriggerBase fields.
                customTrigger.interestLayers = originalTrigger.interestLayers;

                // TriggerParent fields.
                customTrigger.associatedMountable = originalTrigger.associatedMountable;
                customTrigger.parentMountedPlayers = originalTrigger.parentMountedPlayers;
                customTrigger.ParentNPCPlayers = originalTrigger.ParentNPCPlayers;
                customTrigger.overrideOtherTriggers = originalTrigger.overrideOtherTriggers;

                // TriggerParentEnclosed fields.
                customTrigger.Padding = originalTrigger.Padding;
                customTrigger.intersectionMode = originalTrigger.intersectionMode;
                customTrigger.CheckBoundsOnUnparent = originalTrigger.CheckBoundsOnUnparent;

                if (customTrigger.entityContents != null)
                {
                    foreach (var entity in customTrigger.entityContents)
                    {
                        if (entity == null)
                            continue;

                        customTrigger.OnEntityEnter(entity);
                    }
                }

                originalTrigger.enabled = false;
            }

            public static void RemoveFromLift(ElevatorLift lift)
            {
                var customTrigger = GetChildComponent<CustomParentTrigger>(lift);
                if (customTrigger == null)
                    return;

                var originalTrigger = customTrigger._original;
                originalTrigger.enabled = true;

                originalTrigger.contents = customTrigger.contents?.ToHashSet();
                originalTrigger.entityContents = customTrigger.entityContents?.ToHashSet();

                if (originalTrigger.entityContents != null)
                {
                    foreach (var entity in originalTrigger.entityContents)
                    {
                        if (entity == null)
                            continue;

                        originalTrigger.OnEntityEnter(entity);
                    }
                }

                DestroyImmediate(customTrigger);
            }

            private static T GetChildComponent<T>(UnityEngine.Component component) where T : UnityEngine.Component
            {
                foreach (Transform child in component.transform)
                {
                    var childComponent = child.GetComponent<T>();
                    if (childComponent != null && childComponent.GetType() == typeof(T))
                        return childComponent;
                }

                return null;
            }

            // Remove the Deployed layer from the clip mask to avoid issues with clipping through the elavator at high speed.
            private const int ClipMask = CLIP_CHECK_MASK & ~Rust.Layers.Mask.Deployed;

            private TriggerParentEnclosed _original;

            #if OXIDE_PUBLICIZED
            public override bool IsClipping(BaseEntity ent)
            #else
            protected override bool IsClipping(BaseEntity ent)
            #endif
            {
                if (AllowHorsesToBypassClippingChecks && ent is BaseRidableAnimal)
                    return false;

                return GamePhysics.CheckOBB(ent.WorldSpaceBounds(), ClipMask, QueryTriggerInteraction.Ignore);
            }
        }

        #endregion

        #region Configuration

        private int GetPlayerMaxFloors(string userIdString)
        {
            if (_config.MaxFloorsRequiringPermission == null || _config.MaxFloorsRequiringPermission.Length == 0)
                return _config.DefaultMaxFloors;

            for (var i = _config.MaxFloorsRequiringPermission.Length - 1; i >= 0; i--)
            {
                var floorAmount = _config.MaxFloorsRequiringPermission[i];
                if (permission.UserHasPermission(userIdString, GetMaxFloorsPermission(floorAmount)))
                    return floorAmount;
            }

            return _config.DefaultMaxFloors;
        }

        private SpeedConfig GetPlayerSpeedConfig(ulong ownerId)
        {
            if (ownerId == 0 || _config.SpeedsRequiringPermission == null || _config.SpeedsRequiringPermission.Length == 0)
                return _config.DefaultSpeed;

            var userIdString = ownerId.ToString();

            for (var i = _config.SpeedsRequiringPermission.Length - 1; i >= 0; i--)
            {
                var speedConfig = _config.SpeedsRequiringPermission[i];
                if (!string.IsNullOrWhiteSpace(speedConfig.Name) &&
                    permission.UserHasPermission(userIdString, GetSpeedPermission(speedConfig.Name)))
                {
                    return speedConfig;
                }
            }

            return _config.DefaultSpeed;
        }

        private bool TryGetSpeedConfig(Elevator topElevator, out SpeedConfig speedConfig)
        {
            if (topElevator is ElevatorStatic)
            {
                if (_config.StaticElevators.EnableCustomSpeed)
                {
                    speedConfig = _config.StaticElevators.Speed;
                    return true;
                }
            }
            else if (_config.EnableSpeedOptions)
            {
                speedConfig = GetPlayerSpeedConfig(topElevator.OwnerID);
                return true;
            }

            speedConfig = null;
            return false;
        }

        // Don't rename these since they are used in the config.
        private enum EaseType { Linear, Quadratic, Cubic }

        private class SpeedConfig
        {
            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name;

            [JsonProperty("BaseSpeed")]
            public float BaseSpeed = 1.5f;

            [JsonProperty("SpeedIncreasePerFloor", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float SpeedPerAdditionalFloor = 0;

            [JsonProperty("MaxSpeed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(1.5f)]
            public float MaxSpeed = 1.5f;

            [JsonProperty("EaseType")]
            [JsonConverter(typeof(StringEnumConverter))]
            public EaseType EaseType = EaseType.Linear;

            public float GetSpeedForLevels(int levels)
            {
                return Math.Min(Math.Max(BaseSpeed, MaxSpeed), BaseSpeed + (levels - 1) * SpeedPerAdditionalFloor);
            }
        }

        private class StaticElevatorConfig
        {
            [JsonProperty("EnableCustomSpeed")]
            public bool EnableCustomSpeed = false;

            [JsonProperty("Speed")]
            public SpeedConfig Speed = new()
            {
                BaseSpeed = 3.5f,
            };

            [JsonProperty("EnableLiftCounter")]
            public bool EnableLiftCounter = false;
        }

        private class Configuration : BaseConfiguration
        {
            [JsonProperty("DefaultMaxFloors")]
            public int DefaultMaxFloors = VanillaMaxFloors;

            [JsonProperty("MaxFloorsRequiringPermission")]
            public int[] MaxFloorsRequiringPermission = { 10, 15, 20, 100 };

            [JsonProperty("RequirePermissionForPowerless")]
            public bool RequirePermissionForPowerless = true;

            [JsonProperty("RequirePermissionForLiftCounter")]
            public bool RequirePermissionForLiftCounter = true;

            [JsonProperty("MaintainLiftPositionWhenHeightChanges")]
            public bool MaintainLiftPositionWhenHeightChanges = false;

            [JsonProperty("EnsureConsistentOwner")]
            public bool EnsureConsistentOwner = true;

            [JsonProperty("EnableSpeedOptions")]
            public bool EnableSpeedOptions = true;

            [JsonProperty("DefaultSpeed")]
            public SpeedConfig DefaultSpeed = new()
            {
                BaseSpeed = 1.5f,
            };

            [JsonProperty("SpeedsRequiringPermission")]
            public SpeedConfig[] SpeedsRequiringPermission =
            {
                new()
                {
                    Name = "2x",
                    BaseSpeed = 3f,
                },
                new()
                {
                    Name = "4x",
                    BaseSpeed = 6,
                },
                new()
                {
                    Name = "1x.quadratic",
                    BaseSpeed = 0.86f,
                    EaseType = EaseType.Quadratic,
                },
                new()
                {
                    Name = "1.5x.quadratic",
                    BaseSpeed = 1.29f,
                    EaseType = EaseType.Quadratic,
                },
                new()
                {
                    Name = "2x.quadratic",
                    BaseSpeed = 1.72f,
                    EaseType = EaseType.Quadratic,
                },
                new()
                {
                    Name = "1x.cubic",
                    BaseSpeed = 0.72f,
                    EaseType = EaseType.Cubic,
                },
                new()
                {
                    Name = "2x.cubic",
                    BaseSpeed = 1.44f,
                    EaseType = EaseType.Cubic,
                },
            };

            [JsonProperty("StaticElevators")]
            public StaticElevatorConfig StaticElevators = new();
        }

        private Configuration GetDefaultConfig() => new();

        #region Configuration Helpers

        private class BaseConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            var changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion

        #region Localization

        private string GetMessage(IPlayer player, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, player.Id);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private void ChatMessage(BasePlayer player, string messageName, params object[] args)
        {
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));
        }

        private static class Lang
        {
            public const string NoPermissionToFloor = "Deploy.Error.NoPermissionToFloor";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.NoPermissionToFloor] = "Error: You don't have permission to build elevators taller than {0} floors.",
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.NoPermissionToFloor] = "Erro: você não tem permissão para construir elevadores com mais de {0} andares.",
            }, this, "pt-BR");
        }

        #endregion
    }
}
