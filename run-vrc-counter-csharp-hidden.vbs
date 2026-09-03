Option Explicit

Dim shell, files, baseFolder, executable, command
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")

baseFolder = files.GetParentFolderName(WScript.ScriptFullName)
executable = baseFolder & "\VrcCounter-CSharp\VrcCounter.exe"
command = Chr(34) & executable & Chr(34) & " --data-dir " & Chr(34) & baseFolder & "\." & Chr(34)

shell.CurrentDirectory = baseFolder
shell.Run command, 1, False
