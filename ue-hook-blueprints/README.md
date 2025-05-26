# `ui-hook-blueprints` hook functions in cooked blueprints

Inspired by https://github.com/bananaturtlesandwich/spaghetti

## Guide

- Create an unreal project with the same engine version as your cooked asset
- In unreal engine create an asset with the exact name and path of the blueprint you want to hook
  - To figure out the structure of the blueprint you want to hook use [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) to dump the CXX headers
- Create a function with the exact same signature as the function you want to hook, name it `hook_{NAME}` with `{NAME}` being the original name.
- To call the original function from your hooked function create a function with the exact same signature and name it `orig_{NAME}`, you can leave the function empty.
- You can access any function and variable inside your hooked function from the blueprint you're overriding, but you cannot add any new variables.
- Under "Project Settings" > "Packaging" disable "Use Pak File", "Use Io Store" and "Use Zen Store".
- Package your project using configuration "Shipping"
- Run this tool on your asset and the original game asset
- Use [repak](https://github.com/trumank/repak) or [retoc](https://github.com/trumank/retoc) to create a modded .pak or .utoc

## Usage

```sh
Description:
  Hook blueprint functions

Usage:
  ue-hook-blueprints <hook> <original> [options]

Arguments:
  <hook>      the hook-containing blueprint
  <original>  the original blueprint

Options:
  --output <output>                                                 blueprint output, default: overwrite hook
  --ueversion                                                       unreal engine version [default: VER_UE5_4]
  <UNKNOWN|VER_UE4_0|VER_UE4_1|...|VER_UE5_3|VER_UE5_4|VER_UE5_5>
  --mappings <mappings> (REQUIRED)                                  unversioned properties
  --version                                                         Show version information
  -?, -h, --help                                                    Show help and usage information
```