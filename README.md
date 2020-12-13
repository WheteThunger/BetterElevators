## Features

- Build elevators taller than 6 floors without players running commands. Limit can be configured globally or  based on permissions.
- Powerless elevators, optionally based on permission.
- Configurable elevator speed, with optional acceleration and deceleration. Speed can be configured globally or based on permissions.
- Optionally keep the lift in its current position as elevators are added or removed above it.
- Optionally attach a counter to each elevator lift, which shows the current floor and allows players to select a destination floor.

## Permissions

- `betterelevators.powerless` -- Elevators deployed by players with this permission do not require power.
  - Permission not required if you have set `"RequirePermissionForPowerless": false` in the plugin configuration.
- `betterelevators.liftcounter` -- Elevators deployed by players with this permission automatically have a counter attached to the lift which shows the current floor and can be used to select a destination floor.
  - Permission not required if you have set `"RequirePermissionForLiftCounter": false` in the plugin configuration.
  - The counter cannot be used while building blocked, or used to select a floor higher than 100, due to client-side limitations.

### Max floor permissions

The following permissions come with this plugin's **default configuration**. Granting one to a player allows them to build elevators with at most that many floors.
- `betterelevators.maxfloors.10`
- `betterelevators.maxfloors.15`
- `betterelevators.maxfloors.20`
- `betterelevators.maxfloors.100`

You can add more max floor presets in the plugin configuration (`MaxFloorsRequiringPermission`), and the plugin will automatically generate permissions of the format `betterelevators.maxfloors.<number>` when reloaded. If a player has permission to multiple max floor presets, only the last one will apply (based on the order in the config).

Note: The permission is checked when you try to deploy the elevator, meaning you can build on top of any elevator to your allowed height, even if the original owner did not have permission to build as high as you can.

### Speed permissions

The following permissions come with this plugin's **default configuration**. Granting one to a player alters the speed of elevators they deploy.

- `betterelevators.speed.2x`
- `betterelevators.speed.4x`
- `betterelevators.speed.1x.quadratic`
- `betterelevators.speed.1.5x.quadratic`
- `betterelevators.speed.2x.quadratic`
- `betterelevators.speed.1x.cubic`
- `betterelevators.speed.2x.cubic`

You can add more speed presets in the plugin configuration (`SpeedsRequiringPermission`), and the plugin will automatically generate permissions of the format `betterelevators.speed.<name>` when reloaded. If a player has permission to multiple speed presets, only the last one will apply (based on the order in the config).

The quadratic (x²) and cubic (x³) presets will accelerate and decelerate to travel long distances more quickly. The `1x.quadratic` and `1x.cubic` presets are configured to take the same amount of time as vanilla elevators when moving only one floor at a time, but they will be much more time efficient when moving multiple floors at once (e.g., when using the "To Top" and "To Bottom" buttons, or when using the lift counter to move to a specific floor).

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
  "RequirePermissionForPowerless": true,
  "RequirePermissionForLiftCounter": true,
  "MaintainLiftPositionWhenHeightChanges": false,
  "EnsureConsistentOwner": true,
  "EnableSpeedOptions": true,
  "DefaultSpeed": {
    "BaseSpeed": 1.5,
    "EaseType": "Linear"
  },
  "SpeedsRequiringPermission": [
    {
      "Name": "2x",
      "BaseSpeed": 3.0,
      "EaseType": "Linear"
    },
    {
      "Name": "4x",
      "BaseSpeed": 6.0,
      "EaseType": "Linear"
    },
    {
      "Name": "1x.quadratic",
      "BaseSpeed": 0.86,
      "EaseType": "Quadratic"
    },
    {
      "Name": "1.5x.quadratic",
      "BaseSpeed": 1.29,
      "EaseType": "Quadratic"
    },
    {
      "Name": "2x.quadratic",
      "BaseSpeed": 1.72,
      "EaseType": "Quadratic"
    },
    {
      "Name": "1x.cubic",
      "BaseSpeed": 0.72,
      "EaseType": "Cubic"
    },
    {
      "Name": "2x.cubic",
      "BaseSpeed": 1.44,
      "EaseType": "Cubic"
    }
  ]
}
```

- `DefaultMaxFloors` -- The max number of floors allowed per elevator, unless the player building it has been granted permissions for a different number of max floors.
- `MaxFloorsRequiringPermission` -- List of numbers used to automatically generate permissions of the format `betterelevators.maxfloors.<number>`. Granting one to a player allows them to build elevators with at most that many floors.
- `RequirePermissionForPowerless` (`true` or `false`) -- While `true`, players must have the `betterelevators.powerless` permission for their elevators to not require power. While `false`, no elevators require power.
- `RequirePermissionForLiftCounter` (`true` or `false`) -- While `true`, players must have the `betterelevators.liftcounter` permission for their elevators to spawn with a lift counter. While `false`, all elevator lifts spawn with a counter.
- `MaintainLiftPositionWhenHeightChanges` (`true` or `false`) -- While `true`, causes the lift to keeps its position and movement destination when an elevator is added or removed above it. This avoids the annoying vanilla behavior where the lift is destroyed and rebuilt at the top every time the height changes.
  - Tip: You can combine this behavior with powerless elevators to continuously build upward by alternating between deploying an elevator above you and moving the lift upward.
- `EnsureConsistentOwner` (`true` or `false`) -- While `true`, deploying an elevator on top of another will assign the new elevator's `OwnerID` to the same value as the elevator below it, instead of using the deploying player's steam id. This improves the predictability of permission-based features, especially speed, by effectively ensuring that the player who placed the bottom elevator determines the elevator's capabilities.
- `EnableSpeedOptions` (`true` or `false`) -- Must be `true` for the `DefaultSpeed` and `SpeedsRequiringPermission` options to apply. You may set this to `false` to disable this plugin's speed features, if you desire to use other plugins to control elevator speed.
- `DefaultSpeed` -- This speed preset applies to all elevators except those belonging to players that were granted access to presets in `SpeedsRequiringPermission`.
  - `BaseSpeed` -- Base movement speed (vanilla is `1.5`). If acceleration is used, the total travel time is divided by this number instead.
  - `EaseType` (`"Linear"`, `"Quadratic"`, or `"Cubic"`)
    - Set to `"Linear"` (vanilla) to cause the lift to move at a constant speed (the value of `BaseSpeed`).
    - Set to `"Quadratic"` (recommended) to cause the lift to accelerate/decelerate (x² speed).
    - Set to `"Cubic"` to cause the lift to accelerate/decelerate even faster (x³ speed).
- `SpeedsRequiringPermission` -- List of speed presets for use with permissions. A permission is automatically generated for each entry using the format `betterelevators.speed.<name>`. Granting one to a player causes elevators they deploy to move at the configured speed.
  - Note: These presets have the same options as `DefaultSpeed`.

### Legacy speed options

Each speed preset still allows the following options for backwards compatibility. These may be removed in a future version. **These options only work for `EaseType: "Linear"`**, and that is unlikely to change in a future version.

- `SpeedIncreasePerFloor` -- This causes the elevator speed to be dynamically calculated as it starts to move, based on the distance from the destination floor. This does not cause acceleration.
  - Travelling 1 floor will use `BaseSpeed`.
  - Travelling 2 floors at once will use `BaseSpeed + SpeedIncreasePerFloor`.
  - Travelling 3 floors will use `BaseSpeed + (2 * SpeedIncreasePerFloor)`.
  - And so on...
- `MaxSpeed` -- Maximum speed that the elevator lift can reach when factoring in `SpeedIncreasePerFloor`.

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
