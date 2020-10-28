using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Better Elevators", "WhiteThunder", "1.0.0")]
    [Description("Allows elevators to be taller, faster, powerless, and more.")]
    internal class BetterElevators : CovalencePlugin
    {
        #region Fields

        private const int VanillaMaxFloors = 6;
        private const float ElevatorHeight = 3;
        private const float ElevatorLiftLocalOffsetY = 1;

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

            if (!pluginConfig.maintainLiftPositionWhenHeightChanges)
            {
                Unsubscribe(nameof(OnEntityKill));
            }
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
            if (ShouldAllowLiftCounter(elevator.OwnerID) && lift.GetComponent<PowerCounter>() == null)
                AddLiftCounter(lift, elevator.LiftPositionToFloor() + 1, elevator.OwnerID);
        }

        private void OnEntitySpawned(ElevatorIOEntity ioEntity)
        {
            var elevator = ioEntity.GetParentEntity() as Elevator;
            if (elevator == null)
                return;

            var ownerId = elevator.OwnerID;
            if (ownerId == 0)
                return;

            if (permission.UserHasPermission(ownerId.ToString(), PermissionPowerless))
                ioEntity.SetFlag(BaseEntity.Flags.Reserved8, true);
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
                float originalLocalY, originalTime, speed;
                var didCancelAnimation = CancelTweenAndExtractDetails(lift, out originalLocalY, out originalTime, out speed);

                lift.SetParent(elevator, worldPositionStays: true, sendImmediate: true);
                elevator.liftEntity = lift;
                elevatorBelow.liftEntity = null;

                var powerCounter = lift.GetComponentInChildren<PowerCounter>();
                if (powerCounter != null && !permission.UserHasPermission(elevatorBelow.OwnerID.ToString(), PermissionPowerless))
                    powerCounter.SetFlag(BaseEntity.Flags.Reserved8, false);

                if (didCancelAnimation)
                {
                    var newLocalY = originalLocalY - ElevatorHeight;
                    MoveLift(elevator, lift, newLocalY, speed);
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

            float originalLocalY;
            float originalTime;
            float speed;
            bool didCancelAnimation = CancelTweenAndExtractDetails(lift, out originalLocalY, out originalTime, out speed);
            
            lift.SetParent(elevatorBelow, worldPositionStays: true, sendImmediate: true);
            elevatorBelow.liftEntity = lift;
            topElevator.liftEntity = null;

            var powerCounter = lift.GetComponentInChildren<PowerCounter>();
            if (powerCounter != null && !permission.UserHasPermission(elevatorBelow.OwnerID.ToString(), PermissionPowerless))
                powerCounter.SetFlag(BaseEntity.Flags.Reserved8, false);

            if (didCancelAnimation)
            {
                CancelHorseDropToGround(lift);

                var maxLocalY = ElevatorLiftLocalOffsetY;
                var floorDiff = topElevator.Floor - elevatorBelow.Floor;
                var newLocalY = Math.Min(originalLocalY + ElevatorHeight * floorDiff, maxLocalY);
                MoveLift(elevatorBelow, lift, newLocalY, speed);
            }
        }

        private void OnElevatorMove(Elevator elevator, int targetFloor)
        {
            var liftFloor = elevator.LiftPositionToFloor();
            if (targetFloor == liftFloor || !elevator.IsValidFloor(targetFloor))
                return;

            var speedConfig = GetPlayerSpeedConfig(elevator.OwnerID);
            elevator.LiftSpeedPerMetre = speedConfig.GetSpeedForLevels(Math.Abs(targetFloor - liftFloor));

            var worldSpaceFloorPosition = elevator.GetWorldSpaceFloorPosition(targetFloor);
            var vector = elevator.transform.InverseTransformPoint(worldSpaceFloorPosition);
            var distance = Mathf.Abs(elevator.liftEntity.transform.localPosition.y - vector.y);
            StartMoveLiftTimer(elevator.liftEntity, distance, elevator.LiftSpeedPerMetre);
        }

        private void OnElevatorSaved(Elevator elevator, BaseNetworkable.SaveInfo info)
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

            if (elevator.ioEntity == null || !elevator.ioEntity.IsPowered() && !permission.UserHasPermission(elevator.OwnerID.ToString(), PermissionPowerless))
                return false;

            // The lift is parented to the top elevator so elevator.Floor is always the top floor
            var targetFloor = Math.Min(Math.Max(0, amount - 1), elevator.Floor);
            if (player.IsBuildingBlocked() || FloorSelectionWasBlocked(lift, player, targetFloor))
                return false;

            float unusedTimeToTravel;
            elevator.RequestMoveLiftTo(targetFloor, out unusedTimeToTravel);

            return false;
        }

        // Prevent lift counter from being toggled to "show passthrough" mode
        private object OnCounterModeToggle(PowerCounter counter, BasePlayer player, bool doShowPassthrough)
        {
            var counterParent = counter.GetParentEntity();
            if (doShowPassthrough && counterParent is ElevatorLift)
                return false;

            return null;
        }

        // Prevent lift counter from being powered down via the wire tool
        private object OnInputUpdate(PowerCounter counter)
        {
            if (counter.GetParentEntity() is ElevatorLift)
                return false;

            return null;
        }

        private object OnInputUpdate(ElevatorIOEntity ioEntity, int inputAmount)
        {
            if (ioEntity == null)
                return null;

            var elevator = ioEntity.GetParentEntity() as Elevator;
            if (elevator == null || elevator.OwnerID == 0)
                return null;

            var powerless = permission.UserHasPermission(elevator.OwnerID.ToString(), PermissionPowerless);
            if (powerless)
                ioEntity.SetFlag(BaseEntity.Flags.Reserved8, true);

            var lift = elevator.liftEntity;
            if (lift == null)
                return null;

            // Have the counter follow the elevator's power state
            var powerCounter = lift.GetComponentInChildren<PowerCounter>();
            if (powerCounter != null)
                powerCounter.SetFlag(BaseEntity.Flags.Reserved8, powerless || inputAmount > 0 && inputAmount >= ioEntity.ConsumptionAmount());

            // Prevent powerless elevators from being powered down via the wire tool
            if (powerless)
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

        private bool CancelTweenAndExtractDetails(ElevatorLift lift, out float originalLocalY, out float originalTime, out float speed)
        {
            var tweens = LeanTween.descriptions(lift.gameObject);
            originalLocalY = 0;
            originalTime = 0;
            speed = 0;

            if (tweens.Length == 0)
                return false;

            // we only expect one tween to be running for each elevator at a time
            var tween = tweens[0];

            originalLocalY = tween.to.x;
            originalTime = tween.time;

            var remainingDistance = Math.Abs(lift.transform.localPosition.y - originalLocalY);
            var originalDistance = remainingDistance / (1 - tween.ratioPassed);
            speed = originalDistance / tween.time;

            LeanTween.cancel(tween.uniqueId);
            return true;
        }

        private bool ShouldAllowLiftCounter(ulong ownerId) =>
            ownerId != 0 && permission.UserHasPermission(ownerId.ToString(), PermissionLiftCounter);

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

            if (permission.UserHasPermission(ownerId.ToString(), PermissionPowerless))
                counter.SetFlag(BaseEntity.Flags.Reserved8, true);

            counter.counterNumber = currentDisplayFloor;
            counter.targetCounterNumber = currentDisplayFloor;
        }

        private void RemoveGroundWatch(BaseEntity entity)
        {
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
        }

        private void MoveLift(Elevator newTopElevator, ElevatorLift lift, float localY, float speed)
        {
            var distance = Math.Abs(lift.transform.localPosition.y - localY);
            var travelTime = distance / speed;
            LeanTween.moveLocalY(lift.gameObject, localY, travelTime);

            newTopElevator.SetFlag(BaseEntity.Flags.Busy, true);
            newTopElevator.Invoke(newTopElevator.ClearBusy, travelTime);

            StartMoveLiftTimer(lift, distance, speed);
        }

        private void StartMoveLiftTimer(ElevatorLift lift, float distance, float speed)
        {
            Action existingTimerAction;
            if (liftTimerActions.TryGetValue(lift.net.ID, out existingTimerAction))
                lift.CancelInvoke(existingTimerAction);

            var remainderDistance = distance % ElevatorHeight;
            var timePerFloor = ElevatorHeight / speed;
            var floorsToMove = (int)Math.Ceiling(distance / ElevatorHeight);

            var floorsMoved = 0;
            var timeToNextFloor = remainderDistance > 0 ? remainderDistance / speed : timePerFloor;

            Action timerAction = null;
            timerAction = () =>
            {
                UpdateFloorCounter(lift);
                floorsMoved++;
                if (floorsMoved >= floorsToMove)
                    lift.CancelInvoke(timerAction);
            };
            lift.InvokeRepeating(timerAction, timeToNextFloor, timePerFloor);
            liftTimerActions[lift.net.ID] = timerAction;
        }

        private void UpdateFloorCounter(ElevatorLift lift)
        {
            var counter = lift.GetComponentInChildren<PowerCounter>();
            if (counter == null)
                return;

            var elevator = lift.GetParentEntity() as Elevator;
            if (elevator == null)
                return;

            var floor = elevator.LiftPositionToFloor() + 1;

            counter.counterNumber = floor;
            counter.targetCounterNumber = floor;
            counter.SendNetworkUpdate();
        }

        private string GetSpeedPermission(string permissionName) => $"{PermissionSpeedPrefix}.{permissionName}";

        private string GetMaxFloorsPermission(int maxFloors) => $"{PermissionMaxFloorsPrefix}.{maxFloors}";

        private int GetElevatorMaxFloors(Elevator elevator)
        {
            var ownerId = elevator.OwnerID;
            if (ownerId == 0)
                return pluginConfig.defaultMaxFloors;

            return GetPlayerMaxFloors(elevator.OwnerID.ToString());
        }

        private Elevator GetTopElevator(Elevator elevator)
        {
            var currentElevator = elevator;

            Elevator nextElevator;
            while ((nextElevator = currentElevator.GetElevatorInDirection(Elevator.Direction.Up)) != null)
                currentElevator = nextElevator;

            return currentElevator;
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

            [JsonProperty("DefaultSpeed")]
            public SpeedConfig defaultSpeed = new SpeedConfig()
            {
                baseSpeed = 1.5f,
                speedPerAdditionalFloor = 0,
                maxSpeed = 1.5f,
            };

            [JsonProperty("SpeedsRequiringPermission")]
            public SpeedConfig[] speedsRequiringPermission = new SpeedConfig[]
            {
                new SpeedConfig()
                {
                    name = "2x",
                    baseSpeed = 3f,
                    maxSpeed = 3,
                },
                new SpeedConfig
                {
                    name = "4x",
                    baseSpeed = 6,
                    maxSpeed = 6,
                },
                new SpeedConfig
                {
                    name = "2-4x",
                    baseSpeed = 3f,
                    speedPerAdditionalFloor = 1,
                    maxSpeed = 6,
                },
                new SpeedConfig
                {
                    name = "2-8x",
                    baseSpeed = 3f,
                    speedPerAdditionalFloor = 1,
                    maxSpeed = 12,
                },
            };
        }

        internal class SpeedConfig
        {
            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string name;

            [JsonProperty("BaseSpeed")]
            public float baseSpeed = 1.5f;

            [JsonProperty("SpeedIncreasePerFloor")]
            public float speedPerAdditionalFloor = 0;

            [JsonProperty("MaxSpeed")]
            public float maxSpeed = 1.5f;

            public float GetSpeedForLevels(int levels) =>
                Math.Min(Math.Max(baseSpeed, maxSpeed), baseSpeed + (levels - 1) * speedPerAdditionalFloor);
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
