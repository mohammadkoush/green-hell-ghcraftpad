# GHCraftPad

**Craft from the boxes around you, off a pad beside the crafting table.**

Green Hell's crafting table has no "choose a recipe" step: you drop items on it and it shows what
they make, and everything has to be in your backpack first. Crafting at camp means walking to each
box, taking things out, walking back.

Open the crafting table and a pad appears beside it. It lists every recipe you know, and what you
can make right now from three places counted together: what is already on the table, what is in your
backpack, and what is in every storage box within a radius of you. Roll the mouse wheel over a
recipe to choose how many; click it, and the ingredients are pulled onto the table — table first,
then backpack, then the nearest boxes — and the game's own crafting starts. The game makes the item,
exactly as if you had dragged the parts.

**Never a duplicate.** Every pull moves the same item out of its box and onto the table — never a
copy. After every pull the mod counts that item across table, backpack and boxes and compares it
with the count before; any difference is written to the log as an error.

**What goes back.** Every borrowed item remembers its box. Close the table without crafting and the
borrowed items still on the table go back where they came from, not into your backpack. An item you
moved into your backpack by hand is yours and stays. Nothing is ever taken out of your backpack.

**A crafted item that does not fit** in your backpack goes on the ground in front of you, instead of
being left on the table where it is easy to miss.

## Install

Needs [BepInEx 5](https://github.com/BepInEx/BepInEx) (x64). Drop `GHCraftPad.dll` into
`Green Hell\BepInEx\plugins\GHCraftPad\`. Single player.

## Settings

`BepInEx/config/com.mohammadkoush.ghcraftpad.cfg`, written on first run.

| | |
|---|---|
| `Pad.BoxRadiusMetres` | how far around you boxes count (default 15) — also a slider on the pad |
| `Pad.ShowRecipesNotYetLearned` | list locked recipes greyed (default off) |
| `Pad.CraftedItemToGroundIfNoRoom` | default on |

## Build

`powershell -ExecutionPolicy Bypass -File build.ps1` — stock .NET Framework `csc.exe`, references
from the game install. `-NoDeploy` builds without installing. MIT licensed.
