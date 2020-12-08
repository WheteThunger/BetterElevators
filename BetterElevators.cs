using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Better Elevators", "WhiteThunder", "1.0.2")]
    [Description("Allows elevators to be taller, faster, powerless, and more.")]
    internal class BetterElevators : CovalencePlugin
    {
        #region Fields

        private const int VanillaMaxFloors = 6;
        private const float ElevatorHeight = 3;
        private const float ElevatorLiftLocalOffsetY = 1;
        private const float MaxCounterUpdateFrequency = 0.4f;

        private const string PermissionPowerless = "betterelevators.powerless";
        private const string PermissionLiftCounter = "betterelevators.liftcounter";

        private const string PermissionMaxFloorsPrefix = "betterelevators.maxfloors";
        private const string PermissionSpeedPrefix = "betterelevators.speed";

        private const string PrefabElevator = "assets/prefabs/deployable/elevator/elevator.prefab";
        private const string PrefabPowerCounter = "assets/prefabs/deployable/playerioents/counter/counter.prefab";

        private readonly Vector3 LiftCounterPosition = new Vector3(-1.18f, -0.16f, -0.1f);
        private readonly Quaternion LiftCounterRotation = Quaternion.Euler(0, 90, 0);

        private readonly Dictionary<uint, Action> liftTimerActions = new Dictionary<uint, Action>();

        private Configuration pluginConfig;

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionPowerless, this);
            permission.RegisterPermission(PermissionLiftCounter, this);

            foreach (var maxFloorsAmount in pluginConfig.maxFloorsRequiringPermission)
                permission.RegisterPermission(GetMaxFloorsPermission(maxFloorsAmount), this);

            foreach (var speedConfig in pluginConfig.speedsRequiringPermission)
                if (!string.IsNullOrWhiteSpace(speedConfig.name))
                    permission.RegisterPermission(GetSpeedPermission(speedConfig.name), this);
        }

        private void OnServerInitialized(bool initialBoot)
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (initialBoot)
                {
                    var powerCounter = entity as PowerCounter;
                    if (powerCounter != null && powerCounter.GetParentEntity() is ElevatorLift)
                    {
                        RemoveGroundWatch(powerCounter);
                        powerCounter.pickup.enabled = false;
                        continue;
                    }
                }
                else
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
                }
            }
        }

        private void OnEntitySpawned(Elevator elevator)
        {
            // This is required to allow placement to succeed above 6 floors
            // Note: This doesn't contribute to the placement guides appearing client-side
            var elevatorSockets = elevator.GetEntityLinks().Select(link => link.socket).OfType<ConstructionSocket_Elevator>();
            foreach (var socket in elevatorSockets)
                socket.MaxFloor = 999;
        }

        private void OnEntitySpawned(ElevatorLift lift)
        {
            var elevator = lift.GetParentEntity() as Elevator;
            if (elevator == null)
                return;

            // Check for an existing counter since this is also called when loading a save
            if (AllowLiftCounter(elevator.OwnerID) && GetLiftCounter(lift) == null)
                AddLiftCounter(lift, elevator.LiftPositionToFloor() + 1, elevator.OwnerID);
        }

        private void OnEntitySpawned(ElevatorIOEntity ioEntity)
        {
            var elevator = ioEntity.GetParentEntity() as Elevator;
            if (elevator == null)
                return;

            if (IsPowerlessElevator(elevator))
                ioEntity.SetFlag(IOEntity.Flag_HasPower, true);

            MaybeToggleLiftCounter(elevator);
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
                ChatMessage(deployingPlayer, "Deploy.Error.NoPermissionToFloor", maxFloors);
                return false;
            }

            return null;
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var entity = go.ToBaseEntity();
            if (entity == null)
                return;

            var elevator = entity as Elevator;
            if (elevator == null)
                return;

            var elevatorBelow = elevator.GetElevatorInDirection(Elevator.Direction.Down);
            if (elevatorBelow == null)
                return;

            if (pluginConfig.ensureConsistentOwner && elevatorBelow.OwnerID != 0)
                elevator.OwnerID = elevatorBelow.OwnerID;

            var lift = elevatorBelow.liftEntity;
            if (lift != null && pluginConfig.maintainLiftPositionWhenHeightChanges)
            {
                int targetFloor;
                bool didStopMovement = StopLiftMovement(lift, elevatorBelow, out targetFloor);

                lift.SetParent(elevator, worldPositionStays: true, sendImmediate: true);
                elevator.liftEntity = lift;
                elevatorBelow.liftEntity = null;

                if (didStopMovement)
                {
                    NextTick(() =>
                    {
                        if (elevator != null)
                            RestartLiftMovement(elevator, targetFloor);
                    });
                }
            }
        }

        private void OnEntityKill(Elevator elevator)
        {
            if (!pluginConfig.maintainLiftPositionWhenHeightChanges || elevator == null || elevator.Floor == 0)
                return;

            var elevatorBelow = elevator.GetElevatorInDirection(Elevator.Direction.Down);
            if (elevatorBelow == null)
                return;

            var topElevator = GetTopElevator(elevator);
            var lift = topElevator.liftEntity;

            // One reason for the lift being null is that another OnEntityKill call removed it from the top elevator
            if (lift == null)
                return;

            var liftFloor = topElevator.LiftPositionToFloor();

            // If the lift is above the next top elevator, allow it to be destroyed and recreated like normal
            if (liftFloor > elevatorBelow.Floor)
                return;

            int targetFloor;
            bool didStopMovement = StopLiftMovement(lift, topElevator, out targetFloor);

            lift.SetParent(elevatorBelow, worldPositionStays: true, sendImmediate: true);
            elevatorBelow.liftEntity = lift;
            topElevator.liftEntity = null;

            if (didStopMovement)
            {
                CancelHorseDropToGround(lift);
                RestartLiftMovement(elevatorBelow, Math.Min(targetFloor, elevatorBelow.Floor));
            }
        }

        private void OnEntityKill(ElevatorLift lift)
        {
            liftTimerActions.Remove(lift.net.ID);
        }

        private void RestartLiftMovement(Elevator elevator, int targetFloor)
        {
            var lift = elevator.liftEntity;
            if (lift != null)
            {
                lift.ToggleHurtTrigger(false);
            }

            elevator.ClearBusy();
            elevator.CancelInvoke(elevator.ClearBusy);

            var ioEntity = elevator.ioEntity;
            if (ioEntity != null)
            {
                ioEntity.SetFlag(BaseEntity.Flags.Busy, false);
                elevator.ioEntity.SendChangedToRoot(forceUpdate: true);
            }

            float timeToTravel;
            elevator.RequestMoveLiftTo(targetFloor, out timeToTravel);
        }

        private object OnElevatorMove(Elevator elevator, int targetFloor)
        {
            var lift = elevator.liftEntity;
            if (lift == null)
                return null;

            var liftFloor = elevator.LiftPositionToFloor();
            if (targetFloor == liftFloor)
                return null;

            Vector3 worldSpaceFloorPosition = elevator.GetWorldSpaceFloorPosition(targetFloor);
            Vector3 vector = elevator.transform.InverseTransformPoint(worldSpaceFloorPosition);

            var distance = Mathf.Abs(lift.transform.localPosition.y - vector.y);
            float timeToTravel = distance / elevator.LiftSpeedPerMetre;

            if (pluginConfig.enableSpeedOptions)
            {
                // Duplicating vanilla logic since this is replacing default movement
                if (elevator.IsBusy())
                    return false;
                if (elevator.ioEntity != null && !elevator.ioEntity.IsPowered())
                    return false;
                if (!elevator.IsValidFloor(targetFloor))
                    return false;
                if (!lift.CanMove())
                    return false;

                // Custom speed logic
                var speedConfig = GetPlayerSpeedConfig(elevator.OwnerID);
                elevator.LiftSpeedPerMetre = speedConfig.GetSpeedForLevels(Math.Abs(targetFloor - liftFloor));

                LeanTweenType leanTweenType;
                switch (speedConfig.GetEaseType())
                {
                    case EaseType.Quadratic:
                        timeToTravel = Convert.ToSingle(Math.Sqrt(distance)) / speedConfig.baseSpeed;
                        leanTweenType = LeanTweenType.easeInOutQuad;
                        break;

                    case EaseType.Cubic:
                        timeToTravel = Convert.ToSingle(Math.Pow(distance, 1.0 / 3.0)) / speedConfig.baseSpeed;
                        leanTweenType = LeanTweenType.easeInOutCubic;
                        break;

                    default:
                        timeToTravel = distance / elevator.LiftSpeedPerMetre;
                        leanTweenType = LeanTweenType.linear;
                        break;
                }

                LeanTween.moveLocalY(lift.gameObject, vector.y, timeToTravel).setEase(leanTweenType);

                // Duplicating vanilla logic since this is replacing default movement
                elevator.SetFlag(BaseEntity.Flags.Busy, true);
                if (targetFloor < elevator.Floor)
                {
                    lift.ToggleHurtTrigger(true);
                }
                elevator.Invoke(elevator.ClearBusy, timeToTravel);
                if (elevator.ioEntity != null)
                {
                    elevator.ioEntity.SetFlag(BaseEntity.Flags.Busy, true);
                    elevator.ioEntity.SendChangedToRoot(forceUpdate: true);
                }
            }

            if (GetLiftCounter(lift) != null && timeToTravel > 0)
                StartUpdatingLiftCounter(lift, timeToTravel);

            // Disable vanilla movement since we are using custom movement
            if (pluginConfig.enableSpeedOptions)
                return false;

            return null;
        }

        private void OnEntitySaved(Elevator elevator, BaseNetworkable.SaveInfo info)
        {
            // This is where the magic happens... thanks to @JakeRich
            if (!info.forDisk)
                info.msg.elevator.floor = 0;
        }

        private object OnCounterTargetChange(PowerCounter counter, BasePlayer player, int amount)
        {
            var lift = counter.GetParentEntity() as ElevatorLift;
            if (lift == null)
                return null;

            var elevator = lift.GetParentEntity() as Elevator;
            if (elevator == null)
                return null;

            // After this point, we return false to disable the default action since we know the counter is attached to an elevator
            if (elevator.IsBusy())
                return false;

            if (elevator.ioEntity == null || !elevator.ioEntity.IsPowered())
                return false;

            // The lift is parented to the top elevator so elevator.Floor is always the top floor
            var targetFloor = Math.Min(Math.Max(0, amount - 1), elevator.Floor);
            if (player.IsBuildingBlocked() || FloorSelectionWasBlocked(lift, player, targetFloor))
                return false;

            float unusedTimeToTravel;
            elevator.RequestMoveLiftTo(targetFloor, out unusedTimeToTravel);

            return false;
        }

        private object OnCounterModeToggle(PowerCounter counter, BasePlayer player, bool doShowPassthrough)
        {
            // Prevent lift counter from being toggled to "show passthrough" mode
            if (doShowPassthrough && IsLiftCounter(counter))
                return false;

            return null;
        }

        private object OnInputUpdate(ElevatorIOEntity ioEntity, int inputAmount)
        {
            if (ioEntity == null)
                return null;

            var topElevator = ioEntity.GetParentEntity() as Elevator;
            if (topElevator == null)
                return null;

            var lift = topElevator.liftEntity;

            // Update the power state of the lift counter to match elevator power state
            NextTick(() =>
            {
                // Ignore if the lift was destroyed, since it and the counter will be recreated elsewhere
                if (lift == null)
                    return;

                // Get the elevator again since the lift could have changed parent
                topElevator = lift.GetParentEntity() as Elevator;
                if (topElevator == null)
                    return;

                MaybeToggleLiftCounter(topElevator);
            });

            // Prevent powerless elevators from being powered down
            if (IsPowerlessElevator(topElevator))
            {
                ioEntity.SetFlag(IOEntity.Flag_HasPower, true);
                return false;
            }

            return null;
        }

        private object OnEntityTakeDamage(PowerCounter counter)
        {
            if (counter != null && IsLiftCounter(counter))
                return false;

            return null;
        }

        #endregion

        #region Helper Methods

        private bool FloorSelectionWasBlocked(ElevatorLift lift, BasePlayer player, int targetFloor)
        {
            object hookResult = Interface.CallHook("OnElevatorFloorSelect", lift, player, targetFloor);
            return hookResult is bool && (bool)hookResult == false;
        }

        private void CancelHorseDropToGround(ElevatorLift lift)
        {
            foreach (var child in lift.children)
            {
                var horse = child as RidableHorse;
                if (horse != null)
                    horse.Invoke(() => horse.CancelInvoke(horse.DelayedDropToGround), 0);
            }
        }

        private bool StopLiftMovement(ElevatorLift lift, Elevator topElevator, out int targetFloor)
        {
            var tweens = LeanTween.descriptions(lift.gameObject);
            targetFloor = 0;

            if (tweens.Length == 0)
                return false;

            // we only expect one tween to be running for each elevator at a time
            var tween = tweens[0];
            var originalLocalY = tween.to.x;
            targetFloor = topElevator.Floor + (int)((originalLocalY - ElevatorLiftLocalOffsetY) / ElevatorHeight);

            LeanTween.cancel(tween.uniqueId);
            return true;
        }

        private bool AllowLiftCounter(ulong ownerId) =>
            ownerId != 0 && permission.UserHasPermission(ownerId.ToString(), PermissionLiftCounter);

        private bool AllowPowerless(ulong ownerId) =>
            ownerId != 0 && permission.UserHasPermission(ownerId.ToString(), PermissionPowerless);

        private bool IsPowerlessElevator(Elevator elevator) =>
            AllowPowerless(elevator.OwnerID);

        private bool IsLiftCounter(PowerCounter counter) =>
            counter.GetParentEntity() is ElevatorLift;

        private void AddLiftCounter(ElevatorLift lift, int currentDisplayFloor, ulong ownerId)
        {
            var counter = GameManager.server.CreateEntity(PrefabPowerCounter, LiftCounterPosition, LiftCounterRotation) as PowerCounter;
            if (counter == null)
                return;

            counter.pickup.enabled = false;
            counter.OwnerID = ownerId;
            counter.SetParent(lift);
            RemoveGroundWatch(counter);
            counter.Spawn();

            if (AllowPowerless(ownerId))
                counter.SetFlag(IOEntity.Flag_HasPower, true);

            counter.counterNumber = currentDisplayFloor;
            counter.targetCounterNumber = currentDisplayFloor;
        }

        private void RemoveGroundWatch(BaseEntity entity)
        {
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
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

        private void StartUpdatingLiftCounter(ElevatorLift lift, float timeToTravel)
        {
            var liftCounter = GetLiftCounter(lift);
            if (liftCounter == null)
                return;

            Action existingTimerAction;
            if (liftTimerActions.TryGetValue(lift.net.ID, out existingTimerAction))
                lift.CancelInvoke(existingTimerAction);

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
                    liftTimerActions.Remove(lift.net.ID);
                }
            };
            lift.InvokeRepeating(timerAction, MaxCounterUpdateFrequency, MaxCounterUpdateFrequency);
            liftTimerActions[lift.net.ID] = timerAction;
        }

        private void UpdateFloorCounter(ElevatorLift lift, PowerCounter counter)
        {
            // Get the elevator on every update, since the lift can be re-parented
            var elevator = lift.GetParentEntity() as Elevator;
            if (elevator == null || counter == null)
                return;

            var floor = elevator.LiftPositionToFloor() + 1;

            if (counter.counterNumber == floor)
                return;

            counter.counterNumber = floor;
            counter.targetCounterNumber = floor;
            counter.SendNetworkUpdate();
        }

        private string GetSpeedPermission(string permissionName) => $"{PermissionSpeedPrefix}.{permissionName}";

        private string GetMaxFloorsPermission(int maxFloors) => $"{PermissionMaxFloorsPrefix}.{maxFloors}";

        private Elevator GetTopElevator(Elevator elevator) =>
            GetFarthestElevatorInDirection(elevator, Elevator.Direction.Up);

        private Elevator GetFarthestElevatorInDirection(Elevator elevator, Elevator.Direction direction)
        {
            var currentElevator = elevator;

            Elevator nextElevator;
            while ((nextElevator = currentElevator.GetElevatorInDirection(direction)) != null)
                currentElevator = nextElevator;

            return currentElevator;
        }

        private void MaybeToggleLiftCounter(Elevator elevator)
        {
            var lift = elevator.liftEntity;
            if (lift == null)
                return;

            var ioEntity = elevator.ioEntity as ElevatorIOEntity;
            if (ioEntity == null)
                return;

            var liftCounter = GetLiftCounter(lift);
            if (liftCounter == null)
                return;

            if (ioEntity.IsPowered())
                InitializeCounter(liftCounter, elevator.LiftPositionToFloor() + 1);
            else
                ResetCounter(liftCounter);
        }

        private void InitializeCounter(PowerCounter counter, int floor)
        {
            counter.SetFlag(IOEntity.Flag_HasPower, true);
            counter.counterNumber = floor;
            counter.targetCounterNumber = floor;
            counter.SendNetworkUpdate();
        }

        private void ResetCounter(PowerCounter counter)
        {
            counter.SetFlag(IOEntity.Flag_HasPower, false);
            counter.counterNumber = 0;
            counter.targetCounterNumber = 0;
            counter.SendNetworkUpdate();
        }

        #endregion

        #region Configuration

        private int GetPlayerMaxFloors(string userIdString)
        {
            if (pluginConfig.maxFloorsRequiringPermission == null || pluginConfig.maxFloorsRequiringPermission.Length == 0)
                return pluginConfig.defaultMaxFloors;

            for (var i = pluginConfig.maxFloorsRequiringPermission.Length - 1; i >= 0; i--)
            {
                var floorAmount = pluginConfig.maxFloorsRequiringPermission[i];
                if (permission.UserHasPermission(userIdString, GetMaxFloorsPermission(floorAmount)))
                    return floorAmount;
            }

            return pluginConfig.defaultMaxFloors;
        }

        private SpeedConfig GetPlayerSpeedConfig(ulong ownerId)
        {
            if (ownerId == 0 || pluginConfig.speedsRequiringPermission == null || pluginConfig.speedsRequiringPermission.Length == 0)
                return pluginConfig.defaultSpeed;

            var userIdString = ownerId.ToString();

            for (var i = pluginConfig.speedsRequiringPermission.Length - 1; i >= 0; i--)
            {
                var speedConfig = pluginConfig.speedsRequiringPermission[i];
                if (!string.IsNullOrWhiteSpace(speedConfig.name) &&
                    permission.UserHasPermission(userIdString, GetSpeedPermission(speedConfig.name)))
                {
                    return speedConfig;
                }
            }

            return pluginConfig.defaultSpeed;
        }

        internal class Configuration : SerializableConfiguration
        {
            [JsonProperty("DefaultMaxFloors")]
            public int defaultMaxFloors = VanillaMaxFloors;

            [JsonProperty("MaxFloorsRequiringPermission")]
            public int[] maxFloorsRequiringPermission = new int[] { 10, 15, 20, 100 };

            [JsonProperty("MaintainLiftPositionWhenHeightChanges")]
            public bool maintainLiftPositionWhenHeightChanges = false;

            [JsonProperty("EnsureConsistentOwner")]
            public bool ensureConsistentOwner = true;

            [JsonProperty("EnableSpeedOptions")]
            public bool enableSpeedOptions = true;

            [JsonProperty("DefaultSpeed")]
            public SpeedConfig defaultSpeed = new SpeedConfig()
            {
                baseSpeed = 1.5f
            };

            [JsonProperty("SpeedsRequiringPermission")]
            public SpeedConfig[] speedsRequiringPermission = new SpeedConfig[]
            {
                new SpeedConfig()
                {
                    name = "2x",
                    baseSpeed = 3f
                },
                new SpeedConfig
                {
                    name = "4x",
                    baseSpeed = 6
                },
                new SpeedConfig
                {
                    name = "1x.quadratic",
                    baseSpeed = 0.86f,
                    easeType = EaseType.Quadratic.ToString()
                },
                new SpeedConfig
                {
                    name = "1x.cubic",
                    baseSpeed = 0.72f,
                    easeType = EaseType.Cubic.ToString()
                },
            };
        }

        internal enum EaseType { Linear, Quadratic, Cubic }

        internal class SpeedConfig
        {
            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string name;

            [JsonProperty("BaseSpeed")]
            public float baseSpeed = 1.5f;

            [JsonProperty("SpeedIncreasePerFloor", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float speedPerAdditionalFloor = 0;

            [JsonProperty("MaxSpeed", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(1.5f)]
            public float maxSpeed = 1.5f;

            [JsonProperty("EaseType")]
            public string easeType = "Linear";

            public float GetSpeedForLevels(int levels) =>
                Math.Min(Math.Max(baseSpeed, maxSpeed), baseSpeed + (levels - 1) * speedPerAdditionalFloor);

            public EaseType GetEaseType()
            {
                EaseType parsedEaseType;
                return Enum.TryParse(easeType, out parsedEaseType) ? parsedEaseType : EaseType.Linear;
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

        internal class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        internal static class JsonHelper
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

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
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

        protected override void LoadDefaultConfig() => pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                pluginConfig = Config.ReadObject<Configuration>();
                if (pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(pluginConfig, true);
        }

        #endregion

        #region Localization

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(IPlayer player, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, player.Id);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Deploy.Error.NoPermissionToFloor"] = "Error: You don't have permission to build elevators taller than {0} floors.",
            }, this);
        }

        #endregion
    }
}
