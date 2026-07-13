# Project Jumpstart Undertale Mod Manager (PJUM)

A cross-platform mod manager and merge engine for Undertale and Deltarune, built with Avalonia/MVVM on .NET 10. PJUM merges sprite, code, sound, object, and texture mods into a game's `data.win` and launches the result.

## Cloning (read this first)

PJUM pulls in `UndertaleModLib` as a **git submodule**, so a plain `git clone` leaves the engine folder empty and the build fails. So just clone with submodules:

```bash
git clone --recurse-submodules git@github.com:JAAYapps/Project-Jumpstart-Undertale-Mod-Manager.git
```

If you cloned the repo already, just pull the submodule and you should be fine:

```bash
git submodule update --init --recursive
```

## Requirements

- .NET 10 SDK
- Linux (developed on Solus), Windows 10/11 (God help you get through Microslop's garbage). macOS status: TODO

## Building

```bash
dotnet build "Project Jumpstart Undertale Mod Manager/Project Jumpstart Undertale Mod Manager.csproj"
```

## Running

```bash
dotnet run --project "Project Jumpstart Undertale Mod Manager/Project Jumpstart Undertale Mod Manager.csproj"
```

## Project layout

- `Project Jumpstart Undertale Mod Manager/` - the Avalonia app (UI, game/mod management)
- `Project Jumpstart Undertale Mod Manager.Core/` - merge engine and services (`ModMergeService`, importers, `TextureRepacker`)
- `UndertaleModTool/` - **submodule**; provides `UndertaleModLib`, the GameMaker data engine
- `ArchTests/` - architecture boundary tests (enforced via pre-commit hook)
- `MergeTests/` - merge test suite

## Status

What works: sprites / (code / sounds / objects not tested but passes the Unit tests and merge tests) / texture merging, Steam game detection, launch. What's in progress: project-system GUI, etc.

## Credits

Built on [UndertaleModTool](https://github.com/UnderminersTeam/UndertaleModTool) by the Underminers team.

PJUM by Joshua Vanderzee.

## License

Using the GPLv3
