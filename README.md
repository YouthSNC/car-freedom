# Car Freedom

A code mod for **Cities: Skylines II** that gives every resident household its own personal car,
spawned at the household's home so it is never parked far away.

The mod is published on Paradox Mods: <https://mods.paradoxplaza.com/mods/163257/Windows>

> **This mod was written with AI assistance and has not been extensively tested.**
> Back up your save before using it.

## What it does

The vanilla game only gives a household a car if it decides to buy one, and that purchase has to
travel through the city's economy and parking systems. Plenty of households end up with no car at
all, or park so far from home that walking is the faster option.

This mod periodically scans resident households and grants a car to every household that does not
have one. The car is created by the **same call the vanilla purchase flow uses**
(`PersonalCarSelectData.CreateVehicle`), so ownership, parking, driving and pathfinding are all
handled by the game itself. The only difference is the spawn transform: the car starts at the
household's own home instead of at a dealership.

Because everything after the spawn is vanilla, the mod stays compatible with traffic and AI mods.

## Settings

`Options > Mods > Car Freedom`

| Option | Range | Default | Meaning |
|---|---|---|---|
| Minimum household members | 1 - 20 | 3 | Households with fewer members are skipped entirely - no car, and they are not counted as carless either. Set to 1 for the pre-1.2 behaviour (every household). |

Changes apply on the next simulation pass; no reload or restart needed. Cars that were already
granted are never removed when the threshold is raised.

## Requirements

- Cities: Skylines II 1.6.x
- No dependencies on other mods

## Building

The project targets `net48` and references the assemblies in the game's `Managed` folder.

```
dotnet build -c Release
```

The game install directory defaults to `D:\SteamLibrary\steamapps\common\Cities Skylines II`.
Override it if yours is elsewhere:

```
dotnet build -c Release /p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II"
```

The output is `bin/Release/HouseholdCars.dll`. To load it locally, put the dll in

```
%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\HouseholdCars\
```

and make sure the same mod is not also subscribed from Paradox Mods - an assembly present in two
mod locations triggers `IsNotUniqueWarning` and the whole mod is rejected.

## How the scan works

- `GameSimulation` phase, every 64 frames.
- Households are read through chunk-native arrays for the components the query already guarantees
  (`Household`, `PropertyRenter`, `HouseholdCitizen`), so the common case costs no random lookups.
- The ownership test runs before anything expensive; in a mature city it rejects almost everything.
- At most 32 cars are created per tick, so a large existing save fills up over a few game days.
- When a pass finds nothing to do, the next 15 passes return immediately.

## Notes on the vanilla "wants to buy a car" state

Earlier versions skipped households that carried a vanilla `HouseholdNeed` of `Resource.Vehicles`,
on the assumption that vanilla would handle them. That state can never resolve:

1. `HouseholdBehaviorSystem` never re-evaluates a need whose `m_Resource` is not `NoResource`, so it
   is sticky until it is actually served.
2. The need is only created when spendable money exceeds `kCarBuyingMinimumMoney` (10,000), but
   `ResourceBuyerSystem` caps the payment at `(money - kMinimumShoppingMoney) / (marketPrice * 1.4)`.
   When that cap falls below `kCarAmount` (50) the sale still completes but the car spawn is guarded
   by `item.m_Amount == kCarAmount`, so the household pays and receives nothing - then retries.
3. The car is only created when the *seller* has a `PropertyRenter`, i.e. a real occupied dealership.

Those households are now handled by the mod.

## Layout

```
HouseholdCars/
  HouseholdCars.csproj        build definition
  Mod.cs                      IMod entry point: settings + system registration
  Setting.cs                  settings page definition and its English strings
  HouseholdCarGrantSystem.cs  the scan and the vehicle spawn
  Properties/
    PublishConfiguration.xml  Paradox Mods publishing metadata
    Thumbnail.png             store image
  publish.cmd                 pushes a new version to Paradox Mods
```

## Disclaimer

Provided as-is, with no warranty of any kind. This is a hobby project that writes vehicles into
your save file through the game's own ECS pipeline. If that worries you, back up the save first.
