@echo off
rem Opens the designer (authoring build) on a planet tab. The planet designer is now a workspace inside BRUTALDesigner;
rem this is a shortcut for runDesigner.bat --planet home. Other arguments are passed on.
call "%~dp0runDesigner.bat" --planet home %*
