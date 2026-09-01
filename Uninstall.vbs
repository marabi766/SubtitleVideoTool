Option Explicit

Dim shell, fso, appRoot, command
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

appRoot = fso.GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & appRoot & "\Uninstall.ps1"""

shell.Run command, 0, False

