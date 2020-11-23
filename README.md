## Features

- Build elevators taller than 6 floors **without players running commands**. Supports a global default limit, as well as multiple permission-based limits.
- Powerless elevators for players with permission.
- Adjustable speed, including smooth acceleration. Supports a configuable default speed preset, as well as multiple configurable permission-based speed presets.
- Optionally keep the lift in its current position as elevators are added or removed above it.
- Optionally attach a counter to each elevator lift, if the owner has permission, which shows the current floor and allows players to select a destination floor.

## Permissions

- `betterelevators.powerless` -- Elevators deployed by players with this permission do not require power.
- `betterelevators.liftcounter` -- Elevators deployed by players with this permission automatically have a counter attached to the lift which shows the current floor and can be used to select a destination floor.
  - Cannot be used while building blocked, due to client-side limitations.
  - Cannot be used while the lift does not have power, unless the elevator is powerless.
  - Cannot be used to select a floor higher than 100, due to game limitations.

### Max floor permissions

Each number in the `MaxFloorsRequiringPermission` configuration option automatically generates a permission of the format `betterelevators.maxfloors.<number>`. Granting one to a player allows them to build elevators with at most that many floors. Granting multiple of these permissions to a player will cause only the last one to apply (based on the order in the config).

The following permissions come with this plugin's **default configuration**. You can add more max floor presets in the plugin configuration.
- `betterelevators.maxfloors.10`
- `betterelevators.maxfloors.15`
- `betterelevators.maxfloors.20`
- `betterelevators.maxfloors.100`

Note: The permission is checked when you try to deploy the elevator, meaning you can build on top of any elevator to your allowed height, even if the original owner did not have permission to build as high as you can.

### Speed permissions

Each preset in the `SpeedsRequiringPermission` configuration option automatically generates a permission of format `betterelevators.speed.<name>`. Granting one to a player causes elevators they deploy to move at the configured speed. Granting multiple of these permissions to a player will cause only the last one to apply (based on the order in the config).

The following permissions come with this plugin's **default configuration**. You can add more speed presets in the plugin configuration.
- `betterelevators.speed.2x`
- `betterelevators.speed.4x`
- `betterelevators.speed.1x.quadratic`
- `betterelevators.speed.1x.cubic`

The quadratic (x²) and cubic (x³) presets use smooth acceleration/deceleration to travel long distances more quickly. The `1x.quadratic` and `1x.cubic` presets are configured to take the same amount of time as vanilla elevators when moving only one floor at a time, but they will be much more time efficient when moving multiple floors at once (e.g., when using the "To Top" and "To Bottom" buttons, or when using the lift counter).

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
      "Name": "1x.cubic",
      "BaseSpeed": 0.72,
      "EaseType": "Cubic"
    }
  ]
}
```

- `DefaultMaxFloors` -- The max number of floors allowed per elevator, unless the player building it has been granted permissions for a different number of max floors.
- `MaxFloorsRequiringPermission` -- List of numbers used to automatically generate permissions of the format `betterelevators.maxfloors.<number>`. Granting one to a player allows them to build elevators with at most that many floors.
- `MaintainLiftPositionWhenHeightChanges` (`true` or `false`) -- While `true`, causes the lift to keeps its position and movement destination when an elevator is added or removed above it. This avoids the annoying vanilla behavior where the lift is destroyed and rebuilt at the top every time the height changes.
  - Tip: You can combine this behavior with powerless elevators to continuously build upward by alternating between deploying an elevator above you and moving the lift upward.
- `EnsureConsistentOwner` (`true` or `false`) -- While `true`, deploying an elevator on top of another will assign the new elevator's `OwnerID` to the same value as the one below it, instead of using the deploying player's steam id. This improves the predictability of permission-based features, especially speed, by effectively ensuring that the player who placed the bottom elevator determines the elevator's capabilities.
- `EnableSpeedOptions` (`true` or `false`) -- Must be `true` for the `DefaultSpeed` and `SpeedsRequiringPermission` options to apply. You may set this to `false` to disable this plugin's speed features, if you desire to use other plugins to control elevator speed. Note: If you disable speed features after using them for a bit, some elevators may still have their speed altered.
- `DefaultSpeed` -- This speed preset applies to all elevators except those belonging to players with additional permissions.
  - `BaseSpeed` -- Base movement speed (vanilla is `1.5`). If acceleration is used, this is applied afterwards as a multiplier.
  - `EaseType` (`"Linear"`, `"Quadratic"`, or `"Cubic"`)
    - Set to `"Linear"` (default) to cause the lift to move at a constant speed (`BaseSpeed`).
    - Set to `"Quadratic"` to cause the lift to accelerate/decelerate (x² speed).
    - Set to `"Cubic"` to cause the lift to accelerate/decelerate even faster (x³ speed).
- `SpeedsRequiringPermission` -- List of speed configurations for use with permissions. A permission is automatically generated for each entry using the format `betterelevators.speed.<name>`. Granting one to a player causes elevators they deploy to move at the configured speed.
  - Note: These presets have the same options as the `DefaultSpeed` configuration option.

### Legacy speed options

Each speed preset still allows the following options for backwards compatibility. These may be removed in a future version. It is **not recommended** to use these options with `EaseType` set to `"Quadratic"` or `"Cubic"` because that will not work like you probably expect.

- `SpeedIncreasePerFloor` -- This causes the elevator to have dynamically calculated constant speed based on the number of floors it plans to move. This does not cause acceleration.
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
