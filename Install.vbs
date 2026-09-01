Option Explicit

Dim shell, fso, packageRoot, command
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

packageRoot = fso.GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & packageRoot & "\Installer.ps1"""

shell.Run command, 0, False

