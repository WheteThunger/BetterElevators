**Requires Oxide v2.0.4816 or later. You can check your current version with `o.version` in the server console.**

## Features

- Build elevators taller than 6 floors **without players running commands**. Supports a global default limit, as well as multiple permission-based limits.
- Powerless elevators for players with permission.
- Adjustable speed, including dynamic speed based on distance being travelled. Supports a global default speed, as well as multiple permission-based speed configurations.
- Optionally keep the lift in its current position as elevators are added or removed above it.
- Optionally attach counters to elevator lifts, for players with permission, which show the current floor and allow players to select a destination floor.

## Permissions

- `betterelevators.powerless` -- Elevators deployed by players with this permission do not require power.
- `betterelevators.liftcounter` -- Elevators deployed by players with this permission automatically have a counter attached to the lift which shows the current floor and can be used to select a destination floor.
  - Cannot be used while building blocked, due to client-side limitations.
  - Cannot be used while the lift does not have power, unless the elevator is powerless.
  - Cannot be used to select a floor higher than 100, or display higher than 100, due to game limitations.

### Max floor permissions

Each number in the `MaxFloorsRequiringPermission` configuration option automatically generates a permission of the format `betterelevators.maxfloors.<number>`. Granting one to a player allows them to build elevators with at most that many floors. Granting multiple of these permissions to a player will cause only the last one to apply (based on the order in the config).

The following permissions come with this plugin's **default configuration**. You can add more max floor presets in the plugin configuration.
- `betterelevators.maxfloors.10`
- `betterelevators.maxfloors.15`
- `betterelevators.maxfloors.20`
- `betterelevators.maxfloors.100`

Note: The permission is checked when you try to deploy the elevator, meaning you can build on top of any elevator to your allowed height, even if the original owner did not have permission to build as high as you can.

### Speed permissions

Each entry in the `SpeedsRequiringPermission` configuration option automatically generates a permission of format `betterelevators.speed.<name>`. Granting one to a player causes elevators they deploy to move at the configured speed. Granting multiple of these permissions to a player will cause only the last one to apply (based on the order in the config).

The following permissions come with this plugin's **default configuration**. You can add more speed presets in the plugin configuration.
- `betterelevators.speed.2x`
- `betterelevators.speed.4x`
- `betterelevators.speed.2-4x`
- `betterelevators.speed.2-8x`

See the configuration section for more details about how dynamic speeds like `2-4x` work.

Note: Speed permissions are based on the owner of the top elevator. It's recommended to use the `EnsureConsistentOwner` configuration option (on by default) so that each elevator always copies the owner from the one below it for more predictable behavior.

## Configuration

Default configuration:

```json
{
  "DefaultMaxFloors": 6,
  "MaxFloorsRequiringPermission": [
    10,
    15,
    20,
    100
  ],
  "MaintainLiftPositionWhenHeightChanges": false,
  "EnsureConsistentOwner": true,
  "EnableSpeedOptions": true,
  "DefaultSpeed": {
    "BaseSpeed": 1.5,
    "SpeedIncreasePerFloor": 0.0,
    "MaxSpeed": 1.5
  },
  "SpeedsRequiringPermission": [
    {
      "Name": "2x",
      "BaseSpeed": 3.0,
      "SpeedIncreasePerFloor": 0.0,
      "MaxSpeed": 3.0
    },
    {
      "Name": "4x",
      "BaseSpeed": 6.0,
      "SpeedIncreasePerFloor": 0.0,
      "MaxSpeed": 6.0
    },
    {
      "Name": "2-4x",
      "BaseSpeed": 3.0,
      "SpeedIncreasePerFloor": 1.0,
      "MaxSpeed": 6.0
    },
    {
      "Name": "2-8x",
      "BaseSpeed": 3.0,
      "SpeedIncreasePerFloor": 1.0,
      "MaxSpeed": 12.0
    }
  ]
}
```

- `DefaultMaxFloors` -- The max number of floors that players may build elevators, unless they have been granted permissions for a different number of max floors.
- `MaxFloorsRequiringPermission` -- List of numbers used to automatically generate permissions of the format `betterelevators.maxfloors.<number>`. Granting one to a player allows them to build elevators with at most that many floors.
- `MaintainLiftPositionWhenHeightChanges` (`true` or `false`) -- While `true`, causes the lift to keeps its position and velocity when an elevator is added or removed above it. This avoids the annoying vanilla behavior where the lift is destroyed and rebuilt at the top every time the height changes.
  - Tip: You can combine this behavior with powerless elevators to continuously build upward by alternating between deploying an elevator above you and moving the lift.
- `EnsureConsistentOwner` (`true` or `false`) -- While `true`, deploying an elevator on top of another will assign the new elevator's `OwnerID` to the same value as the one below it, instead of using the deploying player's steam id. This improves the predictability of permission-based features, especially speed, by effectively ensuring that the player who placed the bottom elevator determines the elevator's capabilities.
- `EnableSpeedOptions` (`true` or `false`) -- Must be `true` for the `DefaultSpeed` and `SpeedsRequiringPermission` options to apply. You may set this to `false` to disable this plugin's speed features, if you desire to use other plugins to control elevator speed. Note: If you disable speed features after using them for a bit, some elevators may still have their speed altered.
- `DefaultSpeed` -- This speed applies to all elevators except those belonging to players with additional permissions.
  - `BaseSpeed` -- Minimum speed that the lift will move regardless of the number of floors being travelled.
  - `SpeedIncreasePerFloor` -- This causes the elevator to have dynamically calculated speed based on the number of floors being travelled at once, allowing players to travel long distances more quickly. Setting this to the same value as `BaseSpeed`, with a sufficiently high `MaxSpeed`, will cause the elevator to always take the same amount of time to travel any distance.
    - Travelling 1 floor will use `BaseSpeed`.
    - Travelling 2 floors at once will use `BaseSpeed + SpeedIncreasePerFloor`.
    - Travelling 3 floors will use `BaseSpeed + (2 * SpeedIncreasePerFloor)`.
    - And so on...
  - `MaxSpeed` -- Maximum speed that the elevator lift can reach when using dynamic speed.
- `SpeedsRequiringPermission` -- List of speed configurations for use with permissions. A permission is automatically generated for each entry using the format `betterelevators.speed.<name>`. Granting one to a player causes elevators they deploy to move at the configured speed.
  - Note: These configurations have the same options as the `DefaultSpeed` configuration option.

## Localization

```json
{
  "Deploy.Error.NoPermissionToFloor": "Error: You don't have permission to build elevators taller than {0} floors."
}
```

## Developer Hooks

#### OnElevatorFloorSelect

- Called when a player has selected a destination floor with a lift counter.
- Returning `false` will prevent the elevator from moving.
- Returning `null` will result in the default behavior.

```csharp
object OnElevatorFloorSelect(ElevatorLift lift, BasePlayer player, int targetFloor)
```

Note: The `targetFloor` is 0-based, so if the player enters `1`, `targetFloor` will be `0`.
