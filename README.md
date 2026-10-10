# TimberbornMods

Timberborn mods, one folder per mod. Build one with:

    dotnet build <Mod>/code -p:GameManaged=<path to>/Timberborn_Data/Managed

GameManaged defaults to the standard Linux Steam install path. The python
generators under <Mod>/tools produce the models and icons.
